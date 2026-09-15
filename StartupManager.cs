using Microsoft.Win32;

namespace VaultLatch;

internal sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VaultLatch";
    private const string LegacyValueName = "LifeOSGuard";

    private readonly string _sourcePath;

    public StartupManager(string sourcePath)
    {
        _sourcePath = sourcePath;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return (key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value))
                || (key?.GetValue(LegacyValueName) is string legacyValue && !string.IsNullOrWhiteSpace(legacyValue));
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 开机自启动注册表项。");
        if (enabled)
        {
            var stablePath = Watchdog.EnsureStableCopy(_sourcePath, new Watchdog.CrashReporterLogger());
            var executablePath = stablePath ?? _sourcePath;
            var command = stablePath is null
                ? $"\"{executablePath}\""
                : $"\"{executablePath}\" --watchdog \"{_sourcePath}\"";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }
}
