using System.Text.Json.Serialization;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Core;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SyncRootContext))]
internal partial class ConfigurationJsonSerializerContext : JsonSerializerContext
{
}
