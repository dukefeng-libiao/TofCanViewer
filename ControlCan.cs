using System.Runtime.InteropServices;

namespace TofCanViewer;

/// <summary>简易文件日志，写到 exe 同目录的 TofCanViewer.log，用于排查“点了没反应”。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "TofCanViewer.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}

/// <summary>ControlCAN.dll（VCI 接口）的最小封装，只用到打开、初始化、接收。</summary>
internal static class ControlCan
{
    public const uint DevUsbCan1 = 3;
    public const uint DevUsbCan2 = 4;

    /// <summary>VCI_CAN_OBJ 的字节布局：ID(4) TimeStamp(4) TimeFlag SendType RemoteFlag ExternFlag DataLen Data[8] Reserved[3]。</summary>
    public const int ObjSize = 24;
    public const int OffId = 0;
    public const int OffRemote = 10;
    public const int OffExtern = 11;
    public const int OffLen = 12;
    public const int OffData = 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct InitConfig
    {
        public uint AccCode;
        public uint AccMask;
        public uint Reserved;
        public byte Filter;
        public byte Timing0;
        public byte Timing1;
        public byte Mode;
    }

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_OpenDevice(uint devType, uint devIndex, uint reserved);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_CloseDevice(uint devType, uint devIndex);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_InitCAN(uint devType, uint devIndex, uint canIndex, ref InitConfig config);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_StartCAN(uint devType, uint devIndex, uint canIndex);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_ClearBuffer(uint devType, uint devIndex, uint canIndex);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_Receive(uint devType, uint devIndex, uint canIndex, IntPtr buffer, uint length, int waitMs);

    [DllImport("ControlCAN.dll")]
    public static extern uint VCI_Transmit(uint devType, uint devIndex, uint canIndex, IntPtr frames, uint length);
}

/// <summary>打开一路 CAN 并在后台线程收帧，回调在接收线程上执行。</summary>
internal sealed class CanReceiver : IDisposable
{
    private const int BatchSize = 2500;

    private readonly uint _devType;
    private readonly uint _devIndex;
    private readonly uint _canIndex;
    private readonly Action<uint, byte[], int> _onFrame;
    private Thread? _thread;
    private volatile bool _run;
    private readonly int _bitrate;

    public long FramesReceived;
    public long RxErrors;
    public long Reconnects;

    /// <summary>
    /// ZLG USBCAN（SJA1000）常用波特率 → 定时寄存器，源自官方 SDK 手册的 8MHz CAN 时钟表。
    /// BTR0 = SJW|BRP，BTR1 = SAM|TSEG2|TSEG1。
    /// </summary>
    public static readonly (int Bitrate, byte T0, byte T1, string Text)[] BitrateTable =
    {
        (1000000, 0x00, 0x14, "1 Mbit/s"),
        (500000, 0x00, 0x1C, "500 kbit/s"),
        (250000, 0x01, 0x1C, "250 kbit/s"),
        (125000, 0x03, 0x1C, "125 kbit/s"),
        (100000, 0x04, 0x1C, "100 kbit/s"),
        (50000, 0x09, 0x1C, "50 kbit/s"),
    };

    public CanReceiver(uint devType, uint devIndex, uint canIndex, Action<uint, byte[], int> onFrame, int bitrate = 1000000)
    {
        _devType = devType;
        _devIndex = devIndex;
        _canIndex = canIndex;
        _onFrame = onFrame;
        _bitrate = bitrate;
    }

