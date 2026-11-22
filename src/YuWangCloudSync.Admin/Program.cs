using System.Security.Principal;
using Microsoft.Win32;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Admin;

internal static class Program
{
    // Only fixed operations are accepted. No arbitrary registry keys, commands, DLLs or code.
    // Exit codes: 0 success; 1 operation failed; 2 invalid arguments.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (5 or 7) || args[1] != "--user-sid" || args[3] != "--sync-root") return 2;
        var operation = args[0];
        if (operation is not ("--remove-machine-shell-registration" or "--ensure-search-index" or "--remove-search-index")) return 2;
        if ((operation == "--remove-machine-shell-registration") != (args.Length == 7)) return 2;
        try
        {
            var sid = new SecurityIdentifier(args[2]).Value;
            if (!Path.IsPathFullyQualified(args[4])) return 2;
            var path = Path.GetFullPath(args[4]);
            if (operation == "--remove-machine-shell-registration")
            {
                if (args[5] != "--registration-id"
                    || !args[6].StartsWith(ProductIdentity.SyncRootRegistrationPrefix + sid + "!", StringComparison.OrdinalIgnoreCase)) return 2;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    SyncRootShellRegistry.Remove(machine, sid, args[6], path, includeNamespaces: false);
                }
                return 0;
            }
            // Elevation with alternate admin credentials still journals the requesting user's
            // loaded hive, rather than accidentally changing the administrator's HKCU.
            if (operation == "--ensure-search-index" && !ProviderIdentity.OwnsRoot(path, allowLegacy: true)) return 2;
            using var user = Registry.Users.OpenSubKey(sid, writable: true);
            if (user is null) return 1;
            if (operation == "--ensure-search-index") return SyncRootSearchIndex.Ensure(path, null, markerRoot: user) ? 0 : 1;
            return SyncRootSearchIndex.Remove(path, null, markerRoot: user) ? 0 : 1;
        }
        catch (ArgumentException) { return 2; }
        catch (Exception) { return 1; }
    }
}
