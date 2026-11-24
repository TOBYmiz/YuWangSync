using System.Text.Json.Serialization;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Models;
using YuWangCloudSync.Tray;
using YuWangCloudSync.Updates;

namespace YuWangCloudSync;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SyncRootContext))]
[JsonSerializable(typeof(PersistedUpdateState))]
[JsonSerializable(typeof(FileCacheCleanupRecord))]
[JsonSerializable(typeof(UiSelfTestCheck[]))]
[JsonSerializable(typeof(UiSelfTestReport))]
[JsonSerializable(typeof(UiSelfTestErrorReport))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}

internal sealed record UiSelfTestReport(
    int ProcessId,
    bool Passed,
    string ConfigPath,
    string CaptchaPath,
    UiSelfTestCheck[] Checks);

internal sealed record UiSelfTestErrorReport(
    int ProcessId,
    bool Passed,
    string ConfigPath,
    string CaptchaPath,
    string Error);
