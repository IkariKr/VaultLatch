using Microsoft.Win32;
using System.Diagnostics;

namespace VaultLatch;

internal sealed class GuardApplicationContext : ApplicationContext
{
    private const string ApplicationName = "VaultLatch";
    private readonly FileLogger _logger = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly StartupManager _startupManager;
    private readonly VeraCryptController _veraCrypt;
    private readonly SyncthingController _syncthing;
    private readonly ObsidianController _obsidian;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly System.Windows.Forms.Timer _idleTimer;
    private readonly PowerEventWindow _powerWindow;
    private readonly string _sourcePath;
    private readonly string? _stopEventName;
    private GuardSettings _settings;
    private int _protectInProgress;
    private int _mountInProgress;
    private bool _isExiting;

    public GuardApplicationContext(string? stopEventName)
    {
        _stopEventName = stopEventName;
        _sourcePath = Watchdog.GetSourcePath(Environment.GetCommandLineArgs())
            ?? Environment.ProcessPath
            ?? Application.ExecutablePath;
        _startupManager = new StartupManager(_sourcePath);
        _settings = _settingsStore.Load();
        _veraCrypt = new VeraCryptController(_logger);
        _syncthing = new SyncthingController(_logger);
        _obsidian = new ObsidianController(_logger);

        ApplyStartupSetting(_settings.StartWithWindows);

        var menu = new ContextMenuStrip();
        _statusMenuItem = new ToolStripMenuItem("状态：检查中…") { Enabled = false };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("解锁 / 挂载加密卷", null, (_, _) => RunSafely(MountAsync, "托盘挂载操作"));
        menu.Items.Add("安全锁定加密卷", null, (_, _) => RunSafely(() => ProtectAsync("手动锁定"), "托盘锁定操作"));
        menu.Items.Add("锁定 Windows", null, (_, _) => RequestWindowsLock());
        menu.Items.Add("打开 Vault", null, (_, _) => OpenVault());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("打开日志", null, (_, _) => OpenLog());
        _startupMenuItem = new ToolStripMenuItem("随 Windows 登录自动启动") { CheckOnClick = true };
        _startupMenuItem.CheckedChanged += (_, _) => UpdateStartupSettingFromMenu();
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 VaultLatch", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = ApplicationName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => RunSafely(async () =>
        {
            if (_veraCrypt.IsMounted(_settings))
            {
                OpenVault();
            }
            else
            {
                await MountAsync();
            }
        }, "托盘双击操作");

        _startupMenuItem.Checked = _startupManager.IsEnabled();

        _idleTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _idleTimer.Tick += (_, _) => RunSafely(async () =>
        {
            UpdateStatusText();
            await CheckIdleAsync();
        }, "定时状态检查");
        _idleTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _powerWindow = new PowerEventWindow();
        _powerWindow.DisplayTurnedOff += (_, _) => RunSafely(async () =>
        {
            if (_settings.LockOnDisplayOff)
            {
                await ProtectAsync("显示器已关闭");
            }
        }, "显示器关闭事件");
        _powerWindow.SystemSuspending += (_, _) => RunSafely(async () =>
        {
            if (_settings.LockOnSuspend)
            {
                await ProtectAsync("系统即将挂起");
            }
        }, "系统挂起事件");

