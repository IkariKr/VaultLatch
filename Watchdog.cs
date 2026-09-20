using System.Diagnostics;
using System.Text;

namespace VaultLatch;

internal static class Watchdog
{
    private const string WatchdogMutexName = @"Local\VaultLatch.Watchdog";
    private const string StopEventPrefix = @"Local\VaultLatch.Stop.";
    private const string WatchdogArgument = "--watchdog";
    private const string PrimaryArgument = "--primary";
    private const string StopEventArgument = "--stop-event";
    private const string SourceArgument = "--source";
    private const int InitialRetryDelayMilliseconds = 2_000;
    private const int MaximumRetryDelayMilliseconds = 30_000;

    public static string StableDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultLatch");

    public static string GetStableExecutablePath(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(StableDirectory, string.IsNullOrWhiteSpace(fileName) ? "VaultLatch.exe" : fileName);
    }

    public static bool IsWatchdogMode(string[] args, out string targetPath)
    {
        targetPath = string.Empty;
        if (args.Length < 2 || !string.Equals(args[0], WatchdogArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        targetPath = args[1];
        return !string.IsNullOrWhiteSpace(targetPath);
    }

    public static bool IsPrimaryMode(string[] args) =>
        args.Any(argument => string.Equals(argument, PrimaryArgument, StringComparison.OrdinalIgnoreCase));

    public static string? GetSourcePath(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], SourceArgument, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static string? GetStopEventName(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], StopEventArgument, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static string? EnsureStableCopy(string sourcePath, CrashReporterLogger logger)
    {
        string? stablePath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            stablePath = GetStableExecutablePath(sourcePath);
            if (!PathsEqual(sourcePath, stablePath) && !AreApplicationFilesCurrent(sourcePath, StableDirectory))
            {
                CopyApplicationFiles(sourcePath, StableDirectory);
            }

            return stablePath;
        }
        catch (Exception exception)
        {
            logger.Write("创建本地稳定副本失败", exception);
            return stablePath is not null && File.Exists(stablePath) ? stablePath : null;
        }
    }

    internal static bool AreApplicationFilesCurrent(string sourcePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || !Directory.Exists(destinationDirectory))
        {
            return false;
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return false;
        }

        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(sourceDirectory, path), StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(destinationDirectory, path), StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in sourceFiles.Keys)
        {
            if (!destinationFiles.TryGetValue(relativePath, out var destinationPath))
            {
                return false;
            }

            var sourceInfo = new FileInfo(sourceFiles[relativePath]);
            var destinationInfo = new FileInfo(destinationPath);
            if (sourceInfo.Length != destinationInfo.Length || sourceInfo.LastWriteTimeUtc != destinationInfo.LastWriteTimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    internal static void CopyApplicationFiles(string sourcePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到要复制的应用程序文件。", sourcePath);
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new InvalidOperationException("无法确定应用程序所在目录。");
        }

        if (PathsEqual(sourceDirectory, destinationDirectory))
        {
            return;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VaultLatch-copy-{Guid.NewGuid():N}");
        try
        {
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var temporaryFile = Path.Combine(temporaryDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(temporaryFile)!);
                File.Copy(sourceFile, temporaryFile, overwrite: true);
            }

            foreach (var temporaryFile in Directory.EnumerateFiles(temporaryDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(temporaryDirectory, temporaryFile);
                var destinationFile = Path.Combine(destinationDirectory, relativePath);
                var sourceFile = Path.Combine(sourceDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.Copy(temporaryFile, destinationFile, overwrite: true);
                File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public static int Run(string targetPath, CrashReporterLogger logger)
    {
        using var mutex = new Mutex(initiallyOwned: true, WatchdogMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        var stopEventName = StopEventPrefix + Guid.NewGuid().ToString("N");
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName);
        var delay = InitialRetryDelayMilliseconds;

        while (!stopEvent.WaitOne(0))
        {
            Process? process = null;
            try
            {
                if (!File.Exists(targetPath))
                {
                    logger.Write($"主程序暂不可用，等待移动盘恢复：{targetPath}");
                    stopEvent.WaitOne(delay);
                    delay = Math.Min(delay * 2, MaximumRetryDelayMilliseconds);
                    continue;
                }

                process = StartPrimary(targetPath, stopEventName);
                delay = InitialRetryDelayMilliseconds;
                process.WaitForExit();
                var exitCode = process.ExitCode;
                process.Dispose();
                process = null;

                if (stopEvent.WaitOne(0) || exitCode == 0)
                {
                    return 0;
                }

                logger.Write($"主程序异常退出，退出码={exitCode}；{delay}ms 后重试。" );
            }
            catch (Exception exception)
            {
                logger.Write("watchdog 启动或监控主程序失败", exception);
            }
            finally
            {
                process?.Dispose();
            }

            stopEvent.WaitOne(delay);
            delay = Math.Min(delay * 2, MaximumRetryDelayMilliseconds);
        }

        return 0;
    }

    public static void RequestStop(string? stopEventName)
    {
        if (string.IsNullOrWhiteSpace(stopEventName))
        {
            return;
        }

        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            stopEvent.Set();
        }
        catch
        {
        }
    }

    private static Process StartPrimary(string targetPath, string stopEventName)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            Arguments = BuildArguments(PrimaryArgument, StopEventArgument, stopEventName, SourceArgument, targetPath),
        }) ?? throw new InvalidOperationException("无法启动 VaultLatch 主程序。");
    }

    private static string BuildArguments(params string[] arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }

        return builder.ToString();
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    internal sealed class CrashReporterLogger
    {
        public void Write(string message, Exception? exception = null) => CrashReporter.Report(message, exception);
    }
}
