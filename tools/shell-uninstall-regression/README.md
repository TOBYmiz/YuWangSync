# Shell uninstall regression

Run on x64 Windows after building the native extension in Release:

```powershell
dotnet run --project tools/shell-uninstall-regression -c Release -- --shell-extension src/YuWangCloudSync.ShellExt/bin/Release/YuWangCloudSyncShellExt.dll
```

Omit `--shell-extension` to run only the managed checks. The harness uses a private application hive file and redirects predefined registry handles only within its own process. It does not remove installed sync roots.

Checks cover missing target folders, orphan navigation CLSIDs, propagation of write failures, repeatable cleanup, preservation of unrelated registrations and ordinary files, and the production per-root uninstall path. Native checks call the actual DLL registration exports and verify removal of the overlay, CLSID, ProgID and Approved entries.

This does not replace testing the packaged installer/uninstaller in Windows 10 or checking Explorer's visible navigation pane. The harness runs managed code without NativeAOT.
