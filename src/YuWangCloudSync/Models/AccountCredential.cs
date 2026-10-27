namespace YuWangCloudSync.Models;

/// <summary>Write capability observed for one account and one cloud directory.</summary>
public enum DirectoryPermission
{
    Unknown = 0,
    ReadOnly = 1,
    FullAccess = 2,
}
