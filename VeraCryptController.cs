using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace VaultLatch;

internal sealed class VeraCryptController
{
    private readonly FileLogger _logger;
    private readonly VeraCryptDriver _driver;

    public VeraCryptController(FileLogger logger)
    {
        _logger = logger;
        _driver = new VeraCryptDriver(logger);
    }

    public bool IsMounted(GuardSettings settings)
    {
        return Directory.Exists(GetDriveRoot(settings));
    }

    public bool CanAccessDriver() => _driver.CanOpen();

    public bool IsGuiProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("VeraCrypt").Any();
        }
        catch
        {
            return false;
        }
    }

    public void EnsureVaultLatchOwnsBackgroundBehavior()
    {
        var configPath = GetVeraCryptConfigPath();
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["StartOnLogon"] = "0",
                ["MountDevicesOnLogon"] = "0",
                ["MountFavoritesOnLogon"] = "0",
                ["EnableBackgroundTask"] = "0",
                ["CloseBackgroundTaskOnNoVolumes"] = "1",
                ["DismountOnSessionLocked"] = "0",
                ["DismountOnPowerSaving"] = "0",
                ["DismountOnScreenSaver"] = "0",
                ["ForceAutoDismount"] = "0",
                ["MaxVolumeIdleTime"] = "0",
            };

            var changed = false;
            foreach (var element in document.Descendants("config"))
            {
                var key = element.Attribute("key")?.Value;
                if (key is null || !desired.TryGetValue(key, out var value) || element.Value == value)
                {
                    continue;
                }

                element.Value = value;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var backupPath = configPath + ".lifeosguard.bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(configPath, backupPath, overwrite: false);
            }

            document.Save(configPath, SaveOptions.DisableFormatting);
            _logger.Info("已将 VeraCrypt 后台任务及锁屏/省电/屏保/空闲自动卸载关闭；日常保护由 VaultLatch 接管。");
        }
        catch (Exception exception)
        {
            _logger.Error("VaultLatch 接管 VeraCrypt 后台行为设置失败", exception);
        }
    }

    public async Task<bool> MountAsync(GuardSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateForDirectDriver(settings);
        if (IsMounted(settings))
        {
            return true;
        }

        if (!_driver.CanOpen())
        {
            throw new InvalidOperationException("VaultLatch 无法访问 VeraCrypt 驱动，无法执行直连挂载。");
        }

        using var prompt = new MountPasswordForm(settings.VolumePath, settings.DriveLetter);
        if (prompt.ShowDialog() != DialogResult.OK)
        {
            _logger.Info("VaultLatch 驱动直连挂载已由用户取消。");
            return false;
        }

        var passwordUtf8 = prompt.TakePasswordUtf8();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.Info($"开始通过 VeraCrypt 驱动直连挂载到 {settings.DriveLetter}:；未启动 VeraCrypt.exe。");
            var result = await _driver.MountAsync(settings.VolumePath, settings.DriveLetter, passwordUtf8, prompt.Pim);
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                var reason = DescribeMountFailure(result);
                _logger.Error($"VeraCrypt 驱动直连挂载失败：{reason}");
                throw new InvalidOperationException(reason);
            }

            for (var i = 0; i < 40; i++)
            {
                if (IsMounted(settings))
                {
                    _logger.Info($"VeraCrypt 驱动直连挂载成功：{settings.DriveLetter}:。");
                    if (result.FilesystemDirty)
                    {
                        _logger.Error("VeraCrypt 报告挂载后的文件系统 dirty，请安排文件系统检查。");
                    }
                    if (result.VolumeMasterKeyVulnerable)
                    {
                        _logger.Error("VeraCrypt 报告卷主密钥存在已知脆弱状态，请使用 VeraCrypt 官方工具检查卷头。");
                    }
                    return true;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new InvalidOperationException($"VeraCrypt 驱动返回挂载成功，但 {settings.DriveLetter}: 未在预期时间内出现。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordUtf8);
        }
    }

    public async Task<bool> UnmountAsync(GuardSettings settings, bool force)
    {
        if (!IsMounted(settings))
        {
            return true;
        }

        try
        {
            var returnCode = await _driver.UnmountAsync(settings.DriveLetter, ignoreOpenFiles: force);
            if (returnCode != 0)
            {
                _logger.Info($"VeraCrypt 驱动{(force ? "强制" : "正常")}卸载未成功，返回码：{returnCode}。");
                return false;
            }
        }
        catch (Exception exception)
        {
            _logger.Error($"VeraCrypt 驱动{(force ? "强制" : "正常")}卸载失败", exception);
            return false;
        }

        for (var i = 0; i < 20; i++)
        {
            if (!IsMounted(settings))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return !IsMounted(settings);
    }

    public IReadOnlyList<string> GetAutoDismountConflicts()
    {
        var configPath = GetVeraCryptConfigPath();

        if (!File.Exists(configPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var document = XDocument.Load(configPath);
            var values = document
                .Descendants("config")
                .Where(element => element.Attribute("key") is not null)
                .ToDictionary(
                    element => element.Attribute("key")!.Value,
                    element => element.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);

            var conflicts = new List<string>();
            AddEnabledConflict(values, conflicts, "DismountOnSessionLocked", "锁屏自动卸载");
            AddEnabledConflict(values, conflicts, "DismountOnPowerSaving", "省电/睡眠自动卸载");
            AddEnabledConflict(values, conflicts, "DismountOnScreenSaver", "屏保自动卸载");

            if (values.TryGetValue("MaxVolumeIdleTime", out var idleValue)
                && int.TryParse(idleValue, out var idleMinutes)
                && idleMinutes > 0)
            {
                conflicts.Add($"VeraCrypt 空闲自动卸载={idleMinutes} 分钟");
            }

            return conflicts;
        }
        catch (Exception exception)
        {
            _logger.Error("读取 VeraCrypt 自动卸载设置失败", exception);
            return Array.Empty<string>();
        }
    }

    public async Task WipeCacheAsync(GuardSettings settings)
    {
        try
        {
            await _driver.WipePasswordCacheAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("通过 VeraCrypt 驱动擦除密码缓存失败", exception);
        }
    }

    private static string GetVeraCryptConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VeraCrypt",
        "Configuration.xml");

    private static void AddEnabledConflict(
        IReadOnlyDictionary<string, string> values,
        ICollection<string> conflicts,
        string key,
        string label)
    {
        if (values.TryGetValue(key, out var value) && value == "1")
        {
            conflicts.Add(label);
        }
    }

    private static string GetDriveRoot(GuardSettings settings) => $"{settings.DriveLetter}:\\";

    private static string DescribeMountFailure(VeraCryptDriver.MountResult result)
    {
        if (!result.IoctlSucceeded)
        {
            return $"驱动挂载 IOCTL 失败，Win32 错误码 {result.Win32Error}。";
        }

        return result.ReturnCode switch
        {
            3 => "密码、PIM 或密钥文件参数不正确。当前直连挂载支持密码 + 可选 PIM，不支持 Keyfile。",
            5 => "目标盘符不可用。",
            33 => "挂载操作已取消。",
            _ => $"VeraCrypt 驱动返回错误码 {result.ReturnCode}。",
        };
    }

    private static void ValidateForDirectDriver(GuardSettings settings)
    {
        if (!File.Exists(settings.VolumePath))
        {
            throw new FileNotFoundException("未找到 VeraCrypt 容器文件。", settings.VolumePath);
        }
    }
}
