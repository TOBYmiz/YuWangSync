using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace YuWangCloudSync.Updates;

internal sealed record GitHubReleaseAsset(string Name, long Size, string? Sha256);

internal sealed record GitHubRelease(
    string TagName,
    Version Version,
    DateOnly? PublishedDate,
    string Body,
    IReadOnlyList<GitHubReleaseAsset> Assets);

internal readonly record struct DownloadProgress(long BytesRead, long TotalBytes);

internal sealed class GitHubReleaseClient(HttpClient httpClient, string userAgent)
{
    private const string ApiProxy = "https://v6.gh-proxy.org/https://api.github.com/repos/TOBYmiz/YuWangSync/releases/latest";
    private const string ApiOfficial = "https://api.github.com/repos/TOBYmiz/YuWangSync/releases/latest";
    private const string DownloadProxyPrefix = "https://v6.gh-proxy.org/https://github.com/TOBYmiz/YuWangSync/releases/latest/download/";
    private const string DownloadOfficialPrefix = "https://github.com/TOBYmiz/YuWangSync/releases/latest/download/";
    private static readonly Uri[] ApiUris = [new(ApiProxy), new(ApiOfficial)];

    internal async Task<GitHubRelease?> GetLatestReleaseAsync(string requiredAssetName, CancellationToken cancellationToken)
    {
        var notFoundCount = 0;
        Exception? lastError = null;
        foreach (var uri in ApiUris)
        {
            try
            {
                using var endpointTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                endpointTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(userAgent);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                using var response = await httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, endpointTimeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (string.Equals(uri.AbsoluteUri, ApiOfficial, StringComparison.Ordinal))
                        return null;
                    notFoundCount++;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(endpointTimeout.Token).ConfigureAwait(false);
                var release = await ParseReleaseAsync(stream, endpointTimeout.Token).ConfigureAwait(false);
                var requiredAsset = release.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.Name, requiredAssetName, StringComparison.Ordinal));
                if (requiredAsset is null || requiredAsset.Size <= 0
                    || !TryNormalizeDigest(requiredAsset.Sha256, out _))
                {
                    if (string.Equals(uri.AbsoluteUri, ApiOfficial, StringComparison.Ordinal))
                        return release;
                    lastError = new InvalidDataException(
                        $"Release {release.TagName} 缺少资产 {requiredAssetName} 或其 SHA-256 digest 无效。");
                    continue;
                }
                return release;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                lastError = error;
            }
        }

        if (notFoundCount == ApiUris.Length && lastError is null)
            return null;

        throw new HttpRequestException("无法从代理或 GitHub 官方接口读取最新 Release。", lastError);
    }

    internal async Task DownloadAndVerifyAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || !TryNormalizeDigest(asset.Sha256, out var expectedDigest))
            throw new InvalidDataException("Release 资产缺少有效的 SHA-256 digest 或文件大小。");
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new InvalidDataException("更新包目标路径必须是绝对路径。");

        var escapedName = Uri.EscapeDataString(asset.Name);
        var downloadUris = new[]
        {
            new Uri(DownloadProxyPrefix + escapedName),
            new Uri(DownloadOfficialPrefix + escapedName),
        };
        Exception? lastError = null;
        foreach (var uri in downloadUris)
        {
            var partialPath = destinationPath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using var endpointTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                endpointTimeout.CancelAfter(TimeSpan.FromMinutes(20));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(userAgent);
                using var response = await httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, endpointTimeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(endpointTimeout.Token).ConfigureAwait(false);
                await using var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long totalBytes = 0;
                var lastReportedPercent = -1;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, endpointTimeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    totalBytes += read;
                    if (totalBytes > asset.Size)
                        throw new InvalidDataException("下载文件大于 Release 声明的大小。");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), endpointTimeout.Token).ConfigureAwait(false);
                    var percent = (int)Math.Min(100, totalBytes * 100 / asset.Size);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(new DownloadProgress(totalBytes, asset.Size));
                    }
                }
                await destination.FlushAsync(endpointTimeout.Token).ConfigureAwait(false);

                if (totalBytes != asset.Size)
                    throw new InvalidDataException($"下载大小不匹配：期望 {asset.Size} 字节，实际 {totalBytes} 字节。");
                var actualDigest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualDigest), Convert.FromHexString(expectedDigest)))
                    throw new InvalidDataException("下载文件的 SHA-256 与 Release digest 不匹配。");

                await destination.DisposeAsync().ConfigureAwait(false);
                File.Move(partialPath, destinationPath);
                progress?.Report(new DownloadProgress(asset.Size, asset.Size));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(partialPath);
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
            {
                lastError = error;
                TryDeleteFile(partialPath);
                progress?.Report(new DownloadProgress(0, asset.Size));
            }
        }

        throw new InvalidDataException("代理和 GitHub 官方下载均失败或校验未通过，更新包未运行。", lastError);
    }

    internal static GitHubRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub Release 响应缺少 tag_name。");

        var tag = tagElement.GetString()!;
        if (!TryParseReleaseVersion(tag, out var version))
            throw new InvalidDataException($"GitHub Release tag 格式无效：{tag}");

        var publishedDate = root.TryGetProperty("published_at", out var publishedAtElement)
            && publishedAtElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedAtElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var publishedAt)
                ? DateOnly.FromDateTime(publishedAt.UtcDateTime)
                : (DateOnly?)null;
        var body = root.TryGetProperty("body", out var bodyElement)
            && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString() ?? ""
                : "";

        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                if (assetElement.ValueKind != JsonValueKind.Object
                    || !assetElement.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !assetElement.TryGetProperty("size", out var sizeElement)
                    || !sizeElement.TryGetInt64(out var size))
                    continue;

                var digest = assetElement.TryGetProperty("digest", out var digestElement)
                    && digestElement.ValueKind == JsonValueKind.String
                    ? digestElement.GetString()
                    : null;
                assets.Add(new GitHubReleaseAsset(nameElement.GetString()!, size,
                    TryNormalizeDigest(digest, out var normalized) ? normalized : null));
            }
        }

        return new GitHubRelease(tag, version, publishedDate, body, assets);
    }

    internal static bool TryParseReleaseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag) || tag.Length < 2 || tag[0] != 'v') return false;
        var parts = tag.AsSpan(1).ToString().Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiDigit(character))))
            return false;
        return Version.TryParse(tag[1..], out version!) && version.Build >= 0 && version.Revision >= 0;
    }

    private static async Task<GitHubRelease> ParseReleaseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRelease(document.RootElement.GetRawText());
    }

    internal static bool TryNormalizeDigest(string? digest, out string normalized)
    {
        normalized = "";
        if (digest is null) return false;
        var value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : digest;
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character))) return false;
        normalized = value.ToLowerInvariant();
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
