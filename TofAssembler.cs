namespace TofCanViewer;

/// <summary>一幅完整深度图。Z/Snr 按 zone = row * Cols + col 排列，Z=0 表示无效。</summary>
internal sealed class TofImage
{
    public int Seq;
    public int Rows;
    public int Cols;
    public bool Alarm;
    public int OverZones;
    public int ZMinMm;
    public int ValidZones;
    public int OverMinZMm;
    public int OverRow;
    public int OverCol;
    public ushort[] Z = Array.Empty<ushort>();
    public byte[] Snr = Array.Empty<byte>();
    public DateTime Time;
}

internal sealed class TofStatus
{
    public int Stage;
    public int FailStage;
    public int FailRc;
    public int Attempts;
    public int FramesOk;
    public int FramesBad;
    public DateTime Time;

    public static readonly string[] StageNames =
    {
        "IDLE", "DISABLE", "ENABLE", "BL_WAIT", "I2C_OFF", "POWERUP", "CPU_READY", "CHIP_ID", "DOWNLOAD",
        "APP_ID", "GET_CFG", "DEV_INFO", "PRECONFIG", "READ_CFG", "SET_CFG", "INT_ENABLE", "START", "MEASURING",
    };

    public static string Name(int stage) => stage >= 0 && stage < StageNames.Length ? StageNames[stage] : stage.ToString();
}

/// <summary>0x084 + 0x085 诊断，对应 tools/tof_diag.py --brief 里 CAN 状态帧没有的字段。</summary>
internal sealed class TofDiag
{
    public int SpiErrors;
    public int Temp;
    public int LastStatus;
    public int GeometryBad;
    public int AlarmEvents;
    public uint UptimeMs;
    public int OverStreak;
    public int ClearStreak;
    public int FbValidZones;
    public DateTime Time;
}

/// <summary>板子的 g_fb_cfg 阈值，顺序与 fullbox_cfg_t 成员一致。</summary>
internal static class FbCfg
{
    public static readonly string[] Keys = { "rim_z_mm", "margin_mm", "min_zones", "on_frames", "off_frames", "min_valid_mm" };
    public static readonly string[] Labels = { "箱沿 z (mm)", "超高余量 (mm)", "超高 zone 数", "报警连续帧", "解除连续帧", "最小有效 (mm)" };
    public static readonly string[] Help =
    {
        "传感器到箱沿平面的垂直距离",
        "物品高出箱沿这么多才算超高",
        "一帧里至少这么多个 zone 超高",
        "连续这么多帧超高才报警",
        "连续这么多帧正常才解除报警",
        "比这个还近的读数当串扰丢掉",
    };
}

/// <summary>按固件 src/can_app.h 的协议把 CAN 帧拼回整幅图，只在接收线程上调用。</summary>
internal sealed class TofAssembler
{
    public const uint IdHead = 0x080;
    public const uint IdTail = 0x081;
    public const uint IdStatus = 0x082;
    public const uint IdCfg = 0x083;
    public const uint IdDiag1 = 0x084;
    public const uint IdDiag2 = 0x085;
    public const uint IdCmd = 0x0C0;
    public const uint IdZ = 0x100;
    public const uint IdSnr = 0x300;
    public const int MaxZones = 1536;
    /// <summary>一幅图允许丢失的块数，丢块处沿用上一幅的值，超过则整幅丢弃。</summary>
    public const int MaxMissingChunks = 32;

    public event Action<TofImage>? ImageReady;
    public event Action<TofStatus>? StatusReady;
    /// <summary>index, value, 写入被拒</summary>
    public event Action<int, uint, bool>? CfgReady;
    public event Action<TofDiag>? DiagReady;

    private TofDiag _diag = new();

    public long Images;
    public long Incomplete;
    public long SeqGaps;
    public long Patched;
    public long LostChunks;

    private readonly ushort[] _z = new ushort[MaxZones];
    private readonly byte[] _snr = new byte[MaxZones];
    private readonly bool[] _zGot = new bool[MaxZones / 4];
    private readonly bool[] _sGot = new bool[MaxZones / 8];
    private TofImage? _cur;
    private int _zones, _zb, _sb, _zCount, _sCount;
    private int _lastSeq = -1;

