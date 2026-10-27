using Microsoft.Win32;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Updates;

internal enum UpdateInstallVariant
{
    Standard,
    WinAppSdkRequired,
}

internal static class UpdateInstallVariantResolver
{
    private const string VariantValueName = "InstallVariant";
    private const string StandardValue = "standard";
    private const string RequiredValue = "winappsdk-required";
    private const string WindowsAppSdkRuntimeMarker = "Microsoft.Windows.ApplicationModel.Resources.dll";

    internal static UpdateInstallVariant Resolve()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var uninstallKey = baseKey.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductIdentity.Identifier}");
            var storedValue = uninstallKey?.GetValue(VariantValueName) as string;
            if (string.Equals(storedValue, StandardValue, StringComparison.OrdinalIgnoreCase))
                return UpdateInstallVariant.Standard;
            if (string.Equals(storedValue, RequiredValue, StringComparison.OrdinalIgnoreCase))
                return UpdateInstallVariant.WinAppSdkRequired;
        }
        catch (UnauthorizedAccessException)
        {
            // The app normally runs elevated; legacy fallback below remains safe and deterministic.
        }
        catch (System.Security.SecurityException)
        {
        }

        var runtimeMarker = Path.Combine(AppContext.BaseDirectory, WindowsAppSdkRuntimeMarker);
        return File.Exists(runtimeMarker)
            ? UpdateInstallVariant.Standard
            : UpdateInstallVariant.WinAppSdkRequired;
    }

    internal static string GetAssetName(UpdateInstallVariant variant) => variant switch
    {
        UpdateInstallVariant.Standard => ProductIdentity.Identifier + ".exe",
        UpdateInstallVariant.WinAppSdkRequired => ProductIdentity.Identifier + "_WinAppSDK_Required.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}

internal static class UpdateTemporaryFiles
{
    private const int CleanupAttempts = 30;
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(1);

    internal static string CreateTransactionDirectory()
    {
        var root = GetRootPath();
        EnsureDirectoryIsNotReparsePoint(root);
        Directory.CreateDirectory(root);
        EnsureDirectoryIsNotReparsePoint(root);

        var transaction = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        EnsureDirectoryIsNotReparsePoint(transaction);
        return transaction;
    }

    internal static async Task CleanupAfterUpdateAsync()
    {
        var root = GetRootPath();
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                EnsureDirectoryIsNotReparsePoint(root);
                DeleteDirectoryTreeWithoutFollowingReparsePoints(root);
                Serilog.Log.Information("已清理更新临时目录：{Path}", root);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == CleanupAttempts)
                {
                    Serilog.Log.Warning(error, "重试后仍无法清理更新临时目录：{Path}", root);
                    return;
                }

                await Task.Delay(CleanupRetryDelay).ConfigureAwait(false);
            }
        }
    }

    internal static void TryDeleteTransactionDirectory(string path)
    {
        try
        {
            var root = GetRootPath();
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("update-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete an update directory outside the product temp folder.");
            if (Directory.Exists(fullPath))
            {
                EnsureDirectoryIsNotReparsePoint(fullPath);
                DeleteDirectoryTreeWithoutFollowingReparsePoints(fullPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(error, "无法清理失败的更新事务目录：{Path}", path);
        }
    }

    private static string GetRootPath()
    {
        var englishName = ProductIdentity.DisplayNameEn;
        if (string.IsNullOrWhiteSpace(englishName)
            || englishName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(englishName), englishName, StringComparison.Ordinal))
            throw new InvalidOperationException("产品英文显示名不能用作安全的更新临时目录名。");

        var tempPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(tempPath, englishName));
        if (!string.Equals(Path.GetDirectoryName(root)?.TrimEnd(Path.DirectorySeparatorChar), tempPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(root), englishName, StringComparison.Ordinal))
            throw new InvalidOperationException("更新临时目录不在系统临时目录的直接子目录中。");
        return root;
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"更新临时目录包含重解析点，拒绝访问：{path}");
    }

    private static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string path)
    {
        EnsureDirectoryIsNotReparsePoint(path);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            DeleteEntryWithoutFollowingReparsePoints(entry);
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteEntryWithoutFollowingReparsePoints(FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (entry is DirectoryInfo)
                Directory.Delete(entry.FullName, recursive: false);
            else
                File.Delete(entry.FullName);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            DeleteDirectoryTreeWithoutFollowingReparsePoints(entry.FullName);
            return;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
            entry.Attributes = attributes & ~FileAttributes.ReadOnly;
        File.Delete(entry.FullName);
    }
}
