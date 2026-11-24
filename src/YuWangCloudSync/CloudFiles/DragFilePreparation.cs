using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YuWangCloudSync.Core;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

internal sealed record DragPreparationProgress(string Stage, string? FileName = null,
    long? CompletedBytes = null, long? TotalBytes = null, int CompletedFiles = 0, int TotalFiles = 0,
    bool IsComplete = false)
{
    internal double? Percent => CompletedBytes is { } completed && TotalBytes is { } total
        ? total > 0 ? Math.Clamp(completed * 100d / total, 0, 100)
            : TotalFiles > 0 ? CompletedFiles * 100d / TotalFiles : IsComplete ? 100 : 0
        : null;
}

/// <summary>Owns the read handles until OLE returns. No work here runs inside DoDragDrop.</summary>
internal enum SyncRootAvailability { Unknown, OnlineOnly, Local, Pinned }

internal sealed class DragFilePreparation : IDisposable
{
    private sealed class Entry(string path, SafeFileHandle handle, long size)
    {
        internal string Path { get; } = path;
        internal SafeFileHandle Handle { get; } = handle;
        internal long Size { get; } = size;
        internal long? ReadyBytes { get; set; }
    }
    private readonly List<Entry> _entries = [];
    private const uint IncompleteAttributes = 0x00441000; // OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS

