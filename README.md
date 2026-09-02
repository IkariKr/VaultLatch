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

## Requirements

- Windows 10 or Windows 11
- VeraCrypt installed with its driver available
- Syncthing only if Syncthing integration is used

The direct VeraCrypt driver integration is currently developed and tested against VeraCrypt 1.26.29. VaultLatch checks the driver ABI before using it.

## Build

```powershell
dotnet publish VaultLatch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist
```

The published executable is `dist\VaultLatch.exe`.

## License

MIT
