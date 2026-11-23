using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using YuWangCloudSync.CloudFiles;

namespace YuWangCloudSync.Tray;

internal static unsafe class VirtualFileDragSelfTest
{
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    internal static void Run(string path, byte[] expected, Action<bool, string> check,
        Func<CancellationToken, Task<DragFilePreparation>>? prepare = null)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { RunCore(path, expected, check, prepare); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("虚拟拖放回归超时。");
        if (failure is not null) throw new InvalidOperationException("虚拟拖放回归失败。", failure);
    }
    private static void RunCore(string path, byte[] expected, Action<bool, string> check,
        Func<CancellationToken, Task<DragFilePreparation>>? prepare)
    {
        var initialized = CoInitializeEx(0, 2);
        Marshal.ThrowExceptionForHR(initialized);
        try
        {
            check(NativeFileDrag.CheckOriginalItemVisual(new DragItemVisual(new byte[32 * 16 * 4], 32, 16, 2, 2)),
                "original item image initializes on the actual virtual data object without leaking GDI/USER resources");
            RunPrivateMedia(check);
            RunCompletionWithUnusedStream(check);
            var requested = 0; var disposed = 0;
            using var gate = new ManualResetEventSlim(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var entries = new VirtualDragEntry[]
            {
                new("目录", true, null, null),
                new("目录\\延迟内容.txt", false, expected.LongLength, DateTime.UtcNow, token =>
                {
                    Interlocked.Increment(ref requested);
                    gate.Wait(token);
                    IDisposable lease = prepare is null ? new Lease(() => Interlocked.Increment(ref disposed)) : prepare(token).GetAwaiter().GetResult();
                    return Task.FromResult(new VirtualFileContent(path, lease));
                }),
                new("大文件.bin", false, (5L << 30) + 123, null),
                new("空文件.txt", false, 0, null),
            };
            using (var data = new VirtualFileDataObject(entries, timeout.Token))
            {
                data.SetSourceThread();
                var descriptor = F("FileGroupDescriptorW", 1, -1);
                var content = F("FileContents", 4, 1);
                var hdrop = new VirtualFileDataObject.Format { Id = 15, Aspect = 1, Medium = 1, Index = -1 };
                check(Query(data.Pointer, hdrop) < 0 && Query(data.Pointer, F("Shell IDList Array", 1, -1)) < 0,
                    "virtual data never advertises raw paths or Shell PIDLs");
                var medium = GetData(data.Pointer, descriptor);
                try
                {
                    var memory = GlobalLock(medium.Value);
                    try
                    {
                        check(Marshal.ReadInt32(memory) == 4
                            && Marshal.PtrToStringUni(memory + 4 + 592 + 72) == "目录\\延迟内容.txt"
                            && Marshal.ReadInt32(memory + 4 + 2 * 592 + 64) == 1
                            && (uint)Marshal.ReadInt32(memory + 4 + 2 * 592 + 68) == (uint)(((5L << 30) + 123) & uint.MaxValue),
                            "Unicode descriptors preserve directories, empty files and sizes above 4 GiB");
                    }
                    finally { GlobalUnlock(medium.Value); }
                }
                finally { ReleaseStgMedium(ref medium); }
                var formats = ReadFormats(data.Pointer);
                check(formats.Length == 3 && !formats.Contains((ushort)15), "enumeration contains only descriptors, content and copy effect");
                check(Query(data.Pointer, F("FileContents", 4, -1)) == 0 && Query(data.Pointer, F("FileContents", 4, 0)) < 0
                    && Query(data.Pointer, F("FileContents", 1, 1)) < 0
                    && Query(data.Pointer, F("FileContents", 4, 99)) < 0,
                    "directory streams, wrong media and invalid indices are rejected");
                medium = GetData(data.Pointer, content);
                try
                {
                    byte* stat = stackalloc byte[80];
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, void*, uint, int>)(*(nint**)medium.Value)[12])(medium.Value, stat, 1));
                    check(requested == 0 && *(long*)(stat + 16) == expected.LongLength,
                        "GetData and Stat return a lazy stream without hydrating during drag");
                    var asyncId = new Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(data.Pointer, in asyncId, out var capability));
                    try
                    {
                        var methods = *(nint**)capability;
                        int enabled = 0;
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)methods[4])(capability, &enabled));
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)methods[3])(capability, 1));
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[5])(capability, 0));
                        check(enabled == 1 && data.InAsyncOperation, "IDataObjectAsyncCapability starts an asynchronous extraction");
                        // Marshal the lazy stream across apartments. The source STA intentionally does
                        // not pump while the target reads: FTM must keep IO off that source thread.
                        var streamId = new Guid("0000000C-0000-0000-C000-000000000046");
                        Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(in streamId, medium.Value, out var packet));
                        var read = Task.Run(() => ReadMarshaled(packet, streamId, expected.Length + 16));
                        check(SpinWait.SpinUntil(() => Volatile.Read(ref requested) == 1, 3000) && !read.IsCompleted,
                            "receiver stream waits on an independent preparation worker while the source remains available");
                        gate.Set();
                        check(read.WaitAsync(timeout.Token).GetAwaiter().GetResult().AsSpan().SequenceEqual(expected),
                            "cross-apartment extraction receives complete real file content");
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)methods[7])(capability, 0, 0, 1));
                        check(!data.InAsyncOperation, "receiver EndOperation finishes the asynchronous protocol");
                    }
                    finally { Marshal.Release(capability); }
                }
                finally { ReleaseStgMedium(ref medium); }
                data.FinishExtractionAsync(timeout.Token).GetAwaiter().GetResult();
            }
            if (prepare is null) check(disposed == 1, "the final COM release disposes the prepared file lease");
            RunSynchronousAndFailure(path, expected, check);
            RunExternalReceiver(path, expected, check);
        }
        finally { CoUninitialize(); }
    }
    internal static bool SupportsDescriptors(nint data) => Query(data, F("FileGroupDescriptorW", 1, -1)) == 0;
    private static void RunCompletionWithUnusedStream(Action<bool, string> check)
    {
        var requested = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var data = new VirtualFileDataObject([new("探测流.txt", false, 20, null, _ =>
        {
            Interlocked.Increment(ref requested);
            throw new IOException("未使用的探测流不应提取内容。");
        })], timeout.Token);
        var stream = GetData(data.Pointer, F("FileContents", 4, 0));
        var asyncId = new Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4");
        nint capability = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(data.Pointer, in asyncId, out capability));
            var methods = *(nint**)capability;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[5])(capability, 0));
            var finished = data.FinishExtractionAsync(timeout.Token);
            check(!finished.IsCompleted, "the source stays busy until the receiver ends its asynchronous operation");
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)methods[7])(capability, 0, 0, 1));
            finished.GetAwaiter().GetResult();
            check(data.OpenStreams == 1 && requested == 0,
                "EndOperation finishes the source UI even when a receiver retains an unread probe stream");
        }
        finally
        {
            if (capability != 0) Marshal.Release(capability);
            ReleaseStgMedium(ref stream);
        }
        check(data.OpenStreams == 0, "retained probe stream resources release through their own COM lifetime");
    }
    private sealed class MediumOwner(Action release) : DragComObject(OwnerTable)
    {
        internal nint Pointer => Identity;
        protected override nint FindInterface(Guid id) => 0;
        protected override void ReleaseResources() => release();
        private static readonly nint OwnerTable = Table(typeof(MediumOwner),
            (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release);
    }
    private static void RunPrivateMedia(Action<bool, string> check)
    {
        var format = F("YuWang private stream ownership regression", 4, 2);
        for (var transfer = 0; transfer <= 1; transfer++)
        {
            var released = 0; var replacementReleased = 0;
            VirtualFileDataObject.Medium MakeStream(Action onRelease)
            {
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out var stream));
                byte* sample = stackalloc byte[] { 11, 22, 33 };
                uint written;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)(*(nint**)stream)[4])(stream, sample, 3, &written));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)stream)[5])(stream, 0, 0, null));
                var owner = new MediumOwner(() => { Marshal.Release(stream); onRelease(); });
                return new() { Type = 4, Value = stream, Owner = owner.Pointer };
            }
            var original = MakeStream(() => released++);
            var replacement = MakeStream(() => replacementReleased++);
            var received = new VirtualFileDataObject.Medium();
            try
            {
                using (var data = new VirtualFileDataObject([], CancellationToken.None))
                {
                    Marshal.ThrowExceptionForHR(SetData(data.Pointer, format, ref original, transfer));
                    if (transfer != 0) original = default;
                    else { ReleaseStgMedium(ref original); original = default; }
                    check(Query(data.Pointer, format) == 0 && Query(data.Pointer, F("YuWang private stream ownership regression", 1, 2)) < 0
                        && Query(data.Pointer, F("YuWang private stream ownership regression", 4, -1)) < 0,
                        $"private media preserve native type and index (fRelease={transfer})");
                    received = GetData(data.Pointer, format);
                    check(received.Type == 4 && ReadPointer(received.Value, 8).AsSpan().SequenceEqual(new byte[] { 11, 22, 33 }),
                        $"private IStream remains readable after the caller releases its medium (fRelease={transfer})");
                    Marshal.ThrowExceptionForHR(SetData(data.Pointer, format, ref replacement, 1));
                    replacement = default;
                    check(released == 0 && !data.HasFailure,
                        $"replacing private media preserves outstanding receiver ownership (fRelease={transfer})");
                    var denied = format; denied.Id = 15;
                    check(SetData(data.Pointer, denied, ref received, 1) < 0 && released == 0,
                        $"refused raw paths do not take ownership of the caller medium (fRelease={transfer})");
                }
                check(replacementReleased == 1 && released == 0,
                    $"final data-object release frees stored media without invalidating the receiver (fRelease={transfer})");
                ReleaseStgMedium(ref received);
                received = default;
                check(released == 1, $"the receiver releases pUnkForRelease exactly once (fRelease={transfer})");
            }
            finally
            {
                if (original.Type != 0) ReleaseStgMedium(ref original);
                if (replacement.Type != 0) ReleaseStgMedium(ref replacement);
                if (received.Type != 0) ReleaseStgMedium(ref received);
            }
        }
    }
    private static int SetData(nint data, VirtualFileDataObject.Format format, ref VirtualFileDataObject.Medium medium, int release)
    {
        fixed (VirtualFileDataObject.Medium* pointer = &medium)
            return ((delegate* unmanaged[Stdcall]<nint, VirtualFileDataObject.Format*, VirtualFileDataObject.Medium*, int, int>)(*(nint**)data)[7])
                (data, &format, pointer, release);
    }
    internal static Task<string> ReceiveAsync(nint data, string expectedName, string copyPath)
    {
        var asyncId = new Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4");
        var dataId = new Guid("0000010E-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(data, in asyncId, out var capability));
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)(*(nint**)capability)[3])(capability, 1));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)capability)[5])(capability, 0));
            nint packet;
            var hr = CoMarshalInterThreadInterfaceInStream(in dataId, data, out packet);
            if (hr < 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)(*(nint**)capability)[7])(capability, hr, 0, 0);
                Marshal.ThrowExceptionForHR(hr);
            }
            return Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
                nint receiver = 0, asyncReceiver = 0; var result = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(packet, in dataId, out receiver));
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(receiver, in asyncId, out asyncReceiver));
                    var descriptor = GetData(receiver, F("FileGroupDescriptorW", 1, -1));
                    try
                    {
                        var memory = GlobalLock(descriptor.Value);
                        try
                        {
                            if (Marshal.ReadInt32(memory) != 1 || Marshal.PtrToStringUni(memory + 4 + 72) != expectedName)
                                throw new IOException("接收方取得的虚拟文件描述不匹配。");
                        }
                        finally { GlobalUnlock(descriptor.Value); }
                    }
                    finally { ReleaseStgMedium(ref descriptor); }
                    var medium = GetData(receiver, F("FileContents", 4, 0));
                    try
                    {
                        using var output = File.Create(copyPath);
                        var buffer = new byte[64 * 1024];
                        while (true)
                        {
                            var bytes = ReadPointer(medium.Value, buffer.Length);
                            if (bytes.Length == 0) break;
                            output.Write(bytes);
                        }
                    }
                    finally { ReleaseStgMedium(ref medium); }
                    return File.ReadAllText(copyPath);
                }
                catch (Exception error) { result = Marshal.GetHRForException(error); throw; }
                finally
                {
                    if (asyncReceiver != 0)
                    {
                        ((delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)(*(nint**)asyncReceiver)[7])(asyncReceiver, result, 0, result < 0 ? 0u : 1u);
                        Marshal.Release(asyncReceiver);
                    }
                    if (receiver != 0) Marshal.Release(receiver);
                    CoUninitialize();
                }
            });
        }
        finally { Marshal.Release(capability); }
    }
    private static void RunExternalReceiver(string path, byte[] expected, Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YuWangVirtualReceiver-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var calls = 0;
        using var data = new VirtualFileDataObject([new("外部接收.bin", false, expected.Length, null,
            _ => { Interlocked.Increment(ref calls); return Task.FromResult(new VirtualFileContent(path, new Lease(() => { }))); })], CancellationToken.None);
        var id = new Guid("0000010E-0000-0000-C000-000000000046");
        nint stream = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out stream));
            Marshal.ThrowExceptionForHR(CoMarshalInterface(stream, in id, data.Pointer, 0 /* MSHCTX_LOCAL */, 0, 0));
            SeekStart(stream);
            File.WriteAllBytes(Path.Combine(directory, "object.bin"), ReadPointer(stream, 65536));
            using var child = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
            };
            child.StartInfo.ArgumentList.Add("--virtual-file-client");
            child.StartInfo.ArgumentList.Add(directory);
            child.Start();
            if (!child.WaitForExit(10000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("跨进程虚拟文件提取超时。"); }
            var detail = child.StandardError.ReadToEnd();
            if (child.ExitCode != 0) throw new IOException("跨进程虚拟文件接收失败：" + detail);
            check(calls == 1 && File.ReadAllBytes(Path.Combine(directory, "copied.bin")).AsSpan().SequenceEqual(expected),
                "a separate receiver process unmarshals the data object and extracts complete lazy content");
        }
        finally
        {
            if (stream != 0) Marshal.Release(stream);
            foreach (var name in new[] { "object.bin", "copied.bin" })
            { var file = Path.Combine(directory, name); if (File.Exists(file)) File.Delete(file); }
            System.IO.Directory.Delete(directory);
        }
    }
    internal static void RunExternalClient(string directory)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        nint stream = 0, data = 0, capability = 0;
        var hr = 0;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "object.bin"));
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out stream));
            fixed (byte* buffer = bytes)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)(*(nint**)stream)[4])(stream, buffer, (uint)bytes.Length, null));
            SeekStart(stream);
            var id = new Guid("0000010E-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(CoUnmarshalInterface(stream, in id, out data));
            var asyncId = new Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(data, in asyncId, out capability));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)capability)[5])(capability, 0));
            var content = GetData(data, F("FileContents", 4, 0));
            try
            {
                using var output = File.Create(Path.Combine(directory, "copied.bin"));
                while (true)
                { var block = ReadPointer(content.Value, 65536); if (block.Length == 0) break; output.Write(block); }
            }
            finally { ReleaseStgMedium(ref content); }
        }
        catch (Exception error) { hr = Marshal.GetHRForException(error); throw; }
        finally
        {
            if (capability != 0)
            { ((delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)(*(nint**)capability)[7])(capability, hr, 0, hr < 0 ? 0u : 1u); Marshal.Release(capability); }
            if (data != 0) Marshal.Release(data);
            if (stream != 0) Marshal.Release(stream);
            CoUninitialize();
        }
    }
    private static void SeekStart(nint stream)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)stream)[5])(stream, 0, 0, null));
    private static void RunSynchronousAndFailure(string path, byte[] expected, Action<bool, string> check)
    {
        var calls = 0;
        using (var data = new VirtualFileDataObject([new("同步.txt", false, expected.Length, null, token =>
        { Interlocked.Increment(ref calls); return Task.FromResult(new VirtualFileContent(path, new Lease(() => { }))); })], CancellationToken.None))
        {
            data.SetSourceThread();
            var medium = GetData(data.Pointer, F("FileContents", 4, 0));
            try
            {
                var pointer = medium.Value;
                check(Task.Run(() => ReadPointer(pointer, expected.Length + 16)).GetAwaiter().GetResult().AsSpan().SequenceEqual(expected)
                    && calls == 1, "a synchronous receiver can read and wait without async-capability negotiation");
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim(false);
        using (var data = new VirtualFileDataObject([new("取消.txt", false, 42, null, token =>
        { started.Set(); return Task.Delay(Timeout.Infinite, token).ContinueWith<VirtualFileContent>(t => { t.GetAwaiter().GetResult(); throw new InvalidOperationException(); }, TaskScheduler.Default); })], cancellation.Token))
        {
            var medium = GetData(data.Pointer, F("FileContents", 4, 0));
            try
            {
                var pointer = medium.Value;
                var read = Task.Run(() => TryRead(pointer));
                check(started.Wait(3000), "lazy content preparation started for cancellation test");
                cancellation.Cancel();
                check(read.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() < 0 && data.HasFailure,
                    "cancelling preparation returns a stream error to the receiver");
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        using (var data = new VirtualFileDataObject([new("失败.txt", false, 42, null,
            _ => Task.FromException<VirtualFileContent>(new IOException("模拟同步引擎断开")))], CancellationToken.None))
        {
            var medium = GetData(data.Pointer, F("FileContents", 4, 0));
            try
            {
                var pointer = medium.Value;
                check(Task.Run(() => TryRead(pointer)).GetAwaiter().GetResult() < 0 && data.Failure?.Message.Contains("同步引擎") == true,
                    "hydration/backend failure reaches both the receiver HRESULT and source error state");
            }
            finally { ReleaseStgMedium(ref medium); }
        }
    }
    private static byte[] ReadMarshaled(nint packet, Guid id, int size)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        try
        {
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(packet, in id, out var stream));
            try { return ReadPointer(stream, size); } finally { Marshal.Release(stream); }
        }
        finally { CoUninitialize(); }
    }
    private static byte[] ReadPointer(nint stream, int size)
    {
        var bytes = new byte[size]; uint count = 0;
        fixed (byte* buffer = bytes)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)(*(nint**)stream)[3])(stream, buffer, (uint)size, &count));
        return bytes[..(int)count];
    }
    private static int TryRead(nint stream)
    {
        byte* bytes = stackalloc byte[64]; uint count;
        return ((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)(*(nint**)stream)[3])(stream, bytes, 64, &count);
    }
    private static VirtualFileDataObject.Format F(string name, uint medium, int index)
        => new() { Id = (ushort)RegisterClipboardFormatW(name), Aspect = 1, Index = index, Medium = medium };
    private static int Query(nint data, VirtualFileDataObject.Format format)
        => ((delegate* unmanaged[Stdcall]<nint, VirtualFileDataObject.Format*, int>)(*(nint**)data)[5])(data, &format);
    private static VirtualFileDataObject.Medium GetData(nint data, VirtualFileDataObject.Format format)
    {
        VirtualFileDataObject.Medium medium;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, VirtualFileDataObject.Format*, VirtualFileDataObject.Medium*, int>)(*(nint**)data)[3])(data, &format, &medium));
        return medium;
    }
    private static ushort[] ReadFormats(nint data)
    {
        nint enumerator;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)data)[8])(data, 1, &enumerator));
        try
        {
            var formats = new List<ushort>(); var format = new VirtualFileDataObject.Format(); uint fetched;
            var next = (delegate* unmanaged[Stdcall]<nint, uint, VirtualFileDataObject.Format*, uint*, int>)(*(nint**)enumerator)[3];
            while (next(enumerator, 1, &format, &fetched) == 0) formats.Add(format.Id);
            return formats.ToArray();
        }
        finally { Marshal.Release(enumerator); }
    }
    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(nint memory, [MarshalAs(UnmanagedType.Bool)] bool delete, out nint stream);
    [DllImport("ole32.dll")] private static extern int CoMarshalInterface(nint stream, in Guid id, nint value, uint context, nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern int CoUnmarshalInterface(nint stream, in Guid id, out nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoMarshalInterThreadInterfaceInStream(in Guid id, nint value, out nint stream);
    [DllImport("ole32.dll")] private static extern int CoGetInterfaceAndReleaseStream(nint stream, in Guid id, out nint value);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref VirtualFileDataObject.Medium medium);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}
