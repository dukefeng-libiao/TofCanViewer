using System.Text;
using System.Text.Json;

namespace TofCanViewer;

internal sealed class MainForm : Form
{
    private const int BufMax = 60;
    /// <summary>箱沿/料位分析用的帧数，与 box_fill.py 单次读数默认值一致。</summary>
    private const int AnalysisFrames = 15;
    /// <summary>剖面统计用的帧数，与 zone_profile.py --frames 默认值一致。</summary>
    private const int ProfileFrames = 25;
    private const int ViewDepth = 0, ViewRainbow = 1, ViewSnr = 2, ViewFill = 3;

    private readonly ComboBox _devType = NewCombo("USBCAN-II (4)", "USBCAN-I (3)");
    private readonly NumericUpDown _devIndex = NewNum(0, 7, 0);
    private readonly ComboBox _channel = NewCombo("CAN1", "CAN2");
    private readonly ComboBox _bitrate = NewCombo(CanReceiver.BitrateTable.Select(b => (object)b.Text).ToArray());
    private readonly Button _open = NewButton("打开");
    private readonly ComboBox _view = NewCombo("箱体深度", "彩虹深度", "信号 SNR", "料位");
    private readonly NumericUpDown _avg = NewNum(1, 50, 1);
    private readonly NumericUpDown _rangeMin = NewNum(0, 10000, 300, 10);
    private readonly NumericUpDown _rangeMax = NewNum(0, 10000, 900, 10);
    private readonly Button _fit = NewButton("自适应");
    private readonly CheckBox _grid = new() { Text = "网格", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _profKind = NewCombo("图像", "行剖面", "列剖面");
    private readonly NumericUpDown _profIndex = NewNum(0, 255, 16);
    private readonly Button _learn = NewButton("学习空箱");
    private readonly NumericUpDown _expectDepth = NewNum(0, 3000, 260, 10);
    private readonly Button _loadRef = NewButton("载入参考");
    private readonly Button _record = NewButton("记录料位");
    private readonly Button _thresh = NewButton("板子阈值…");
    private readonly Button _save = NewButton("保存 CSV");
    private readonly Label _alarm = BigLabel(16f);
    private readonly Label _fillLabel = BigLabel(20f);
    private readonly InfoView _info = new() { Dock = DockStyle.Fill };
    private readonly HeatmapPanel _map = new() { Dock = DockStyle.Fill };
    private readonly ProfileChart _profile = new() { Dock = DockStyle.Fill, Visible = false };
    private Point _picked = new(-1, -1);
    private readonly ToolStripStatusLabel _stLink = new();
    private readonly ToolStripStatusLabel _stRate = new();
    private readonly ToolStripStatusLabel _stHover = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 40 };

    private readonly TofAssembler _asm = new();
    private readonly List<ushort[]> _zBuf = new();
    private readonly List<byte[]> _sBuf = new();
    private CanReceiver? _rx;
    private TofImage? _latest;
    private TofStatus? _status;
    private TofImage? _shown;
    private double[] _zMed = Array.Empty<double>();
    private double[] _zShow = Array.Empty<double>();
    private double[] _sMed = Array.Empty<double>();
    private BoxRect? _rect;
    private double? _zRim;
    private EmptyBoxRef? _ref;
    private FillResult? _fill;
    private long _lastImages;
    private long _fpsTick = Environment.TickCount64;
    private long _infoTick;
    private long _logTick;
    private double _fps;
    private int _alarmState = -1;
    private bool _stale = true;
    private StreamWriter? _csv;
    private string _csvPath = "";
    private long _csvTick;
    private int _csvRows;
    private readonly uint?[] _cfg = new uint?[FbCfg.Keys.Length];
    private DateTime _cfgTime;
    private volatile bool _cfgRejected;
    private TofDiag? _diag;

    private static string RefPath => Path.Combine(AppContext.BaseDirectory, "empty_box.json");

    public MainForm()
    {
        Text = "TMF8829 CAN 上位机";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(Dp(1250), Dp(800));
        MinimumSize = new Size(Dp(900), Dp(560));
        WindowState = FormWindowState.Maximized;
        Log.Write($"=== 窗体启动 pid={Environment.ProcessId} 位宽={IntPtr.Size * 8} dpi={DeviceDpi} 日志={Log.Path}");

        _devType.Width = Dp(120);
        _devIndex.Width = Dp(45);
        _channel.Width = Dp(65);
        _bitrate.Width = Dp(95);
        _view.Width = Dp(95);
        _avg.Width = Dp(50);
        _rangeMin.Width = Dp(65);
        _rangeMax.Width = Dp(65);
        _profKind.Width = Dp(80);
        _profIndex.Width = Dp(50);
        _expectDepth.Width = Dp(60);
        foreach (var c in new[] { _devType, _channel, _bitrate, _view, _profKind }) c.SelectedIndex = 0;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(Dp(4), Dp(4), Dp(4), Dp(2)),
        };
        bar.Controls.AddRange(new Control[]
        {
            Lbl("设备"), _devType, Lbl("索引"), _devIndex, Lbl("通道"), _channel, Lbl("波特率"), _bitrate, _open, _thresh,
            Sep(), _learn, Lbl("箱深 mm"), _expectDepth, _loadRef, _record, Sep(), _save,
            Lbl("视图"), _view, Lbl("显示帧数"), _avg, Lbl("彩虹范围 mm"), _rangeMin, Lbl("~"), _rangeMax, _fit, _grid,
            Sep(), _profKind, _profIndex,
        });
        bar.SetFlowBreak(_save, true);

