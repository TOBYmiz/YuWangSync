using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Net;

public sealed partial class YuWangClient
{
    // Follow the Web Stream uploader, including its exclusive range end. Do not use multipart.
    public const int UploadChunkSize = 10 * 1024 * 1024;

    private static void ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException("无效的云端文件名。", nameof(name));
    }

    private static IReadOnlyList<YuWangItem> RequireCompleteListing(string html, ulong disk, ulong folder)
    {
        var items = HtmlParser.ExtractItems(html);
        var declared = System.Text.RegularExpressions.Regex.Matches(html, @"showItem\('[A-Z]','(\d+)'")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Count();
        if (!IsExpectedFolderPage(html, disk, folder) || declared != items.Count)
            throw new IOException("服务器未返回完整的目标目录列表。");
        return items;
    }

    public async Task<YuWangItem> CreateRemoteFolderAsync(ulong disk, ulong parent, string name,
        CancellationToken ct = default, ulong createdId = 0, Action<ulong>? rememberCreated = null)
    {
        ValidateRemoteName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        await _folderContextGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var html = await FetchWritableFolderCoreAsync(disk, parent).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("目标目录没有已确认的写入权限。");
            var before = RequireCompleteListing(html, disk, parent);
            if (before.Any(i => i.Id != createdId && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("云端已存在同名条目，保留本地目录等待处理。");
            YuWangItem created;
            if (createdId != 0)
                created = before.SingleOrDefault(i => i.Id == createdId && i.IsFolder)
                    ?? throw new IOException("待恢复的新目录 ID 已不存在，停止重复创建。");
            else
            {
                var fields = HtmlParser.ExtractPageFormFields(html);
                if (fields.Count == 0) throw new IOException("新建目录缺少 pageForm。");
                linked.Token.ThrowIfCancellationRequested();
                await PostFormCoreAsync("/fmsFolderInfoIconAction.do?method=addSave", fields).ConfigureAwait(false);
                var after = RequireCompleteListing(await FetchFolderHtmlCoreAsync(disk, parent).ConfigureAwait(false), disk, parent);
                var previous = before.Select(i => i.Id).ToHashSet();
                var added = after.Where(i => i.IsFolder && !previous.Contains(i.Id)).ToArray();
                if (added.Length != 1) throw new IOException("无法唯一确认新建的云端目录，不继续重命名。");
                created = added[0];
                rememberCreated?.Invoke(created.Id);
            }
            if (created.Name == name) return created;
            linked.Token.ThrowIfCancellationRequested();
            var rename = await PostFormCoreAsync("/fmsFolderInfoAction.do?method=changeFolderName",
                [new("fid", created.Id.ToString(CultureInfo.InvariantCulture)), new("newname", name)])
                .ConfigureAwait(false);
            if (!rename.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"云端目录已创建但改名未确认，目录 ID={created.Id}。");
            var verified = RequireCompleteListing(await FetchFolderHtmlCoreAsync(disk, parent).ConfigureAwait(false), disk, parent);
            return verified.SingleOrDefault(i => i.IsFolder && i.Id == created.Id && i.Name == name)
                ?? throw new IOException("新建目录的名称未通过复查。");
        }
        finally { _folderContextGate.Release(); }
    }

    /// <summary>
    /// Upload a stable, seekable local snapshot. A matching pre-existing name is accepted only
    /// after downloading and hashing it, so retry after a lost final reply cannot duplicate uploads.
    /// Different content is never overwritten through the unverified same-name Web branch.
    /// </summary>
    public async Task<YuWangItem> UploadNewFileAsync(ulong disk, ulong folder, string name, Stream source,
        DateTime modifiedUtc, byte[] sha256, CancellationToken ct = default, Action<UploadProgress>? progress = null,
        UploadAttempt? attempt = null, Action<UploadAttempt>? rememberAttempt = null)
    {
        ValidateRemoteName(name);
        if (!source.CanSeek || !source.CanRead || sha256.Length != 32)
            throw new ArgumentException("上传需要可寻址的稳定文件快照及 SHA-256。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        await _folderContextGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await UploadNewFileCoreAsync(disk, folder, name, source, modifiedUtc, sha256, linked.Token, progress,
                attempt, rememberAttempt).ConfigureAwait(false);
        }
        finally { _folderContextGate.Release(); }
    }

    private async Task<YuWangItem> UploadNewFileCoreAsync(ulong disk, ulong folder, string name, Stream source,
        DateTime modifiedUtc, byte[] sha256, CancellationToken ct, Action<UploadProgress>? progress,
        UploadAttempt? attempt, Action<UploadAttempt>? rememberAttempt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        {
            ReportUploadProgress(progress, new(UploadStage.Preparing, source.Length, 0, 0));
            var html = await FetchWritableFolderCoreAsync(disk, folder, fresh: true).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("目标目录没有已确认的写入权限。");
            var before = RequireCompleteListing(html, disk, folder);
            if (attempt is not null)
            {
                var recovered = await ResolveUploadedFileAsync(disk, folder, name, source.Length, sha256,
                    before, attempt, rememberAttempt, linked.Token, progress).ConfigureAwait(false);
                if (recovered is not null) return recovered;
                if (attempt.MayHaveCommitted)
                    return await WaitForUploadedFileAsync(disk, folder, name, source.Length, sha256,
                        attempt, rememberAttempt, linked.Token, progress, source.Length).ConfigureAwait(false);
            }
            var sameName = before.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameName.Length != 0)
            {
                if (sameName.Length == 1 && !sameName[0].IsFolder
                    && await VerifyRemoteContentAsync(sameName[0].Id, source.Length, sha256, linked.Token, progress).ConfigureAwait(false))
                {
                    ReportUploadProgress(progress, new(UploadStage.Finalizing, source.Length, source.Length, source.Length, source.Length));
                    return sameName[0];
                }
                throw new IOException("云端存在不同内容的同名条目，未执行覆盖。");
            }
            if (!HtmlParser.TryGetHiddenUInt64(html, "userid", out var userId) || userId == 0)
                throw new IOException("上传页面缺少有效的 userid。");

            // Opening the upload page alone is not a permission check: the writable parent
            // was verified above under the same session-context gate.
            using (var page = MakeRequest(HttpMethod.Get,
                $"/fmsFileInfoAction.do?method=beforeUpload&diskid={disk}&folderid={folder}"))
            using (var response = await SendAsync(page, _requestTimeout, ct: linked.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (HtmlParser.IsLoginPage(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false)))
                    throw new UnauthorizedAccessException("上传会话已失效。");
            }
            var query = "name=" + Uri.EscapeDataString(name)
                + "&type=application%2Foctet-stream&size=" + source.Length.ToString(CultureInfo.InvariantCulture)
                + "&modified=" + Uri.EscapeDataString(new DateTimeOffset(modifiedUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
                + $"&userid={userId}&loginname=" + Uri.EscapeDataString(Username) + $"&diskid={disk}&folderid={folder}";
            using var tokenReply = await GetUploadReplyAsync("/TokenServlet?" + query, linked.Token).ConfigureAwait(false);
            if (!tokenReply.RootElement.TryGetProperty("token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenElement.GetString()))
                throw new IOException("上传服务没有返回 token。");
            // Tokens and full request URLs must never appear in diagnostics.
            var endpoint = "/StreamServlet?token=" + Uri.EscapeDataString(tokenElement.GetString()!) + "&client=html5&" + query;
            // A completed token can publish a directory entry in GET. Persist the baseline
            // before sending it; a lost reply must never cause another blind upload.
            attempt = new(attempt?.BeforeIds ?? before.Select(i => i.Id).ToArray(), true);
            rememberAttempt?.Invoke(attempt);
            using var offsetReply = await GetUploadReplyAsync(endpoint, linked.Token).ConfigureAwait(false);
            var resumed = ReadUploadOffset(offsetReply);
            if (resumed > source.Length)
                throw new IOException($"服务器续传偏移越界 offset={resumed} total={source.Length}，保留本地文件。");
            // The Web uploader queries the token before every attempt and continues at start.
            // A completed token can create the directory entry during that GET, with no POST.
            // Its prefix is only a transfer hint: verify the entire remote file before binding
            // or allowing ReplaceRemoteFileAsync to delete the original backup.
            _log($"[UPLOAD] 服务端上传偏移 offset={resumed} total={source.Length} disk={disk} folder={folder}");
            attempt = attempt with { MayHaveCommitted = resumed == source.Length };
            rememberAttempt?.Invoke(attempt);
            source.Position = resumed;
            var buffer = new byte[(int)Math.Min(UploadChunkSize, Math.Max(1, source.Length))];
            var position = resumed;
            var sendEmptyFile = source.Length == 0;
            ReportUploadProgress(progress, new(UploadStage.Uploading, source.Length, position, position, ResumedBytes: resumed));
            while (position < source.Length || sendEmptyFile)
            {
                sendEmptyFile = false;
                linked.Token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, source.Length - position);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), linked.Token).ConfigureAwait(false);
                var chunkStart = position;
                using var content = new UploadChunkContent(buffer.AsMemory(0, count), sent =>
                    ReportUploadProgress(progress, new(UploadStage.Uploading, source.Length, chunkStart + sent, chunkStart, ResumedBytes: resumed)));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {position}-{position + count}/{source.Length}");
                if (position + count == source.Length)
                {
                    attempt = attempt with { MayHaveCommitted = true };
                    rememberAttempt?.Invoke(attempt);
                }
                using var request = MakeRequest(HttpMethod.Post, endpoint, content);
                using var response = await SendAsync(request, _downloadTimeout, ct: linked.Token).ConfigureAwait(false);
                EnsureUploadResponseStatus(response);
                using var reply = ParseUploadReply(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false));
                var acknowledged = ReadUploadOffset(reply);
                if (acknowledged != position + count)
                    throw new IOException($"服务器确认的分片偏移不符合预期 expected={position + count} actual={acknowledged} total={source.Length}。");
                position += count;
                ReportUploadProgress(progress, new(UploadStage.Uploading, source.Length, position, position, ResumedBytes: resumed));
            }
            return await WaitForUploadedFileAsync(disk, folder, name, source.Length, sha256,
                attempt, rememberAttempt, linked.Token, progress, resumed).ConfigureAwait(false);
        }
    }

    private async Task<YuWangItem> WaitForUploadedFileAsync(ulong disk, ulong folder, string name, long length,
        byte[] sha256, UploadAttempt attempt, Action<UploadAttempt>? rememberAttempt,
        CancellationToken ct, Action<UploadProgress>? progress, long resumed)
    {
        const int checks = 10;
        var previous = attempt.BeforeIds.ToHashSet();
        // A final byte ACK precedes publication in the directory on this server. Retry only
        // fresh metadata reads here; never request a new token or re-send the final chunk.
        for (var check = 0; check < checks; check++)
        {
            if (check > 0) await Task.Delay(Math.Min(4000, 250 << (check - 1)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var items = RequireCompleteListing(await FetchFolderHtmlCoreAsync(disk, folder, fresh: true)
                .ConfigureAwait(false), disk, folder);
            var added = items.Where(i => !previous.Contains(i.Id)).ToArray();
            var diagnostic = string.Join("; ", added.Take(3).Select(i => $"{i.Id}:{i.Name.Replace('\r', ' ').Replace('\n', ' ')}"));
            _log($"[UPLOAD] 目录确认 poll={check + 1}/{checks} items={items.Count} newItems={added.Length} disk={disk} folder={folder} added=[{diagnostic}]");
            var result = await ResolveUploadedFileAsync(disk, folder, name, length, sha256,
                items, attempt, rememberAttempt, ct, progress, resumed).ConfigureAwait(false);
            if (result is not null) return result;
        }
        throw new IOException("上传已被服务器确认，但等待新鲜目录列表后仍未出现可确认的文件；保留上传记录，不再次发送文件。");
    }

    private async Task<YuWangItem?> ResolveUploadedFileAsync(ulong disk, ulong folder, string name, long length,
        byte[] sha256, IReadOnlyList<YuWangItem> items, UploadAttempt attempt,
        Action<UploadAttempt>? rememberAttempt, CancellationToken ct, Action<UploadProgress>? progress, long resumed = 0)
    {
        var previous = attempt.BeforeIds.ToHashSet();
        var suffixPattern = "^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileNameWithoutExtension(name))
            + @"_[0-9]{6}" + System.Text.RegularExpressions.Regex.Escape(Path.GetExtension(name)) + "$";
        bool MatchesName(string candidate) => candidate.Equals(name, StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(candidate, suffixPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var candidates = items.Where(i => !i.IsFolder && !previous.Contains(i.Id)
            && (attempt.CandidateId == 0 ? MatchesName(i.Name) : i.Id == attempt.CandidateId)).ToArray();
        if (candidates.Length == 0)
        {
            if (attempt.CandidateId != 0) throw new IOException($"已记录的上传条目不在目标目录 file={attempt.CandidateId}；停止重复上传。");
            return null;
        }
        if (candidates.Length != 1)
            throw new IOException($"上传结果不唯一 candidates={candidates.Length}，保留云端条目和本地文件，停止重复上传。");
        var candidate = candidates[0];
        if (!MatchesName(candidate.Name))
            throw new IOException($"已记录的上传条目被其他操作改名 file={candidate.Id}，停止自动处理。");
        // The assigned name is not evidence of ownership/content. Require a new ID in this
        // attempt's directory delta and a full hash match before restoring the requested name.
        rememberAttempt?.Invoke(attempt with { CandidateId = candidate.Id, MayHaveCommitted = true });
        if (!await VerifyRemoteContentAsync(candidate.Id, length, sha256, ct, progress, resumed).ConfigureAwait(false))
            throw new IOException($"上传内容未通过完整校验 file={candidate.Id}；保留本地内容，停止重复上传。");
        if (candidate.Name != name)
        {
            var writable = await FetchWritableFolderCoreAsync(disk, folder, fresh: true).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("上传后恢复文件名时，目录写入权限未确认。");
            var current = RequireCompleteListing(writable, disk, folder);
            if (!current.Any(i => i.Id == candidate.Id && i.Name == candidate.Name)
                || current.Any(i => i.Id != candidate.Id && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"上传后的文件名发生冲突 file={candidate.Id}，保留记录等待核对。");
            ct.ThrowIfCancellationRequested();
            if (!await RenameFileCoreAsync(disk, folder, candidate.Id, name).ConfigureAwait(false))
                throw new IOException($"上传文件已校验，恢复原文件名尚未确认 file={candidate.Id}；下次仅核对该 ID，不重新上传。");
            candidate = RequireCompleteListing(await FetchFolderHtmlCoreAsync(disk, folder, fresh: true).ConfigureAwait(false), disk, folder)
                .Single(i => i.Id == candidate.Id && i.Name == name);
            _log($"[UPLOAD] 服务端附加后缀已还原 file={candidate.Id} disk={disk} folder={folder}");
        }
        ReportUploadProgress(progress, new(UploadStage.Finalizing, length, length, length, length, resumed));
        return candidate;
    }

    /// <summary>Rename the old item, upload and verify its replacement, then recycle the old ID.</summary>
    public async Task<YuWangItem> ReplaceRemoteFileAsync(ulong disk, ulong folder, ulong oldId, string name,
        string backupName, Stream source, DateTime modifiedUtc, byte[] sha256,
        Action<ulong> rememberNewId, CancellationToken ct = default, Action<UploadProgress>? progress = null,
        UploadAttempt? attempt = null, Action<UploadAttempt>? rememberAttempt = null)
    {
        ValidateRemoteName(name);
        ValidateRemoteName(backupName);
        if (name.Equals(backupName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("备份名称必须唯一。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        await _folderContextGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ReportUploadProgress(progress, new(UploadStage.Preparing, source.Length, 0, 0));
            var html = await FetchWritableFolderCoreAsync(disk, folder).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("覆盖目标没有已确认的写入权限。");
            var items = RequireCompleteListing(html, disk, folder);
            var old = items.SingleOrDefault(i => i.Id == oldId && !i.IsFolder);
            if (old is null)
            {
                var completed = items.Where(i => !i.IsFolder && i.Name == name).ToArray();
                if (completed.Length == 1
                    && await VerifyRemoteContentAsync(completed[0].Id, source.Length, sha256, linked.Token, progress).ConfigureAwait(false))
                {
                    rememberNewId(completed[0].Id);
                    ReportUploadProgress(progress, new(UploadStage.Finalizing, source.Length, source.Length, source.Length, source.Length));
                    return completed[0];
                }
                throw new IOException("原文件已不在目标目录，停止覆盖。");
            }
            if (old.Name != name && old.Name != backupName)
                throw new IOException("原文件被其他操作改名，停止覆盖。");
            YuWangItem replacement;
            var backupAttempted = old.Name == backupName;
            try
            {
                if (old.Name == name)
                {
                    if (items.Any(i => i.Id != oldId && (i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || i.Name.Equals(backupName, StringComparison.OrdinalIgnoreCase))))
                        throw new IOException("覆盖名称发生冲突。");
                    linked.Token.ThrowIfCancellationRequested();
                    backupAttempted = true;
                    if (!await RenameFileCoreAsync(disk, folder, oldId, backupName).ConfigureAwait(false))
                        throw new IOException("原文件改为备份名未通过复查。");
                }
                linked.Token.ThrowIfCancellationRequested();
                replacement = await UploadNewFileCoreAsync(disk, folder, name, source, modifiedUtc, sha256, linked.Token, progress,
                    attempt, rememberAttempt)
                    .ConfigureAwait(false);
                // Persist the new ID before deleting the old item. A local metadata failure can
                // then finish binding this same upload after restart instead of uploading again.
                rememberNewId(replacement.Id);
            }
            catch
            {
                if (!backupAttempted) throw;
                // A lost final response can leave a new item with the original name. Preserve
                // that item under a separate name before restoring the known old ID. Never
                // delete an unverified upload while recovering the original name.
                try
                {
                    var rollbackHtml = await FetchWritableFolderCoreAsync(disk, folder).ConfigureAwait(false)
                        ?? throw new IOException("还原时权限未知。");
                    var rollbackItems = RequireCompleteListing(rollbackHtml, disk, folder);
                    if (!rollbackItems.Any(i => i.Id == oldId && !i.IsFolder && (i.Name == backupName || i.Name == name)))
                        throw new IOException("原文件已被其他操作改变，停止自动还原。");
                    var collisions = rollbackItems.Where(i => i.Id != oldId && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (collisions.Length > 1 || collisions.Any(i => i.IsFolder))
                        throw new IOException("还原时发现多个同名条目。");
                    foreach (var item in collisions)
                    {
                        var failedName = Path.GetFileNameWithoutExtension(name) + "_failed_" + item.Id + Path.GetExtension(name);
                        if (rollbackItems.Any(i => i.Id != item.Id && i.Name.Equals(failedName, StringComparison.OrdinalIgnoreCase))
                            || !await RenameFileCoreAsync(disk, folder, item.Id, failedName).ConfigureAwait(false))
                            throw new IOException("未能保留未确认的新文件。");
                    }
                    if (!await RenameFileCoreAsync(disk, folder, oldId, name).ConfigureAwait(false))
                        throw new IOException("原文件名称未能还原。");
                    _log($"[WRITE] 覆盖失败，原文件名称已还原 file={oldId}");
                }
                catch (Exception recovery)
                {
                    _log($"[WRITE] 覆盖还原未完成，保留备份 file={oldId} backup={backupName} reason={recovery.GetType().Name}");
                }
                throw;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!await DeleteRemoteItemCoreAsync(disk, folder, oldId, false).ConfigureAwait(false))
                throw new IOException("新文件已校验，旧文件删除未确认；保留写回记录等待续办。");
            return replacement;
        }
        finally { _folderContextGate.Release(); }
    }

    private async Task<bool> RenameFileCoreAsync(ulong disk, ulong folder, ulong id, string name)
    {
        var result = await PostFormCoreAsync("/fmsFileInfoAction.do?method=changeFileName",
            [new("fid", id.ToString(CultureInfo.InvariantCulture)), new("newname", name)]).ConfigureAwait(false);
        if (!result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        return RequireCompleteListing(await FetchFolderHtmlCoreAsync(disk, folder, fresh: true).ConfigureAwait(false), disk, folder)
            .Any(i => i.Id == id && !i.IsFolder && i.Name == name);
    }

    private async Task<JsonDocument> GetUploadReplyAsync(string endpoint, CancellationToken ct)
    {
        // Match the Web uploader's random query parameter: cached token/offset replies can
        // otherwise replay an obsolete transfer state when the same file is retried.
        using var request = MakeRequest(HttpMethod.Get, endpoint + "&_=" + Guid.NewGuid().ToString("N"));
        using var response = await SendAsync(request, _requestTimeout, ct: ct).ConfigureAwait(false);
        EnsureUploadResponseStatus(response);
        return ParseUploadReply(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static void EnsureUploadResponseStatus(HttpResponseMessage response)
    {
        // The deployed Stream Web uploader also accepts 308 for an incomplete transfer.
        // JSON success and the exact offset are still validated by the caller.
        if ((int)response.StatusCode != 308) response.EnsureSuccessStatusCode();
    }

    private static JsonDocument ParseUploadReply(string body)
    {
        JsonDocument reply;
        try { reply = JsonDocument.Parse(body); }
        catch (JsonException) { throw new IOException("上传服务返回了非预期响应。"); }
        if (reply.RootElement.ValueKind == JsonValueKind.Object
            && reply.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            return reply;
        reply.Dispose();
        throw new IOException("上传服务没有确认操作成功。");
    }

    private static long ReadUploadOffset(JsonDocument reply)
    {
        if (reply.RootElement.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number
            && start.TryGetInt64(out var offset) && offset >= 0)
            return offset;
        throw new IOException("上传响应缺少有效的 start 偏移。");
    }

    private async Task<bool> VerifyRemoteContentAsync(ulong id, long length, byte[] expected, CancellationToken ct,
        Action<UploadProgress>? progress = null, long resumed = 0)
    {
        ReportUploadProgress(progress, new(UploadStage.Verifying, length, length, length, ResumedBytes: resumed));
        using var request = MakeRequest(HttpMethod.Post, "/fmsFileInfoAction.do?method=downSelectFile",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("selectitemid", id.ToString(CultureInfo.InvariantCulture))]));
        using var response = await SendAsync(request, _downloadTimeout, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        timeout.CancelAfter(_downloadTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > length) return false;
            hash.AppendData(buffer, 0, count);
            ReportUploadProgress(progress, new(UploadStage.Verifying, length, length, length, total, resumed));
        }
        return total == length && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected);
    }

    private void ReportUploadProgress(Action<UploadProgress>? observer, UploadProgress value)
    {
        try { observer?.Invoke(value); }
        catch (Exception error) { _log($"[UPLOAD] 进度接收器异常（忽略）: {error.GetType().Name}"); }
    }

    private sealed class UploadChunkContent(ReadOnlyMemory<byte> bytes, Action<long> progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var written = 0;
            while (written < bytes.Length)
            {
                var count = Math.Min(64 * 1024, bytes.Length - written);
                await stream.WriteAsync(bytes.Slice(written, count), ct).ConfigureAwait(false);
                written += count;
                progress(written);
            }
        }
    }
}
