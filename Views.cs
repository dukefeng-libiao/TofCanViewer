using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TofCanViewer;

internal sealed record ColorBar(Func<double, int> Color, double Lo, double Hi, string Unit);

/// <summary>一帧要画的内容：像素已按视图配好色。</summary>
internal sealed class MapFrame
{
    public int Rows, Cols;
    public int[] Px = Array.Empty<int>();
    public string? Title, Sub;
    public Dictionary<string, SideEdge>? Sides;
    public Point? Marker;
    public ColorBar? Bar;
    public int ProfileRow = -1, ProfileCol = -1;
}

/// <summary>zone 图：整数像素格子 + 网格 + 箱沿红线（同 box_edges.draw_map）+ 色标 + 标题。</summary>
internal sealed class HeatmapPanel : Control
{
    private Bitmap? _bmp;
    private Bitmap? _bar;
    private MapFrame? _f;
    private ColorBar? _barSpec;
    private Point? _hover;
    private bool _showGrid = true;
    private Rectangle _dest;
    private int _cell;

    public HeatmapPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.Gainsboro;
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; Invalidate(); }
    }

    public Point? Hover
    {
        get => _hover;
        set
        {
            if (_hover == value) return;
            _hover = value;
            Invalidate();
        }
    }

    public void SetFrame(MapFrame f)
    {
        if (f.Rows * f.Cols == 0 || f.Px.Length < f.Rows * f.Cols) return;
        if (_bmp == null || _bmp.Width != f.Cols || _bmp.Height != f.Rows)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(f.Cols, f.Rows, PixelFormat.Format32bppArgb);
        }
        var data = _bmp.LockBits(new Rectangle(0, 0, f.Cols, f.Rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int r = 0; r < f.Rows; r++)
            Marshal.Copy(f.Px, r * f.Cols, data.Scan0 + r * data.Stride, f.Cols);
        _bmp.UnlockBits(data);

        if (f.Bar != null && (_bar == null || _barSpec == null || f.Bar.Lo != _barSpec.Lo || f.Bar.Hi != _barSpec.Hi || f.Bar.Color != _barSpec.Color))
        {
            _bar ??= new Bitmap(1, 256, PixelFormat.Format32bppArgb);
            for (int y = 0; y < 256; y++)
                _bar.SetPixel(0, y, Color.FromArgb(f.Bar.Color(f.Bar.Hi - (f.Bar.Hi - f.Bar.Lo) * y / 255.0)));
        }
        _barSpec = f.Bar;
        _f = f;
        Invalidate();
    }

    public bool HitTest(Point p, out int row, out int col)
    {
        row = col = 0;
        if (_f == null || _cell == 0 || !_dest.Contains(p))
            return false;
        col = (p.X - _dest.X) / _cell;
        row = (p.Y - _dest.Y) / _cell;
        return col < _f.Cols && row < _f.Rows;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var f = _f;
        if (f == null || _bmp == null)
        {
            TextRenderer.DrawText(g, "等待图像…", Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        using var titleFont = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        int pad = Dp(10);
        int head = f.Title != null ? titleFont.Height + Font.Height + Dp(8) : 0;
        int barW = _barSpec != null ? Dp(90) : 0;
        int w = ClientSize.Width - barW - 2 * pad, h = ClientSize.Height - head - 2 * pad;
        // 格子边长取整数像素，每格大小一致
        _cell = Math.Max(1, Math.Min(w / f.Cols, h / f.Rows));
        int dw = _cell * f.Cols, dh = _cell * f.Rows;
        _dest = new Rectangle(pad + Math.Max(0, (w - dw) / 2), head + pad + Math.Max(0, (h - dh) / 2), dw, dh);

        if (f.Title != null)
        {
            TextRenderer.DrawText(g, f.Title, titleFont, new Point(pad, Dp(4)), Color.White);
            if (f.Sub != null)
                TextRenderer.DrawText(g, f.Sub, Font, new Point(pad, Dp(6) + titleFont.Height), Color.Silver);
        }

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_bmp, _dest);
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (_showGrid && _cell >= 6)
        {
            using var pen = new Pen(Color.FromArgb(50, 0, 0, 0));
            for (int c = 1; c < f.Cols; c++)
                g.DrawLine(pen, _dest.X + c * _cell, _dest.Top, _dest.X + c * _cell, _dest.Bottom - 1);
            for (int r = 1; r < f.Rows; r++)
                g.DrawLine(pen, _dest.Left, _dest.Y + r * _cell, _dest.Right - 1, _dest.Y + r * _cell);
        }
        if (f.ProfileRow >= 0)
        {
            using var pen = new Pen(Color.Cyan, Math.Max(1, Dp(2)));
            g.DrawRectangle(pen, _dest.X, _dest.Y + f.ProfileRow * _cell, _dest.Width - 1, _cell - 1);
        }
        if (f.ProfileCol >= 0)
        {
            using var pen = new Pen(Color.Cyan, Math.Max(1, Dp(2)));
            g.DrawRectangle(pen, _dest.X + f.ProfileCol * _cell, _dest.Y, _cell - 1, _dest.Height - 1);
        }
        if (f.Sides != null) DrawRim(g, f);
        if (f.Marker is Point m) DrawCell(g, f, m, Color.White, Math.Max(2, Dp(2)));
        if (_hover is Point hv) DrawCell(g, f, hv, Color.Yellow, 1);
        DrawBar(g);
    }

    /// <summary>同 box_edges.draw_map：每条可见边画一条红线，线的两端取相邻可见边的位置。</summary>
    private void DrawRim(Graphics g, MapFrame f)
    {
        var s = f.Sides!;
        int At(string side, int def) =>
            s.TryGetValue(side, out var e) && e.Visible ? (int)Math.Round((e.Coord + 0.5) * _cell) : def;
        int left = _dest.X + At("left", 0), right = _dest.X + At("right", f.Cols * _cell);
        int top = _dest.Y + At("top", 0), bottom = _dest.Y + At("bottom", f.Rows * _cell);
        using var pen = new Pen(Color.FromArgb(255, 40, 40), Math.Max(2, Dp(3)));
        foreach (var (side, e) in s)
        {
            if (!e.Visible) continue;
            if (side == "left") g.DrawLine(pen, left, top, left, bottom);
            else if (side == "right") g.DrawLine(pen, right, top, right, bottom);
            else if (side == "top") g.DrawLine(pen, left, top, right, top);
            else if (side == "bottom") g.DrawLine(pen, left, bottom, right, bottom);
        }
    }

    private void DrawCell(Graphics g, MapFrame f, Point p, Color color, int width)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= f.Cols || p.Y >= f.Rows) return;
        using var pen = new Pen(color, width);
        g.DrawRectangle(pen, _dest.X + p.X * _cell, _dest.Y + p.Y * _cell, _cell - 1, _cell - 1);
    }

    private void DrawBar(Graphics g)
    {
        var b = _barSpec;
        if (b == null || _bar == null) return;
        var r = new Rectangle(_dest.Right + Dp(14), _dest.Top, Dp(16), _dest.Height);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_bar, r);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.DrawRectangle(Pens.Gray, r.X, r.Y, r.Width - 1, r.Height - 1);
        for (int k = 0; k <= 4; k++)
        {
            double val = b.Hi - (b.Hi - b.Lo) * k / 4.0;
            int y = r.Top + (r.Height - 1) * k / 4;
            g.DrawLine(Pens.Gray, r.Right, y, r.Right + Dp(4), y);
            string text = k == 0 && b.Unit.Length > 0 ? $"{val:0} {b.Unit}" : $"{val:0}";
            TextRenderer.DrawText(g, text, Font, new Point(r.Right + Dp(6), y - Font.Height / 2), ForeColor);
        }
    }

    private int Dp(int v) => v * DeviceDpi / 96;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bmp?.Dispose();
            _bar?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>同 zone_profile.chart：深度（min..max 竖条 + 中位点 + 箱沿带）、帧间标准差、SNR 三栏。</summary>