        var side = new Panel { Dock = DockStyle.Right, Width = Dp(340), BackColor = Color.FromArgb(28, 28, 30), Padding = new Padding(Dp(8)) };
        _alarm.Height = Dp(48);
        _fillLabel.Height = Dp(56);
        _fillLabel.Margin = new Padding(0, Dp(4), 0, 0);
        side.Controls.Add(_info);
        side.Controls.Add(_fillLabel);
        side.Controls.Add(_alarm);

        _profile.Visible = false;
        var status = new StatusStrip { SizingGrip = false };
        status.Items.AddRange(new ToolStripItem[] { _stLink, new ToolStripSeparator(), _stRate, new ToolStripSeparator(), _stHover });

        // Dock 按添加的逆序生效：bar 顶、status 底、side 右、剖面在左下、分隔条在剖面之上、map 填满剩余
        Controls.Add(_map);
        Controls.Add(_profile);
        Controls.Add(side);
        Controls.Add(status);
        Controls.Add(bar);

        _asm.ImageReady += img => Volatile.Write(ref _latest, img);
        _asm.StatusReady += st => Volatile.Write(ref _status, st);
        _asm.CfgReady += (i, v, rejected) =>
        {
            if (i < 0 || i >= _cfg.Length) return;
            _cfg[i] = v;
            _cfgTime = DateTime.Now;
            if (rejected) _cfgRejected = true;
        };
        _asm.DiagReady += d => Volatile.Write(ref _diag, d);
        _thresh.Click += (_, _) => EditThresholds();
        _open.Click += (_, _) => ToggleOpen();
        _save.Click += (_, _) => SaveCsv();
        _fit.Click += (_, _) => FitRange();
        _learn.Click += (_, _) => LearnEmpty();
        _loadRef.Click += (_, _) => LoadReferenceDialog();
        _record.Click += (_, _) => ToggleRecord();
        _view.SelectedIndexChanged += (_, _) => Redraw();
        _avg.ValueChanged += (_, _) => Refresh_();
        _rangeMin.ValueChanged += (_, _) => Redraw();
        _rangeMax.ValueChanged += (_, _) => Redraw();
        _grid.CheckedChanged += (_, _) => _map.ShowGrid = _grid.Checked;
        _profKind.SelectedIndexChanged += (_, _) =>
        {
            bool prof = _profKind.SelectedIndex != 0;
            if (_shown != null)
                _profIndex.Maximum = Math.Max(0, (_profKind.SelectedIndex == 2 ? _shown.Cols : _shown.Rows) - 1);
            // 切到剖面时用图像上最后点的位置
            int pick = _profKind.SelectedIndex == 1 ? _picked.Y : _picked.X;
            if (prof && pick >= 0) _profIndex.Value = Math.Min(pick, (int)_profIndex.Maximum);
            _profile.Visible = prof;
            _map.Visible = !prof;
            Refresh_();
        };
        _profIndex.ValueChanged += (_, _) => Refresh_();
        _map.MouseMove += (_, e) => ShowHover(e.Location);
        _map.MouseLeave += (_, _) =>
        {
            _map.Hover = null;
            _stHover.Text = "";
        };
        _map.MouseClick += (_, e) => PickProfile(e.Location);
        _ui.Tick += (_, _) => UiTick();
        _ui.Start();
        FormClosing += (_, _) =>
        {
            _ui.Stop();
            _csv?.Dispose();
            _csv = null;
            _rx?.Dispose();
            _rx = null;
        };

