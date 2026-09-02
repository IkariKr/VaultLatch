using Microsoft.Win32;

namespace VaultLatch;

internal sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VaultLatch";
    private const string LegacyValueName = "LifeOSGuard";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return (key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value))
            || (key?.GetValue(LegacyValueName) is string legacyValue && !string.IsNullOrWhiteSpace(legacyValue));
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }
}
