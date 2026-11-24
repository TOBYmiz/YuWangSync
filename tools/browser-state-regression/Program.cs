using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

if (args.Contains("--account-sessions"))
{
    var root = Directory.CreateTempSubdirectory("yuwang-session-regression-").FullName;
    var legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
    const string server = "https://example.invalid";
    const string current = "synthetic-current-session+123";
    const string other = "synthetic-other-session+456";
    const string stale = "synthetic-old-session";
    static string LegacyJson(string value) => "[{\"name\":\"JSESSIONID\",\"value\":\"" + value
        + "\",\"domain\":\"example.invalid\",\"path\":\"/\"}]";
    static string Key(string user) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..20];
    try
    {
        File.WriteAllText(Path.Combine(root, $"cookies-{Key("first")}.json"), LegacyJson(current));
        File.WriteAllText(Path.Combine(root, $"cookies-{Key("second")}.json"), LegacyJson(other));
        var suffix = $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(Path.Combine(root, $"cookies-{Key("first")}.json{suffix}"), LegacyJson(stale));
        File.WriteAllText(Path.Combine(root, $"cookies-{Key("recovered")}.json{suffix}"), LegacyJson(other));
        File.WriteAllText(Path.Combine(legacy, $"cookies-backup-{Guid.NewGuid():N}.json"), LegacyJson(stale));
        File.WriteAllText(Path.Combine(legacy, "cookies.json"), LegacyJson(stale));
        var unrelated = Path.Combine(root, "cookies-unrelated.json.manual.tmp");
        File.WriteAllText(unrelated, "unrelated");
        var store = new AccountSessionStore(root);
        var failures = 0;
        store.MigratePlaintext("first", server, legacy, () => failures++);
        Check("legacy per-account sessions migrate, scoped file beats single-account file",
            failures == 0 && store.Load(" FIRST ", server) == current && store.Load("second", server) == other);
        Check("interrupted account write can be recovered; orphan backup is discarded",
            store.Load("recovered", server) == other
            && Directory.EnumerateFiles(root, "cookies*", SearchOption.AllDirectories).SequenceEqual([unrelated]));
        var firstPath = Path.Combine(root, "credentials", $"session-{Key("first")}.dpapi");
        var encrypted = File.ReadAllText(firstPath);
        Check("credential uses current-user DPAPI with no plaintext cookie on disk",
            !encrypted.Contains(current, StringComparison.Ordinal) && DpapiSecret.Unprotect(encrypted).EndsWith(current, StringComparison.Ordinal));
        Check("session cannot be loaded for another account or server origin", store.Load("third", server) is null
            && store.Load("first", "http://example.invalid") is null && store.Load("first", "https://other.invalid") is null);
        store.Save("first", server, stale);
        Check("new login atomically updates only the selected account", store.Load("first", server) == stale && store.Load("second", server) == other);
        File.WriteAllText(firstPath, "damaged");
        Check("damaged credential is ignored, never treated as plaintext", store.Load("first", server) is null);
    }
    finally { Directory.Delete(root, recursive: true); }
    return;
}

