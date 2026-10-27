using System.Security.Principal;
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
        return StorageProviderSyncRootManager.GetCurrentSyncRoots()
            .Where(root => root.ProviderId == ProviderIdentity.Id)
            .Select(root => root.Path?.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void Remove(string syncRootPath)
    {
        var registered = StorageProviderSyncRootManager.GetCurrentSyncRoots()
            .FirstOrDefault(root => root.Id == RegistrationId && PathsEqual(root.Path.Path, syncRootPath));
        if (registered != null) StorageProviderSyncRootManager.Unregister(registered.Id);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
