namespace TofCanViewer;

/// <summary>改板子上的 g_fb_cfg 阈值，等同 tools/tof_diag.py --set NAME=VALUE，只改 RAM，重新上电恢复默认。</summary>
internal sealed class ThresholdForm : Form
{
    private readonly NumericUpDown[] _num = new NumericUpDown[FbCfg.Keys.Length];
    private readonly uint?[] _board;

    /// <param name="board">板子当前值，null 表示还没收到</param>
    /// <param name="rimZ">上位机测得的箱沿 z，可一键填入 rim_z_mm</param>
    public ThresholdForm(uint?[] board, double? rimZ)
    {
        _board = board;
        Text = "板子报警阈值（写入 RAM，重新上电恢复默认）";
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Dp(10));

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill };
        grid.Controls.Add(Head("参数"), 0, 0);
        grid.Controls.Add(Head("板子当前"), 1, 0);
        grid.Controls.Add(Head("新值"), 2, 0);
        grid.Controls.Add(Head("说明"), 3, 0);
        for (int i = 0; i < _num.Length; i++)
        {
            grid.Controls.Add(Cell($"{FbCfg.Labels[i]}  {FbCfg.Keys[i]}"), 0, i + 1);
            grid.Controls.Add(Cell(board[i]?.ToString() ?? "未收到"), 1, i + 1);
            _num[i] = new NumericUpDown { Minimum = 0, Maximum = 65535, Width = Dp(80), Value = board[i] ?? 0, Anchor = AnchorStyles.Left, Enabled = board[i] != null };
            grid.Controls.Add(_num[i], 2, i + 1);
            grid.Controls.Add(Cell(FbCfg.Help[i], Color.DimGray), 3, i + 1);
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Dp(8), 0, 0) };
        var ok = new Button { Text = "写入板子", AutoSize = true, DialogResult = DialogResult.OK, Enabled = board.All(v => v != null) };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var useRim = new Button { Text = rimZ is double z ? $"箱沿 z 用当前测得的 {z:F0} mm" : "箱沿 z：上位机未测到", AutoSize = true, Enabled = rimZ != null && board[0] != null };
        useRim.Click += (_, _) => _num[0].Value = (decimal)Math.Round(rimZ!.Value);
        buttons.Controls.AddRange(new Control[] { ok, cancel, useRim });
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        if (!ok.Enabled)
            Controls.Add(new Label { Text = "还没收到板子的阈值（0x083），先确认 CAN 已打开且板子是新固件。", AutoSize = true, ForeColor = Color.Firebrick, Dock = DockStyle.Top });
    }

    /// <summary>与板子当前值不同的字段：(index, 新值)。</summary>
    public List<(int Index, uint Value)> Changes()
    {
        var o = new List<(int, uint)>();
        for (int i = 0; i < _num.Length; i++)
            if (_board[i] is uint cur && (uint)_num[i].Value != cur)
                o.Add((i, (uint)_num[i].Value));
        return o;
    }

    private int Dp(int v) => v * DeviceDpi / 96;

    private static Label Head(string t) => new() { Text = t, AutoSize = true, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), Margin = new Padding(3, 3, 12, 6) };

    private static Label Cell(string t, Color? c = null) =>
        new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = c ?? SystemColors.ControlText, Margin = new Padding(3, 6, 12, 3) };
}