        TryLoadReference(RefPath, quiet: true);
        UpdateAlarm();
        UpdateInfo();
        UpdateStatusBar();
    }

    // ---------------- 控件工具 ----------------

    private int Dp(int v) => v * DeviceDpi / 96;

    private static ComboBox NewCombo(params object[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
        c.Items.AddRange(items);
        return c;
    }

    private static NumericUpDown NewNum(int min, int max, int value, int inc = 1) =>
        new() { Minimum = min, Maximum = max, Value = value, Increment = inc, Anchor = AnchorStyles.Left };

    private static Button NewButton(string text) =>
        new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 6, 0) };

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

    private static Label BigLabel(float size) => new()
    {
        Dock = DockStyle.Top,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = Color.DimGray,
    };

    private Control Sep() => new Label { AutoSize = false, Width = Dp(14), Height = 1 };

    // ---------------- CAN ----------------

    /// <summary>命令行自检：自动打开 CAN，运行 ms 毫秒后把统计写入日志并退出。</summary>
    public void AutoOpen(int ms)
    {
        Log.Write("=== AutoOpen 开始");
        ToggleOpen();
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            Log.Write("=== AutoOpen 结束 " + Snapshot() + " " + AnalysisSnapshot());
            Application.Exit();
        };
        t.Start();
    }

    private string Snapshot()
    {
        var rx = _rx;
        return $"CAN帧={(rx == null ? -1 : Interlocked.Read(ref rx.FramesReceived))} " +
               $"读错误={(rx == null ? 0 : Interlocked.Read(ref rx.RxErrors))} " +
               $"重连={(rx == null ? 0 : Interlocked.Read(ref rx.Reconnects))} " +
               $"完整图={Interlocked.Read(ref _asm.Images)} 修补={Interlocked.Read(ref _asm.Patched)} " +
               $"丢图={Interlocked.Read(ref _asm.Incomplete)} 丢块={Interlocked.Read(ref _asm.LostChunks)} " +
               $"序号跳变={Interlocked.Read(ref _asm.SeqGaps)}";
    }

    private string AnalysisSnapshot()
    {
        var sb = new StringBuilder();
        sb.Append($"箱沿边={_rect?.Seen ?? 0}/4 箱沿z={(_zRim is double z ? z.ToString("F0") : "-")}");
        if (_fill?.Fill is double f) sb.Append($" 料位={f:F0}%");
        sb.Append(" 阈值=" + string.Join(",", _cfg.Select(v => v?.ToString() ?? "?")));
        if (_diag is TofDiag d) sb.Append($" spi错={d.SpiErrors} 温度={d.Temp} 运行={d.UptimeMs / 1000}s");
        return sb.ToString();
    }

    // ---------------- 板子阈值（tof_diag.py --set） ----------------

    private void EditThresholds()
    {
        var rx = _rx;
        if (rx == null)
        {
            MessageBox.Show(this, "先打开 CAN。", "板子阈值", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        rx.Send(TofAssembler.IdCmd, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0 });
        using var dlg = new ThresholdForm((uint?[])_cfg.Clone(), _zRim);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var changes = dlg.Changes();
        if (changes.Count == 0) return;
        _cfgRejected = false;
        int fails = 0;
        foreach (var (i, v) in changes)
        {
            var cmd = new byte[8];
            cmd[0] = 0x01;
            cmd[1] = (byte)i;
            BitConverter.GetBytes(v).CopyTo(cmd, 2);
            if (!rx.Send(TofAssembler.IdCmd, cmd)) fails++;
            Log.Write($"写阈值 {FbCfg.Keys[i]}={v}");
        }
        if (fails > 0)
            MessageBox.Show(this, $"{fails} 条命令发送失败，检查 CAN 连接。", "板子阈值", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        var t = new System.Windows.Forms.Timer { Interval = 800 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            var bad = changes.Where(c => _cfg[c.Index] != c.Value).ToList();
            if (bad.Count == 0 && !_cfgRejected)
                _stHover.Text = "阈值已写入：" + string.Join("  ", changes.Select(c => $"{FbCfg.Keys[c.Index]}={c.Value}"));
            else
                MessageBox.Show(this, "板子没有确认以下字段（可能没收到命令，或者固件不支持）：\n" +
                                      string.Join("\n", bad.Select(c => $"{FbCfg.Keys[c.Index]}：期望 {c.Value}，板子 {_cfg[c.Index]?.ToString() ?? "?"}")),
                    "板子阈值", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        t.Start();
    }

    private void ToggleOpen()
    {
        if (_rx != null)
        {
            _rx.Dispose();
            _rx = null;
            _open.Text = "打开";
            Log.Write("用户关闭 CAN " + Snapshot());
            UpdateStatusBar();
            return;
        }
        uint type = _devType.SelectedIndex == 0 ? ControlCan.DevUsbCan2 : ControlCan.DevUsbCan1;
        int bitrate = CanReceiver.BitrateTable[Math.Max(0, _bitrate.SelectedIndex)].Bitrate;
        Log.Write($"用户点击打开 devType={type} devIndex={_devIndex.Value} ch={_channel.SelectedIndex} bitrate={bitrate}");
        var rx = new CanReceiver(type, (uint)_devIndex.Value, (uint)_channel.SelectedIndex, _asm.Feed, bitrate);
        try
        {
            rx.Open();
        }
        catch (Exception ex)
        {
            Log.Write("打开失败: " + ex);
            MessageBox.Show(this, ex.Message + "\n\n若设备刚被其他软件占用或卡死，拔插 USB-CAN 后重试。",
                "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _rx = rx;
        _open.Text = "关闭";
        _logTick = Environment.TickCount64;
        Log.Write("打开成功");
        UpdateStatusBar();
    }

    // ---------------- 主循环 ----------------

    private void UiTick()
    {
        long now = Environment.TickCount64;
        if (now - _fpsTick >= 1000)
        {
            long images = Interlocked.Read(ref _asm.Images);
            _fps = (images - _lastImages) * 1000.0 / (now - _fpsTick);
            _lastImages = images;
            _fpsTick = now;
        }

        var img = Volatile.Read(ref _latest);
        if (img != null && !ReferenceEquals(img, _shown))
            Present(img);

        bool stale = _shown == null || (DateTime.Now - _shown.Time).TotalSeconds > 1.5;
        if (stale != _stale)
        {
            _stale = stale;
            if (_rx != null)
                Log.Write((stale ? "图像中断 " : "图像恢复 ") + Snapshot());
        }
        UpdateAlarm();

        if (now - _infoTick >= 250)
        {
            _infoTick = now;
            UpdateInfo();
            UpdateStatusBar();
        }
        if (_rx != null && now - _logTick >= 10000)
        {
            _logTick = now;
            Log.Write("统计 " + Snapshot() + " " + AnalysisSnapshot());
        }
        if (_csv != null && !_stale && now - _csvTick >= 2000)
        {
            _csvTick = now;
            WriteFillRow();
        }
    }

    // ---------------- 料位记录（box_fill.py --watch） ----------------

    private void ToggleRecord()
    {
        if (_csv != null)
        {
            _csv.Dispose();
            _csv = null;
            _record.Text = "记录料位";
            Log.Write($"停止记录料位 {_csvPath} 共 {_csvRows} 行");
            MessageBox.Show(this, $"共记录 {_csvRows} 行：\n{_csvPath}", "记录料位", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_ref == null)
        {
            MessageBox.Show(this, "先学习空箱或载入参考。", "记录料位", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _csvPath = Path.Combine(dir, $"fill_watch_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _csv = new StreamWriter(_csvPath, false, new UTF8Encoding(false));
        _csv.WriteLine("time,fill_pct,p10,p50,p90,valid,roi,over,drift_mm");
        _csvRows = 0;
        _csvTick = 0;
        _record.Text = "停止记录";
        Log.Write("开始记录料位 " + _csvPath);
    }

    /// <summary>列与 box_fill.py --watch 的 CSV 相同。</summary>
    private void WriteFillRow()
    {
        var m = _fill;
        if (_csv == null || m == null) return;
        string now = DateTime.Now.ToString("HH:mm:ss");
        string drift = m.Drift is double d ? d.ToString("+0;-0;+0") : "";
        _csv.WriteLine(m.Fill is double f
            ? $"{now},{f:F1},{m.P10:F1},{m.P50:F1},{m.P90:F1},{m.Valid},{m.Roi},{m.Over},{drift}"
            : $"{now},,,,,{m.Valid},{m.Roi},{m.Over},");
        _csv.Flush();
        _csvRows++;
    }

    private void Present(TofImage img)
    {
        if (_shown != null && (_shown.Rows != img.Rows || _shown.Cols != img.Cols))
        {
            _zBuf.Clear();
            _sBuf.Clear();
        }
        _shown = img;
        _zBuf.Add(img.Z);
        _sBuf.Add(img.Snr);
        if (_zBuf.Count > BufMax)
        {
            _zBuf.RemoveAt(0);
            _sBuf.RemoveAt(0);
        }
        Refresh_();
    }

    private static List<T> Last<T>(List<T> buf, int n) => buf.GetRange(Math.Max(0, buf.Count - n), Math.Min(n, buf.Count));

    /// <summary>重新做中值、箱沿、料位，然后重画图和剖面。</summary>
    private void Refresh_()
    {
        var img = _shown;
        if (img == null) return;
        int n = img.Rows * img.Cols;
        int k = (int)_avg.Value;
        // 画面跟最新帧（可选少量帧中值）；箱沿和料位用 15 帧中值，结果与脚本一致
        _zShow = k <= 1 ? img.Z.Select(v => (double)v).ToArray() : BoxMath.ZoneMedian(Last(_zBuf, k), n, 0.5);
        _sMed = k <= 1 ? img.Snr.Select(v => (double)v).ToArray() : BoxMath.ZoneMedian(Last(_sBuf, k), n);
        _zMed = BoxMath.ZoneMedian(Last(_zBuf, AnalysisFrames), n, 0.5);
        _rect = BoxMath.FindRectangle(_zMed, img.Rows, img.Cols);
        _zRim = _rect.ZRim ?? BoxMath.EstimateRim(_zMed);
        _fill = RefMatches(img) ? BoxMath.Measure(_zMed, _ref!) : null;
        _profIndex.Maximum = Math.Max(0, (_profKind.SelectedIndex == 2 ? img.Cols : img.Rows) - 1);
        Redraw();
        UpdateProfile();
    }

    private bool RefMatches(TofImage img) => _ref != null && _ref.Rows == img.Rows && _ref.Cols == img.Cols;

    // ---------------- 绘图 ----------------

    private void Redraw()
    {
        int view = _view.SelectedIndex;
        _rangeMin.Enabled = _rangeMax.Enabled = _fit.Enabled = view == ViewRainbow;
        var img = _shown;
        if (img == null || _zShow.Length != img.Rows * img.Cols) return;

        var f = new MapFrame { Rows = img.Rows, Cols = img.Cols, Px = new int[img.Rows * img.Cols] };
        f.ProfileRow = _picked.Y < f.Rows ? _picked.Y : -1;
        f.ProfileCol = _picked.X < f.Cols ? _picked.X : -1;
        if (img.OverZones > 0) f.Marker = new Point(img.OverCol, img.OverRow);

        if (view == ViewFill && RefMatches(img) && _fill != null)
            PaintFill(f);
        else if (view == ViewSnr)
            PaintSnr(f);
        else if (view == ViewRainbow || _zRim == null)
            PaintRainbow(f, view != ViewRainbow ? "没找到箱沿平台，改用彩虹配色" : null);
        else
            PaintDepth(f, view == ViewFill ? "未学习空箱：清空箱子后点“学习空箱”" : null);
        _map.SetFrame(f);
    }

    /// <summary>同 box_edges.draw_map：箱沿白 → 黄 → 绿 → 箱底蓝，红线是箱沿内边。</summary>
    private void PaintDepth(MapFrame f, string? note)
    {
        double zr = _zRim!.Value;
        double deep = BoxMath.Percentile(_zMed, 0.9) ?? zr + 1;
        for (int i = 0; i < f.Px.Length; i++)
            f.Px[i] = BoxMath.DepthColor(_zShow[i], zr, deep);
        var s = _rect!.Sides;
        f.Sides = s;
        f.Title = note ?? $"箱沿边找到 {_rect.Seen}/4";
        var sub = $"箱沿 z {zr:F0} mm";
        if (s["left"].Visible && s["right"].Visible && s["top"].Visible && s["bottom"].Visible)
            sub += $"   开口 {s["right"].Mm - s["left"].Mm:F0} x {s["bottom"].Mm - s["top"].Mm:F0} mm";
        f.Sub = sub + $"   （白 = 箱沿 ±25 mm，品红 = 比箱沿更近，黄→蓝 = 越来越深，红线 = 箱沿内边）";
    }

    private void PaintRainbow(MapFrame f, string? note)
    {
        double lo = (double)_rangeMin.Value, hi = Math.Max((double)_rangeMax.Value, lo + 1);
        int Color(double v) => BoxMath.Jet(1.0 - (v - lo) / (hi - lo));
        for (int i = 0; i < f.Px.Length; i++)
            f.Px[i] = _zShow[i] == 0 ? BoxMath.Argb(0, 0, 0) : Color(_zShow[i]);
        f.Bar = new ColorBar(Color, lo, hi, "mm");
        f.Title = note ?? "深度 z";
        f.Sub = "近 = 红，远 = 蓝，黑 = 无数据";
    }

    private void PaintSnr(MapFrame f)
    {
        double hi = Math.Max(1, _sMed.DefaultIfEmpty(0).Max());
        int Color(double v) => BoxMath.Jet(v / hi);
        for (int i = 0; i < f.Px.Length; i++)
            f.Px[i] = Color(_sMed[i]);
        f.Bar = new ColorBar(Color, 0, hi, "");
        f.Title = "传感器 SNR";
        f.Sub = $"最大 {hi:F0}";
    }

    /// <summary>同 box_fill.draw_fill：非箱底区域压暗，箱底 zone 按料高 0% 蓝 → 100% 红。</summary>
    private void PaintFill(MapFrame f)
    {
        var r = _ref!;
        var m = _fill!;
        double deep = BoxMath.Percentile(_zMed, 0.9) ?? r.ZRim + 1;
        var roi = new HashSet<int>(r.Roi);
        for (int i = 0; i < f.Px.Length; i++)
        {
            int c = BoxMath.DepthColor(_zShow[i], r.ZRim, deep);
            if (!roi.Contains(i))
                c = BoxMath.Blend(BoxMath.Argb(0, 0, 0), c, 0.55);
            else if (_zShow[i] != 0)
                c = BoxMath.FillColor(Math.Min(Math.Max(r.Z[i] - _zShow[i], 0.0), 1.2 * r.Depth) / r.Depth);
            f.Px[i] = c;
        }
        f.Sides = r.Sides;
        f.Title = m.Fill is double fill ? $"料位 {fill:F0} %" : "料位 未知";
        f.Sub = $"有数据 {m.Valid}/{m.Roi} 箱底 zone   超箱沿 {m.Over}   （箱底 zone：蓝 0% … 红 100%，红线 = 箱沿内边）";
    }

    // ---------------- 剖面（zone_profile.py） ----------------

    private void PickProfile(Point p)
    {
        if (_map.HitTest(p, out int row, out int col))
        {
            _picked = new Point(col, row);
            Redraw();
        }
    }

    private void UpdateProfile()
    {
        var img = _shown;
        if (img == null || _profKind.SelectedIndex == 0) return;
        bool isRow = _profKind.SelectedIndex == 1;
        int rows = img.Rows, cols = img.Cols;
        int line = (int)Math.Min(_profIndex.Value, isRow ? rows - 1 : cols - 1);
        int[] idx = isRow ? Enumerable.Range(0, cols).Select(c => line * cols + c).ToArray()
                          : Enumerable.Range(0, rows).Select(r => r * cols + line).ToArray();
        var frames = Last(_zBuf, ProfileFrames);
        var st = BoxMath.LineStats(frames, idx);
        double zr = BoxMath.EstimateRim(BoxMath.ZoneMedian(frames, rows * cols, 0)) ?? _zRim ?? st.Where(s => s.N > 0).Select(s => s.Med).DefaultIfEmpty(0).Min();
        double half = isRow ? cols / 2.0 : rows / 2.0, span = isRow ? 0.75 * cols : rows;
        var lat = Enumerable.Range(0, idx.Length).Select(k => (k + 0.5 - half) / span * (st[k].N > 0 ? st[k].Med : zr)).ToArray();
        var snrAll = BoxMath.ZoneMedian(Last(_sBuf, frames.Count), rows * cols);
        var snr = idx.Select(i => snrAll[i]).ToArray();

        var (walls, report) = BoxMath.ProfileReport(st, lat, frames.Count, zr, 25, snr);
        string title = $"{(isRow ? "第 " + line + " 行" : "第 " + line + " 列")}（{rows} x {cols} zone，{frames.Count} 帧），箱沿 z = {zr:F0} mm";
        const string sub = "蓝 = 帧间 min..max，红 = 中位，绿带 = 箱沿 ±25 mm，橙 = 箱壁";
        _profile.SetData(st, lat, snr, zr, 25, title, sub, isRow ? "x" : "y", walls, report);
    }

    // ---------------- 空箱参考（box_fill.py） ----------------

    private void LearnEmpty()
    {
        var img = _shown;
        if (img == null)
        {
            MessageBox.Show(this, "还没有收到图像。", "学习空箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int k = Math.Min(25, _zBuf.Count);
        if (k < 10)
        {
            MessageBox.Show(this, $"至少需要 10 帧，现在只有 {k} 帧，等几秒再试。", "学习空箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var z = BoxMath.ZoneMedian(Last(_zBuf, k), img.Rows * img.Cols, 0.5);
        EmptyBoxRef r;
        try
        {
            r = BoxMath.Calibrate(z, img.Rows, img.Cols);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "学习空箱失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        double expect = (double)_expectDepth.Value;
        var problems = BoxMath.CheckReference(r, expect > 0 ? expect : null);
        string summary = $"用最近 {k} 帧的中值：\n箱沿 z = {r.ZRim:F0} mm，空箱底 z = {r.ZFloor:F0} mm → 箱深 {r.Depth:F0} mm（期望 {expect:F0} mm）\n" +
                         $"盯住的箱底 zone：{r.Roi.Length}（占箱沿内有数据 zone 的 {100 * r.FloorFraction:F0}%）";
        if (problems.Count > 0)
        {
            var ans = MessageBox.Show(this, summary + "\n\n看起来不像视野良好的空箱：\n- " + string.Join("\n- ", problems) +
                                            "\n\n清空箱子、保持箱子和模块不动再试。仍然保存？",
                "学习空箱", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ans != DialogResult.Yes) return;
        }
        _ref = r;
        File.WriteAllText(RefPath, JsonSerializer.Serialize(r));
        Log.Write($"学习空箱 箱沿z={r.ZRim:F0} 箱底z={r.ZFloor:F0} 箱深={r.Depth:F0} roi={r.Roi.Length} 问题={problems.Count}");
        _view.SelectedIndex = ViewFill;
        Refresh_();
        if (problems.Count == 0)
            MessageBox.Show(this, summary + "\n\n已保存：" + RefPath, "学习空箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadReferenceDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "空箱参考|*.json", Title = "载入 empty_box.json（脚本 run_set_empty.bat 生成的也可以）" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (TryLoadReference(dlg.FileName, quiet: false))
        {
            File.Copy(dlg.FileName, RefPath, overwrite: true);
            _view.SelectedIndex = ViewFill;
            Refresh_();
        }
    }

    private bool TryLoadReference(string path, bool quiet)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var r = JsonSerializer.Deserialize<EmptyBoxRef>(File.ReadAllText(path));
            if (r == null || r.Z.Length != r.Rows * r.Cols || r.Depth <= 0)
                throw new InvalidDataException("文件内容不完整");
            _ref = r;
            Log.Write($"载入空箱参考 {path} {r.Rows}x{r.Cols} 箱深={r.Depth:F0}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"载入空箱参考失败 {path}: {ex.Message}");
            if (!quiet)
                MessageBox.Show(this, ex.Message, "载入参考失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    // ---------------- 侧栏、状态栏 ----------------

    private void UpdateAlarm()
    {
        int state = _stale ? 0 : (_shown!.Alarm ? 2 : 1);
        if (state != _alarmState)
        {
            _alarmState = state;
            (_alarm.Text, _alarm.BackColor) = state switch
            {
                0 => ("无图像", Color.DimGray),
                1 => ("板子：正常", Color.SeaGreen),
                _ => ("板子：超高报警", Color.Firebrick),
            };
        }

        string text;
        Color back;
        if (_ref == null)
            (text, back) = ("料位：未学习空箱", Color.DimGray);
        else if (_stale || _fill?.Fill is not double fill)
            (text, back) = ("料位 --", Color.DimGray);
        else
            (text, back) = ($"料位 {fill:F0} %", Color.FromArgb(BoxMath.FillColor(Math.Clamp(fill / 100.0, 0, 1))));
        if (_fillLabel.Text != text) _fillLabel.Text = text;
        if (_fillLabel.BackColor != back) _fillLabel.BackColor = back;
    }

    private void UpdateInfo()
    {
        var rx = _rx;
        var img = _shown;
        var st = Volatile.Read(ref _status);
        var lines = new List<(string, string?)>();

        if (_fill != null && _ref != null)
        {
            var m = _fill;
            lines.Add(("料位", null));
            if (m.Fill is double fill)
            {
                lines.Add(("平均料高", $"{fill / 100.0 * _ref.Depth:F0} / {_ref.Depth:F0} mm"));
                lines.Add(("P10/50/90", $"{m.P10:F0} / {m.P50:F0} / {m.P90:F0} %"));
            }
            lines.Add(("有数据", (m.Valid < 0.5 * m.Roi ? "!" : "") + $"{m.Valid}/{m.Roi} zone"));
            lines.Add(("超箱沿", (m.Over > 0 ? "!" : "") + $"{m.Over} zone"));
            if (m.Drift is double d)
                lines.Add(("箱沿偏移", (Math.Abs(d) > BoxMath.DriftWarnMm ? "!" : "") + $"{d:+0;-0;0} mm" +
                                      (Math.Abs(d) > BoxMath.DriftWarnMm ? " 需重学" : "")));
        }

        if (_rect != null)
        {
            var s = _rect.Sides;
            lines.Add(("箱体", null));
            lines.Add(("箱沿边", $"{_rect.Seen}/4"));
            lines.Add(("箱沿 z", _zRim is double zr ? $"{zr:F0} mm" : "-"));
            if (s["left"].Visible && s["right"].Visible)
                lines.Add(("开口宽", $"{s["right"].Mm - s["left"].Mm:F0} mm"));
            if (s["top"].Visible && s["bottom"].Visible)
                lines.Add(("开口高", $"{s["bottom"].Mm - s["top"].Mm:F0} mm"));
            foreach (var (key, name) in new[] { ("left", "左"), ("right", "右"), ("top", "上"), ("bottom", "下") })
            {
                var e = s[key];
                if (e.Found == 0)
                {
                    lines.Add((name, "!未找到"));
                    continue;
                }
                string state = e.Visible ? "可见" : e.Rejected ? "剔除" : e.Scattered ? "散乱" : "太弱";
                lines.Add((name, (e.Visible ? "" : "!") + $"{state} {e.Found}/{e.Lines} {e.Mm:+0;-0} mm 沿{e.Rim:F0}"));
            }
            if (_shown != null && _shown.Rows > 8 && BoxMath.RowCoherence(_zMed, _shown.Rows, _shown.Cols) is var (adj, two))
                lines.Add(("行一致性", (adj > two ? "!" : "") + $"邻行 {adj:F1} / 隔行 {two:F1} mm"));
            lines.Add(("参考箱深", _ref != null ? $"{_ref.Depth:F0} mm" : "!未学习空箱"));
        }

        lines.Add(("接收", null));
        lines.Add(("帧率", $"{_fps:F1} fps"));
        lines.Add(("CAN 帧", rx == null ? "-" : Interlocked.Read(ref rx.FramesReceived).ToString()));
        lines.Add(("完整/修补", $"{Interlocked.Read(ref _asm.Images)} / {Interlocked.Read(ref _asm.Patched)}"));
        long lost = Interlocked.Read(ref _asm.Incomplete);
        lines.Add(("丢图", (lost > 0 ? "!" : "") + lost));
        lines.Add(("读错误/重连", rx == null ? "-" : $"{Interlocked.Read(ref rx.RxErrors)} / {Interlocked.Read(ref rx.Reconnects)}"));

        lines.Add(("板子", null));
        if (img != null)
        {
            lines.Add(("分辨率", $"{img.Cols} x {img.Rows}  #{img.Seq}"));
            lines.Add(("有效 zone", img.ValidZones.ToString()));
            lines.Add(("最近 z", $"{img.ZMinMm} mm"));
            if (img.OverZones > 0)
                lines.Add(("超高", $"!{img.OverZones} zone，{img.OverMinZMm} mm"));
        }
        if (st != null)
        {
            lines.Add(("阶段", TofStatus.Name(st.Stage)));
            if (st.FailStage != 0)
                lines.Add(("失败", $"!{TofStatus.Name(st.FailStage)} rc={st.FailRc}"));
            lines.Add(("测量帧", $"ok={st.FramesOk} bad={st.FramesBad}"));
        }
        if (Volatile.Read(ref _diag) is TofDiag dg)
        {
            var up = TimeSpan.FromMilliseconds(dg.UptimeMs);
            lines.Add(("运行时间", $"{(int)up.TotalHours}:{up.Minutes:D2}:{up.Seconds:D2}"));
            lines.Add(("温度", $"{dg.Temp} ℃"));
            lines.Add(("SPI/几何错", (dg.SpiErrors + dg.GeometryBad > 0 ? "!" : "") + $"{dg.SpiErrors} / {dg.GeometryBad}"));
            lines.Add(("状态字", $"0x{dg.LastStatus:X2}"));
            lines.Add(("报警次数", dg.AlarmEvents.ToString()));
            lines.Add(("超高/正常帧", $"{dg.OverStreak} / {dg.ClearStreak}"));
        }
        if (_cfg.Any(v => v != null))
        {
            lines.Add(("板子阈值", null));
            for (int i = 0; i < _cfg.Length; i++)
                lines.Add((FbCfg.Labels[i], _cfg[i]?.ToString() ?? "?"));
            if ((DateTime.Now - _cfgTime).TotalSeconds > 5)
                lines.Add(("更新", $"!{(DateTime.Now - _cfgTime).TotalSeconds:F0} s 前"));
        }
        _info.SetLines(lines);
    }

    private void UpdateStatusBar()
    {
        if (_rx == null)
        {
            _stLink.Text = "● 未连接";
            _stLink.ForeColor = Color.Gray;
        }
        else if (_stale)
        {
            _stLink.Text = "● 已打开，无数据";
            _stLink.ForeColor = Color.DarkOrange;
        }
        else
        {
            _stLink.Text = "● 接收中";
            _stLink.ForeColor = Color.SeaGreen;
        }
        _stRate.Text = $"{_fps:F1} fps   丢图 {Interlocked.Read(ref _asm.Incomplete)}" + (_csv != null ? $"   ● 记录料位 {_csvRows} 行" : "");
    }

    private void ShowHover(Point p)
    {
        var img = _shown;
        if (img == null || !_map.HitTest(p, out int row, out int col))
        {
            _map.Hover = null;
            _stHover.Text = "";
            return;
        }
        _map.Hover = new Point(col, row);
        int i = row * img.Cols + col;
        var sb = new StringBuilder($"行 {row,2}  列 {col,2}    本帧 {img.Z[i]} mm    15 帧中值 {(i < _zMed.Length ? _zMed[i] : 0):F0} mm    SNR {img.Snr[i]}");
        if (_fill != null && _ref != null && _fill.Heights.TryGetValue(i, out double h))
            sb.Append($"    料高 {h:F0} mm（{h / _ref.Depth * 100:F0}%）");
        _stHover.Text = sb.ToString();
    }

    private void FitRange()
    {
        var v = _zMed.Where(z => z > 0).OrderBy(z => z).ToArray();
        if (v.Length < 10) return;
        int lo = (int)v[v.Length * 2 / 100] / 10 * 10;
        int hi = ((int)v[Math.Min(v.Length - 1, v.Length * 98 / 100)] + 9) / 10 * 10;
        if (hi <= lo) hi = lo + 10;
        _rangeMax.Value = Math.Min(hi, (int)_rangeMax.Maximum);
        _rangeMin.Value = Math.Min(lo, (int)_rangeMin.Maximum);
    }

    private void SaveCsv()
    {
        var img = _shown;
        if (img == null)
            return;
        using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"tof_{img.Time:yyyyMMdd_HHmmss}_{img.Seq}.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var sb = new StringBuilder("row,col,z_mm,z_median_mm,snr\n");
        for (int r = 0; r < img.Rows; r++)
            for (int c = 0; c < img.Cols; c++)
            {
                int i = r * img.Cols + c;
                sb.Append(r).Append(',').Append(c).Append(',').Append(img.Z[i]).Append(',')
                  .Append(i < _zMed.Length ? _zMed[i].ToString("F1") : "").Append(',').Append(img.Snr[i]).Append('\n');
            }
        File.WriteAllText(dlg.FileName, sb.ToString());
    }
}
