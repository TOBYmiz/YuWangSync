using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Tray;

internal sealed class MainWindowState
{
    public string[] NodePath { get; set; } = [];
    public double SidebarWidth { get; set; } = 264;
    public double[] ColumnWidths { get; set; } = [];
}

[JsonSerializable(typeof(MainWindowState))]
[JsonSerializable(typeof(string[]))]
internal partial class MainWindowStateJsonContext : JsonSerializerContext { }

internal static class MainWindowStateStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MainWindowState> SessionStates = new(StringComparer.OrdinalIgnoreCase);

    internal static string[] NormalizeNodePath(string[]? path)
    {
        if (path is not { Length: > 0 } || path[0] != "root") return [];
        // Older versions included TreeView's hidden root before the visible cloud root.
        var start = 0;
        while (start + 1 < path.Length && path[start + 1] == "root") start++;
        return path[start..];
    }

    private static MainWindowState Copy(MainWindowState state) => new()
    {
        NodePath = NormalizeNodePath(state.NodePath),
        SidebarWidth = state.SidebarWidth,
        ColumnWidths = state.ColumnWidths is { } widths ? [.. widths] : [],
    };

    private static string GetPath(SyncRootContext config)
    {
        // Keep accounts, servers and disk-tree entry points isolated without storing account names.
        var key = JsonSerializer.Serialize(new[] { config.Server.Trim().TrimEnd('/').ToUpperInvariant(),
            config.Username.Trim().ToUpperInvariant(), config.RootContainerTypeId.ToString() },
            MainWindowStateJsonContext.Default.StringArray);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.DataDirectory, "browser-state", digest + ".json");
    }

    internal static MainWindowState Load(SyncRootContext config)
    {
        try
        {
            var path = GetPath(config);
            lock (Gate)
                if (SessionStates.TryGetValue(path, out var previous)) return Copy(previous);
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), MainWindowStateJsonContext.Default.MainWindowState)
                    ?? new MainWindowState()
                : new MainWindowState();
            state = Copy(state);
            lock (Gate) SessionStates[path] = state;
            return Copy(state);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "读取主界面浏览位置失败，将使用默认位置");
            return new MainWindowState();
        }
    }

    internal static void Save(SyncRootContext config, MainWindowState state)
    {
        string? temporary = null;
        try
        {
            var path = GetPath(config);
            var snapshot = Copy(state);
            // Keep reopening in the same process independent of a failed disk write.
            lock (Gate) SessionStates[path] = snapshot;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot,
                MainWindowStateJsonContext.Default.MainWindowState));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) { Serilog.Log.Warning(error, "保存主界面浏览位置失败"); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }
}