    /// <summary>按 <see cref="_bitrate"/> 查表取 SJA1000 定时寄存器；未命中时退回 500 kbit/s。</summary>
    public void Open()
    {
        Log.Write($"程序目录 {AppContext.BaseDirectory}");
        OpenDevice();
        _run = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "CAN RX" };
        _thread.Start();
    }

    private void OpenDevice()
    {
        Log.Write($"VCI_OpenDevice(devType={_devType}, devIndex={_devIndex}) ...");
        uint rc = ControlCan.VCI_OpenDevice(_devType, _devIndex, 0);
        Log.Write($"VCI_OpenDevice rc={rc}");
        if (rc != 1)
            throw new InvalidOperationException($"VCI_OpenDevice 失败 (rc={rc})：检查设备类型/索引、驱动和是否被其他程序占用");

        var timing = Array.Find(BitrateTable, b => b.Bitrate == _bitrate);
        if (timing.Bitrate == 0) timing = Array.Find(BitrateTable, b => b.Bitrate == 500000);
        Log.Write($"位定时 {_bitrate} bit/s -> Timing0=0x{timing.T0:X2} Timing1=0x{timing.T1:X2}");

        var cfg = new ControlCan.InitConfig
        {
            AccCode = 0,
            AccMask = 0xFFFFFFFF,
            Filter = 1,
            Timing0 = timing.T0,
            Timing1 = timing.T1,
            Mode = 0,
        };
        if (ControlCan.VCI_InitCAN(_devType, _devIndex, _canIndex, ref cfg) != 1)
        {
            ControlCan.VCI_CloseDevice(_devType, _devIndex);
            throw new InvalidOperationException("VCI_InitCAN 失败");
        }
        Log.Write("VCI_InitCAN ok");
        ControlCan.VCI_ClearBuffer(_devType, _devIndex, _canIndex);
        if (ControlCan.VCI_StartCAN(_devType, _devIndex, _canIndex) != 1)
        {
            ControlCan.VCI_CloseDevice(_devType, _devIndex);
            throw new InvalidOperationException("VCI_StartCAN 失败");
        }
        Log.Write("VCI_StartCAN ok");
    }

    /// <summary>适配器偶发卡死时 VCI_Receive 一直返回 -1，只有关掉重开设备才能恢复。</summary>
    private void Reconnect()
    {
        Log.Write($"VCI_Receive 连续失败超过 1s，重开设备（累计读错误 {Interlocked.Read(ref RxErrors)}）");
        ControlCan.VCI_CloseDevice(_devType, _devIndex);
        Thread.Sleep(300);
        try
        {
            OpenDevice();
            Interlocked.Increment(ref Reconnects);
            Log.Write("重连成功");
        }
        catch (Exception ex)
        {
            Log.Write("重连失败: " + ex.Message);
            Thread.Sleep(700);
        }
    }

    private void ReceiveLoop()
    {
        IntPtr buf = Marshal.AllocHGlobal(ControlCan.ObjSize * BatchSize);
        var data = new byte[8];
        long errSince = 0;
        try
        {
            while (_run)
            {
                uint n = ControlCan.VCI_Receive(_devType, _devIndex, _canIndex, buf, BatchSize, 50);
                if (n > BatchSize)
                {
                    Interlocked.Increment(ref RxErrors);
                    long now = Environment.TickCount64;
                    if (errSince == 0)
                        errSince = now;
                    else if (now - errSince > 1000 && _run)
                    {
                        Reconnect();
                        errSince = 0;
                    }
                    Thread.Sleep(20);
                    continue;
                }
                errSince = 0;
                if (n == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = buf + i * ControlCan.ObjSize;
                    if (Marshal.ReadByte(p, ControlCan.OffExtern) != 0 || Marshal.ReadByte(p, ControlCan.OffRemote) != 0)
                        continue;
                    uint id = (uint)Marshal.ReadInt32(p, ControlCan.OffId);
                    int len = Math.Min((int)Marshal.ReadByte(p, ControlCan.OffLen), 8);
                    Marshal.Copy(p + ControlCan.OffData, data, 0, len);
                    _onFrame(id, data, len);
                }
                Interlocked.Add(ref FramesReceived, n);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>发一帧标准数据帧，成功返回 true。</summary>
    public bool Send(uint id, byte[] data)
    {
        IntPtr p = Marshal.AllocHGlobal(ControlCan.ObjSize);
        try
        {
            for (int i = 0; i < ControlCan.ObjSize; i++)
                Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, ControlCan.OffId, (int)id);
            Marshal.WriteByte(p, ControlCan.OffLen, (byte)Math.Min(data.Length, 8));
            Marshal.Copy(data, 0, p + ControlCan.OffData, Math.Min(data.Length, 8));
            uint rc = ControlCan.VCI_Transmit(_devType, _devIndex, _canIndex, p, 1);
            Log.Write($"发送 0x{id:X3} [{string.Join(' ', data.Select(b => b.ToString("X2")))}] rc={rc}");
            return rc == 1;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(2000);
        ControlCan.VCI_CloseDevice(_devType, _devIndex);
    }
}