    public void Feed(uint id, byte[] d, int len)
    {
        if (len < 8)
            return;

        if (id == IdHead)
        {
            if (_cur != null)
                Interlocked.Increment(ref Incomplete);
            StartImage(d);
        }
        else if (id >= IdZ && id < IdZ + _zGot.Length)
        {
            int k = (int)(id - IdZ);
            if (_cur == null || k >= _zb)
                return;
            for (int j = 0; j < 4; j++)
            {
                int n = k * 4 + j;
                if (n < _zones)
                    _z[n] = U16(d, 2 * j);
            }
            if (!_zGot[k]) { _zGot[k] = true; _zCount++; }
        }
        else if (id >= IdSnr && id < IdSnr + _sGot.Length)
        {
            int k = (int)(id - IdSnr);
            if (_cur == null || k >= _sb)
                return;
            int count = Math.Min(8, _zones - k * 8);
            Array.Copy(d, 0, _snr, k * 8, count);
            if (!_sGot[k]) { _sGot[k] = true; _sCount++; }
        }
        else if (id == IdTail)
        {
            FinishImage(d);
        }
        else if (id == IdStatus)
        {
            StatusReady?.Invoke(new TofStatus
            {
                Stage = d[0],
                FailStage = d[1],
                FailRc = (sbyte)d[2],
                Attempts = d[3],
                FramesOk = U16(d, 4),
                FramesBad = U16(d, 6),
                Time = DateTime.Now,
            });
        }
        else if (id == IdCfg)
        {
            CfgReady?.Invoke(d[0], (uint)(d[2] | d[3] << 8 | d[4] << 16 | d[5] << 24), d[6] != 0);
        }
        else if (id == IdDiag1)
        {
            _diag.SpiErrors = U16(d, 0);
            _diag.Temp = (sbyte)d[2];
            _diag.LastStatus = d[3];
            _diag.GeometryBad = U16(d, 4);
            _diag.AlarmEvents = U16(d, 6);
        }
        else if (id == IdDiag2)
        {
            _diag.UptimeMs = (uint)(d[0] | d[1] << 8 | d[2] << 16 | d[3] << 24);
            _diag.OverStreak = d[4];
            _diag.ClearStreak = d[5];
            _diag.FbValidZones = U16(d, 6);
            _diag.Time = DateTime.Now;
            DiagReady?.Invoke(_diag);
            _diag = new TofDiag
            {
                SpiErrors = _diag.SpiErrors, Temp = _diag.Temp, LastStatus = _diag.LastStatus,
                GeometryBad = _diag.GeometryBad, AlarmEvents = _diag.AlarmEvents,
            };
        }
    }

    private void StartImage(byte[] d)
    {
        int rows = d[2], cols = d[3];
        _cur = null;
        _zones = rows * cols;
        if (_zones == 0 || _zones > MaxZones)
            return;

        _zb = (_zones + 3) / 4;
        _sb = (_zones + 7) / 8;
        _zCount = _sCount = 0;
        Array.Clear(_zGot);
        Array.Clear(_sGot);
        _cur = new TofImage
        {
            Seq = U16(d, 0),
            Rows = rows,
            Cols = cols,
            Alarm = (d[4] & 1) != 0,
            OverZones = d[5],
            ZMinMm = U16(d, 6),
        };
    }

    private void FinishImage(byte[] d)
    {
        var img = _cur;
        _cur = null;
        if (img == null)
            return;
        if (U16(d, 0) != img.Seq)
        {
            Interlocked.Increment(ref Incomplete);
            return;
        }
        int missing = (_zb - _zCount) + (_sb - _sCount);
        if (missing > 0)
        {
            Interlocked.Add(ref LostChunks, missing);
            if (missing > MaxMissingChunks)
            {
                Interlocked.Increment(ref Incomplete);
                return;
            }
            Interlocked.Increment(ref Patched);
        }

        if (_lastSeq >= 0)
        {
            int gap = (img.Seq - _lastSeq - 1) & 0xFFFF;
            if (gap < 0x8000)
                Interlocked.Add(ref SeqGaps, gap);
        }
        _lastSeq = img.Seq;

        img.ValidZones = U16(d, 2);
        img.OverMinZMm = U16(d, 4);
        img.OverRow = d[6];
        img.OverCol = d[7];
        img.Z = _z.AsSpan(0, _zones).ToArray();
        img.Snr = _snr.AsSpan(0, _zones).ToArray();
        img.Time = DateTime.Now;
        Interlocked.Increment(ref Images);
        ImageReady?.Invoke(img);
    }

    private static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
}
