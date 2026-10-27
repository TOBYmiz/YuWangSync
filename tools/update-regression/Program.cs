using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YuWangCloudSync.Updates;

var passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

try
{
Check("four component release tag accepted",
    GitHubReleaseClient.TryParseReleaseVersion("v1.2.3.4", out var parsedVersion)
    && parsedVersion == new Version(1, 2, 3, 4));
Check("short, missing prefix, and prerelease tags rejected",
    !GitHubReleaseClient.TryParseReleaseVersion("v1.2.3", out _)
    && !GitHubReleaseClient.TryParseReleaseVersion("1.2.3.4", out _)
    && !GitHubReleaseClient.TryParseReleaseVersion("v1.2.3.4-beta", out _));

var today = new DateOnly(2026, 10, 26);
Check("check schedule is once per local date",
    !UpdateSchedule.IsDue(today, today) && UpdateSchedule.IsDue(today.AddDays(-1), today));
Check("only numerically newer four-part versions count as updates",
    UpdateSchedule.IsNewerVersion(new Version(1, 0, 0, 1), "1.0.0.0")
    && !UpdateSchedule.IsNewerVersion(new Version(1, 0, 0, 0), "1.0.0.0")
    && !UpdateSchedule.IsNewerVersion(new Version(1, 0, 0, 0), "1.0.0.1"));
Check("failed check can retry because only successful dates are recorded",
    UpdateSchedule.IsDue(null, today));
Check("next local day delay is calculated from local wall clock",
    UpdateSchedule.UntilNextLocalDay(new DateTime(2026, 10, 26, 23, 30, 0)) == TimeSpan.FromMinutes(30));

var statePath = Path.Combine(AppContext.BaseDirectory, "state-test-" + Guid.NewGuid().ToString("N"), "update-state.json");
try
{
    var savedState = new PersistedUpdateState(today, "v1.2.3.4", "YuWangCloudSync.exe", 1024, new string('a', 64));
    UpdateStateStore.Write(statePath, savedState);
    Check("update state round-trips successful check date and release metadata",
        UpdateStateStore.Read(statePath) == savedState);
}
finally
{
    var stateDirectory = Path.GetDirectoryName(statePath)!;
    if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
}

var assetBytes = Encoding.UTF8.GetBytes("verified installer payload");
var digest = Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant();
var releaseJson = JsonSerializer.Serialize(new
{
    tag_name = "v1.2.3.4",
    assets = new[]
    {
        new { name = "YuWangCloudSync.exe", size = assetBytes.Length, digest = "sha256:" + digest },
        new { name = "YuWangCloudSync_WinAppSDK_Required.exe", size = assetBytes.Length, digest = "sha256:" + digest },
    },
});
var release = GitHubReleaseClient.ParseRelease(releaseJson);
Check("latest release retains both install variant assets and digest",
    release.Assets.Count == 2 && release.Assets.All(asset => asset.Sha256 == digest));

var callOrder = new List<string>();
using (var http = new HttpClient(new StubHandler(request =>
       {
           callOrder.Add(request.RequestUri!.Host);
           if (request.RequestUri.Host == "v6.gh-proxy.org")
               return new HttpResponseMessage(HttpStatusCode.BadGateway);
           return JsonResponse(releaseJson);
       })) { Timeout = Timeout.InfiniteTimeSpan })
{
    var client = new GitHubReleaseClient(http, "YuWangCloudSync/1.0.0.0");
    var latest = await client.GetLatestReleaseAsync("YuWangCloudSync.exe", CancellationToken.None);
    Check("API uses proxy first then official fallback", latest?.TagName == "v1.2.3.4"
        && callOrder.SequenceEqual(new[] { "v6.gh-proxy.org", "api.github.com" }));
}

using (var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
       { Timeout = Timeout.InfiniteTimeSpan })
{
    var client = new GitHubReleaseClient(http, "YuWangCloudSync/1.0.0.0");
    Check("404 from proxy and official means no published release",
        await client.GetLatestReleaseAsync("YuWangCloudSync.exe", CancellationToken.None) is null);
}

var assetMetadata = release.Assets[0];
var badAssetBytes = (byte[])assetBytes.Clone();
badAssetBytes[0] ^= 0x01;
var downloadOrder = new List<string>();
var progressValues = new List<DownloadProgress>();
var tempRoot = Path.Combine(AppContext.BaseDirectory, "test-temp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var destination = Path.Combine(tempRoot, assetMetadata.Name);
    using var http = new HttpClient(new StubHandler(request =>
    {
        downloadOrder.Add(request.RequestUri!.Host);
        if (request.RequestUri.Host == "v6.gh-proxy.org")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(badAssetBytes) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assetBytes) };
    })) { Timeout = Timeout.InfiniteTimeSpan };
    var client = new GitHubReleaseClient(http, "YuWangCloudSync/1.0.0.0");
    await client.DownloadAndVerifyAsync(assetMetadata, destination,
        new InlineProgress<DownloadProgress>(progressValues.Add), CancellationToken.None);
    Check("digest mismatch retries official asset and writes verified bytes",
        File.ReadAllBytes(destination).SequenceEqual(assetBytes)
        && downloadOrder.SequenceEqual(new[] { "v6.gh-proxy.org", "github.com" }));
    Check("download progress reports final byte count", progressValues.Last().BytesRead == assetBytes.Length
        && progressValues.Last().TotalBytes == assetBytes.Length);
}
finally
{
    Directory.Delete(tempRoot, recursive: true);
}

var missingDigest = assetMetadata with { Sha256 = null };
using (var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected HTTP request")))
       { Timeout = Timeout.InfiniteTimeSpan })
{
    var client = new GitHubReleaseClient(http, "YuWangCloudSync/1.0.0.0");
    try
    {
        await client.DownloadAndVerifyAsync(missingDigest, Path.Combine(AppContext.BaseDirectory, "missing-digest.exe"),
            null, CancellationToken.None);
        throw new InvalidOperationException("Missing digest unexpectedly allowed download.");
    }
    catch (InvalidDataException)
    {
        Check("missing digest blocks download before network access", true);
    }
}

Console.WriteLine($"All {passed} update regression checks passed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
};

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(handler(request));
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
