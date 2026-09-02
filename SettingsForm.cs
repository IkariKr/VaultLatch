namespace VaultLatch;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _veraCryptPath;
    private readonly TextBox _volumePath;
    private readonly TextBox _driveLetter;
    private readonly TextBox _vaultPath;
    private readonly TextBox _syncthingPath;
    private readonly TextBox _folderId;
    private readonly NumericUpDown _idleTimeout;
    private readonly NumericUpDown _graceSeconds;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _lockOnSessionLock;
    private readonly CheckBox _lockOnDisplayOff;
    private readonly CheckBox _lockOnSuspend;
    private readonly CheckBox _lockOnIdle;
    private readonly CheckBox _closeObsidian;
    private readonly CheckBox _forceUnmount;
    private readonly CheckBox _resumeSyncthing;
    private readonly CheckBox _openObsidian;

    public GuardSettings Settings { get; }

    public SettingsForm(GuardSettings currentSettings, bool startupEnabled)
    {
        Settings = currentSettings.Clone();
        Text = "VaultLatch 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 650);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 18,
            AutoSize = false,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        var intro = new Label
        {
            Text = "VaultLatch 日常直接访问 VeraCrypt 驱动，不启动 VeraCrypt.exe。Windows 锁屏后在后台暂停同步、关闭 Obsidian 并卸载加密卷。",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
        };
        panel.Controls.Add(intro, 0, 0);
        panel.SetColumnSpan(intro, 2);

        _veraCryptPath = AddTextRow(panel, 1, "VeraCrypt.exe（安装参考）", currentSettings.VeraCryptPath);
        _volumePath = AddTextRow(panel, 2, "VeraCrypt 容器", currentSettings.VolumePath);
        _driveLetter = AddTextRow(panel, 3, "挂载盘符", currentSettings.DriveLetter);
        _vaultPath = AddTextRow(panel, 4, "Vault 路径", currentSettings.VaultPath);
        _syncthingPath = AddTextRow(panel, 5, "syncthing.exe", currentSettings.SyncthingPath);
        _folderId = AddTextRow(panel, 6, "Syncthing Folder ID", currentSettings.SyncthingFolderId);

        _idleTimeout = AddNumberRow(panel, 7, "空闲自动保护加密卷（分钟）", currentSettings.IdleTimeoutMinutes, 0, 1440);
        _graceSeconds = AddNumberRow(panel, 8, "写入释放等待（秒）", currentSettings.GraceSeconds, 0, 30);

        _startWithWindows = AddCheckRow(panel, 9, "随 Windows 登录自动启动", startupEnabled);
        _lockOnSessionLock = AddCheckRow(panel, 10, "Windows 已锁屏后后台保护加密卷（主触发）", currentSettings.LockOnSessionLock);
        _lockOnDisplayOff = AddCheckRow(panel, 11, "显示器关闭时额外保护加密卷（不等同于锁屏）", currentSettings.LockOnDisplayOff);
        _lockOnSuspend = AddCheckRow(panel, 12, "睡眠/挂起前保护加密卷", currentSettings.LockOnSuspend);
        _lockOnIdle = AddCheckRow(panel, 13, "用户空闲时额外保护加密卷（不会主动锁 Windows）", currentSettings.LockOnIdle);
        _closeObsidian = AddCheckRow(panel, 14, "保护前请求关闭 Obsidian", currentSettings.CloseObsidianBeforeLock);
        _forceUnmount = AddCheckRow(panel, 15, "使用无交互强制卸载（避免占用确认弹窗）", currentSettings.ForceUnmountFallback);

        var bottomOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _resumeSyncthing = new CheckBox { AutoSize = true, Text = "挂载后自动恢复 Syncthing 同步", Checked = currentSettings.ResumeSyncthingAfterMount };
        _openObsidian = new CheckBox { AutoSize = true, Text = "挂载后自动打开 Obsidian", Checked = currentSettings.OpenObsidianAfterMount };
        bottomOptions.Controls.Add(_resumeSyncthing);
        bottomOptions.Controls.Add(_openObsidian);
        panel.Controls.Add(new Label { Text = "解锁后", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 16);
        panel.Controls.Add(bottomOptions, 1, 16);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(90, 30) };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, Size = new Size(90, 30) };
        save.Click += (_, _) => SaveValues();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        panel.Controls.Add(buttons, 0, 17);
        panel.SetColumnSpan(buttons, 2);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private void SaveValues()
    {
        Settings.VeraCryptPath = _veraCryptPath.Text;
        Settings.VolumePath = _volumePath.Text;
        Settings.DriveLetter = _driveLetter.Text;
        Settings.VaultPath = _vaultPath.Text;
        Settings.SyncthingPath = _syncthingPath.Text;
        Settings.SyncthingFolderId = _folderId.Text;
        Settings.IdleTimeoutMinutes = Decimal.ToInt32(_idleTimeout.Value);
        Settings.GraceSeconds = Decimal.ToInt32(_graceSeconds.Value);
        Settings.StartWithWindows = _startWithWindows.Checked;
        Settings.LockOnSessionLock = _lockOnSessionLock.Checked;
        Settings.LockOnDisplayOff = _lockOnDisplayOff.Checked;
        Settings.LockOnSuspend = _lockOnSuspend.Checked;
        Settings.LockOnIdle = _lockOnIdle.Checked;
        Settings.CloseObsidianBeforeLock = _closeObsidian.Checked;
        Settings.ForceUnmountFallback = _forceUnmount.Checked;
        Settings.ResumeSyncthingAfterMount = _resumeSyncthing.Checked;
        Settings.OpenObsidianAfterMount = _openObsidian.Checked;
        Settings.Normalize();
    }

    private static TextBox AddTextRow(TableLayoutPanel panel, int row, string label, string value)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var input = new TextBox { Text = value, Dock = DockStyle.Fill };
        panel.Controls.Add(input, 1, row);
        return input;
    }

    private static NumericUpDown AddNumberRow(TableLayoutPanel panel, int row, string label, int value, int min, int max)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var input = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 120 };
        panel.Controls.Add(input, 1, row);
        return input;
    }

    private static CheckBox AddCheckRow(TableLayoutPanel panel, int row, string text, bool value)
    {
        var check = new CheckBox { Text = text, Checked = value, AutoSize = true };
        panel.Controls.Add(check, 0, row);
        panel.SetColumnSpan(check, 2);
        return check;
    }
}
