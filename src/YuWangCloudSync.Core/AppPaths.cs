namespace YuWangCloudSync.Core;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                    localAppData = Path.Combine(userProfile, "AppData", "Local");
            }

            if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
                throw new InvalidOperationException("无法解析当前用户的 LocalAppData 目录，无法打开应用数据目录");

            return Path.GetFullPath(Path.Combine(localAppData, ProductIdentity.UserDataDirectoryName));
        }
    }

    public static string ConfigPath => Path.Combine(DataDirectory, "appsettings.json");

    public static string ResolveDataPath(string path) => Path.GetFullPath(path, DataDirectory);

    public static string MigrateDataFile(string name, string legacyDirectory)
    {
        Directory.CreateDirectory(DataDirectory);
        var destination = ResolveDataPath(name);
        var source = Path.GetFullPath(name, legacyDirectory);
        if (!File.Exists(destination) && File.Exists(source)
            && !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }
        return destination;
    }
}
