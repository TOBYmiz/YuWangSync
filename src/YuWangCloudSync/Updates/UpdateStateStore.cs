using System.Text.Json;

namespace YuWangCloudSync.Updates;

internal sealed record PersistedUpdateState(
    DateOnly? LastSuccessfulCheckDate,
    string? LatestTagName,
    string? AssetName,
    long? AssetSize,
    string? Sha256,
    DateOnly? PublishedDate = null,
    string? ReleaseNotes = null);

internal static class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static PersistedUpdateState? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PersistedUpdateState>(File.ReadAllText(path), JsonOptions);
    }

    internal static void Write(string path, PersistedUpdateState state)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Update state file has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }
}
