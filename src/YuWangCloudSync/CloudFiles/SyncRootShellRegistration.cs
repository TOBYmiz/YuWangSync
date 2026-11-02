using System.Security.Principal;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Storage.Streams;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.CloudFiles;

internal static class SyncRootShellRegistration
{
    private static string RegistrationId => ProductIdentity.SyncRootRegistrationPrefix
        + WindowsIdentity.GetCurrent().User!.Value + "!Default";

    public static void Update(string syncRootPath, string providerName, string iconPath)
    {
        if (!ProviderIdentity.OwnsRoot(syncRootPath)) return;
        var registered = StorageProviderSyncRootManager.GetCurrentSyncRoots()
            .FirstOrDefault(root => root.Id == RegistrationId);
        var info = registered ?? new StorageProviderSyncRootInfo { Id = RegistrationId };
        info.Path = StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(syncRootPath)).AsTask().GetAwaiter().GetResult();
        info.DisplayNameResource = providerName;
        info.IconResource = iconPath + ",0";
        info.Version = ProductVersion.Current;
        info.HydrationPolicy = StorageProviderHydrationPolicy.Progressive;
        info.PopulationPolicy = StorageProviderPopulationPolicy.Full;
        info.ProviderId = ProviderIdentity.Id;
        using var writer = new DataWriter();
        writer.WriteBytes(ProviderIdentity.RootIdentity);
        info.Context = writer.DetachBuffer();
        StorageProviderSyncRootManager.Register(info);
        ExplorerStateNotifier.NotifyItem(syncRootPath);
    }

    public static bool NeedsHydrationPolicyRefresh(string syncRootPath)
    {
        var registered = StorageProviderSyncRootManager.GetCurrentSyncRoots()
            .FirstOrDefault(root => root.Id == RegistrationId);
        return registered == null
            || !PathsEqual(registered.Path.Path, syncRootPath)
            || registered.HydrationPolicy != StorageProviderHydrationPolicy.Progressive;
    }

    public static string[] GetRegisteredRootPaths()
    {
        return GetRegisteredRoots()
            .Select(root => root.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static (string Id, string Path)[] GetRegisteredRoots()
    {
        var roots = new List<(string Id, string Path)>();
        try
        {
            roots.AddRange(StorageProviderSyncRootManager.GetCurrentSyncRoots()
                .Where(root => root.ProviderId == ProviderIdentity.Id)
                .Select(root => (root.Id, Path: root.Path?.Path))
                .Where(root => !string.IsNullOrWhiteSpace(root.Path))
                .Select(root => (root.Id, Path: Path.GetFullPath(root.Path!))));
        }
        catch
        {
            // The WinRT view can be empty for an unpackaged process even while the
            // machine-wide SyncRootManager registration remains after a forced removal.
        }

        roots.AddRange(GetRegistryRegisteredRoots());
        return roots
            .DistinctBy(root => (root.Id, root.Path))
            .ToArray();
    }

    public static void Remove(string registrationId, string syncRootPath)
    {
        try
        {
            var registered = StorageProviderSyncRootManager.GetCurrentSyncRoots()
                .FirstOrDefault(root => root.Id == registrationId
                    && root.ProviderId == ProviderIdentity.Id
                    && root.Path != null
                    && PathsEqual(root.Path.Path, syncRootPath));
            if (registered != null) StorageProviderSyncRootManager.Unregister(registered.Id);
        }
        catch { /* 注册表兜底仍可清理残留项。 */ }

        RemoveRegistryRegistration(registrationId, syncRootPath);
    }

    public static void Remove(string syncRootPath)
    {
        Remove(RegistrationId, syncRootPath);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static (string Id, string Path)[] GetRegistryRegisteredRoots()
    {
        var roots = new List<(string Id, string Path)>();
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) return roots.ToArray();
        var registrationPrefix = ProductIdentity.SyncRootRegistrationPrefix + sid + "!";

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? baseKey = null;
            RegistryKey? manager = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                manager = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
                if (manager == null) continue;

                foreach (var id in manager.GetSubKeyNames())
                {
                    if (!id.StartsWith(registrationPrefix,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var userRoots = manager.OpenSubKey(id + "\\UserSyncRoots");
                    var path = userRoots?.GetValue(sid) as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string fullPath;
                    try { fullPath = Path.GetFullPath(path); }
                    catch { continue; }
                    roots.Add((id, fullPath));
                }
            }
            catch
            {
                // Registry access can be denied for one view; continue with the others.
            }
            finally
            {
                manager?.Dispose();
                baseKey?.Dispose();
            }
        }
        return roots.ToArray();
    }

    private static void RemoveRegistryRegistration(string registrationId, string syncRootPath)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)
            || !registrationId.StartsWith(ProductIdentity.SyncRootRegistrationPrefix + sid + "!",
                StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? baseKey = null;
            RegistryKey? manager = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                manager = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager", writable: true);
                if (manager == null) continue;
                string? registeredPath;
                using (var userRoots = manager.OpenSubKey(registrationId + "\\UserSyncRoots", writable: false))
                    registeredPath = userRoots?.GetValue(sid) as string;
                if (string.IsNullOrWhiteSpace(registeredPath) || !PathsEqual(registeredPath, syncRootPath)) continue;
                manager.DeleteSubKeyTree(registrationId, throwOnMissingSubKey: false);
            }
            catch
            {
                // A later elevated uninstall can retry if this view is read-only.
            }
            finally
            {
                manager?.Dispose();
                baseKey?.Dispose();
            }
        }
    }
}
