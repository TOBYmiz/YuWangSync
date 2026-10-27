namespace YuWangCloudSync.Models;

public sealed record PlaceholderRestoreInfo(
    string ParentPath,
    string Name,
    bool IsFolder,
    long? Size,
    DateTime? UploadTime);