    internal static async Task<DragFilePreparation> CreateAsync(IReadOnlyList<string> paths,
        Func<bool> backendAvailable, Action<string> progress, CancellationToken cancellationToken,
        Action<DragPreparationProgress>? detailedProgress = null)
    {
        // The worker owns all native resources even if cancellation lets the UI return first.
        var worker = Task.Run(() =>
        {
            var preparation = new DragFilePreparation();
            try
            {
                foreach (var path in paths) preparation.CollectEntry(path, backendAvailable, progress, detailedProgress, cancellationToken);
                preparation.HydrateEntries(backendAvailable, progress, detailedProgress, cancellationToken);
                preparation.ValidateReady();
                cancellationToken.ThrowIfCancellationRequested();
                detailedProgress?.Invoke(new("文件内容已就绪", CompletedBytes: preparation._entries.Sum(entry => entry.Size),
                    TotalBytes: preparation._entries.Sum(entry => entry.Size), CompletedFiles: preparation._entries.Count,
                    TotalFiles: preparation._entries.Count, IsComplete: true));
                return preparation;
            }
            catch { preparation.Dispose(); throw; }
        });
        try { return await worker.WaitAsync(cancellationToken); }
        catch
        {
            // CancelIoEx can complete later. Do not free OVERLAPPED or close its event before that.
            _ = worker.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                else _ = task.Exception;
            }, TaskScheduler.Default);
            throw;
        }
    }

    private void CollectEntry(string path, Func<bool> backendAvailable, Action<string> progress,
        Action<DragPreparationProgress>? detailedProgress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!backendAvailable()) throw new IOException("同步引擎未运行或正在停止，请等待同步连接恢复后再拖放。");
        detailedProgress?.Invoke(new("正在统计文件", Path.GetFileName(path), TotalFiles: _entries.Count));
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0 && !ProviderIdentity.OwnsPlaceholder(path))
                throw new IOException($"无法拖放指向其他位置的目录：{path}");
            progress($"正在准备文件夹“{Path.GetFileName(path)}”…");
            // Enumerating an on-demand directory runs on this worker, never on the UI/OLE thread.
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                CollectEntry(child, backendAvailable, progress, detailedProgress, token);
            return;
        }

        var handle = CreateFileW(path, 0x80000000, 7, 0, 3, 0x40200000, 0); // READ, OVERLAPPED, OPEN_REPARSE_POINT
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeError(path, error);
        }
        try
        {
            if (!GetFileSizeEx(handle, out var size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _entries.Add(new(path, handle, size));
        }
        catch { handle.Dispose(); throw; }
    }

    private void HydrateEntries(Func<bool> backendAvailable, Action<string> progress,
        Action<DragPreparationProgress>? detailedProgress, CancellationToken token)
    {
        var totalBytes = _entries.Sum(entry => entry.Size);
        foreach (var entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            entry.ReadyBytes = NeedsHydration(entry.Handle) ? ReadHydratedBytes(entry) : entry.Size;
        }
        var completedFiles = 0;
        void Report(Entry entry)
        {
            // Include already cached bytes, including files that will be processed later.
            // An unavailable native counter produces indeterminate UI, never a guessed value.
            long? ready = 0;
            foreach (var file in _entries) ready += file.ReadyBytes;
            detailedProgress?.Invoke(new("正在下载文件", Path.GetFileName(entry.Path), ready, totalBytes,
                completedFiles, _entries.Count));
        }
        foreach (var entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            if (!backendAvailable()) throw new IOException("同步引擎已停止，无法继续准备文件。");
            progress($"正在准备文件“{Path.GetFileName(entry.Path)}”…");
            Report(entry);
            if (NeedsHydration(entry.Handle))
                Hydrate(entry.Handle, entry.Path, token, () =>
                {
                    // Keep a high-water mark: committing a range moves bytes from in-flight to on-disk.
                    var sampled = ReadHydratedBytes(entry);
                    entry.ReadyBytes = sampled is { } bytes && entry.ReadyBytes is { } previous
                        ? Math.Max(bytes, previous) : sampled;
                    Report(entry);
                });
            token.ThrowIfCancellationRequested();
            if (NeedsHydration(entry.Handle)) throw new IOException($"文件“{Path.GetFileName(entry.Path)}”未下载完整，已取消拖放。");
            entry.ReadyBytes = entry.Size;
            completedFiles++;
            Report(entry);
        }
    }

    private static unsafe long? ReadHydratedBytes(Entry entry)
    {
        // CF_PLACEHOLDER_STANDARD_INFO begins with OnDiskDataSize; FileId (the NTFS ID that the
        // provider's FETCH_DATA callback also sees) is at offset 40. Allow the full identity tail
        // (up to 4 KiB); querying metadata never reads/hydrates file data.
        var buffer = stackalloc byte[8192];
        var result = GetPlaceholderInfo(entry.Handle, 1 /* STANDARD */, (nint)buffer, 8192, out var returned);
        if (result < 0 || returned < sizeof(long)) return null;
        var onDisk = *(long*)buffer;
        // OnDiskDataSize only grows per committed range; add bytes already received for ranges in flight.
        if (returned >= 48) onDisk += Callbacks.GetInFlightHydrationBytes(*(long*)(buffer + 40));
        return Math.Clamp(onDisk, 0, entry.Size);
    }

    private static unsafe void Hydrate(SafeFileHandle handle, string path, CancellationToken token, Action reportProgress)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        overlapped->EventHandle = completed.SafeWaitHandle.DangerousGetHandle();
        try
        {
            token.ThrowIfCancellationRequested();
            var hr = CfHydratePlaceholder(handle, 0, -1 /* CF_EOF */, 0, (nint)overlapped);
            // Vanara HRESULT.Code exposes the Win32 code, not the full HRESULT bit pattern.
            if (hr.Failed && hr.Code == 997) // HRESULT_FROM_WIN32(ERROR_IO_PENDING)
            {
                var pointer = (nint)overlapped;
                using var registration = token.Register(() => CancelIoEx(handle, pointer));
                var drained = false;
                try
                {
                    // Sample persisted bytes while the independent hydration request runs.
                    // The UI thread and the OLE loop never wait for this operation.
                    while (!completed.WaitOne(100))
                    {
                        token.ThrowIfCancellationRequested();
                        reportProgress();
                    }
                    var success = GetOverlappedResult(handle, pointer, out _, true);
                    var error = Marshal.GetLastWin32Error();
                    drained = true;
                    token.ThrowIfCancellationRequested();
                    if (!success) throw NativeError(path, error);
                }
                finally
                {
                    // A progress callback failure must also drain pending IO before freeing
                    // OVERLAPPED, its event or the file handle.
                    if (!drained)
                    {
                        CancelIoEx(handle, pointer);
                        GetOverlappedResult(handle, pointer, out _, true);
                    }
                }
            }
            else if (hr.Failed) throw NativeError(path, (int)hr.Code & 0xffff);
            token.ThrowIfCancellationRequested();
            ExplorerStateNotifier.NotifyItem(path);
        }
        finally { NativeMemory.Free(overlapped); }
    }

    internal void ValidateReady()
    {
        foreach (var entry in _entries)
            if (NeedsHydration(entry.Handle))
                throw new IOException($"文件“{Path.GetFileName(entry.Path)}”内容尚未就绪，已取消拖放。");
    }

    // Inspect attributes/tag/size without opening the content or triggering hydration.
    internal static (long Size, long? ReadyBytes, bool IsReady) ReadMetadata(string path)
    {
        using var handle = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) throw NativeError(path, Marshal.GetLastWin32Error());
        var partial = NeedsHydration(handle);
        if (!GetFileSizeEx(handle, out var size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (size, partial ? ReadHydratedBytes(new Entry(path, handle, size)) : size, !partial);
    }

    internal static SyncRootAvailability ReadAvailability(string path)
    {
        using var handle = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) return SyncRootAvailability.Unknown;
        if (NeedsHydration(handle, out var attributes)) return SyncRootAvailability.OnlineOnly;
        const uint pinned = 0x00080000; // FILE_ATTRIBUTE_PINNED, independent of hydration
        return (attributes & pinned) != 0 ? SyncRootAvailability.Pinned : SyncRootAvailability.Local;
    }

    private static bool NeedsHydration(SafeFileHandle handle) => NeedsHydration(handle, out _);

    private static bool NeedsHydration(SafeFileHandle handle, out uint attributes)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        attributes = info.Attributes;
        var state = CfGetPlaceholderStateFromAttributeTag((Vanara.PInvoke.FileFlagsAndAttributes)info.Attributes, info.ReparseTag);
        if (state == CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_INVALID)
            throw new IOException("无法读取文件的占位符状态。");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            && (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) == 0)
            throw new IOException("无法拖放符号链接或未识别的重解析文件，请选择原始文件后重试。");
        return (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL) != 0
            || (info.Attributes & IncompleteAttributes) != 0;
    }

    private static IOException NativeError(string path, int code)
    {
        var reason = code switch
        {
            362 => "同步引擎未运行或连接已断开",
            5 => "没有访问权限",
            32 => "文件正被其他程序占用",
            53 or 64 or 1231 or 1232 or 426 => "网络已中断，请检查网络后重试",
            121 or 1460 => "下载超时，请检查网络后重试",
            389 => "云操作不成功，请检查网络、账户权限与同步日志",
            _ => new Win32Exception(code).Message,
        };
        return new IOException($"无法准备“{Path.GetFileName(path)}”：{reason}（错误 {code}）。");
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.Handle.Dispose();
        _entries.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);
    [DllImport("cldapi.dll", EntryPoint = "CfGetPlaceholderInfo", ExactSpelling = true)]
    private static extern int GetPlaceholderInfo(SafeFileHandle handle, int infoClass, nint buffer, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, nint overlapped, out uint transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