if (Array.IndexOf(args, "--setup-session") is var setupIndex && setupIndex >= 0)
{
    var script = Path.GetFullPath(args[setupIndex + 1]);
    var root = Directory.CreateTempSubdirectory("yuwang-setup-session- (+)-").FullName;
    var input = Path.Combine(root, "input.txt");
    var handoff = Path.Combine(root, "handoff.txt");
    var image = Path.Combine(root, "captcha.bmp");
    const string username = "Fixture+Account";
    const string password = "synthetic-password";
    const string initial = "synthetic-captcha+1";
    const string rotated = "synthetic-login+2";
    var allocate = new TcpListener(IPAddress.Loopback, 0);
    allocate.Start();
    var port = ((IPEndPoint)allocate.LocalEndpoint).Port;
    allocate.Stop();
    var server = $"http://127.0.0.1:{port}";
    using var http = new HttpListener();
    http.Prefixes.Add(server + "/");
    try
    {
        http.Start();
        var serverTask = ServeAsync();
        await RunHelperAsync("GetCaptcha", 0, server);
        Check("installer captcha handoff has only a public server line and protected cookie data",
            ReadHandoff(initial, string.Empty) && File.Exists(image));
        await RunHelperAsync("SaveSession", 42, username, root);
        await RunHelperAsync("Login", 0, server, username, password, "1234");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Check("installer login rotates the cookie and binds the protected handoff to the verified account",
            ReadHandoff(rotated, username.ToLowerInvariant()));
        await RunHelperAsync("SaveSession", 42, "another-account", root);
        var store = new AccountSessionStore(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.ToLowerInvariant())))[..20];
        var scoped = Path.Combine(root, $"cookies-{key}.json");
        var single = Path.Combine(root, "cookies.json");
        File.WriteAllText(scoped, "synthetic legacy data");
        File.WriteAllText(single, "synthetic legacy data");
        await RunHelperAsync("SaveSession", 0, username, root);
        Check("installer credential loads through the application and old plaintext is removed",
            store.Load(username, server) == rotated && !File.Exists(scoped) && !File.Exists(single)
            && store.Load(username, server.Replace("http:", "https:")) is null);
        store.Save(username, server, initial);
        await RunHelperAsync("SaveSession", 0, username, root);
        Check("installer atomically replaces an existing DPAPI credential without a backup or plaintext artifact",
            store.Load(username, server) == rotated && !File.Exists(input)
            && Directory.EnumerateFiles(Path.Combine(root, "credentials")).Count() == 1
            && !File.ReadAllText(Path.Combine(root, "credentials", $"session-{key}.dpapi")).Contains(rotated));
    }
    finally
    {
        http.Stop();
        Directory.Delete(root, recursive: true);
    }
    return;

    bool ReadHandoff(string token, string account)
    {
        var lines = File.ReadAllLines(handoff, Encoding.Unicode);
        return lines.Length == 2 && lines[0] == server && !lines[1].Contains(token)
            && DpapiSecret.Unprotect(lines[1]).StartsWith($"{server}\n{account}\n{token}\nJSESSIONID={token}", StringComparison.Ordinal);
    }

    async Task RunHelperAsync(string operation, int expected, params string[] lines)
    {
        File.WriteAllLines(input, lines, Encoding.Unicode);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-Operation", operation, "-InputPath", input, "-SessionPath", handoff, "-ImagePath", image, "-TargetHeight", "20" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var transcript = (await output) + (await errors);
        Check($"{operation} returns {expected} without exposing session data (actual {process.ExitCode})",
            process.ExitCode == expected && !transcript.Contains(initial) && !transcript.Contains(rotated));
    }

    async Task ServeAsync()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+cW9kAAAAASUVORK5CYII=");
        for (var i = 0; i < 3; i++)
        {
            var context = await http.GetContextAsync();
            using var response = context.Response;
            var path = context.Request.Url?.AbsolutePath;
            byte[] bytes;
            if (i == 0 && path == "/welcome.jsx")
            {
                response.Headers.Add("Set-Cookie", $"JSESSIONID={initial}; Path=/");
                bytes = [];
            }
            else if (i == 1 && path == "/servlet/VerificationCode"
                && context.Request.Headers["Cookie"]?.Contains(initial) == true)
            {
                response.ContentType = "image/png";
                bytes = png;
            }
            else if (i == 2 && path == "/main.jsx" && context.Request.HttpMethod == "POST"
                && context.Request.Headers["Cookie"]?.Contains(initial) == true)
            {
                await context.Request.InputStream.CopyToAsync(Stream.Null);
                response.Headers.Add("Set-Cookie", $"JSESSIONID={rotated}; Path=/");
                bytes = "synthetic login succeeded"u8.ToArray();
            }
            else { response.StatusCode = 400; bytes = []; }
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
    }
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
