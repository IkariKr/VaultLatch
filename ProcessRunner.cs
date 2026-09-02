using System.Diagnostics;

namespace VaultLatch;

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        int timeoutSeconds = 30)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{executable}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            throw new TimeoutException($"进程执行超时：{executable}");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
