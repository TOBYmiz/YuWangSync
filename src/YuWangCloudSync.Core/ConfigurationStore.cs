using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Core;

public static class ConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly ConfigurationJsonSerializerContext SerializerContext = new(Options);

    public static SyncRootContext Load(string? legacyDirectory = null, bool decryptPassword = true,
        string? configurationPath = null, bool migrate = true)
    {
        var targetPath = configurationPath ?? AppPaths.ConfigPath;
        var legacyPath = legacyDirectory == null ? null : Path.Combine(legacyDirectory, "appsettings.json");
        var source = File.Exists(targetPath) ? targetPath : legacyPath;
        if (source == null || !File.Exists(source))
            throw new FileNotFoundException("请重装应用配置服务器、账号和同步目录。", targetPath);

        var json = File.ReadAllText(source);
        var config = JsonSerializer.Deserialize(json, SerializerContext.SyncRootContext)
            ?? throw new InvalidDataException("配置文件为空。");
        var document = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("配置文件必须为 JSON 对象。");
        var hasLegacyProviderName = config.ProviderName is "育网云" or "育网云同步";
        if (hasLegacyProviderName) config.ProviderName = ProductIdentity.SyncRootDefaultName;
        var hasProviderVersionSetting = ContainsProperty(document, "ProviderVersion");
        var hasProtectedAccountNames = ReadBooleanProperty(document, "AccountNamesProtected");
        var hasUnifiedCache = ContainsProperty(document, "FolderCacheMinutes");
        var hasAccounts = ContainsProperty(document, "Accounts");
        var hasLegacyCache = ContainsProperty(document, "FolderListCacheMinutes")
            || ContainsProperty(document, "FolderRefreshCacheMinutes");
        if (!hasUnifiedCache && hasLegacyCache)
        {
            // Preserve the former effective freshness window (both gates had to expire).
            // If a legacy file omitted one field, use the old model default for that field.
            var hasLegacyList = ContainsProperty(document, "FolderListCacheMinutes");
            var hasLegacyRefresh = ContainsProperty(document, "FolderRefreshCacheMinutes");
            var listMinutes = ReadIntProperty(document, "FolderListCacheMinutes") ?? (hasLegacyList ? 0 : 10);
            var refreshMinutes = ReadIntProperty(document, "FolderRefreshCacheMinutes") ?? (hasLegacyRefresh ? 0 : 60);
            config.FolderCacheMinutes = Math.Max(
                NormalizeLegacyCacheMinutes(listMinutes),
                NormalizeLegacyCacheMinutes(refreshMinutes));
        }
        var passwordKey = document.Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals("Password", StringComparison.OrdinalIgnoreCase));
        var plaintext = passwordKey == null ? "" : document[passwordKey]?.GetValue<string>() ?? "";
        if (hasProtectedAccountNames)
        {
            try { UnprotectAccountNames(config); }
            catch (Exception error)
            {
                throw new InvalidDataException("无法解密配置中的账号名，请使用原 Windows 用户配置或重新添加账户。", error);
            }
        }
        config.Accounts ??= [];
        var activeAccount = string.IsNullOrWhiteSpace(config.ActiveAccount)
            ? config.Username : config.ActiveAccount;
        if (config.Accounts.Count == 0 && !string.IsNullOrWhiteSpace(config.Username))
        {
            config.Accounts.Add(new AccountCredential
            {
                Username = config.Username,
                ProtectedPassword = config.ProtectedPassword
            });
        }
        var activeCredential = config.Accounts.FirstOrDefault(account =>
            account.Username.Equals(activeAccount, StringComparison.OrdinalIgnoreCase));
        if (activeCredential is null && !string.IsNullOrWhiteSpace(config.Username))
        {
            activeCredential = new AccountCredential
            {
                Username = config.Username,
                ProtectedPassword = config.ProtectedPassword
            };
            config.Accounts.Add(activeCredential);
            activeAccount = config.Username;
        }
        config.ActiveAccount = activeAccount;
        if (activeCredential is not null)
        {
            config.Username = activeCredential.Username;
            config.ProtectedPassword = activeCredential.ProtectedPassword;
        }
        if (decryptPassword)
        {
            try
            {
                var protectedPassword = activeCredential?.ProtectedPassword ?? config.ProtectedPassword;
                config.Password = string.IsNullOrWhiteSpace(protectedPassword)
                    ? plaintext : DpapiSecret.Unprotect(protectedPassword);
            }
            catch (Exception error)
            {
                throw new InvalidDataException("无法解密用户密码，请重新安装应用。", error);
            }
        }

        if (migrate && (!string.Equals(source, targetPath, StringComparison.OrdinalIgnoreCase)
                        || passwordKey != null || hasLegacyCache || !hasAccounts
                        || hasProviderVersionSetting || hasLegacyProviderName || !hasProtectedAccountNames))
        {
            if (activeCredential is not null && string.IsNullOrWhiteSpace(activeCredential.ProtectedPassword))
                activeCredential.ProtectedPassword = DpapiSecret.Protect(plaintext);
            if (activeCredential is not null)
                config.ProtectedPassword = activeCredential.ProtectedPassword;
            SaveProtected(config, targetPath);
            RemoveLegacyPassword(source);
        }
        return config;
    }

    private static bool ContainsProperty(JsonObject document, string name) =>
        document.Any(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ReadBooleanProperty(JsonObject document, string name)
    {
        var key = FindPropertyKey(document, name);
        return key is not null && document[key] is JsonValue value
            && value.TryGetValue<bool>(out var result) && result;
    }

    private static string? FindPropertyKey(JsonObject document, string name) =>
        document.Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void UnprotectAccountNames(SyncRootContext config)
    {
        config.Username = UnprotectAccountName(config.Username);
        config.ActiveAccount = UnprotectAccountName(config.ActiveAccount);
        foreach (var account in config.Accounts ?? [])
            account.Username = UnprotectAccountName(account.Username);
    }

    private static string UnprotectAccountName(string value) =>
        string.IsNullOrWhiteSpace(value) ? value : DpapiSecret.Unprotect(value);

    private static void ProtectAccountNames(JsonObject document)
    {
        if (ReadBooleanProperty(document, "AccountNamesProtected")) return;

        ProtectAccountNameProperty(document, "Username");
        ProtectAccountNameProperty(document, "ActiveAccount");
        var accountsKey = FindPropertyKey(document, "Accounts");
        if (accountsKey is not null && document[accountsKey] is JsonArray accounts)
        {
            foreach (var account in accounts.OfType<JsonObject>())
                ProtectAccountNameProperty(account, "Username");
        }

        var markerKey = FindPropertyKey(document, "AccountNamesProtected") ?? "AccountNamesProtected";
        document[markerKey] = true;
    }

    private static void ProtectAccountNameProperty(JsonObject document, string name)
    {
        var key = FindPropertyKey(document, name);
        if (key is null || document[key] is not JsonValue value
            || !value.TryGetValue<string>(out var username)
            || string.IsNullOrWhiteSpace(username))
            return;

        document[key] = DpapiSecret.Protect(username);
    }

    private static int? ReadIntProperty(JsonObject document, string name)
    {
        foreach (var entry in document)
            if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && entry.Value is JsonValue value
                && value.TryGetValue<int>(out var result))
                return result;
        return null;
    }

    private static int NormalizeLegacyCacheMinutes(int minutes) =>
        minutes is >= 1 and <= 120 ? minutes : 30;

    public static void Save(SyncRootContext config, string? configurationPath = null)
    {
        config.ProtectedPassword = DpapiSecret.Protect(config.Password);
        config.ActiveAccount = string.IsNullOrWhiteSpace(config.ActiveAccount) ? config.Username : config.ActiveAccount;
        config.Accounts ??= [];
        var active = config.Accounts.FirstOrDefault(account =>
            account.Username.Equals(config.ActiveAccount, StringComparison.OrdinalIgnoreCase));
        if (active is null)
        {
            active = new AccountCredential { Username = config.ActiveAccount };
            config.Accounts.Add(active);
        }
        active.Username = config.Username;
        active.ProtectedPassword = config.ProtectedPassword;
        SaveProtected(config, configurationPath ?? AppPaths.ConfigPath);
    }

    public static void SaveEncrypted(SyncRootContext config, string? configurationPath = null)
    {
        if (string.IsNullOrWhiteSpace(config.ProtectedPassword))
            throw new InvalidDataException("缺少加密凭据。");
        config.ActiveAccount = string.IsNullOrWhiteSpace(config.ActiveAccount) ? config.Username : config.ActiveAccount;
        config.Accounts ??= [];
        var active = config.Accounts.FirstOrDefault(account =>
            account.Username.Equals(config.ActiveAccount, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            config.Accounts.Add(new AccountCredential { Username = config.Username, ProtectedPassword = config.ProtectedPassword });
        else
            active.ProtectedPassword = config.ProtectedPassword;
        SaveProtected(config, configurationPath ?? AppPaths.ConfigPath);
    }

    public static void RemoveLegacyPassword(string path)
    {
        if (!File.Exists(path)) return;
        var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("旧配置文件必须为 JSON 对象。");
        foreach (var key in document.Select(entry => entry.Key)
                     .Where(key => key.Equals("Password", StringComparison.OrdinalIgnoreCase)).ToArray())
            document.Remove(key);
        ProtectAccountNames(document);
        WriteAtomic(path, document.ToJsonString(Options));
    }

    private static void SaveProtected(SyncRootContext config, string path)
    {
        var document = JsonSerializer.SerializeToNode(config, SerializerContext.SyncRootContext) as JsonObject
            ?? throw new InvalidDataException("配置无法序列化为 JSON 对象。");
        ProtectAccountNames(document);
        WriteAtomic(path, document.ToJsonString(Options));
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
