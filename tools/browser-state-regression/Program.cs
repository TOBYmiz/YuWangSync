using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Tray;

var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var testAccount = "browser-state-regression-" + Guid.NewGuid().ToString("N");
var passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

SyncRootContext Config(string suffix = "", string server = "https://example.invalid", ulong root = 0)
    => new() { Username = testAccount + suffix, Server = server, RootContainerTypeId = root };

string StatePath(SyncRootContext config)
{
    var key = JsonSerializer.Serialize(new[] { config.Server.Trim().TrimEnd('/').ToUpperInvariant(),
        config.Username.Trim().ToUpperInvariant(), config.RootContainerTypeId.ToString() },
        MainWindowStateJsonContext.Default.StringArray);
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    var path = Path.Combine(AppPaths.DataDirectory, "browser-state", digest + ".json");
    files.Add(path);
    return path;
}

try
{
    var config = Config();
    var file = StatePath(config);
    var defaults = MainWindowStateStore.Load(config);
    Check("new account has expanded empty favorites at the default height",
        defaults.Favorites.Count == 0 && defaults.FavoritesExpanded && defaults.FavoritesHeight == 180);
    var state = new MainWindowState
    {
        NodePath = ["root", "disk:10", "folder:20"],
        ColumnWidths = [300, 140, 110, 90],
        FavoritesExpanded = false,
        FavoritesHeight = 260,
        Favorites =
        [
            new() { DiskId = 10, FolderId = 20, Name = "资料", DisplayPath = "育网云 / 个人盘 / 资料",
                NodePath = ["root", "disk:10", "folder:20"] },
            new() { DiskId = 10, FolderId = 0, Name = "个人盘", NodePath = ["root", "disk:10"] },
        ],
    };
    MainWindowStateStore.Save(config, state);
    var diskState = JsonSerializer.Deserialize(File.ReadAllText(file), MainWindowStateJsonContext.Default.MainWindowState)!;
    Check("generated JSON persists folder and disk favorites, height and collapse state",
        diskState.Favorites.Count == 2 && diskState.Favorites[0].Name == "资料"
        && diskState.Favorites[0].NodePath.SequenceEqual(state.Favorites[0].NodePath)
        && diskState.Favorites[1].FolderId == 0 && diskState.FavoritesHeight == 260 && !diskState.FavoritesExpanded);

    state.Favorites[0].Name = "mutated input";
    state.Favorites[0].NodePath[1] = "disk:999";
    state.Favorites.Clear();
    var loaded = MainWindowStateStore.Load(config);
    Check("saved favorites are independent of the caller's list and nested paths",
        loaded.Favorites.Count == 2 && loaded.Favorites[0].Name == "资料" && loaded.Favorites[0].NodePath[1] == "disk:10");
    loaded.Favorites[0].Name = "mutated result";
    loaded.Favorites[0].NodePath[1] = "disk:888";
    loaded.ColumnWidths[0] = 999;
    var reloaded = MainWindowStateStore.Load(config);
    Check("reopening returns a deep snapshot of favorites and column widths",
        reloaded.Favorites[0].Name == "资料" && reloaded.Favorites[0].NodePath[1] == "disk:10"
        && reloaded.ColumnWidths[0] == 300);

    foreach (var other in new[] { Config("-other"), Config(server: "https://other.invalid"), Config(root: 42) })
    {
        StatePath(other);
        Check("favorites isolated from account/server/root: " + other.Username + " / " + other.Server + " / " + other.RootContainerTypeId,
            MainWindowStateStore.Load(other).Favorites.Count == 0);
        MainWindowStateStore.Save(other, new MainWindowState { FavoritesHeight = 320 });
    }
    Check("other profiles cannot overwrite the first profile's favorites",
        MainWindowStateStore.Load(config).Favorites.Count == 2 && MainWindowStateStore.Load(config).FavoritesHeight == 260);
    var normalized = Config(server: " HTTPS://EXAMPLE.INVALID/ ");
    normalized.Username = " " + config.Username.ToUpperInvariant() + " ";
    Check("equivalent account and server spellings reopen the same favorites",
        MainWindowStateStore.Load(normalized).Favorites.Count == 2);

    var legacy = Config("-legacy");
    File.WriteAllText(StatePath(legacy), """{"NodePath":["root","root","disk:10"],"SidebarWidth":280}""");
    var legacyState = MainWindowStateStore.Load(legacy);
    Check("existing browser state upgrades without losing location or sidebar width",
        legacyState.NodePath.SequenceEqual(new[] { "root", "disk:10" }) && legacyState.SidebarWidth == 280
        && legacyState.FavoritesExpanded && legacyState.FavoritesHeight == 180 && legacyState.Favorites.Count == 0);

    var malformed = Config("-malformed");
    File.WriteAllText(StatePath(malformed), """
        {"FavoritesHeight":-50,"Favorites":[null,{"DiskId":0},{"DiskId":10,"FolderId":20,
        "Name":null,"NodePath":["root","root","disk:10","folder:20"]},{"DiskId":10,"FolderId":20}]}
        """);
    var sanitized = MainWindowStateStore.Load(malformed);
    Check("invalid entries and duplicates are filtered without losing valid favorites",
        sanitized.Favorites.Count == 1 && sanitized.Favorites[0].DiskId == 10 && sanitized.Favorites[0].Name == ""
        && sanitized.Favorites[0].NodePath.SequenceEqual(new[] { "root", "disk:10", "folder:20" })
        && sanitized.FavoritesHeight == 180);
    var corrupt = Config("-corrupt");
    File.WriteAllText(StatePath(corrupt), "{broken");
    Check("corrupt state falls back to usable defaults", MainWindowStateStore.Load(corrupt).FavoritesExpanded);

    var empty = MainWindowStateStore.Load(config);
    empty.Favorites.Clear();
    MainWindowStateStore.Save(config, empty);
    Check("removing favorites persists an empty list",
        JsonSerializer.Deserialize(File.ReadAllText(file), MainWindowStateJsonContext.Default.MainWindowState)!.Favorites.Count == 0);
    Console.WriteLine($"All {passed} browser-state checks passed.");
}
finally
{
    foreach (var file in files) File.Delete(file);
}