        _logger.Info($"VaultLatch 已启动；卷={_settings.VolumePath}；盘符={_settings.DriveLetter}:；Folder={_settings.SyncthingFolderId}。");
        _logger.Info($"VeraCrypt 驱动直连访问：{(_veraCrypt.CanAccessDriver() ? "可用" : "不可用")}。");
        _veraCrypt.EnsureVaultLatchOwnsBackgroundBehavior();
        WarnIfVeraCryptGuiRunning();
        WarnIfVeraCryptAutoDismountConflicts();
        UpdateStatusText();
        _ = InitializeStateAsync();
    }

    private void WarnIfVeraCryptGuiRunning()
    {
        if (!_veraCrypt.IsGuiProcessRunning())
        {
            return;
        }

        _logger.Error("检测到 VeraCrypt.exe GUI/后台进程仍在运行。VaultLatch 使用驱动直连模式；请退出 VeraCrypt 托盘进程，避免形成第二套锁屏处理链路。");
        ShowBalloon(
            "请退出 VeraCrypt 后台程序",
            "VaultLatch 已直接管理 VeraCrypt 驱动。请将 VeraCrypt 托盘程序退出一次，之后日常无需再启动它。",
            ToolTipIcon.Warning);
    }

    private void WarnIfVeraCryptAutoDismountConflicts()
    {
        var conflicts = _veraCrypt.GetAutoDismountConflicts();
        if (conflicts.Count == 0)
        {
            return;
        }

        var detail = string.Join("、", conflicts);
        _logger.Error($"检测到 VeraCrypt 自动卸载与 VaultLatch 冲突：{detail}。请关闭 VeraCrypt 的锁屏/省电/屏保/空闲自动卸载，由 VaultLatch 统一处理。");
        ShowBalloon(
            "VeraCrypt 设置冲突",
            "检测到 VeraCrypt 自带自动卸载仍开启，可能再次出现占用确认弹窗。请在 VeraCrypt 首选项中关闭相关自动卸载。",
            ToolTipIcon.Warning);
    }

    private async Task InitializeStateAsync()
    {
        try
        {
            if (_veraCrypt.IsMounted(_settings))
            {
                if (_settings.ResumeSyncthingAfterMount && _syncthing.MarkerExists(_settings))
                {
                    await _syncthing.SetFolderPausedAsync(_settings, paused: false);
                }
            }
            else
            {
                await _syncthing.SetFolderPausedAsync(_settings, paused: true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("初始化 VaultLatch 状态失败", exception);
        }
        finally
        {
            UpdateStatusText();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.LockOnSessionLock)
        {
            RunSafely(async () =>
            {
                _logger.Info("已收到 Windows SessionLock 事件；系统已进入锁屏状态，开始后台保护加密卷。");
                await ProtectAsync("Windows 已锁定");
            }, "Windows 锁屏事件");
        }
    }

    private void RunSafely(Func<Task> operation, string operationName)
    {
        _ = RunSafelyAsync(operation, operationName);
    }

    private async Task RunSafelyAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error($"{operationName}失败", exception);
            CrashReporter.Report($"{operationName}未处理异常", exception);
        }
    }

    private void RequestWindowsLock()
    {
        _logger.Info("用户从托盘请求锁定 Windows；立即交给 Windows 锁屏，VaultLatch 将在 SessionLock 事件后后台保护加密卷。");
        if (!NativeMethods.LockWindows())
        {
            _logger.Error("Windows 锁屏请求失败。");
            ShowBalloon("Windows 锁屏失败", "系统未接受锁屏请求，加密卷未进入锁屏触发流程。", ToolTipIcon.Error);
        }
    }

    private async Task CheckIdleAsync()
    {
        if (_isExiting || !_settings.LockOnIdle || _settings.IdleTimeoutMinutes <= 0 || !_veraCrypt.IsMounted(_settings))
        {
            return;
        }

        var idleMilliseconds = NativeMethods.GetIdleMilliseconds();
        var thresholdMilliseconds = (uint)_settings.IdleTimeoutMinutes * 60_000u;
        if (idleMilliseconds >= thresholdMilliseconds)
        {
            await ProtectAsync($"已超过 {_settings.IdleTimeoutMinutes} 分钟未操作");
        }
    }

    private async Task ProtectAsync(string reason)
    {
        if (_isExiting || Interlocked.Exchange(ref _protectInProgress, 1) != 0)
        {
            return;
        }

        var wasMounted = _veraCrypt.IsMounted(_settings);
        var forced = false;
        var syncPaused = false;
        var unmounted = !wasMounted;

        try
        {
            _logger.Info($"开始保护加密卷：{reason}；mounted={wasMounted}。");
            _statusMenuItem.Text = "状态：正在安全锁定…";

            syncPaused = await _syncthing.SetFolderPausedAsync(_settings, paused: true);

            if (wasMounted)
            {
                var quiesceTimer = Stopwatch.StartNew();
                if (_settings.CloseObsidianBeforeLock)
                {
                    await _obsidian.CloseGracefullyAsync(_settings.GraceSeconds);
                }

                var minimumQuiesce = TimeSpan.FromSeconds(_settings.GraceSeconds);
                var remainingQuiesce = minimumQuiesce - quiesceTimer.Elapsed;
                if (remainingQuiesce > TimeSpan.Zero)
                {
                    await Task.Delay(remainingQuiesce);
                }

                _logger.Info($"写入静默期完成；目标={_settings.GraceSeconds}s；实际={quiesceTimer.Elapsed.TotalSeconds:F1}s。");

                _logger.Info("通过 VeraCrypt 驱动直接执行正常卸载；该路径不经过 VeraCrypt GUI。");
                unmounted = await _veraCrypt.UnmountAsync(_settings, force: false);
                if (!unmounted && _settings.ForceUnmountFallback)
                {
                    forced = true;
                    _logger.Info("驱动级正常卸载未成功，进入驱动级无交互强制卸载兜底。");
                    unmounted = await _veraCrypt.UnmountAsync(_settings, force: true);
                }
            }

            await _veraCrypt.WipeCacheAsync(_settings);

            if (unmounted)
            {
                var detail = forced ? "已通过无交互强制模式卸载" : "已安全卸载";
                if (!syncPaused)
                {
                    detail += "；Syncthing 暂停状态未能确认";
                }
                ShowBalloon("VaultLatch 已锁定", $"{reason}；{detail}。", syncPaused ? ToolTipIcon.Info : ToolTipIcon.Warning);
                _logger.Info($"加密卷保护完成：{reason}；forced={forced}；syncPaused={syncPaused}。");
            }
            else
            {
                ShowBalloon("VaultLatch 锁定失败", $"{reason}；{_settings.DriveLetter}: 仍处于挂载状态，请立即检查。", ToolTipIcon.Error);
                _logger.Error($"加密卷保护失败：{reason}；{_settings.DriveLetter}: 仍挂载。 ");
            }
        }
        catch (Exception exception)
        {
            _logger.Error($"保护加密卷失败：{reason}", exception);
            ShowBalloon("VaultLatch 保护异常", "保护流程发生异常，详情已写入日志。", ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _protectInProgress, 0);
            UpdateStatusText();
        }
    }

    private async Task MountAsync()
    {
        if (_isExiting || Interlocked.Exchange(ref _mountInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_veraCrypt.IsMounted(_settings))
            {
                _statusMenuItem.Text = "状态：等待 VeraCrypt 密码…";
                var mounted = await _veraCrypt.MountAsync(_settings);
                if (!mounted)
                {
                    ShowBalloon("VaultLatch 未挂载", "挂载已取消或失败。", ToolTipIcon.Warning);
                    return;
                }
            }

            if (!Directory.Exists(_settings.VaultPath))
            {
                ShowBalloon("Vault 路径异常", $"已挂载 {_settings.DriveLetter}:，但未找到 {_settings.VaultPath}。", ToolTipIcon.Error);
                return;
            }

            if (_settings.ResumeSyncthingAfterMount)
            {
                if (!_syncthing.MarkerExists(_settings))
                {
                    ShowBalloon("同步保持暂停", "未找到 .stfolder 标记，为避免错误删除未恢复 Syncthing。", ToolTipIcon.Warning);
                    _logger.Error($"挂载后未找到 Syncthing marker：{Path.Combine(_settings.VaultPath, ".stfolder")}");
                }
                else
                {
                    var resumed = await _syncthing.SetFolderPausedAsync(_settings, paused: false);
                    if (!resumed)
                    {
                        ShowBalloon("同步恢复失败", "加密卷已挂载，但 Syncthing 未能自动恢复。", ToolTipIcon.Warning);
                    }
                }
            }

            if (_settings.OpenObsidianAfterMount)
            {
                _obsidian.OpenVault(_settings);
            }

            ShowBalloon("VaultLatch 已解锁", $"{_settings.DriveLetter}: 已挂载。", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _logger.Error("解锁 / 挂载加密卷失败", exception);
            ShowBalloon("VaultLatch 解锁失败", exception.Message, ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _mountInProgress, 0);
            UpdateStatusText();
        }
    }

    private void OpenVault()
    {
        if (!_veraCrypt.IsMounted(_settings) || !Directory.Exists(_settings.VaultPath))
        {
            ShowBalloon("VaultLatch 已锁定", "请先解锁 / 挂载加密卷。", ToolTipIcon.Warning);
            return;
        }

        try
        {
            _obsidian.OpenVault(_settings);
        }
        catch (Exception exception)
        {
            _logger.Error("打开 Obsidian Vault 失败", exception);
            ShowBalloon("打开失败", "无法打开 Obsidian，详情已写入日志。", ToolTipIcon.Error);
        }
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsForm(_settings, _startupManager.IsEnabled());
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var previousSettings = _settings;
        var candidateSettings = dialog.Settings;
        if (!_settingsStore.TrySave(candidateSettings))
        {
            _logger.Error("设置保存失败，保留当前内存配置。");
            _startupMenuItem.Checked = previousSettings.StartWithWindows;
            ShowBalloon("设置未保存", "配置文件不可写，已保留原设置。", ToolTipIcon.Warning);
            return;
        }

        _settings = candidateSettings;
        ApplyStartupSetting(_settings.StartWithWindows);
        _startupMenuItem.Checked = _startupManager.IsEnabled();
        _logger.Info($"设置已保存；空闲={_settings.IdleTimeoutMinutes}min；grace={_settings.GraceSeconds}s；force={_settings.ForceUnmountFallback}。");
        UpdateStatusText();
        ShowBalloon("VaultLatch", "设置已保存。", ToolTipIcon.Info);
    }

    private void UpdateStartupSettingFromMenu()
    {
        if (_isExiting || _startupMenuItem.Checked == _settings.StartWithWindows)
        {
            return;
        }

        var desired = _startupMenuItem.Checked;
        _settings.StartWithWindows = desired;
        if (!_settingsStore.TrySave(_settings))
        {
            _settings.StartWithWindows = !desired;
            _startupMenuItem.Checked = !desired;
            _logger.Error("开机自启动设置未能持久化。");
            ShowBalloon("设置未保存", "配置文件不可写，未更改开机自启动设置。", ToolTipIcon.Warning);
            return;
        }

        ApplyStartupSetting(desired);
    }

    private void ApplyStartupSetting(bool enabled)
    {
        try
        {
            _startupManager.SetEnabled(enabled);
        }
        catch (Exception exception)
        {
            _logger.Error("更新开机自启动设置失败", exception);
        }
    }

    private void UpdateStatusText()
    {
        if (_isExiting)
        {
            return;
        }

        try
        {
            var mounted = _veraCrypt.IsMounted(_settings);
            _statusMenuItem.Text = mounted
                ? $"状态：已解锁（{_settings.DriveLetter}:）"
                : "状态：已锁定";
            _trayIcon.Text = mounted ? "VaultLatch - 已解锁" : "VaultLatch - 已锁定";
        }
        catch (Exception exception)
        {
            _logger.Error("更新托盘状态失败", exception);
            _statusMenuItem.Text = "状态：设备不可用";
            _trayIcon.Text = "VaultLatch - 设备不可用";
        }
    }

    private void OpenLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logger.LogPath) || !File.Exists(_logger.LogPath))
            {
                ShowBalloon("日志不可用", "当前没有可打开的日志文件。", ToolTipIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(_logger.LogPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logger.Error("打开日志失败", exception);
        }
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        try
        {
            _trayIcon.ShowBalloonTip(5_000, title, message, icon);
        }
        catch (Exception exception)
        {
            _logger.Error("显示托盘提示失败", exception);
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _idleTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _powerWindow.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _logger.Info("VaultLatch 已退出。\n");
        Watchdog.RequestStop(_stopEventName);
        _logger.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (!_isExiting)
        {
            ExitApplication();
            return;
        }

        base.ExitThreadCore();
    }
}
