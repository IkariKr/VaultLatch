using System.Text.Json;

namespace VaultLatch;

internal sealed class GuardSettings
{
    public const int CurrentSettingsVersion = 3;

    public int SettingsVersion { get; set; }
    public string VeraCryptPath { get; set; } = @"D:\Program Files\VeraCrypt\VeraCrypt.exe";
    public string VolumePath { get; set; } = string.Empty;
    public string DriveLetter { get; set; } = "V";
    public string VaultPath { get; set; } = @"V:\Vault";
    public string SyncthingPath { get; set; } = "syncthing.exe";
    public string SyncthingFolderId { get; set; } = string.Empty;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public int GraceSeconds { get; set; } = 3;
    public bool StartWithWindows { get; set; } = true;
    public bool LockOnSessionLock { get; set; } = true;
    public bool LockOnDisplayOff { get; set; } = false;
    public bool LockOnSuspend { get; set; } = true;
    public bool LockOnIdle { get; set; } = false;
    public bool CloseObsidianBeforeLock { get; set; } = true;
    public bool ForceUnmountFallback { get; set; } = true;
    public bool ResumeSyncthingAfterMount { get; set; } = true;
    public bool OpenObsidianAfterMount { get; set; } = false;

    public GuardSettings Clone() => (GuardSettings)MemberwiseClone();

    public void Normalize()
    {
        DriveLetter = string.IsNullOrWhiteSpace(DriveLetter) ? "V" : DriveLetter.Trim().TrimEnd(':').Substring(0, 1).ToUpperInvariant();
        IdleTimeoutMinutes = Math.Clamp(IdleTimeoutMinutes, 0, 1_440);
        GraceSeconds = Math.Clamp(GraceSeconds, 0, 30);
        VeraCryptPath = VeraCryptPath.Trim();
        VolumePath = VolumePath.Trim();
        VaultPath = VaultPath.Trim();
        SyncthingPath = SyncthingPath.Trim();
        SyncthingFolderId = SyncthingFolderId.Trim();
    }
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public SettingsStore()
    {
        // Keep the legacy settings location so existing installations retain their configuration.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LifeOSGuard");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public GuardSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = new GuardSettings { SettingsVersion = GuardSettings.CurrentSettingsVersion };
                return defaults;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<GuardSettings>(json, JsonOptions) ?? new GuardSettings();
            var storedVersion = GetStoredVersion(json);
            if (storedVersion < GuardSettings.CurrentSettingsVersion)
            {
                // v2/v3: Windows SessionLock is the primary lock trigger. Display-off and
                // VaultLatch's own idle timer remains an opt-in extra-protection mechanism.
                settings.LockOnDisplayOff = false;
                settings.LockOnIdle = false;
                settings.SettingsVersion = GuardSettings.CurrentSettingsVersion;
                settings.Normalize();
                Save(settings);
                return settings;
            }

            settings.Normalize();
            return settings;
        }
        catch
        {
            return new GuardSettings();
        }
    }

    public bool TrySave(GuardSettings settings)
    {
        try
        {
            settings.SettingsVersion = GuardSettings.CurrentSettingsVersion;
            settings.Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(GuardSettings settings)
    {
        if (!TrySave(settings))
        {
            throw new IOException($"无法保存 VaultLatch 设置：{_settingsPath}");
        }
    }

    private static int GetStoredVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(nameof(GuardSettings.SettingsVersion), out var versionElement) &&
                versionElement.TryGetInt32(out var version))
            {
                return version;
            }
        }
        catch (JsonException)
        {
        }

        return 1;
    }
}
