using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using YuWangCloudSync.CloudFiles;

namespace YuWangCloudSync.Tray;

internal sealed record VirtualFileContent(string Path, IDisposable Lease) : IDisposable
{
    public void Dispose() => Lease.Dispose();
}

internal sealed class VirtualDragEntry(string name, bool directory, long? size, DateTime? modified,
    Func<CancellationToken, Task<VirtualFileContent>>? prepare = null)
{
    internal string Name { get; } = name;
    internal bool Directory { get; } = directory;
    internal long? Size { get; } = size;
    internal DateTime? Modified { get; } = modified;
    private readonly object _gate = new();
    private Task<VirtualFileContent>? _content;
    internal Task<VirtualFileContent> Prepare(CancellationToken token)
    {
        lock (_gate) return _content ??= Task.Run(() => (prepare
            ?? throw new IOException("文件夹没有内容流。"))(token), token);
    }
    internal bool IsPreparationStarted { get { lock (_gate) return _content is not null; } }
    internal void DisposeContent()
    {
        lock (_gate)
        {
            if (_content is null) return;
            _ = _content.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
                else _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

// Raw COM vtables make lifetime and FTM aggregation explicit under NativeAOT. No managed
// apartment projection or runtime-generated COM stubs are used. All exported callbacks catch errors.
internal abstract unsafe class DragComObject : IDisposable
{
    private int _references = 1;
    private readonly nint[] _nodes;
    private readonly GCHandle _root;
    private nint _marshaler;
    protected nint Identity => _nodes[0];
    protected DragComObject(params nint[] tables)
    {
        _root = GCHandle.Alloc(this);
        _nodes = new nint[tables.Length];
        try
        {
            for (var i = 0; i < tables.Length; i++)
            {
                var node = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
                if (node == null) throw new OutOfMemoryException();
                node[0] = tables[i]; node[1] = GCHandle.ToIntPtr(_root);
                _nodes[i] = (nint)node;
            }
        }
        catch
        {
            foreach (var node in _nodes) NativeMemory.Free((void*)node);
            _root.Free(); throw;
        }
    }
    protected nint Node(int index) => _nodes[index];
    protected abstract nint FindInterface(Guid id);
    protected abstract void ReleaseResources();
    protected static T Get<T>(nint self) where T : DragComObject
        => (T)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;
    internal uint Retain() => (uint)Interlocked.Increment(ref _references);
    private uint Drop()
    {
        var remaining = Interlocked.Decrement(ref _references);
        if (remaining == 0)
        {
            try { ReleaseResources(); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            finally
            {
                if (_marshaler != 0) Marshal.Release(_marshaler);
                foreach (var node in _nodes) NativeMemory.Free((void*)node);
                _root.Free();
            }
        }
        return (uint)remaining;
    }
    public void Dispose() => Drop(); // Each owner releases exactly its own reference.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    protected static int Query(nint self, Guid* iid, nint* result)
    {
        if (result == null || iid == null) return unchecked((int)0x80004003);
        *result = 0;
        try
        {
            var owner = Get<DragComObject>(self);
            if (*iid == new Guid("00000003-0000-0000-C000-000000000046")) // IMarshal
            {
                lock (owner._nodes)
                {
                    if (owner._marshaler == 0)
                        Marshal.ThrowExceptionForHR(CoCreateFreeThreadedMarshaler(owner.Identity, out owner._marshaler));
                    return Marshal.QueryInterface(owner._marshaler, in *iid, out *result);
                }
            }
            var pointer = *iid == new Guid("00000000-0000-0000-C000-000000000046")
                ? owner.Identity : owner.FindInterface(*iid);
            if (pointer == 0) return unchecked((int)0x80004002);
            owner.Retain(); *result = pointer; return 0;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    protected static uint AddRef(nint self) => Get<DragComObject>(self).Retain();
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    protected static uint Release(nint self) => Get<DragComObject>(self).Drop();
    protected static nint Table(Type owner, params nint[] methods)
    {
        var table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(owner, methods.Length * sizeof(nint));
        methods.CopyTo(new Span<nint>(table, methods.Length)); return (nint)table;
    }
    [DllImport("ole32.dll")] private static extern int CoCreateFreeThreadedMarshaler(nint outer, out nint inner);
}

/// <summary>Only descriptors and lazy streams are exposed. Neither HDROP nor PIDLs can escape.</summary>
internal sealed unsafe class VirtualFileDataObject : DragComObject
{
    [StructLayout(LayoutKind.Sequential)] internal struct Format
    { internal ushort Id; internal nint Device; internal uint Aspect; internal int Index; internal uint Medium; }
    [StructLayout(LayoutKind.Sequential)] internal struct Medium
    { internal uint Type; internal nint Value, Owner; }
    private static readonly ushort Descriptor = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly ushort Contents = (ushort)RegisterClipboardFormatW("FileContents");
    private static readonly ushort PreferredEffect = (ushort)RegisterClipboardFormatW("Preferred DropEffect");
    private static readonly ushort ShellIdList = (ushort)RegisterClipboardFormatW("Shell IDList Array");
    private const int BadFormat = unchecked((int)0x80040064);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int InvalidIndex = unchecked((int)0x80040068);
    private readonly IReadOnlyList<VirtualDragEntry> _entries;
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, byte[]> _extra = [];
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private int _async = 1, _inOperation, _streams;
    private int _sourceThread;
    internal nint Pointer => Identity;
    internal bool InAsyncOperation => Volatile.Read(ref _inOperation) != 0;
    internal bool HasFailure => Volatile.Read(ref _failure) is not null;
    internal void SetSourceThread() => _sourceThread = Environment.CurrentManagedThreadId;
    internal bool IsSourceThread => Environment.CurrentManagedThreadId == _sourceThread;
    internal VirtualFileDataObject(IReadOnlyList<VirtualDragEntry> entries, CancellationToken token)
        : base(DataTable, AsyncTable)
    {
        _entries = entries; _token = token;
    }
    protected override nint FindInterface(Guid id)
        => id == new Guid("0000010E-0000-0000-C000-000000000046") ? Identity
            : id == new Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4") ? Node(1) : 0;
    protected override void ReleaseResources()
    { foreach (var entry in _entries) entry.DisposeContent(); }
    internal void Fail(Exception error)
    { Interlocked.CompareExchange(ref _failure, error, null); }
    internal void StreamOpened() { Retain(); Interlocked.Increment(ref _streams); }
    internal void StreamFinished() => Interlocked.Decrement(ref _streams);
    internal int OpenStreams => Volatile.Read(ref _streams);
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal Task OperationEnded => _ended.Task;
    internal Task FinishExtractionAsync(CancellationToken token) => VirtualDragWait.FinishAsync(this, token);
    private Format[] Formats()
    {
        lock (_gate) return new[]
        {
            new Format { Id = Descriptor, Aspect = 1, Index = -1, Medium = 1 },
            new Format { Id = Contents, Aspect = 1, Index = -1, Medium = 4 },
            new Format { Id = PreferredEffect, Aspect = 1, Index = -1, Medium = 1 },
        }.Concat(_extra.Keys.Where(id => id != PreferredEffect).Select(id =>
            new Format { Id = id, Aspect = 1, Index = -1, Medium = 1 })).ToArray();
    }
    private int CanGet(Format format, bool query = false)
    {
        if (format.Device != 0) return unchecked((int)0x80040065);
        if (format.Aspect != 1) return unchecked((int)0x8004006B);
        if (format.Id == 15 || format.Id == ShellIdList) return BadFormat;
        if (format.Id == Contents)
        {
            if ((format.Medium & 4) == 0) return unchecked((int)0x80040069);
            if (query && format.Index == -1) return 0;
            return format.Index < 0 || format.Index >= _entries.Count || _entries[format.Index].Directory ? InvalidIndex : 0;
        }
        if ((format.Medium & 1) == 0) return unchecked((int)0x80040069);
        lock (_gate) return format.Index == -1 && (format.Id == Descriptor || format.Id == PreferredEffect
            || _extra.ContainsKey(format.Id)) ? 0 : BadFormat;
    }
    private byte[] Descriptors()
    {
        // FILEDESCRIPTORW is 592 bytes on both x86/x64, cFileName starts at byte 72.
        var bytes = new byte[checked(4 + _entries.Count * 592)];
        BitConverter.TryWriteBytes(bytes.AsSpan(), _entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i]; var span = bytes.AsSpan(4 + i * 592, 592);
            if (entry.Name.Length == 0 || entry.Name.Length >= 260) throw new IOException("拖放文件相对路径超过 259 个字符。");
            uint flags = 0x80000004; // FD_UNICODE | FD_ATTRIBUTES
            BitConverter.TryWriteBytes(span[36..], entry.Directory ? 0x10u : 0x80u);
            if (entry.Size is { } size && !entry.Directory)
            {
                flags |= 0x40; BitConverter.TryWriteBytes(span[64..], (uint)((ulong)size >> 32));
                BitConverter.TryWriteBytes(span[68..], (uint)size);
            }
            if (entry.Modified is { } modified)
            { flags |= 0x20; BitConverter.TryWriteBytes(span[56..], modified.ToUniversalTime().ToFileTimeUtc()); }
            BitConverter.TryWriteBytes(span, flags);
            Encoding.Unicode.GetBytes(entry.Name, span[72..]);
        }
        return bytes;
    }
    private static Medium Memory(byte[] bytes)
    {
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) throw new OutOfMemoryException();
        var buffer = GlobalLock(memory);
        if (buffer == 0) { GlobalFree(memory); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
        finally { GlobalUnlock(memory); }
        return new Medium { Type = 1, Value = memory };
    }
    private static readonly nint DataTable = Table(typeof(VirtualFileDataObject),
        (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, Medium*, int>)&GetData,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, Medium*, int>)&GetDataHere,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, int>)&QueryData,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, Format*, int>)&Canonical,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, Medium*, int, int>)&SetData,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&EnumFormats,
        (nint)(delegate* unmanaged[Stdcall]<nint, Format*, uint, nint, uint*, int>)&Advise,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Unadvise,
        (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&EnumAdvise);
    private static readonly nint AsyncTable = Table(typeof(VirtualFileDataObject),
        (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release,
        (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&SetAsync,
        (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetAsync,
        (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Start,
        (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&InOperation,
        (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)&End);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(nint self, Format* format, Medium* medium)
    {
        if (format == null || medium == null) return unchecked((int)0x80004003);
        *medium = default;
        try
        {
            var owner = Get<VirtualFileDataObject>(self);
            var hr = owner.CanGet(*format); if (hr < 0) return hr;
            if (format->Id == Contents)
            {
                owner._token.ThrowIfCancellationRequested();
                var stream = new VirtualFileStream(owner, owner._entries[format->Index], owner._token);
                *medium = new Medium { Type = 4, Value = stream.Pointer }; // STGMEDIUM owns the initial reference.
            }
            else
            {
                byte[] bytes;
                if (format->Id == Descriptor) bytes = owner.Descriptors();
                else { lock (owner._gate) bytes = owner._extra.TryGetValue(format->Id, out var value) ? value : BitConverter.GetBytes(1); }
                *medium = Memory(bytes);
            }
            return 0;
        }
        catch (Exception error) { Get<VirtualFileDataObject>(self).Fail(error); return Marshal.GetHRForException(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryData(nint self, Format* format)
    { try { return format == null ? unchecked((int)0x80004003) : Get<VirtualFileDataObject>(self).CanGet(*format, query: true); } catch (Exception e) { return Marshal.GetHRForException(e); } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDataHere(nint self, Format* format, Medium* medium) => NotImplemented;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Canonical(nint self, Format* input, Format* output)
    { if (output == null) return unchecked((int)0x80004003); *output = default; return 0x40130; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetData(nint self, Format* format, Medium* medium, int release)
    {
        if (format == null || medium == null) return unchecked((int)0x80004003);
        try
        {
            if (format->Id == 15 || format->Id == ShellIdList || format->Id == Descriptor || format->Id == Contents) return BadFormat;
            if (medium->Type != 1 || medium->Value == 0) return unchecked((int)0x80040069);
            var size = checked((int)GlobalSize(medium->Value));
            var buffer = GlobalLock(medium->Value); if (buffer == 0) throw new Win32Exception();
            byte[] bytes;
            try { bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size); }
            finally { GlobalUnlock(medium->Value); }
            var owner = Get<VirtualFileDataObject>(self);
            lock (owner._gate) owner._extra[format->Id] = bytes;
            if (release != 0) ReleaseStgMedium(ref *medium);
            return 0;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumFormats(nint self, uint direction, nint* result)
    {
        if (result == null) return unchecked((int)0x80004003); *result = 0;
        if (direction != 1) return NotImplemented;
        try { var formats = Get<VirtualFileDataObject>(self).Formats(); fixed (Format* p = formats) return SHCreateStdEnumFmtEtc((uint)formats.Length, p, out *result); }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Advise(nint self, Format* format, uint flags, nint sink, uint* connection) => unchecked((int)0x80040003);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unadvise(nint self, uint connection) => unchecked((int)0x80040003);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumAdvise(nint self, nint* result) { if (result != null) *result = 0; return unchecked((int)0x80040003); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetAsync(nint self, int enabled) { Volatile.Write(ref Get<VirtualFileDataObject>(self)._async, enabled != 0 ? 1 : 0); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetAsync(nint self, int* enabled) { if (enabled == null) return unchecked((int)0x80004003); *enabled = Volatile.Read(ref Get<VirtualFileDataObject>(self)._async); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Start(nint self, nint context)
    { Volatile.Write(ref Get<VirtualFileDataObject>(self)._inOperation, 1); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InOperation(nint self, int* active) { if (active == null) return unchecked((int)0x80004003); *active = Volatile.Read(ref Get<VirtualFileDataObject>(self)._inOperation); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int End(nint self, int hr, nint context, uint effect)
    {
        var owner = Get<VirtualFileDataObject>(self);
        if (hr < 0) { var error = Marshal.GetExceptionForHR(hr) ?? new IOException($"目标提取失败：0x{hr:X8}"); owner.Fail(error); owner._ended.TrySetResult(); }
        else owner._ended.TrySetResult();
        Volatile.Write(ref owner._inOperation, 0);
        return 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, Format* formats, out nint result);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref Medium medium);
}

internal sealed unsafe class VirtualFileStream : DragComObject
{
    private readonly VirtualFileDataObject _owner;
    private readonly VirtualDragEntry _entry;
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private nint _stream;
    private bool _finished;
    internal nint Pointer => Identity;
    internal VirtualFileStream(VirtualFileDataObject owner, VirtualDragEntry entry, CancellationToken token)
        : base(StreamTable) { _owner = owner; _entry = entry; _token = token; owner.StreamOpened(); }
    protected override nint FindInterface(Guid id) => id == new Guid("0000000C-0000-0000-C000-000000000046")
        || id == new Guid("0C733A30-2A1C-11CE-ADE5-00AA0044773D") ? Identity : 0;
    private void Finish() { if (!_finished) { _finished = true; _owner.StreamFinished(); } }
    protected override void ReleaseResources()
    { lock (_gate) { if (_stream != 0) Marshal.Release(_stream); Finish(); } _owner.Dispose(); }
    private nint Ready()
    {
        _token.ThrowIfCancellationRequested();
        if (_stream != 0) return _stream;
        // This guard is never hit by out-of-process synchronous receivers (FTM calls
        // arrive on an RPC worker). Do not wait inside the source's DoDragDrop stack.
        if (_owner.IsSourceThread) throw new COMException("内容正在独立准备，请在目标线程读取。", unchecked((int)0x8000000A));
        var content = _entry.Prepare(_token).WaitAsync(_token).GetAwaiter().GetResult();
        _token.ThrowIfCancellationRequested();
        if (_entry.Size is { } expected && new FileInfo(content.Path).Length != expected)
            throw new IOException("文件大小在拖放期间发生变化，请重新拖放。");
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(content.Path, 0x20 /* read/share deny write */, 0, false, 0, out _stream));
        return _stream;
    }
    private int Error(Exception error) { _owner.Fail(error); Finish(); return Marshal.GetHRForException(error); }
    private static readonly nint StreamTable = Table(typeof(VirtualFileStream),
        (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release,
        (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)&Read,
        (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)&Write,
        (nint)(delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)&Seek,
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&SetSize,
        (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong*, ulong*, int>)&CopyTo,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Commit,
        (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Revert,
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, int>)&Lock,
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, int>)&Unlock,
        (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, int>)&Stat,
        (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Clone);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Read(nint self, void* buffer, uint count, uint* read)
    {
        if (read != null) *read = 0;
        var owner = Get<VirtualFileStream>(self);
        lock (owner._gate) try
        {
            if (count == 0) return 0;
            if (buffer == null) return unchecked((int)0x80004003);
            var stream = owner.Ready(); uint actual = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)(*(nint**)stream)[3])(stream, buffer, count, &actual);
            if (read != null) *read = actual;
            if (hr == 1 || actual == 0) owner.Finish();
            if (hr < 0) owner._owner.Fail(Marshal.GetExceptionForHR(hr) ?? new IOException("读取文件失败。"));
            return hr;
        }
        catch (Exception error) { return owner.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Write(nint self, void* buffer, uint count, uint* written) { if (written != null) *written = 0; return unchecked((int)0x80030005); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Seek(nint self, long offset, uint origin, ulong* position)
    {
        var owner = Get<VirtualFileStream>(self); lock (owner._gate) try
        { var stream = owner.Ready(); return ((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)stream)[5])(stream, offset, origin, position); }
        catch (Exception error) { return owner.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetSize(nint self, ulong size) => unchecked((int)0x80030005);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyTo(nint self, nint target, ulong count, ulong* read, ulong* written)
    {
        var owner = Get<VirtualFileStream>(self); lock (owner._gate) try
        {
            var stream = owner.Ready();
            return ((delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong*, ulong*, int>)(*(nint**)stream)[7])(stream, target, count, read, written);
        }
        catch (Exception error) { return owner.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Commit(nint self, uint flags) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Revert(nint self) => unchecked((int)0x80030001);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Lock(nint self, ulong offset, ulong count, uint flags) => unchecked((int)0x80030001);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unlock(nint self, ulong offset, ulong count, uint flags) => unchecked((int)0x80030001);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stat(nint self, void* stat, uint flags)
    {
        if (stat == null) return unchecked((int)0x80004003);
        var owner = Get<VirtualFileStream>(self); lock (owner._gate) try
        {
            if (owner._stream == 0 && owner._entry.Size is { } size)
            {
                // STATSTG: pointer name, DWORD type, aligned ULARGE_INTEGER size. x64 is the app's supported ABI.
                new Span<byte>(stat, 80).Clear();
                if ((flags & 1) == 0) *(nint*)stat = Marshal.StringToCoTaskMemUni(owner._entry.Name);
                *(uint*)((byte*)stat + 8) = 2; *(long*)((byte*)stat + 16) = size;
                return 0;
            }
            var stream = owner.Ready(); return ((delegate* unmanaged[Stdcall]<nint, void*, uint, int>)(*(nint**)stream)[12])(stream, stat, flags);
        }
        catch (Exception error) { return owner.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Clone(nint self, nint* result)
    {
        if (result == null) return unchecked((int)0x80004003); *result = 0;
        var owner = Get<VirtualFileStream>(self); lock (owner._gate) try
        {
            var stream = owner.Ready();
            // Return a lazy wrapper sharing the prepared content; preserve the independent cursor.
            ulong position;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)stream)[5])(stream, 0, 1, &position));
            var clone = new VirtualFileStream(owner._owner, owner._entry, owner._token);
            try
            {
                var cloned = clone.Ready();
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)cloned)[5])(cloned, checked((long)position), 0, null));
                *result = clone.Pointer; return 0;
            }
            catch { clone.Dispose(); throw; }
        }
        catch (Exception error) { return owner.Error(error); }
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out nint stream);
}

internal static class VirtualDragWait
{
    internal static async Task FinishAsync(VirtualFileDataObject data, CancellationToken token)
    {
        while (data.InAsyncOperation || data.OpenStreams != 0)
        {
            token.ThrowIfCancellationRequested();
            if (data.Failure is { } failure) throw new IOException(failure.Message, failure);
            await Task.Delay(50, token);
        }
        token.ThrowIfCancellationRequested();
        if (data.Failure is { } error) throw new IOException(error.Message, error);
        if (data.OperationEnded.IsCompleted) await data.OperationEnded;
    }
}
