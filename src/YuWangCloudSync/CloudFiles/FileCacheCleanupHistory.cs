using System.Globalization;
using System.Text.Json;

namespace YuWangCloudSync.CloudFiles;

internal sealed record FileCacheCleanupRecord(DateTimeOffset CompletedAt, long FreedBytes);

internal static class FileCacheCleanupHistory
{
    internal static FileCacheCleanupRecord? Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonSerializerContext.Default.FileCacheCleanupRecord)
        : null;

    internal static void Write(string path, FileCacheCleanupRecord record)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath,
                JsonSerializer.Serialize(record, AppJsonSerializerContext.Default.FileCacheCleanupRecord));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static string Format(FileCacheCleanupRecord? record, DateTimeOffset now)
    {
        if (record is null) return "上次清理：无记录";
        var completed = record.CompletedAt.ToLocalTime();
        var current = now.ToLocalTime();
        var culture = CultureInfo.InvariantCulture;
        var date = (completed.Year == current.Year ? "" : completed.ToString("yyyy 年 ", culture))
            + (completed.Month == current.Month ? "" : completed.ToString("MM 月 ", culture))
            + (completed.Day == current.Day ? "" : completed.ToString("dd 日 ", culture));
        return $"上次清理：{date}{completed.ToString("HH:mm", culture)} [{SyncRootProbeResult.FormatBytes(record.FreedBytes)}]";
    }
}
