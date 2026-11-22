using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YuWangCloudSync.Core;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Owns the read handles until OLE returns. No work here runs inside DoDragDrop.</summary>
internal sealed class DragFilePreparation : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private readonly List<string> _files = [];
    private const uint IncompleteAttributes = 0x00441000; // OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS

    internal static async Task<DragFilePreparation> CreateAsync(IReadOnlyList<string> paths,
        Func<bool> backendAvailable, Action<string> progress, CancellationToken cancellationToken)
    {
        // The worker owns all native resources even if cancellation lets the UI return first.
        var worker = Task.Run(() =>
        {
            var preparation = new DragFilePreparation();
            try
            {
                foreach (var path in paths) preparation.PrepareEntry(path, backendAvailable, progress, cancellationToken);
                preparation.ValidateReady();
                cancellationToken.ThrowIfCancellationRequested();
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

    private void PrepareEntry(string path, Func<bool> backendAvailable, Action<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!backendAvailable()) throw new IOException("同步引擎未运行或正在停止，请等待同步连接恢复后再拖放。");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0 && !ProviderIdentity.OwnsPlaceholder(path))
                throw new IOException($"无法拖放指向其他位置的目录：{path}");
            progress($"正在准备文件夹“{Path.GetFileName(path)}”…");
            // Enumerating an on-demand directory runs on this worker, never on the UI/OLE thread.
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                PrepareEntry(child, backendAvailable, progress, token);
            return;
        }

        progress($"正在准备文件“{Path.GetFileName(path)}”…");
        var handle = CreateFileW(path, 0x80000000, 7, 0, 3, 0x40200000, 0); // READ, OVERLAPPED, OPEN_REPARSE_POINT
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeError(path, error);
        }
        try
        {
            if (NeedsHydration(handle)) Hydrate(handle, path, token);
            token.ThrowIfCancellationRequested();
            if (NeedsHydration(handle)) throw new IOException($"文件“{Path.GetFileName(path)}”仍未完整水化，已取消拖放。");
            _handles.Add(handle);
            _files.Add(path);
        }
        catch { handle.Dispose(); throw; }
    }

    private static unsafe void Hydrate(SafeFileHandle handle, string path, CancellationToken token)
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
                // Only this independent worker waits. Cancellation requests native cancellation,
                // then drains completion before releasing any memory referenced by the kernel.
                if (!GetOverlappedResult(handle, pointer, out _, true))
                {
                    var error = Marshal.GetLastWin32Error();
                    token.ThrowIfCancellationRequested();
                    throw NativeError(path, error);
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
        for (var index = 0; index < _handles.Count; index++)
            if (NeedsHydration(_handles[index]))
                throw new IOException($"文件“{Path.GetFileName(_files[index])}”内容尚未就绪，已取消拖放。");
    }

    private static bool NeedsHydration(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
        foreach (var handle in _handles) handle.Dispose();
        _handles.Clear();
        _files.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
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
