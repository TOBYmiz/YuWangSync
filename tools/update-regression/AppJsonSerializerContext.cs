using System.Text.Json.Serialization;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Updates;

namespace YuWangCloudSync;

[JsonSerializable(typeof(PersistedUpdateState))]
[JsonSerializable(typeof(FileCacheCleanupRecord))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
