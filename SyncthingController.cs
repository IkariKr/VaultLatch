namespace VaultLatch;

internal sealed class SyncthingController
{
    private readonly FileLogger _logger;

    public SyncthingController(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> SetFolderPausedAsync(GuardSettings settings, bool paused)
    {
        var executable = ResolveExecutable(settings.SyncthingPath);
        if (executable is null)
        {
            _logger.Error("未找到 syncthing.exe，无法切换配置的 Syncthing Folder 状态。");
            return false;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                executable,
                new[] { "cli", "config", "folders", settings.SyncthingFolderId, "paused", "set", paused ? "true" : "false" },
                timeoutSeconds: 15);

            if (result.ExitCode != 0)
            {
                _logger.Error($"Syncthing 设置 paused={paused} 失败，退出码 {result.ExitCode}：{result.Error.Trim()}");
                return false;
            }

            var verified = await IsFolderPausedAsync(settings);
            if (verified != paused)
            {
                _logger.Error($"Syncthing paused 状态校验失败；期望 {paused}，实际 {verified}。");
                return false;
            }

            _logger.Info($"Syncthing 文件夹 {settings.SyncthingFolderId} 已{(paused ? "暂停" : "恢复")}。");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error($"切换 Syncthing paused={paused} 失败", exception);
            return false;
        }
    }

    public async Task<bool?> IsFolderPausedAsync(GuardSettings settings)
    {
        var executable = ResolveExecutable(settings.SyncthingPath);
        if (executable is null)
        {
            return null;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                executable,
                new[] { "cli", "config", "folders", settings.SyncthingFolderId, "paused", "get" },
                timeoutSeconds: 10);
            if (result.ExitCode != 0)
            {
                return null;
            }

            if (bool.TryParse(result.Output.Trim(), out var paused))
            {
                return paused;
            }
        }
        catch (Exception exception)
        {
            _logger.Error("读取 Syncthing paused 状态失败", exception);
        }

        return null;
    }

    public bool MarkerExists(GuardSettings settings)
    {
        try
        {
            var markerPath = Path.Combine(settings.VaultPath, ".stfolder");
            return File.Exists(markerPath) || Directory.Exists(markerPath);
        }
        catch (Exception exception)
        {
            _logger.Error("检查 Syncthing marker 失败", exception);
            return false;
        }
    }

    private static string? ResolveExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetAlias = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "syncthing.exe");
        if (File.Exists(wingetAlias))
        {
            return wingetAlias;
        }

        const string installRoot = @"D:\Program Files\Syncthing";
        try
        {
            if (Directory.Exists(installRoot))
            {
                var candidate = Directory.EnumerateFiles(installRoot, "syncthing.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Removable or partially accessible install directories are treated as unavailable.
        }

        return null;
    }
}
