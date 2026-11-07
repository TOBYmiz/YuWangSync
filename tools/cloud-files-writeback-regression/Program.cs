using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using YuWangCloudSync.Core;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangCloudSync.Storage;

try
{
if (args is ["--native-io", var operation, var localPath, var value])
{
    var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "_buildtmp", "native-writeback")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(localPath).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Fixture path outside native test directory.");
    if (operation == "write") File.WriteAllBytes(localPath, Convert.FromBase64String(value));
    else if (operation == "delete") File.Delete(localPath);
    else if (operation == "try-delete-dir")
    {
        try { Directory.Delete(localPath, false); }
        catch (IOException) { }
    }
    else if (operation == "delete-tree") Directory.Delete(localPath, true);
    else if (operation == "move-dir" && Path.GetFullPath(value).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Move(localPath, value);
    else if (operation == "rename" && Path.GetFullPath(value).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) File.Move(localPath, value);
    else throw new ArgumentException("Unknown native fixture operation.");
    return;
}
var passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Fails(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected an unsuccessful write.");
}
static YuWangClient Client(WebServer server) => new("http://localhost", "test-user", "unused", _ => { }, httpHandler: server);
static async Task<YuWangItem> Upload(YuWangClient client, byte[] data, string name = "新建 & 测试.pptx", Action<UploadProgress>? progress = null,
    UploadAttempt? attempt = null, Action<UploadAttempt>? rememberAttempt = null)
{
    using var source = new MemoryStream(data);
    return await client.UploadNewFileAsync(7, 10, name, source, DateTime.UtcNow, SHA256.HashData(data), progress: progress,
        attempt: attempt, rememberAttempt: rememberAttempt);
}
static async Task<YuWangItem> Replace(YuWangClient client, byte[] data, Action<ulong>? remember = null, CancellationToken ct = default)
{
    using var source = new MemoryStream(data);
    return await client.ReplaceRemoteFileAsync(7, 10, 100, "report.docx", "report_back.docx",
        source, DateTime.UtcNow, SHA256.HashData(data), remember ?? (_ => { }), ct);
}
var oldBytes = Encoding.UTF8.GetBytes("old document");
var newBytes = Encoding.UTF8.GetBytes("new document content");

await Test("cloud names retain closing parentheses and HTML entities", () =>
{
    var parsed = HtmlParser.ExtractItems("""<div onclick="showItem('D','21')"><span title="文件名：新建文件夹 (2)"></span></div><div onclick="showItem('F','22')"><span title="文件名：答案 (&amp;)).docx<br>文件大小：12 B"></span></div>""");
    Check(parsed.Count == 2 && parsed[0].Name == "新建文件夹 (2)" && parsed[1].Name == "答案 (&)).docx", "file name parser trimmed literal characters");
    return Task.CompletedTask;
});
await Test("new file: exact 10 MiB raw chunks, encoded name, verified bytes", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var data = new byte[YuWangClient.UploadChunkSize + 17];
    RandomNumberGenerator.Fill(data);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, data, progress: progress.Add);
    Check(web.Items[item.Id].Bytes.SequenceEqual(data), "upload content changed");
    Check(web.Ranges.SequenceEqual(new[] { $"bytes 0-10485760/{data.Length}", $"bytes 10485760-{data.Length}/{data.Length}" }), "exclusive Web range boundaries");
    Check(web.Events.IndexOf("download:" + item.Id) > web.Events.IndexOf("chunk"), "missing content verification");
    Check(progress.Any(p => p.Stage == UploadStage.Uploading && p.SentBytes > 0 && p.SentBytes < YuWangClient.UploadChunkSize && p.ConfirmedBytes == 0), "missing progress inside a chunk before server ACK");
    Check(progress.All(p => p.ConfirmedBytes <= p.SentBytes && p.SentBytes <= data.Length && p.TotalBytes == data.Length), "invalid progress counters");
    var sent = progress.Where(p => p.Stage == UploadStage.Uploading).Select(p => p.SentBytes).ToArray();
    Check(sent.SequenceEqual(sent.Order()), "upload progress moved backwards");
    Check(progress.Any(p => p.Stage == UploadStage.Verifying && p.VerifiedBytes == data.Length)
        && progress[^1].Stage == UploadStage.Finalizing, "upload completed before hash verification");
});
await Test("zero-byte new file receives a final confirmation", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var item = await Upload(client, [], "empty.txt");
    Check(web.Items[item.Id].Bytes.Length == 0 && web.Ranges.Single() == "bytes 0-0/0", "empty upload");
});
await Test("read-only destination performs no write", async () =>
{
    using var web = new WebServer { ReadOnly = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0 && web.Items.Count == 0, "readonly uploaded");
});
await Test("wrong target page performs no upload", async () =>
{
    using var web = new WebServer { WrongPage = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0, "wrong target uploaded");
});
await Test("malformed partial listing is never a write snapshot", async () =>
{
    using var web = new WebServer { PartialPage = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0, "partial listing uploaded");
});
await Test("bad chunk acknowledgement fails", async () =>
{
    using var web = new WebServer { BadOffset = true };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    await Fails(() => Upload(client, newBytes, progress: progress.Add));
    Check(!progress.Any(p => p.Stage == UploadStage.Finalizing || p.ConfirmedBytes != 0), "bad acknowledgement reported confirmed/success progress");
});
await Test("progress observer exceptions do not abort a verified upload", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var item = await Upload(client, newBytes, progress: _ => throw new InvalidOperationException("fixture observer"));
    Check(web.Items[item.Id].Bytes.SequenceEqual(newBytes), "observer broke upload");
});
await Test("partial upload resumes at the returned byte offset and verifies the entire file", async () =>
{
    var data = new byte[YuWangClient.UploadChunkSize + 19];
    RandomNumberGenerator.Fill(data);
    const int offset = 13; // Neither zero nor a chunk boundary.
    using var web = new WebServer { InitialOffset = offset, InitialBytes = data[..offset], UseResumeIncompleteStatus = true };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, data, progress: progress.Add);
    Check(web.Ranges.SequenceEqual(new[] { $"bytes {offset}-{offset + YuWangClient.UploadChunkSize}/{data.Length}",
        $"bytes {offset + YuWangClient.UploadChunkSize}-{data.Length}/{data.Length}" }), "resume did not slice at the server offset");
    Check(web.Items[item.Id].Bytes.SequenceEqual(data), "resumed content changed");
    Check(progress.Where(p => p.Stage != UploadStage.Preparing).All(p => p.ResumedBytes == offset), "resumed byte count was lost");
    Check(progress.Any(p => p.Stage == UploadStage.Uploading && p.SentBytes > offset && p.ConfirmedBytes == offset), "resume hid writes before ACK");
    Check(progress.Any(p => p.Stage == UploadStage.Verifying && p.VerifiedBytes == data.Length)
        && progress[^1].Stage == UploadStage.Finalizing, "resumed prefix was not fully verified");
});
await Test("completed token skips POST and verifies the resulting directory entry", async () =>
{
    using var web = new WebServer { InitialOffset = newBytes.Length, InitialBytes = newBytes };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, newBytes, progress: progress.Add);
    Check(web.Ranges.Count == 0 && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "completed token sent duplicate content");
    Check(web.Events.Contains("download:" + item.Id) && progress[^1].Stage == UploadStage.Finalizing
        && progress[^1].ResumedBytes == newBytes.Length, "completed token bypassed verification");
});
await Test("out-of-range and malformed initial offsets never send data", async () =>
{
    foreach (var offset in new object[] { -1L, newBytes.Length + 1L, "1", 0.5, true })
    {
        using var web = new WebServer { InitialOffsetReply = offset };
        using var client = Client(web);
        await Fails(() => Upload(client, newBytes));
        Check(web.Ranges.Count == 0, "invalid resume wrote data");
    }
});
await Test("completed token without a directory entry is not accepted", async () =>
{
    using var web = new WebServer { InitialOffset = newBytes.Length, InitialBytes = newBytes, PublishCompletedResume = false };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    await Fails(() => Upload(client, newBytes, progress: progress.Add));
    Check(web.Ranges.Count == 0 && !progress.Any(p => p.Stage == UploadStage.Finalizing), "missing completed entry accepted");
});
await Test("stale resumed bytes cannot pass full-file verification", async () =>
{
    foreach (var offset in new[] { 1, newBytes.Length })
    {
        var stale = newBytes[..offset];
        stale[0] ^= 1;
        using var web = new WebServer { InitialOffset = offset, InitialBytes = stale };
        using var client = Client(web);
        var progress = new List<UploadProgress>();
        await Fails(() => Upload(client, newBytes, progress: progress.Add));
        Check(!progress.Any(p => p.Stage == UploadStage.Finalizing), "stale token reported verified success");
        Check(web.Items.Count == 1 && web.Recycled.Count == 0, "unverified remote content was deleted");
    }
});
await Test("replacement with a stale resumed prefix restores the original backup", async () =>
{
    using var web = new WebServer { InitialOffset = 1, InitialBytes = [0xff] };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Items[100].Bytes.SequenceEqual(oldBytes)
        && web.Recycled.Count == 0, "invalid resumed replacement lost the original");
    Check(web.Items.Values.Any(i => i.Id != 100 && i.Name.StartsWith("report_failed_")), "invalid replacement was not preserved");
});
await Test("same-name retry verifies and reuses uploaded ID", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var first = await Upload(client, newBytes);
    var second = await Upload(client, newBytes);
    Check(first.Id == second.Id && web.TokenCount == 1, "retry duplicated upload");
});
await Test("server-assigned six-digit suffix is verified by ID and renamed once", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true };
    web.Add(90, "新建 & 测试_000090.pptx", oldBytes);
    using var client = Client(web);
    var item = await Upload(client, newBytes);
    Check(item.Name == "新建 & 测试.pptx" && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "assigned name was not restored");
    Check(web.Items[90].Name == "新建 & 测试_000090.pptx" && web.Items[90].Bytes.SequenceEqual(oldBytes), "pre-existing suffix was changed");
    Check(web.TokenCount == 1 && web.Events.IndexOf("download:" + item.Id)
        < web.Events.IndexOf("rename:" + item.Id + ":" + item.Name), "renamed before verifying uploaded content");
});
await Test("final byte ACK waits for fresh delayed directory publication without reuploading", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, PublishedListingDelay = 2, StaleUploadListings = true };
    using var client = Client(web);
    UploadAttempt? attempt = null;
    var item = await Upload(client, newBytes, rememberAttempt: value => attempt = value);
    Check(web.TokenCount == 1 && web.Ranges.Count == 1 && web.FreshListRequests >= 3, "directory wait duplicated upload or used stale list");
    Check(web.Items.Count == 1 && item.Name == "新建 & 测试.pptx" && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "delayed suffixed upload was not verified and renamed");
    Check(attempt is { MayHaveCommitted: true, CandidateId: > 0 }, "directory wait lost its durable upload evidence");
});
await Test("cancellation during delayed directory publication preserves upload evidence", async () =>
{
    using var stop = new CancellationTokenSource();
    using var web = new WebServer { AssignUploadSuffix = true, HidePublishedUploads = true };
    web.AfterPublishedListing = () => stop.Cancel();
    using var client = Client(web);
    UploadAttempt? attempt = null;
    using var source = new MemoryStream(newBytes);
    await Fails(() => client.UploadNewFileAsync(7, 10, "cancelled.txt", source, DateTime.UtcNow,
        SHA256.HashData(newBytes), stop.Token, rememberAttempt: value => attempt = value));
    Check(stop.IsCancellationRequested && attempt is { MayHaveCommitted: true }, "cancelled publication wait discarded its receipt");
    Check(web.TokenCount == 1 && web.Items.Count == 1 && !web.Events.Any(e => e.StartsWith("rename:")), "cancelled wait wrote another file or renamed an unverified candidate");
});
await Test("lost final reply with assigned suffix recovers without another upload", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, LoseFinalReply = true };
    UploadAttempt? attempt = null;
    using (var client = Client(web))
        await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
    Check(attempt is { MayHaveCommitted: true }, "final POST was sent without durable uncertainty marker");
    web.LoseFinalReply = false;
    using var retry = Client(web);
    var item = await Upload(retry, newBytes, attempt: attempt, rememberAttempt: value => attempt = value);
    Check(web.TokenCount == 1 && web.Items.Count == 1 && item.Name == "新建 & 测试.pptx", "lost reply duplicated suffixed upload");
});
await Test("unpublished partial upload can still resume without losing uncertainty protection", async () =>
{
    var data = new byte[YuWangClient.UploadChunkSize + 7];
    RandomNumberGenerator.Fill(data);
    using var web = new WebServer { FailPartialChunkOnce = true, AssignUploadSuffix = true };
    UploadAttempt? attempt = null;
    using var client = Client(web);
    await Fails(() => Upload(client, data, rememberAttempt: value => attempt = value));
    Check(attempt is { MayHaveCommitted: false } && web.Items.Count == 0, "partial chunk incorrectly marked as published");
    web.InitialOffset = YuWangClient.UploadChunkSize;
    web.InitialBytes = data[..YuWangClient.UploadChunkSize];
    var item = await Upload(client, data, attempt: attempt, rememberAttempt: value => attempt = value);
    Check(web.Items.Count == 1 && web.Items[item.Id].Bytes.SequenceEqual(data), "partial retry failed to resume");
});
await Test("missing or ambiguous published entries never start another token", async () =>
{
    foreach (var ambiguous in new[] { false, true })
    {
        using var web = new WebServer { AssignUploadSuffix = true, HidePublishedUploads = !ambiguous, PublishExtraUpload = ambiguous };
        using var client = Client(web);
        UploadAttempt? attempt = null;
        await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
        await Fails(() => Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value));
        Check(web.TokenCount == 1 && web.Items.Count == (ambiguous ? 2 : 1), "ambiguous outcome created another duplicate");
        if (!ambiguous)
        {
            web.HidePublishedUploads = false;
            var item = await Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value);
            Check(web.TokenCount == 1 && web.Items[item.Id].Name == "新建 & 测试.pptx", "late directory entry was not recovered");
        }
    }
});
await Test("candidate survives SQLite reopen and failed rename without retransmission", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "_buildtmp", "writeback-regression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var db = Path.Combine(root, "attempt.db");
    using var web = new WebServer { AssignUploadSuffix = true, RefuseUploadedRename = true };
    var hash = Convert.ToHexString(SHA256.HashData(newBytes));
    try
    {
        using (var cache = new SqliteCache(db))
        using (var client = Client(web))
        {
            cache.SaveWritebackReceipt("u", "p", hash, 7, 10, "新建 & 测试.pptx", 0);
            await Fails(() => Upload(client, newBytes, rememberAttempt: value => cache.SaveUploadAttempt("u", "p", hash, value)));
            Check(cache.GetUploadAttempt("u", "p", hash) is { CandidateId: > 0, MayHaveCommitted: true }, "candidate was not persisted before rename");
            Check(cache.GetUploadAttempt("other", "p", hash) is null, "upload attempt crossed accounts");
        }
        web.RefuseUploadedRename = false;
        using (var cache = new SqliteCache(db))
        using (var client = Client(web))
        {
            var item = await Upload(client, newBytes, attempt: cache.GetUploadAttempt("u", "p", hash),
                rememberAttempt: value => cache.SaveUploadAttempt("u", "p", hash, value));
            Check(web.TokenCount == 1 && web.Items.Count == 1 && item.Name == "新建 & 测试.pptx", "reopened receipt duplicated upload");
            cache.RemoveWritebackReceipt("u", "p", hash);
            Check(cache.GetUploadAttempt("u", "p", hash) is null, "completed receipt retained stale attempt");
        }
    }
    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
});
await Test("corrupt suffixed candidate is never renamed or blindly uploaded again", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, CorruptDownload = true };
    using var client = Client(web);
    UploadAttempt? attempt = null;
    await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
    await Fails(() => Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value));
    Check(web.TokenCount == 1 && web.Items.Count == 1 && web.Items.Values.Single().Name.EndsWith("_001000.pptx"), "invalid candidate was renamed or duplicated");
});
await Test("new-file API rejects different same-name content", async () =>
{
    using var web = new WebServer();
    web.Add(100, "新建 & 测试.pptx", oldBytes);
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0 && web.Items[100].Bytes.SequenceEqual(oldBytes), "original overwritten");
});
await Test("overwrite: backup, upload, hash, persist ID, recycle old item", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    var item = await Replace(client, newBytes, id => web.Events.Add("remember:" + id));
    Check(web.Items[item.Id].Name == "report.docx" && !web.Items.ContainsKey(100), "replacement not committed");
    Check(web.Recycled[100].Name == "report_back.docx" && web.Recycled[100].Bytes.SequenceEqual(oldBytes), "old content not retained in recycle bin");
    Check(web.Events.IndexOf("rename:100:report_back.docx") < web.Events.IndexOf("token"), "backup must precede upload");
    Check(web.Events.IndexOf("download:" + item.Id) < web.Events.IndexOf("remember:" + item.Id)
        && web.Events.IndexOf("remember:" + item.Id) < web.Events.IndexOf("delete:100"), "old deleted before verified persistent new ID");
});
await Test("token failure restores original filename and bytes", async () =>
{
    using var web = new WebServer { FailToken = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Recycled.Count == 0, "rollback failed");
});
await Test("hash mismatch preserves new item and restores original", async () =>
{
    using var web = new WebServer { CorruptDownload = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Recycled.Count == 0, "corrupt upload deleted old");
    Check(web.Items.Values.Any(i => i.Id != 100 && i.Name.StartsWith("report_failed_")), "unverified upload not preserved");
});
await Test("lost final upload reply preserves data and restores original", async () =>
{
    using var web = new WebServer { LoseFinalReply = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Items.Count == 2 && web.Recycled.Count == 0, "ambiguous upload rollback");
});
await Test("cleanup failure resumes without another upload", async () =>
{
    using var web = new WebServer { RefuseDelete = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report_back.docx", "cleanup failure lost backup");
    web.RefuseDelete = false;
    await Replace(client, newBytes);
    Check(web.TokenCount == 1 && web.Items.Count == 1 && web.Recycled.ContainsKey(100), "cleanup reuploaded");
});
await Test("restart after backup rename resumes replacement", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report_back.docx", oldBytes);
    using var client = Client(web);
    await Replace(client, newBytes);
    Check(web.Items.Count == 1 && web.Recycled.ContainsKey(100), "backup restart failed");
});
await Test("restart after old deletion reuses verified new item", async () =>
{
    using var web = new WebServer();
    web.Add(200, "report.docx", newBytes);
    using var client = Client(web);
    var result = await Replace(client, newBytes);
    Check(result.Id == 200 && web.TokenCount == 0, "completed restart duplicated upload");
});
await Test("new folder uses addSave and verified rename", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var result = await client.CreateRemoteFolderAsync(7, 10, "新建文件夹 & 目录 (2)");
    Check(result.IsFolder && web.Items[result.Id].Name == "新建文件夹 & 目录 (2)", "folder not renamed");
});
await Test("directory rename failure resumes its recorded ID without another addSave", async () =>
{
    using var web = new WebServer { RenameHttpError = true };
    using var client = Client(web);
    ulong created = 0;
    await Fails(() => client.CreateRemoteFolderAsync(7, 10, "resumed", rememberCreated: id => created = id));
    Check(created != 0 && web.Items.Count == 1, "created ID not persisted before rename");
    web.RenameHttpError = false;
    var result = await client.CreateRemoteFolderAsync(7, 10, "resumed", createdId: created);
    Check(result.Id == created && web.Items.Count == 1, "directory recovery duplicated addSave");
});
await Test("move and rename verifies both source and destination", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    Check(await client.RenameRemoteItemAsync(7, 10, 100, false, 20, "moved.docx"), "move failed");
    Check(web.Items[100].Folder == 20 && web.Items[100].Name == "moved.docx", "move missed new name");
});
await Test("HTTP 500 with true body never confirms rename", async () =>
{
    using var web = new WebServer { RenameHttpError = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(async () => { await client.RenameRemoteItemAsync(7, 10, 100, false, 10, "other.docx"); });
});
await Test("cache folder move updates descendants, literal percent and underscore", () =>
{
    using var cache = new SqliteCache(":memory:");
    cache.SaveSubFolder(7, 30, 10, "a_%", @"C:\root\a_%");
    cache.SaveSubFolder(7, 31, 30, "child", @"C:\root\a_%\child");
    cache.SaveSubFolder(7, 32, 10, "sibling", @"C:\root\abc\child");
    cache.ApplyRemoteRename(new(7, 10, 30, 0), 20, "moved", @"C:\root\dest\moved");
    Check(cache.GetFolderLocalPath(7, 31) == @"C:\root\dest\moved\child", "stale descendant path");
    Check(cache.GetFolderLocalPath(7, 32) == @"C:\root\abc\child", "wildcard changed unrelated folder");
    cache.SaveWritebackReceipt("u", "p", "hash", 7, 10, "report_back.docx", 0, 100);
    cache.SaveWritebackReceipt("u", "p", "hash", 7, 10, "ignored", 200);
    var receipt = cache.GetWritebackReceipt("u", "p", "hash");
    Check(receipt is { Id: 200, OldId: 100, Name: "report_back.docx" }, "receipt lost old ID/name");
    Check(cache.GetWritebackReceipt("other", "p", "hash") is null, "receipt crossed account");
    Check(cache.HasOtherPendingWriteback("u", "p", "different") && !cache.HasOtherPendingWriteback("u", "p", "hash"), "changed-content recovery guard");
    cache.CompleteBoundWriteback("u", "p", 200, "hash");
    Check(cache.GetWritebackReceipt("u", "p", "hash") is not null, "removed pending mark-in-sync retry");
    cache.CompleteBoundWriteback("u", "p", 200, "next-edit");
    Check(!cache.HasOtherPendingWriteback("u", "p", "next-edit"), "old bound receipt blocks next edit");
    return Task.CompletedTask;
});
await Test("directory watcher discovers new files, waits for writer, persists upload across restart", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "_buildtmp", "writeback-regression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using var web = new WebServer { AssignUploadSuffix = true };
    using var client = Client(web);
    using var cache = new SqliteCache(":memory:");
    cache.SaveSubFolder(7, 10, 0, "root", root);
    Callbacks.Cache = cache;
    Callbacks.Client = client;
    Callbacks.UserKey = "watcher-test";
    Callbacks.SyncRootPath = root;
    Callbacks.Log = _ => { };
    var path = Path.Combine(root, "from-explorer.txt");
    var hash = Convert.ToHexString(SHA256.HashData(newBytes));
    try
    {
        Callbacks.StartWriteback();
        await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await writer.WriteAsync(newBytes);
            await writer.FlushAsync();
            await Task.Delay(3000);
            Check(web.TokenCount == 0, "uploaded a file still open for writing");
        }
        // Simulate the next ordinary LastWrite event after the application closes its handle.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (cache.GetWritebackReceipt("watcher-test", path, hash) is not { Id: > 0 } && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Callbacks.StopWriteback();
        Check(web.TokenCount == 1 && web.Items.Values.Any(i => i.Name == "from-explorer.txt" && i.Bytes.SequenceEqual(newBytes)), "watcher failed to upload new file");
        Check(File.ReadAllBytes(path).SequenceEqual(newBytes), "failed local CFAPI binding lost file bytes");
        // This ordinary temp directory deliberately has no SyncRoot, so native conversion
        // fails. Its durable receipt must let the real watcher reuse the verified upload.
        Callbacks.StartWriteback();
        await Task.Delay(3500);
        Callbacks.StopWriteback();
        Check(web.TokenCount == 1, "restart duplicated an upload after local binding failed");
    }
    finally
    {
        Callbacks.StopWriteback();
        Callbacks.Cache = null;
        Callbacks.Client = null;
        Callbacks.Log = null;
        Directory.Delete(root, true);
    }
});
await Test("watcher uploads a new directory tree and selects an available _back name", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "_buildtmp", "writeback-regression", Guid.NewGuid().ToString("N"));
    var sub = Path.Combine(root, "new-folder");
    Directory.CreateDirectory(sub);
    using var web = new WebServer();
    web.Add(100, "report.docx", oldBytes);
    web.Add(101, "report_back.docx", oldBytes);
    using var client = Client(web);
    using var cache = new SqliteCache(":memory:");
    cache.SaveSubFolder(7, 10, 0, "root", root);
    Callbacks.Cache = cache;
    Callbacks.Client = client;
    Callbacks.UserKey = "tree-test";
    Callbacks.SyncRootPath = root;
    Callbacks.Log = _ => { };
    var path = Path.Combine(root, "report.docx");
    var child = Path.Combine(sub, "child.txt");
    File.WriteAllBytes(path, newBytes);
    File.WriteAllBytes(child, newBytes);
    var hash = Convert.ToHexString(SHA256.HashData(newBytes));
    try
    {
        Callbacks.StartWriteback();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((cache.GetWritebackReceipt("tree-test", path, hash) is not { Id: > 0 }
            || cache.GetWritebackReceipt("tree-test", child, hash) is not { Id: > 0 }) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Callbacks.StopWriteback();
        Check(web.Recycled.TryGetValue(100, out var backup) && backup.Name == "report_back_1.docx", "backup name collision");
        Check(web.Items[101].Name == "report_back.docx", "existing backup was changed");
        var createdFolder = web.Items.Values.Single(i => i.Directory && i.Name == "new-folder");
        Check(web.Items.Values.Any(i => i.Folder == createdFolder.Id && i.Name == "child.txt" && i.Bytes.SequenceEqual(newBytes)), "nested file not uploaded");
        Check(File.ReadAllBytes(path).SequenceEqual(newBytes) && File.ReadAllBytes(child).SequenceEqual(newBytes), "local bytes changed");
    }
    finally
    {
        Callbacks.StopWriteback();
        Callbacks.Cache = null;
        Callbacks.Client = null;
        Callbacks.Log = null;
        Directory.Delete(root, true);
    }
});
await Test("watcher distinguishes confirmed read-only permission from unknown", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "_buildtmp", "writeback-regression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using var web = new WebServer { ReadOnly = true };
    using var client = Client(web);
    using var cache = new SqliteCache(":memory:");
    cache.SaveSubFolder(7, 10, 0, "root", root);
    var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
    Callbacks.Cache = cache;
    Callbacks.Client = client;
    Callbacks.UserKey = "readonly-log";
    Callbacks.SyncRootPath = root;
    Callbacks.Log = messages.Enqueue;
    var path = Path.Combine(root, "local.txt");
    File.WriteAllBytes(path, newBytes);
    try
    {
        Callbacks.StartWriteback();
        await WaitUntil(() => messages.Any(m => m.Contains("permission=ReadOnly")), "explicit read-only diagnostic");
        Callbacks.StopWriteback();
        Check(web.TokenCount == 0 && web.Items.Count == 0 && File.ReadAllBytes(path).SequenceEqual(newBytes), "read-only diagnostic performed a write or lost data");
        Check(!messages.Any(m => m.Contains("当前目录写入权限未确认")), "read-only incorrectly labeled unknown");
    }
    finally
    {
        Callbacks.StopWriteback();
        Callbacks.Cache = null;
        Callbacks.Client = null;
        Callbacks.Log = null;
        Directory.Delete(root, true);
    }
});
if (args.Contains("--native"))
await Test("native SyncRoot: new-file binding, external edits, rename and delete callbacks", async () =>
{
    var testBase = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "_buildtmp", "native-writeback", Guid.NewGuid().ToString("N")));
    var root = Path.Combine(testBase, "root");
    Directory.CreateDirectory(root);
    using var web = new WebServer();
    using var client = Client(web);
    using var cache = new SqliteCache(":memory:");
    cache.SaveSubFolder(7, 10, 0, "root", root);
    cache.SaveDirectoryPermission("native-test", 7, 10, DirectoryPermission.FullAccess);
    Callbacks.Cache = cache;
    Callbacks.Client = client;
    Callbacks.UserKey = "native-test";
    Callbacks.SyncRootPath = root;
    var nativeMessages = new System.Collections.Concurrent.ConcurrentQueue<string>();
    Callbacks.Log = message => { nativeMessages.Enqueue(message); Console.WriteLine(message); };
    Callbacks.SuppressDeleteRecovery = false;
    var registered = false;
    var connected = false;
    CF_CONNECTION_KEY connection = default;
    var callbacks = CallbackTable.Build();
    var bytes = new FileIdentity(7, 0, 10, 0).Serialize();
    var pointer = Marshal.AllocHGlobal(bytes.Length);
    try
    {
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        NativeSuccess(CfRegisterSyncRoot(root, new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(), ProviderName = "YuWang writeback regression",
            ProviderVersion = "1.0", ProviderId = Guid.NewGuid(), FileIdentity = pointer, FileIdentityLength = (uint)bytes.Length
        }, new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new() { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PROGRESSIVE },
            Population = new() { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_PARTIAL },
            InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME
        }, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE), "register native writeback root");
        registered = true;
        NativeSuccess(CfConnectSyncRoot(root, callbacks, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out connection), "connect native writeback root");
        connected = true;
        Callbacks.StartWriteback();
        // Seed only SQLite, with no virtual/disk/local ancestors. This reproduces the
        // prefetch path-not-found failure, including the fresh-cache branch after restart.
        cache.SaveDiskTree("native-test",
        [
            new() { Id = 80, NodeType = "container", ParentId = 0, Name = "Shared(I:)" },
            new() { Id = 81, NodeType = "container", ParentId = 80, Name = "Class" },
            new() { Id = 90, NodeType = "disk", ParentId = 81, Name = "Disk" }
        ]);
        var diskPath = Path.Combine(root, "Shared(I_)", "Class", "Disk");
        var folderPath = Path.Combine(diskPath, "nested");
        var leafPath = Path.Combine(folderPath, "leaf");
        cache.SaveFolder(90, 0, 0, "Disk", diskPath, [new() { Type = 'D', Id = 91, Name = "nested" }]);
        cache.SaveFolder(90, 91, 0, "nested", folderPath, [new() { Type = 'D', Id = 92, Name = "leaf" }]);
        cache.SaveFolder(90, 92, 91, "leaf", leafPath, [new() { Type = 'F', Id = 93, Name = "cloud.txt", Size = 12 }]);
        var tree = new PlaceholderDirectoryTree(root, cache, "native-test", 0, Console.WriteLine);
        var requestsBeforeTree = web.TokenCount;
        var roots = tree.PrepareCachedTree();
        Check(roots is [{ diskId: 90 }], "virtual disk tree not prepared");
        Check(ProviderIdentity.TryReadPlaceholderIdentity(leafPath, out var leaf) && leaf.FileId == 92 && leaf.FolderId == 91, "cached deep directory identity");
        Check(ProviderIdentity.TryReadPlaceholderIdentity(Path.Combine(leafPath, "cloud.txt"), out var cloud) && cloud.FileId == 93, "cached file placeholder missing");
        Check(PlaceholderUpdater.TryGetPlaceholderDataState(Path.Combine(leafPath, "cloud.txt"), out var cloudState) && cloudState.OnDiskDataSize == 0, "tree creation hydrated data");
        Check(cache.IsFolderCached(90, 92, TimeSpan.FromMinutes(30)), "tree materialization expired HTTP cache");
        tree.PrepareCachedTree();
        Check(web.TokenCount == requestsBeforeTree, "local cloud tree was uploaded as user content");
        var cachedCloudFile = Path.Combine(leafPath, "cloud.txt");
        File.SetAttributes(cachedCloudFile, File.GetAttributes(cachedCloudFile) & ~FileAttributes.ReadOnly);
        Check(PlaceholderUpdater.DeletePlaceholderSafe(cachedCloudFile, false), "remove fixture cloud placeholder");
        Check(PlaceholderUpdater.DeletePlaceholderSafe(leafPath, true), "remove fixture leaf directory");
        Check(PlaceholderUpdater.DeletePlaceholderSafe(folderPath, true), "remove fixture ancestor directory");
        Check(!Directory.Exists(folderPath), "repair test requires a genuinely missing ancestor");
        tree.EnsureDirectory(90, 92, leafPath);
        tree.PopulateDirectory(90, 92, leafPath, cache.ListCachedItems(90, 92));
        Check(Directory.Exists(folderPath) && ProviderIdentity.OwnsPlaceholder(Path.Combine(leafPath, "cloud.txt")), "missing cached ancestor not repaired");
        var nativeCollision = Path.Combine(diskPath, "local-content");
        Directory.CreateDirectory(nativeCollision);
        File.WriteAllText(Path.Combine(nativeCollision, "keep.txt"), "keep");
        cache.SaveSubFolder(90, 94, 0, "local-content", nativeCollision);
        await Fails(() => { tree.EnsureDirectory(90, 94, nativeCollision); return Task.CompletedTask; });
        Check(File.ReadAllText(Path.Combine(nativeCollision, "keep.txt")) == "keep", "ordinary local directory collision was changed");
        // Remove the fixture directory while it is still ordinary: it is deliberately
        // unrelated to the fake Web server and must not become a user upload.
        Directory.Delete(nativeCollision, true);
        cache.RemoveCachedItem(new(90, 0, 94, 0));
        Console.WriteLine("PASS native prebuilt virtual/disk/cached tree, cache-hit repair, no hydration and local collision preservation");
        var path = Path.Combine(root, "native.txt");
        await NativeIo("write", path, Convert.ToBase64String(oldBytes));
        await WaitUntil(() => ProviderIdentity.TryReadPlaceholderIdentity(path, out _)
            && PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state) && state.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, "new-file native binding");
        Check(ProviderIdentity.TryReadPlaceholderIdentity(path, out var first), "new identity absent");
        Check(File.ReadAllBytes(path).SequenceEqual(oldBytes), "native conversion truncated content");
        Check(web.TokenCount == 1, "native new-file upload missing");
        await NativeIo("write", path, Convert.ToBase64String(newBytes));
        await WaitUntil(() => ProviderIdentity.TryReadPlaceholderIdentity(path, out var id) && id.FileId != first.FileId
            && PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state) && state.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, "external edit replacement");
        Check(ProviderIdentity.TryReadPlaceholderIdentity(path, out var second), "replacement identity absent");
        Check(web.Recycled[first.FileId].Name == "native_back.txt" && File.ReadAllBytes(path).SequenceEqual(newBytes), "native overwrite content/backup");
        var renamed = Path.Combine(root, "native-renamed.txt");
        await NativeIo("rename", path, renamed);
        await WaitUntil(() => cache.GetFileLocalPath(second.FileId) == renamed, "rename callback cache update");
        await Task.Delay(3000);
        Check(web.Items[second.FileId].Name == "native-renamed.txt" && web.TokenCount == 2, "rename duplicated content upload");
        var existing = Path.Combine(root, "existing.txt");
        File.WriteAllBytes(existing, oldBytes);
        web.Add(600, "existing.txt", oldBytes);
        cache.SaveFileMetadata(7, 10, 600, "existing.txt", "2026-11-05 12:00:00", oldBytes.Length);
        Check(PlaceholderUpdater.TryBindIdentity(existing, new(7, 10, 600, 1), true), "seed previously downloaded cloud file");
        var existingRenamed = Path.Combine(root, "existing-renamed.txt");
        await NativeIo("rename", existing, existingRenamed);
        await WaitUntil(() => cache.GetFileLocalPath(600) == existingRenamed, "existing file rename callback");
        await Task.Delay(3000);
        Check(web.Items[600].Name == "existing-renamed.txt" && web.TokenCount == 2, "unhashed clean rename uploaded content");
        await NativeIo("delete", existingRenamed, "unused");
        await WaitUntil(() => cache.GetFileLocalPath(600) is null, "existing file delete completion");
        await NativeIo("delete", renamed, "unused");
        await WaitUntil(() => cache.GetFileLocalPath(second.FileId) is null, "delete callback cloud/cache completion");
        Check(web.Recycled.ContainsKey(second.FileId) && !File.Exists(renamed), "native delete did not reach cloud");
        var sourceDirectory = Path.Combine(root, "新建文件夹 (2)");
        var targetDirectory = Path.Combine(root, "target-parent");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        web.Items[700] = new(700, "新建文件夹 (2)", [], true, 10);
        web.Items[701] = new(701, "target-parent", [], true, 10);
        web.Items[702] = new(702, "nested", [], true, 700);
        web.Items[703] = new(703, "nested.txt", oldBytes, false, 702);
        cache.SaveSubFolder(7, 700, 10, "新建文件夹 (2)", sourceDirectory);
        cache.SaveSubFolder(7, 701, 10, "target-parent", targetDirectory);
        cache.SaveFolder(7, 700, 10, "新建文件夹 (2)", sourceDirectory, [new() { Type = 'D', Id = 702, Name = "nested" }]);
        var nested = Path.Combine(sourceDirectory, "nested");
        cache.SaveFolder(7, 702, 700, "nested", nested, [new() { Type = 'F', Id = 703, Name = "nested.txt", Size = oldBytes.Length }]);
        Check(PlaceholderUpdater.TryBindIdentity(sourceDirectory, new(7, 10, 700, 0), true), "seed source directory");
        Check(PlaceholderUpdater.TryBindIdentity(targetDirectory, new(7, 10, 701, 0), true), "seed target directory");
        PlaceholderCreator.CreateBatch(sourceDirectory, 7, 700, [(702, "nested", true, null, null)], readOnly: false);
        PlaceholderCreator.CreateBatch(nested, 7, 702, [(703, "nested.txt", false, oldBytes.Length, null)], readOnly: false);
        Check(cache.GetDirectoryPermission("native-test", 7, 701) == DirectoryPermission.Unknown, "target permission must start unknown");
        // A file can be moved into a newly created folder before Explorer has enumerated it.
        var movable = Path.Combine(root, "move-me.txt");
        File.WriteAllBytes(movable, oldBytes);
        web.Add(710, "move-me.txt", oldBytes);
        cache.SaveFileMetadata(7, 10, 710, "move-me.txt", "2026-11-05 12:00:00", oldBytes.Length);
        Check(PlaceholderUpdater.TryBindIdentity(movable, new(7, 10, 710, 1), true), "seed movable file");
        var movedFile = Path.Combine(targetDirectory, "move-me.txt");
        await NativeIo("rename", movable, movedFile);
        await WaitUntil(() => cache.GetFileLocalPath(710) == movedFile, "file move into unenumerated target");
        Check(ProviderIdentity.TryReadPlaceholderIdentity(movedFile, out var movedIdentity) && movedIdentity.FolderId == 701, "moved file retained stale parent identity");
        var movedDirectory = Path.Combine(targetDirectory, "新建文件夹 (2)");
        await NativeIo("move-dir", sourceDirectory, movedDirectory);
        await WaitUntil(() => cache.GetFolderLocalPath(7, 700) == movedDirectory, "directory move completion");
        Check(!Directory.Exists(sourceDirectory) && ProviderIdentity.TryReadPlaceholderIdentity(movedDirectory, out var movedFolder)
            && movedFolder.FolderId == 701 && movedFolder.FileId == 700, "directory move left source or stale parent");
        Check(cache.GetFolderLocalPath(7, 702) == Path.Combine(movedDirectory, "nested") && web.Items[700].Folder == 701, "directory descendant paths not moved");
        await NativeIo("try-delete-dir", targetDirectory, "unused");
        Check(Directory.Exists(targetDirectory), "nonempty directory unexpectedly deleted");
        await NativeIo("delete-tree", targetDirectory, "unused");
        await WaitUntil(() => cache.GetFolderLocalPath(7, 701) is null, "retry target-parent recursive delete after failed attempt");
        Check(!Directory.Exists(targetDirectory) && web.Recycled.ContainsKey(701), "target parent remained undeletable after moves");
        Console.WriteLine("PASS native file/folder moves, closing parenthesis, unknown target permission, and target-parent delete retry");
        // Pause after serialization but before the server ACK: new ordinary files must
        // expose actual Shell transfer state before becoming cloud placeholders.
        var progressPath = Path.Combine(root, "upload-progress.bin");
        var progressData = new byte[8 * 1024];
        RandomNumberGenerator.Fill(progressData);
        var serialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.BeforeChunkReply = () => { serialized.TrySetResult(); return releaseAck.Task; };
        try
        {
            await NativeIo("write", progressPath, Convert.ToBase64String(progressData));
            await serialized.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitUntil(() => nativeMessages.Any(m => m.Contains("phase=Uploading") && m.Contains(progressPath)
                && m.Contains($"sent={progressData.Length}/{progressData.Length} confirmed=0")), "report sent bytes separately from server ACK");
            Check(!nativeMessages.Any(m => m.Contains(progressPath) && m.Contains("phase=Completed")), "upload marked complete before server ACK");
            Check(!nativeMessages.Any(m => m.Contains(progressPath) && m.Contains("进度上报不可用")), "Shell rejected active upload progress");
            Check(!ProviderIdentity.TryReadPlaceholderIdentity(progressPath, out _), "ordinary upload was bound before verification");
            releaseAck.TrySetResult();
            await WaitUntil(() => ProviderIdentity.TryReadPlaceholderIdentity(progressPath, out _)
                && nativeMessages.Any(m => m.Contains(progressPath) && m.Contains("phase=Completed")), "complete progress only after verified identity binding");
            Check(!nativeMessages.Any(m => m.Contains(progressPath) && m.Contains("进度上报不可用")), "Shell rejected completed transfer state");
            Check(File.ReadAllBytes(progressPath).SequenceEqual(progressData), "progress reporting changed uploaded data");
        }
        finally { web.BeforeChunkReply = null; releaseAck.TrySetResult(); }
        foreach (var offset in new[] { 3, newBytes.Length })
        {
            var resumedPath = Path.Combine(root, $"upload-resumed-{offset}.bin");
            web.InitialOffset = offset;
            web.InitialBytes = newBytes[..offset];
            var previousChunkCount = web.Ranges.Count;
            try
            {
                await NativeIo("write", resumedPath, Convert.ToBase64String(newBytes));
                await WaitUntil(() => ProviderIdentity.TryReadPlaceholderIdentity(resumedPath, out _)
                    && nativeMessages.Any(m => m.Contains(resumedPath) && m.Contains("phase=Completed")
                        && m.Contains($"resumed={offset}")), "bind verified resumed upload and report resumed bytes");
                Check(PlaceholderUpdater.TryGetPlaceholderDataState(resumedPath, out var resumedState)
                    && resumedState.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, "resumed upload not in sync");
                Check(File.ReadAllBytes(resumedPath).SequenceEqual(newBytes), "resumed upload changed local bytes");
                Check(web.Ranges.Count - previousChunkCount == (offset == newBytes.Length ? 0 : 1), "resumed upload sent an incorrect number of chunks");
                Check(!nativeMessages.Any(m => m.Contains(resumedPath) && m.Contains("进度上报不可用")), "Shell rejected resumed progress");
            }
            finally { web.InitialOffset = 0; web.InitialBytes = []; }
        }
        var failedPath = Path.Combine(root, "upload-failed.bin");
        web.BadOffset = true;
        await NativeIo("write", failedPath, Convert.ToBase64String(newBytes));
        await WaitUntil(() => nativeMessages.Any(m => m.Contains(failedPath) && m.Contains("phase=Failed")), "report failed upload without completing progress");
        Check(!nativeMessages.Any(m => m.Contains(failedPath) && (m.Contains("phase=Completed") || m.Contains("进度上报不可用"))), "failed upload reported completed or Shell rejected failure state");
        Check(!ProviderIdentity.TryReadPlaceholderIdentity(failedPath, out _) && File.ReadAllBytes(failedPath).SequenceEqual(newBytes), "failed upload marked synchronized or lost data");
        web.BadOffset = false;
        var cancelledPath = Path.Combine(root, "upload-cancelled.bin");
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.BeforeChunkReply = () => { cancelSent.TrySetResult(); return cancelRelease.Task; };
        try
        {
            await NativeIo("write", cancelledPath, Convert.ToBase64String(newBytes));
            await cancelSent.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Callbacks.StopWriteback();
            Check(nativeMessages.Any(m => m.Contains(cancelledPath) && m.Contains("phase=Cancelled"))
                && !nativeMessages.Any(m => m.Contains(cancelledPath) && (m.Contains("phase=Completed") || m.Contains("进度上报不可用"))), "cancelled transfer completed or Shell rejected paused state");
            Check(File.ReadAllBytes(cancelledPath).SequenceEqual(newBytes), "cancelled upload lost bytes");
        }
        finally { web.BeforeChunkReply = null; cancelRelease.TrySetResult(); }
        Console.WriteLine("PASS native upload Shell progress before ACK, after binding, failure and cancellation");
    }
    finally
    {
        Callbacks.StopWriteback();
        Callbacks.SuppressDeleteRecovery = true;
        if (connected) NativeSuccess(CfDisconnectSyncRoot(connection), "disconnect native test");
        if (registered) NativeSuccess(CfUnregisterSyncRoot(root), "unregister native test");
        GC.KeepAlive(callbacks);
        Marshal.FreeHGlobal(pointer);
        Callbacks.Cache = null;
        Callbacks.Client = null;
        Callbacks.Log = null;
        Callbacks.SuppressDeleteRecovery = false;
        var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "_buildtmp", "native-writeback")) + Path.DirectorySeparatorChar;
        if (!testBase.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Refusing unsafe fixture cleanup.");
        foreach (var localFile in Directory.EnumerateFiles(testBase, "*", SearchOption.AllDirectories))
            File.SetAttributes(localFile, File.GetAttributes(localFile) & ~FileAttributes.ReadOnly);
        Directory.Delete(testBase, true);
    }
});
Console.WriteLine($"Writeback regression: {passed} passed.");

