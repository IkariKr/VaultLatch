namespace VaultLatch;

internal static class CrashReporter
{
    private static readonly object Gate = new();
    private static int _registered;
    private static string? _reportPath;
    private static bool _terminateOnUiException;

    public static void Register(bool terminateOnUiException)
    {
        _terminateOnUiException = terminateOnUiException;
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
            {
                Report("未处理的 UI 线程异常", eventArgs.Exception);
                if (_terminateOnUiException)
                {
                    Environment.Exit(1);
                }
            };
        }
        catch
        {
        }

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                Report("未处理的应用程序异常", eventArgs.ExceptionObject as Exception);
        }
        catch
        {
        }

        try
        {
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                Report("未观察的后台任务异常", eventArgs.Exception);
                eventArgs.SetObserved();
            };
        }
        catch
        {
        }
    }

    public static void Report(string message, Exception? exception = null)
    {
        try
        {
            var path = GetReportPath();
            if (path is null)
            {
                return;
            }

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}"
                + (exception is null ? string.Empty : $": {exception}")
                + Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Crash reporting must never become another source of process failure.
        }
    }

    private static string? GetReportPath()
    {
        if (_reportPath is not null)
        {
            return _reportPath;
        }

        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            var directory = Path.Combine(root, "VaultLatch");
            Directory.CreateDirectory(directory);
            _reportPath = Path.Combine(directory, "crash.log");
            return _reportPath;
        }
        catch
        {
            return null;
        }
    }
}
