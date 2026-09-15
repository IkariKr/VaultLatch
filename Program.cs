using System.Diagnostics;
using System.Threading;

namespace VaultLatch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Watchdog.IsWatchdogMode(args, out var targetPath))
        {
            CrashReporter.Register(terminateOnUiException: false);
            var logger = new Watchdog.CrashReporterLogger();
            Environment.ExitCode = Watchdog.Run(targetPath, logger);
            return;
        }

        CrashReporter.Register(terminateOnUiException: Watchdog.IsPrimaryMode(args));

        var sourcePath = Watchdog.GetSourcePath(args)
            ?? Environment.ProcessPath
            ?? Application.ExecutablePath;
        if (!Watchdog.IsPrimaryMode(args))
        {
            var stablePath = Watchdog.EnsureStableCopy(sourcePath, new Watchdog.CrashReporterLogger());
            if (stablePath is not null && !PathsEqual(sourcePath, stablePath) && StartWatchdog(stablePath, sourcePath))
            {
                return;
            }
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\VaultLatch.Singleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new GuardApplicationContext(Watchdog.GetStopEventName(args)));
    }

    private static bool StartWatchdog(string stablePath, string targetPath)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = stablePath,
                WorkingDirectory = Path.GetDirectoryName(stablePath) ?? Watchdog.StableDirectory,
                UseShellExecute = false,
                Arguments = BuildArguments("--watchdog", targetPath),
            }) is not null;
        }
        catch (Exception exception)
        {
            CrashReporter.Report("启动 watchdog 失败，继续使用当前进程", exception);
            return false;
        }
    }

    private static string BuildArguments(params string[] arguments)
    {
        return string.Join(" ", arguments.Select(argument => $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""));
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
