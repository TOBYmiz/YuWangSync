using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

try
{
    CloudFilesPlatform.EnsureSupported();
    Console.WriteLine(CloudFilesPlatform.Describe());
    Check(IntPtr.Size == 8 && Marshal.SizeOf<CF_SYNC_POLICIES>() == 20, "x64 baseline CF_SYNC_POLICIES ABI");
    Check(Marshal.OffsetOf<CF_PLACEHOLDER_STANDARD_INFO>(nameof(CF_PLACEHOLDER_STANDARD_INFO.PinState)).ToInt32() == 32
        && Marshal.OffsetOf<CF_PLACEHOLDER_STANDARD_INFO>(nameof(CF_PLACEHOLDER_STANDARD_INFO.FileIdentity)).ToInt32() == 60,
        "placeholder status and variable identity offsets");

    // Golden SDK byte layout, independent of the managed struct writer.
    var bytes = new byte[56];
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 4096);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 8192);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), 123456);
    var parameters = new CF_CALLBACK_PARAMETERS { ParamSize = 64, Content = bytes };
    Check(CloudFilesInterop.TryReadFetchData(parameters, out var fetch)
        && fetch.RequiredFileOffset == 4096 && fetch.RequiredLength == 8192 && fetch.LastDehydrationTime == 123456,
        "NativeAOT fetch-data callback decoding");
    parameters.ParamSize = 32;
    Check(CloudFilesInterop.TryReadFetchData(parameters, out fetch) && fetch.LastDehydrationTime == 0,
        "short callback ignores optional tail even when backing array contains data");
    parameters.ParamSize = 31;
    Check(!CloudFilesInterop.TryReadFetchData(parameters, out _), "truncated callback rejected");
    parameters.ParamSize = 64;
    parameters.Content = new byte[23];
    Check(!CloudFilesInterop.TryReadFetchData(parameters, out _), "truncated backing array rejected");
    parameters.Content = bytes;
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), long.MaxValue);
    Check(!CloudFilesInterop.TryReadFetchData(parameters, out _), "overflowing data range rejected");

    var renameTarget = Marshal.StringToHGlobalUni(@"\folder\renamed.pptx");
    try
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), renameTarget.ToInt64());
        Check(CloudFilesInterop.ReadRenameTarget(parameters) == @"\folder\renamed.pptx", "NativeAOT rename target");
    }
    finally { Marshal.FreeHGlobal(renameTarget); }

    var op = CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.TRANSFERDATA
    {
        Buffer = new IntPtr(0x1234), Offset = 4096, Length = 8192
    });
    var opBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref op, 1));
    Check(op.ParamSize == 40 && BinaryPrimitives.ReadInt64LittleEndian(opBytes[16..]) == 0x1234
        && BinaryPrimitives.ReadInt64LittleEndian(opBytes[24..]) == 4096
        && BinaryPrimitives.ReadInt64LittleEndian(opBytes[32..]) == 8192, "TRANSFER_DATA native layout");
    Check(CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS()).ParamSize == 40
        && CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.ACKDEHYDRATE()).ParamSize == 32
        && CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.ACKDELETE()).ParamSize == 16
        && CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.ACKRENAME()).ParamSize == 16,
        "all production operation union sizes");
    var callbackInfo = new CF_CALLBACK_INFO { StructSize = 0 };
    var key = 12345L;
    callbackInfo.RequestKey = Unsafe.As<long, CF_REQUEST_KEY>(ref key);
    Check(CloudFilesInterop.CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA)
        .RequestKey.Equals(default(CF_REQUEST_KEY)), "missing RequestKey tail ignored");
    callbackInfo.StructSize = (uint)Marshal.SizeOf<CF_CALLBACK_INFO>();
    Check(CloudFilesInterop.CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA)
        .RequestKey.Equals(callbackInfo.RequestKey), "present RequestKey preserved");

    if (args.Contains("--abi-only")) return;

    // An isolated NTFS root exercises the actual platform's pin notification and the
    // production attribute watcher/updater. No account, network, WinUI or shell registration.
    var root = Path.Combine(Path.GetTempPath(), "YuWangCompat-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "pin-test.bin");
    var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
    var errors = new ConcurrentQueue<Exception>();
    var messages = new ConcurrentQueue<string>();
    var requestCount = 0;
    var registered = false;
    var connected = false;
    CF_CONNECTION_KEY connection = default;
    var watcher = new CloudFilePinStateWatcher(root, message => { messages.Enqueue(message); Console.WriteLine(message); });
    CF_CALLBACK hydrate = (in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS args) =>
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!CloudFilesInterop.TryReadFetchData(args, out var request))
                throw new InvalidDataException("native fetch parameters");
            Interlocked.Increment(ref requestCount);
            var offset = checked((int)request.RequiredFileOffset);
            var length = checked((int)Math.Min(request.RequiredLength, payload.Length - offset));
            buffer = Marshal.AllocHGlobal(length);
            Marshal.Copy(payload, offset, buffer, length);
            var operation = CloudFilesInterop.CreateOperationInfo(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);
            var transfer = CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Buffer = buffer, Offset = offset, Length = length, CompletionStatus = NTStatus.STATUS_SUCCESS
            });
            RequireSuccess(CfExecute(operation, ref transfer), "native hydration receipt");
        }
        catch (Exception error) { errors.Enqueue(error); }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    };
    CF_CALLBACK_REGISTRATION[] callbacks =
    [
        new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = hydrate },
        CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
    ];
    var rootIdentity = Marshal.AllocHGlobal(16);
    var fileIdentity = FileIdentity.SyncRoot.Serialize();
    var fileIdentityBuffer = Marshal.AllocHGlobal(fileIdentity.Length);
    try
    {
        Marshal.Copy(ProviderIdentity.RootIdentity, 0, rootIdentity, 16);
        Marshal.Copy(fileIdentity, 0, fileIdentityBuffer, fileIdentity.Length);
        var registration = new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = "YuWang CFAPI compatibility regression", ProviderVersion = "1.0", ProviderId = ProviderIdentity.Id,
            SyncRootIdentity = rootIdentity, SyncRootIdentityLength = 16,
            FileIdentity = fileIdentityBuffer, FileIdentityLength = (uint)fileIdentity.Length
        };
        var policies = new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new()
            {
                Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PROGRESSIVE,
                Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED
            },
            Population = new() { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL }
        };
        RequireSuccess(CfRegisterSyncRoot(root, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE), "register");
        registered = true;
        Check(ProviderIdentity.OwnsRoot(root), "NativeAOT provider root identity");
        Check(CloudFilesInterop.TryGetProviderName(root, out var name) && name == registration.ProviderName,
            "NativeAOT provider lookup");
        RequireSuccess(CfConnectSyncRoot(root, callbacks, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out connection), "connect");
        connected = true;
        CF_PLACEHOLDER_CREATE_INFO[] placeholders =
        [
            new()
            {
                RelativeFileName = Path.GetFileName(path), FileIdentity = fileIdentityBuffer,
                FileIdentityLength = (uint)fileIdentity.Length,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                FsMetadata = new() { FileSize = payload.Length, BasicInfo = new() { FileAttributes = FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL } }
            }
        ];
        RequireSuccess(CfCreatePlaceholders(root, placeholders, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var processed), "create placeholder");
        Check(processed == 1 && placeholders[0].Result.Succeeded && ProviderIdentity.OwnsPlaceholder(path), "created owned placeholder");
        watcher.Start();
        SetPin(CF_PIN_STATE.CF_PIN_STATE_PINNED);
        WaitFor(() => PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)
            && state.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED && state.OnDiskDataSize >= payload.Length,
            "Always keep on this device hydrates without callback failure");
        Check(requestCount > 0 && errors.IsEmpty, "native callback completed");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(payload), "hydrated content integrity");
        SetPin(CF_PIN_STATE.CF_PIN_STATE_UNPINNED);
        WaitFor(() => PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state) && state.OnDiskDataSize == 0,
            "Free up space dehydrates via production attribute watcher");
        Check(messages.Any(m => m.Contains("[PIN] 捕获占位属性变化")), "production pin watcher received attribute events");
        SetPin(CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED);
        Check(PlaceholderUpdater.TryGetPlaceholderDataState(path, out var unspecified)
            && unspecified.PinState == CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED && unspecified.OnDiskDataSize == 0,
            "UNSPECIFIED does not hydrate");
        Console.WriteLine("PASS: native pin / hydrate / unpin / dehydrate cycle.");
    }
    finally
    {
        watcher.Stop();
        if (connected) RequireSuccess(CfDisconnectSyncRoot(connection), "disconnect");
        if (registered) RequireSuccess(CfUnregisterSyncRoot(root), "unregister");
        Marshal.FreeHGlobal(fileIdentityBuffer);
        Marshal.FreeHGlobal(rootIdentity);
        GC.KeepAlive(callbacks);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    void SetPin(CF_PIN_STATE state)
    {
        var handle = Native.Open(path, 0x00040080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { RequireSuccess(CfSetPinState(new HFILE(handle), state, CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, IntPtr.Zero), "set pin"); }
        finally { Native.CloseHandle(handle); }
    }

    void WaitFor(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (errors.TryDequeue(out var error)) throw new Exception(label, error);
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(label);
            Thread.Sleep(50);
        }
        Check(true, label);
    }

    static void RequireSuccess(HRESULT result, string operation)
    {
        if (result.Failed) throw new InvalidOperationException($"{operation}: 0x{unchecked((uint)(int)result):X8}");
    }

    static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

internal static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr Open(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