internal sealed class ProfileChart : Control
{
    private ZoneLine[] _st = Array.Empty<ZoneLine>();
    private double[] _lat = Array.Empty<double>();
    private double[]? _snr;
    private double _zRim, _tol = 25;
    private string _title = "", _sub = "", _axis = "x";
    private List<double> _walls = new();
    private List<string> _report = new();

    public ProfileChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 8.5f);
    }

    public void SetData(ZoneLine[] st, double[] lat, double[]? snr, double zRim, double tol, string title, string sub, string axis,
        List<double> walls, List<string> report)
    {
        _walls = walls;
        _report = report;
        _st = st;
        _lat = lat;
        _snr = snr;
        _zRim = zRim;
        _tol = tol;
        _title = title;
        _sub = sub;
        _axis = axis;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var ks = Enumerable.Range(0, _st.Length).Where(k => _st[k].N > 0).ToList();
        if (ks.Count == 0)
        {
            TextRenderer.DrawText(g, "剖面：没有数据", Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;

        double xmin = ks.Min(k => _lat[k]) - 20, xmax = ks.Max(k => _lat[k]) + 20;
        double zmin = Math.Min(ks.Min(k => _st[k].Lo), _zRim - _tol) - 20, zmax = ks.Max(k => _st[k].Hi) + 20;
        int fh = Font.Height;
        int reportW = Math.Min(Dp(420), ClientSize.Width / 3);
        int left = Dp(52), right = Dp(14) + reportW, top = fh * 2 + Dp(10);
        int bottomAxis = fh * 2 + Dp(4);
        int gap = fh + Dp(8);
        int avail = ClientSize.Height - top - bottomAxis - 2 * gap;
        if (avail < Dp(60)) return;
        bool hasSnr = _snr != null;
        int h1 = (int)(avail * (hasSnr ? 0.6 : 0.75));
        int h2 = (avail - h1) / (hasSnr ? 2 : 1);
        int top1 = top, top2 = top1 + h1 + gap, top3 = top2 + h2 + gap;
        int width = ClientSize.Width;

        float Px(double x) => (float)(left + (x - xmin) * (width - left - right) / (xmax - xmin));
        float Py(double z) => (float)(top1 + (z - zmin) * h1 / (zmax - zmin));

        TextRenderer.DrawText(g, _title, Font, new Point(left, Dp(2)), Color.Black);
        TextRenderer.DrawText(g, _sub, Font, new Point(left, Dp(2) + fh), Color.DimGray);

        var panels = new List<(int Top, int H, string Name)> { (top1, h1, "深度 z (mm)，越近越靠上"), (top2, h2, "帧间标准差 (mm)，超过 15 mm 标红") };
        if (hasSnr) panels.Add((top3, h2, "传感器 SNR"));
        using var gridPen = new Pen(Color.FromArgb(225, 225, 225));
        foreach (var (pt, ph, name) in panels)
        {
            g.DrawRectangle(Pens.Gray, left, pt, width - left - right, ph);
            TextRenderer.DrawText(g, name, Font, new Point(left, pt - fh - 1), Color.Black);
        }
        var last = panels[^1];
        for (int x = (int)Math.Ceiling(xmin / 50.0) * 50; x <= xmax; x += 50)
        {
            foreach (var (pt, ph, _) in panels)
                g.DrawLine(gridPen, Px(x), pt, Px(x), pt + ph);
            TextRenderer.DrawText(g, x.ToString(), Font, new Point((int)Px(x) - Dp(12), last.Top + last.H + 2), Color.Black);
        }
        TextRenderer.DrawText(g, $"{_axis} 方向离轴线的距离 (mm)", Font, new Point(width - right - Dp(200), last.Top + last.H + fh + 2), Color.Black);
        int zstep = zmax - zmin > 600 ? 100 : 50;
        for (int z = (int)Math.Ceiling(zmin / zstep) * zstep; z <= zmax; z += zstep)
        {
            g.DrawLine(gridPen, left, Py(z), width - right, Py(z));
            TextRenderer.DrawText(g, z.ToString(), Font, new Point(Dp(4), (int)Py(z) - fh / 2), Color.Black);
        }

        using (var band = new SolidBrush(Color.FromArgb(215, 240, 215)))
            g.FillRectangle(band, left + 1, Py(_zRim - _tol), width - left - right - 1, Py(_zRim + _tol) - Py(_zRim - _tol));
        using (var wallPen = new Pen(Color.FromArgb(255, 140, 0), 2) { DashPattern = new[] { 3f, 2f } })
            foreach (var x in _walls)
                g.DrawLine(wallPen, Px(x), top1, Px(x), top1 + h1);

        var reportRect = new Rectangle(width - reportW, top1, reportW - Dp(6), ClientSize.Height - top1);
        TextRenderer.DrawText(g, string.Join("\n", _report), Font, reportRect, Color.Black,
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);

        using var link = new Pen(Color.FromArgb(240, 170, 170));
        for (int a = 0; a + 1 < _st.Length; a++)
        {
            int b = a + 1;
            if (_st[a].N > 0 && _st[b].N > 0 && Math.Abs(_st[a].Med - _st[b].Med) <= 70)
                g.DrawLine(link, Px(_lat[a]), Py(_st[a].Med), Px(_lat[b]), Py(_st[b].Med));
        }
        using var bar = new Pen(Color.FromArgb(40, 90, 220), 3);
        using var dot = new SolidBrush(Color.FromArgb(220, 30, 30));
        float rdot = Dp(3);
        for (int k = 0; k < _st.Length; k++)
        {
            var s = _st[k];
            if (s.N == 0)
            {
                TextRenderer.DrawText(g, "x", Font, new Point((int)Px(_lat[k]) - 4, top1 + h1 - fh - 2), Color.Black);
                continue;
            }
            g.DrawLine(bar, Px(_lat[k]), Py(s.Lo), Px(_lat[k]), Py(s.Hi));
            g.FillEllipse(dot, Px(_lat[k]) - rdot, Py(s.Med) - rdot, 2 * rdot, 2 * rdot);
        }

        g.SmoothingMode = SmoothingMode.None;
        const double stdMax = 30.0;
        float bw = Math.Max(2, Dp(4));
        using var grey = new SolidBrush(Color.FromArgb(120, 120, 120));
        foreach (int k in ks)
        {
            double v = Math.Min(_st[k].Std, stdMax);
            float y = (float)(top2 + h2 - v * h2 / stdMax);
            g.FillRectangle(_st[k].Std > BoxMath.FlickerMm ? dot : grey, Px(_lat[k]) - bw, y, 2 * bw, top2 + h2 - y);
        }
        float yf = (float)(top2 + h2 - BoxMath.FlickerMm * h2 / stdMax);
        using (var red = new Pen(Color.FromArgb(220, 30, 30)))
            g.DrawLine(red, left, yf, width - right, yf);

        if (_snr != null)
        {
            double topSnr = Math.Max(1, ks.Max(k => _snr[k]) * 1.1);
            using var green = new SolidBrush(Color.FromArgb(70, 150, 90));
            foreach (int k in ks)
            {
                float y = (float)(top3 + h2 - _snr[k] * h2 / topSnr);
                g.FillRectangle(green, Px(_lat[k]) - bw, y, 2 * bw, top3 + h2 - y);
            }
            TextRenderer.DrawText(g, $"满刻度 {topSnr:F0}", Font, new Point(left + 4, top3 + 2), Color.DimGray);
        }
    }

    private int Dp(int v) => v * DeviceDpi / 96;
}

/// <summary>键值对统计面板，内容不变就不重绘。</summary>
internal sealed class InfoView : Control
{
    private List<(string Key, string? Value)> _lines = new();
    private string _sig = "";

    public InfoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.Gainsboro;
        Font = new Font("Consolas", 10f);
    }

    /// <summary>Value 为 null 的行当作分组标题；Value 以 "!" 开头时标橙色。</summary>
    public void SetLines(List<(string Key, string? Value)> lines)
    {
        string sig = string.Join("\n", lines.Select(l => l.Key + "\t" + l.Value));
        if (sig == _sig) return;
        _sig = sig;
        _lines = lines;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var head = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        int x = Dp(4), keyW = Dp(118), lh = Font.Height + Dp(4), y = Dp(2);
        foreach (var (k, v) in _lines)
        {
            if (v == null)
            {
                y += Dp(6);
                TextRenderer.DrawText(g, k, head, new Point(x, y), Color.FromArgb(110, 170, 255));
                y += head.Height + Dp(2);
                continue;
            }
            bool warn = v.StartsWith('!');
            TextRenderer.DrawText(g, k, Font, new Point(x, y), Color.Gray);
            TextRenderer.DrawText(g, warn ? v[1..] : v, Font, new Point(x + keyW, y), warn ? Color.Orange : ForeColor);
            y += lh;
        }
    }

    private int Dp(int v) => v * DeviceDpi / 96;
}
