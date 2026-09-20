# VaultLatch

VaultLatch is a small Windows tray utility for managing a VeraCrypt volume around Windows session locks.

It can:

- mount and unmount a VeraCrypt file container from the tray;
- access the VeraCrypt driver directly for mount, unmount, and password-cache wipe operations;
- protect the configured volume after Windows enters the locked state;
- pause and resume a configured Syncthing folder;
- optionally close and reopen an Obsidian vault;
- provide optional display-off, suspend, and idle triggers.

VaultLatch does not delay or replace Windows locking. When `SessionLock` is enabled, Windows locks first and VaultLatch performs volume cleanup in the background.

## 移动磁盘与掉盘恢复

首次从移动磁盘启动时，VaultLatch 会将自身复制到 `%LOCALAPPDATA%\\VaultLatch`，并由本地副本运行 watchdog。watchdog 不依赖移动磁盘：当原始盘符暂时消失时保持运行，盘符恢复后自动重新启动主程序；主程序异常退出时也会按递增间隔重试。正常从托盘退出时会通知 watchdog 停止，避免退出后被再次拉起。

如果启用了“随 Windows 登录自动启动”，注册表启动项会指向本地副本的 watchdog，而不是移动磁盘上的 exe。这样即使移动磁盘未连接，系统登录过程也不会因为找不到主程序而报错。

日志和崩溃记录写入 `%LOCALAPPDATA%\\VaultLatch`。所有磁盘探测、日志写入、配置保存和后台事件均采用失败降级策略，移动磁盘不可用时托盘程序保持运行并显示“设备不可用”。

## Requirements

- Windows 10 or Windows 11
- VeraCrypt installed with its driver available
- Syncthing only if Syncthing integration is used

The direct VeraCrypt driver integration is currently developed and tested against VeraCrypt 1.26.29. VaultLatch checks the driver ABI before using it.

## Build

```powershell
dotnet publish .\VaultLatch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\dist-local
```

The published executable is `dist-local\VaultLatch.exe`. Prefer copying the complete single-file output to a local NTFS directory before starting it; do not execute it directly from a removable or OneDrive-backed drive.

## License

MIT
