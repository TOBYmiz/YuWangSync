using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YuWangCloudSync.Core;

/// <summary>Account session credentials use the same current-user DPAPI as saved account passwords.</summary>
public sealed class AccountSessionStore(string dataDirectory)
{
    private static readonly Regex LegacyAccountFile = new(
        @"\Acookies-([0-9a-f]{20})\.json(?:\.[0-9a-f]{32}(\.tmp))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyBackup = new(
        @"\Acookies-backup-[0-9a-f]{32}\.json\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string? Load(string username, string server) => Read(AccountKey(username), new Uri(server));

    public void Save(string username, string server, string session)
        => Write(AccountKey(username), new Uri(server), session);

    public void MigratePlaintext(string activeUsername, string server, string legacyDirectory, Action? onFailure = null)
    {
        var activeKey = AccountKey(activeUsername);
        var origin = new Uri(server);
        var directories = new[] { Path.GetFullPath(dataDirectory), Path.GetFullPath(legacyDirectory) }
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();
        var temporary = new List<(string Path, string Key)>();
        var backups = new List<string>();
        var singleAccount = new List<string>();
        // Account-scoped files take precedence over interrupted writes and the single-account format.
        foreach (var directory in directories)
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                singleAccount.Add(Path.Combine(directory, "cookies.json"));
                foreach (var path in Directory.EnumerateFiles(directory, "cookies-*"))
                {
                    var name = Path.GetFileName(path);
                    if (LegacyAccountFile.Match(name) is { Success: true } match)
                    {
                        var key = match.Groups[1].Value.ToUpperInvariant();
                        if (match.Groups[2].Success) temporary.Add((path, key));
                        else Migrate(path, key);
                    }
                    else if (LegacyBackup.IsMatch(name)) backups.Add(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                onFailure?.Invoke();
            }
        }
        foreach (var (path, key) in temporary) Migrate(path, key);
        foreach (var path in singleAccount) Migrate(path, activeKey);
        // Installer backups encode only a random ID, so cannot be assigned to an account.
        foreach (var path in backups) Migrate(path, null);

        void Migrate(string path, string? key)
        {
            try
            {
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                if (key is not null && Read(key, origin) is null && ReadLegacy(path, origin.Host) is { } session)
                    Write(key, origin, session); // Write verifies the DPAPI record before deleting the only legacy copy.
                // A malformed, foreign-host or unattributed backup cannot be reused safely.
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                // Do not log the exception or the contents of a credential file.
                onFailure?.Invoke();
            }
        }
    }

    private static string AccountKey(string username) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant())))[..20];

    private string CredentialPath(string key) => Path.Combine(dataDirectory, "credentials", $"session-{key}.dpapi");

    private static string EnvelopePrefix(string key, Uri server) => $"{key}\n{server.GetLeftPart(UriPartial.Authority)}\n";

    private string? Read(string key, Uri server)
    {
        try
        {
            var path = CredentialPath(key);
            if (!File.Exists(path)) return null;
            var decrypted = DpapiSecret.Unprotect(File.ReadAllText(path));
            var prefix = EnvelopePrefix(key, server);
            if (!decrypted.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var session = decrypted[prefix.Length..];
            return IsSessionValue(session) ? session : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidDataException) { return null; }
    }

    private void Write(string key, Uri server, string session)
    {
        if (!IsSessionValue(session)) throw new InvalidDataException("服务器未返回有效会话。");
        var path = CredentialPath(key);
        ConfigurationStore.WriteAtomic(path, DpapiSecret.Protect(EnvelopePrefix(key, server) + session));
        if (!string.Equals(Read(key, server), session, StringComparison.Ordinal))
            throw new IOException("会话凭据写入后无法验证。");
    }

    private static string? ReadLegacy(string path, string host)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var cookie in doc.RootElement.EnumerateArray())
            {
                if (cookie.ValueKind != JsonValueKind.Object || GetString(cookie, "name") != "JSESSIONID"
                    || !string.Equals(GetString(cookie, "domain"), host, StringComparison.OrdinalIgnoreCase)) continue;
                if (GetString(cookie, "value")?.Trim() is { } session && IsSessionValue(session)) return session;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? GetString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSessionValue(string value) => value.Length is > 0 and <= 4096
        && value.All(c => c is > ' ' and < '\u007f' and not ';' and not ',' and not '"' and not '\\');
}
