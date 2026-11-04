using Microsoft.Win32;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.CloudFiles;

// Keep registry operations separate from WinRT so the fallback can be exercised in
// an isolated registry tree, including entries whose target folder no longer exists.
internal static class SyncRootShellRegistry
{
    internal const string ManagerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    internal const string ClassesPath = @"Software\Classes\CLSID";
    internal const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    internal static (string Id, string Path)[] ReadRoots(RegistryKey baseKey, string sid,
        bool includeNamespaces)
    {
        var roots = new List<(string Id, string Path)>();
        using var manager = baseKey.OpenSubKey(ManagerPath);
        if (manager != null)
        {
            foreach (var id in manager.GetSubKeyNames())
            {
                if (!OwnsRegistrationId(id, sid)) continue;
                using var userRoots = manager.OpenSubKey(id + @"\UserSyncRoots");
                if (TryGetFullPath(userRoots?.GetValue(sid) as string, out var path))
                    roots.Add((id, path));
            }
        }

        if (includeNamespaces)
            roots.AddRange(ReadNamespaceRoots(baseKey).Select(root =>
                (ProductIdentity.SyncRootRegistrationPrefix + sid + "!Default", root.Path)));
        return roots.ToArray();
    }

    internal static void Remove(RegistryKey baseKey, string sid, string registrationId,
        string syncRootPath, bool includeNamespaces)
    {
        if (!OwnsRegistrationId(registrationId, sid)) return;

        // Removing SyncRootManager alone can leave Explorer's generated CLSID and
        // navigation entry behind. Only remove classes with this product's icon and
        // the exact target path; a display name is not an ownership marker.
        if (includeNamespaces)
        {
            foreach (var root in ReadNamespaceRoots(baseKey).Where(root => PathsEqual(root.Path, syncRootPath)))
            {
                baseKey.DeleteSubKeyTree(ExplorerPath + @"\Desktop\NameSpace\" + root.ClassId, false);
                baseKey.DeleteSubKeyTree(ExplorerPath + @"\MyComputer\NameSpace\" + root.ClassId, false);
                foreach (var menu in new[] { "ClassicStartMenu", "NewStartPanel" })
                {
                    using var hidden = baseKey.OpenSubKey(ExplorerPath + @"\HideDesktopIcons\" + menu, true);
                    hidden?.DeleteValue(root.ClassId, false);
                }
                baseKey.DeleteSubKeyTree(ClassesPath + "\\" + root.ClassId, false);
            }
        }

        // Read before requesting write access: unelevated setup recovery must still
        // work when the machine hive contains no matching registration.
        using (var userRoots = baseKey.OpenSubKey(ManagerPath + "\\" + registrationId + @"\UserSyncRoots"))
        {
            var registeredPath = userRoots?.GetValue(sid) as string;
            if (registeredPath == null || !PathsEqual(registeredPath, syncRootPath)) return;
        }
        baseKey.DeleteSubKeyTree(ManagerPath + "\\" + registrationId, false);
    }

    private static (string ClassId, string Path)[] ReadNamespaceRoots(RegistryKey baseKey)
    {
        var roots = new List<(string ClassId, string Path)>();
        using var classes = baseKey.OpenSubKey(ClassesPath);
        if (classes == null) return roots.ToArray();
        foreach (var classId in classes.GetSubKeyNames())
        {
            if (!Guid.TryParse(classId, out _)) continue;
            using var icon = classes.OpenSubKey(classId + @"\DefaultIcon");
            var iconResource = icon?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(iconResource)) continue;
            var separator = iconResource.LastIndexOf(',');
            var iconPath = (separator < 0 ? iconResource : iconResource[..separator]).Trim().Trim('"');
            if (!string.Equals(Path.GetFileName(iconPath), ProductIdentity.ExecutableFileName,
                    StringComparison.OrdinalIgnoreCase)) continue;
            using var properties = classes.OpenSubKey(classId + @"\Instance\InitPropertyBag");
            if (TryGetFullPath(properties?.GetValue("TargetFolderPath") as string, out var path))
                roots.Add((classId, path));
        }
        return roots.ToArray();
    }

    private static bool OwnsRegistrationId(string id, string sid) =>
        id.StartsWith(ProductIdentity.SyncRootRegistrationPrefix + sid + "!", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetFullPath(string? path, out string fullPath)
    {
        fullPath = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool PathsEqual(string left, string right) =>
        TryGetFullPath(left, out var fullLeft) && TryGetFullPath(right, out var fullRight)
        && string.Equals(fullLeft.TrimEnd('\\'), fullRight.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