static void NativeSuccess(HRESULT result, string operation)
{
    if (result.Failed) throw new IOException(operation + ": " + result);
}
static async Task WaitUntil(Func<bool> predicate, string name)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(100);
    Check(predicate(), "Timed out: " + name);
}
static async Task NativeIo(string operation, string path, string value)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in new[] { "--native-io", operation, path, value }) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new IOException("Failed to start native fixture IO.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { child.Kill(); throw; }
    Check(child.ExitCode == 0, "External IO failed: " + await child.StandardError.ReadToEndAsync());
}
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

sealed class WebServer : HttpMessageHandler
{
    public sealed record Item(ulong Id, string Name, byte[] Bytes, bool Directory = false, ulong Folder = 10);
    public Dictionary<ulong, Item> Items { get; } = [];
    public Dictionary<ulong, Item> Recycled { get; } = [];
    public List<string> Events { get; } = [];
    public List<string> Ranges { get; } = [];
    public bool ReadOnly, WrongPage, PartialPage, BadOffset, FailToken, CorruptDownload, LoseFinalReply, RefuseDelete, RenameHttpError;
    public long InitialOffset;
    public byte[] InitialBytes = [];
    public object? InitialOffsetReply;
    public bool PublishCompletedResume = true;
    public bool UseResumeIncompleteStatus;
    public bool AssignUploadSuffix, RefuseUploadedRename, HidePublishedUploads, PublishExtraUpload, FailPartialChunkOnce;
    public bool StaleUploadListings;
    public int PublishedListingDelay, FreshListRequests;
    public Action? AfterPublishedListing;
    private int _hiddenUploadReads;
    private readonly HashSet<ulong> _publishedUploads = [];
    public int TokenCount;
    public Func<Task>? BeforeChunkReply;
    private ulong _nextId = 1000, _currentFolder = 10, _cut;
    private readonly Dictionary<string, (string Name, ulong Folder, long Size, MemoryStream Data)> _uploads = [];
    public void Add(ulong id, string name, byte[] data) => Items[id] = new(id, name, data);
    static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text) };
    static Dictionary<string, string> Fields(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
            p => p.Length == 1 ? "" : Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    string Page(ulong folder)
    {
        var b = new StringBuilder($"<form id='pageForm'><input name='diskid' value='{(WrongPage ? 999 : 7)}'><input name='folderid' value='{folder}'><input name='userid' value='144'><input name='parentid' value='0'>");
        foreach (var action in new[] { "new", "delete", "cut", "paste", "upload" })
            b.Append(ReadOnly ? $"<span class='toolbar_{action}1'>disabled</span>" : $"<a href='javascript:write()'><span class='toolbar_{action}'>write</span></a>");
        foreach (var item in Items.Values.Where(i => i.Folder == folder && (!HidePublishedUploads || !_publishedUploads.Contains(i.Id))))
            b.Append($"<div onclick=\"showItem('{(item.Directory ? 'D' : 'F')}','{item.Id}')\"><span title=\"文件名：{WebUtility.HtmlEncode(item.Name)}<br>文件大小：{item.Bytes.Length} B<br>上传时间:2026-11-05 12:00:00\"></span></div>");
        if (PartialPage) b.Append("<div onclick=\"showItem('F','99999')\"></div>");
        return b.Append("</form>").ToString();
    }
    private void PublishUpload((string Name, ulong Folder, long Size, MemoryStream Data) upload)
    {
        _hiddenUploadReads = PublishedListingDelay;
        for (var count = 0; count < (PublishExtraUpload ? 2 : 1); count++)
        {
            var id = _nextId++;
            var name = AssignUploadSuffix ? Path.GetFileNameWithoutExtension(upload.Name) + "_" + id.ToString("D6") + Path.GetExtension(upload.Name) : upload.Name;
            Items[id] = new(id, name, upload.Data.ToArray(), false, upload.Folder);
            _publishedUploads.Add(id);
        }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var q = Fields(uri.Query);
        var method = q.GetValueOrDefault("method", "");
        if (method == "list")
        {
            _currentFolder = ulong.Parse(q["folderid"]);
            var fresh = q.ContainsKey("_");
            if (fresh)
            {
                FreshListRequests++;
                CheckRange(request.Headers.CacheControl is { NoCache: true, NoStore: true });
            }
            var previousHide = HidePublishedUploads;
            if (_publishedUploads.Count > 0)
            {
                HidePublishedUploads |= (StaleUploadListings && !fresh) || _hiddenUploadReads-- > 0;
                AfterPublishedListing?.Invoke();
            }
            try { return Text(Page(_currentFolder)); }
            finally { HidePublishedUploads = previousHide; }
        }
        if (method == "beforePaste") return Text(ReadOnly ? "forbidden" : _cut == 0 ? "false" : "true");
        if (method == "beforeUpload") return Text("<html>Stream uploader</html>");
        if (uri.AbsolutePath == "/TokenServlet")
        {
            CheckRange(q.ContainsKey("_"));
            Events.Add("token");
            TokenCount++;
            if (FailToken) return Text("failure", HttpStatusCode.ServiceUnavailable);
            var token = "test-token-" + TokenCount;
            var data = new MemoryStream();
            data.Write(InitialBytes);
            _uploads[token] = (q["name"], ulong.Parse(q["folderid"]), long.Parse(q["size"]), data);
            return Text(JsonSerializer.Serialize(new { success = true, token }));
        }
        if (uri.AbsolutePath == "/StreamServlet")
        {
            var upload = _uploads[q["token"]];
            if (request.Method == HttpMethod.Get)
            {
                CheckRange(q.ContainsKey("_"));
                if (PublishCompletedResume && InitialOffset > 0 && InitialOffset == upload.Size && InitialOffsetReply is null)
                {
                    PublishUpload(upload);
                }
                return Text(JsonSerializer.Serialize(new { success = true, start = InitialOffsetReply ?? InitialOffset }),
                    UseResumeIncompleteStatus && InitialOffset < upload.Size ? (HttpStatusCode)308 : HttpStatusCode.OK);
            }
            Events.Add("chunk");
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var range = request.Content.Headers.GetValues("Content-Range").Single();
            Ranges.Add(range);
            if (request.Content.Headers.ContentType?.MediaType != "application/octet-stream") throw new InvalidOperationException("multipart upload");
            var start = upload.Data.Length;
            CheckRange(range == $"bytes {start}-{start + bytes.Length}/{upload.Size}");
            upload.Data.Write(bytes);
            if (upload.Data.Length == upload.Size)
            {
                PublishUpload(upload);
                if (LoseFinalReply) throw new HttpRequestException("Simulated lost final reply");
            }
            else if (FailPartialChunkOnce)
            {
                FailPartialChunkOnce = false;
                throw new HttpRequestException("Simulated interrupted partial upload");
            }
            if (BeforeChunkReply is { } beforeReply) await beforeReply().WaitAsync(cancellationToken);
            return Text(JsonSerializer.Serialize(new { success = true, start = upload.Data.Length + (BadOffset ? 1 : 0) }),
                UseResumeIncompleteStatus && upload.Data.Length < upload.Size ? (HttpStatusCode)308 : HttpStatusCode.OK);
        }
        var f = request.Content is null ? new Dictionary<string, string>() : Fields(await request.Content.ReadAsStringAsync(cancellationToken));
        if (method == "downSelectFile")
        {
            var id = ulong.Parse(f["selectitemid"]);
            Events.Add("download:" + id);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(CorruptDownload ? [1, 2, 3] : Items[id].Bytes) };
        }
        if (method is "changeFileName" or "changeFolderName")
        {
            if (RenameHttpError) return Text("true", HttpStatusCode.InternalServerError);
            var id = ulong.Parse(f["fid"]);
            if (RefuseUploadedRename && _publishedUploads.Contains(id)) return Text("false");
            Events.Add("rename:" + id + ":" + f["newname"]);
            Items[id] = Items[id] with { Name = f["newname"] };
            return Text("true");
        }
        if (method == "delBatchRecord")
        {
            var id = ulong.Parse(f["checkid"][2..]);
            Events.Add("delete:" + id);
            if (!RefuseDelete) { Recycled[id] = Items[id]; Items.Remove(id); }
            return Text("true");
        }
        if (method == "addSave")
        {
            var id = _nextId++;
            Items[id] = new(id, "新建文件夹", [], true, _currentFolder);
            return Text(Page(_currentFolder));
        }
        if (method == "beforeCut")
        {
            _cut = ulong.Parse(f["checkids"].TrimEnd(';')[2..]);
            return Text("true");
        }
        if (method == "pasteRecord")
        {
            Items[_cut] = Items[_cut] with { Folder = ulong.Parse(f["folderid"]) };
            _cut = 0;
            return Text(Page(_currentFolder));
        }
        throw new InvalidOperationException("Unexpected fixture request: " + uri.AbsolutePath + " method=" + method);
    }
    static void CheckRange(bool condition) { if (!condition) throw new InvalidOperationException("wrong upload Content-Range"); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var upload in _uploads.Values) upload.Data.Dispose();
        base.Dispose(disposing);
    }
}
