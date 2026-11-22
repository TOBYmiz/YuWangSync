using YuWangCloudSync.Core;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Clears only the explicitly selected sync root; never follows foreign directory links.</summary>
internal static class AccountSwitchCleanup
{
    internal sealed record Result(long Deleted, long Failed, string[] Errors);

    internal static Result Run(string syncRoot, Action<long, long> progress, Action<string> log)
    {
        var root = Path.GetFullPath(syncRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length <= 3 || string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("拒绝清理盘符根目录。");
        if (!Directory.Exists(root)) return new(0, 0, []);
        // This guard is intentionally checked before *any* recursive enumeration or deletion.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 && !ProviderIdentity.OwnsSyncRootPlaceholder(root))
            throw new IOException("同步根目录是未确认归属的重解析目录，已停止清理。");
        long deleted = 0, failed = 0;
        var errors = new List<string>();
        void Failed(string path, Exception error)
        {
            failed++;
            if (errors.Count < 5) errors.Add($"{Path.GetFileName(path)}：{error.Message}");
            log($"账户切换清理失败：{path}；{error.Message}");
        }
        void Visit(DirectoryInfo directory)
        {
            FileSystemInfo[] entries;
            try { entries = directory.GetFileSystemInfos(); }
            catch (Exception error) { Failed(directory.FullName, error); return; }
            foreach (var entry in entries)
            {
                var disabledPopulation = false;
                try
                {
                    var attributes = entry.Attributes; // Reuse enumeration metadata instead of reopening every file.
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var reparse = (attributes & FileAttributes.ReparsePoint) != 0;
                        var owned = reparse && ProviderIdentity.OwnsPlaceholder(entry.FullName);
                        if (!reparse || owned)
                        {
                            // Cleaning an unexpanded cloud folder must not fetch its remote children.
                            if (owned) disabledPopulation = PlaceholderUpdater.TryDisableOnDemandPopulation(entry.FullName, log);
                            Visit(new DirectoryInfo(entry.FullName));
                        }
                        if ((attributes & FileAttributes.ReadOnly) != 0 && (!reparse || owned))
                            File.SetAttributes(entry.FullName, attributes & ~FileAttributes.ReadOnly);
                        try { Directory.Delete(entry.FullName, recursive: false); }
                        catch when (owned)
                        {
                            if (!PlaceholderUpdater.DeletePlaceholderSafe(entry.FullName, true, log)) throw;
                        }
                    }
                    else
                    {
                        // Ordinary deletion works for connected placeholders and avoids a per-file
                        // CfGetPlaceholderInfo identity query plus an existence check on the success path.
                        try
                        {
                            if ((attributes & FileAttributes.ReadOnly) != 0
                                && ((attributes & FileAttributes.ReparsePoint) == 0 || ProviderIdentity.OwnsPlaceholder(entry.FullName)))
                                File.SetAttributes(entry.FullName, attributes & ~FileAttributes.ReadOnly);
                            File.Delete(entry.FullName);
                        }
                        catch
                        {
                            if (!ProviderIdentity.OwnsPlaceholder(entry.FullName)
                                || !PlaceholderUpdater.DeletePlaceholderSafe(entry.FullName, false, log)) throw;
                        }
                    }
                    deleted++;
                }
                catch (Exception error)
                {
                    // Restore on-demand enumeration for a surviving folder if the switch aborts.
                    if (disabledPopulation) PlaceholderUpdater.TryEnableOnDemandPopulation(entry.FullName, log);
                    Failed(entry.FullName, error);
                }
                progress(deleted, failed);
            }
        }
        Visit(new DirectoryInfo(root));
        try
        {
            if (failed == 0 && Directory.EnumerateFileSystemEntries(root).Any())
                Failed(root, new IOException("目录中仍有残留项目，请关闭正在使用同步目录的程序后重试。"));
        }
        catch (Exception error) { Failed(root, error); }
        progress(deleted, failed);
        return new(deleted, failed, errors.ToArray());
    }
}
