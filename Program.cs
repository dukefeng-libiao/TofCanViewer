namespace TofCanViewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 任何未处理异常都写入日志并弹窗，杜绝“点了没反应”式的静默失败
        Application.ThreadException += (_, e) =>
        {
            Log.Write("UI 线程未处理异常: " + e.Exception);
            MessageBox.Show(e.Exception.ToString(), "程序异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("后台线程未处理异常: " + e.ExceptionObject);

        Log.Write($"=== 进程启动 args=[{string.Join(' ', args)}]");
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        // --auto[=秒]：启动后自动打开 CAN，运行指定秒数（默认 5）后把统计写日志并退出
        var auto = args.FirstOrDefault(a => a.StartsWith("--auto", StringComparison.OrdinalIgnoreCase));
        if (auto != null)
        {
            int sec = 5;
            var parts = auto.Split('=');
            if (parts.Length == 2 && int.TryParse(parts[1], out int v) && v > 0)
                sec = v;
            form.Shown += (_, _) => form.AutoOpen(sec * 1000);
        }
        Application.Run(form);
    }
}
