using System.Diagnostics;

namespace VaultLatch;

internal sealed class ObsidianController
{
    private readonly FileLogger _logger;

    public ObsidianController(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<int> CloseGracefullyAsync(int waitSeconds)
    {
        var processes = Process.GetProcessesByName("Obsidian");
        if (processes.Length == 0)
        {
            return 0;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                }
            }
            catch (Exception exception)
            {
                _logger.Error($"请求关闭 Obsidian 失败（PID {process.Id}）", exception);
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (processes.All(IsExited))
            {
                break;
            }

            await Task.Delay(250);
        }

        var remaining = processes.Count(process => !IsExited(process));
        foreach (var process in processes)
        {
            process.Dispose();
        }

        _logger.Info($"Obsidian 优雅退出完成；仍运行进程数：{remaining}。");
        return remaining;
    }

    public void OpenVault(GuardSettings settings)
    {
        if (!Directory.Exists(settings.VaultPath))
        {
            return;
        }

        var uri = $"obsidian://open?path={Uri.EscapeDataString(settings.VaultPath)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        _logger.Info($"已请求打开 Obsidian Vault：{settings.VaultPath}");
    }

    private static bool IsExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }
}
