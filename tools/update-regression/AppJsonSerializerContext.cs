using System.Text.Json.Serialization;
using YuWangCloudSync.Updates;

namespace YuWangCloudSync;

[JsonSerializable(typeof(PersistedUpdateState))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
