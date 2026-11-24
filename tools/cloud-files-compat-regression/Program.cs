using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Tray;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

try
{
    if (args.Length == 2 && args[0] == "--virtual-file-client")
    {
        VirtualFileDragSelfTest.RunExternalClient(args[1]);
        return;
    }
    CloudFilesPlatform.EnsureSupported();
    if (args.Contains("--search-progress"))
    {
        var fixtureBase = Path.GetFullPath(args.ElementAtOrDefault(Array.IndexOf(args, "--search-progress") + 1)
            ?? Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "YuWangCloudSync-search-progress-regression"));
        var fixture = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
        var searchRoot = Path.Combine(fixture, "索引 空格 # %");
        var child = Path.Combine(searchRoot, "子目录");
        var exclusionAdded = false;
        Directory.CreateDirectory(child);
        try
        {
            var rootUrl = SyncRootSearchIndex.FolderUrl(searchRoot);
            var childUrl = SyncRootSearchIndex.FolderUrl(child);
            Check(rootUrl.EndsWith(Path.DirectorySeparatorChar) && rootUrl.Contains("索引 空格 # %"), "search scope uses a fully decoded file URL and directory boundary");
            using (var scope = new SyncRootSearchIndex.SearchScope())
            {
                var initial = scope.Contains(rootUrl);
                Console.WriteLine($"Initial search scope: included={initial.included} reason={initial.reason}");
                Check(initial == (false, 0), "isolated root starts outside the search scope");
            }
            Check(SyncRootSearchIndex.Ensure(searchRoot, Console.WriteLine), "register an unindexed root for Explorer progress");
            Check(SyncRootSearchIndex.Ensure(searchRoot, Console.WriteLine), "index registration is repeatable");
            Check(SyncRootSearchIndex.Ensure(child, Console.WriteLine), "child reuses inherited index inclusion");
            Check(SyncRootSearchIndex.Remove(child, Console.WriteLine), "unowned inherited inclusion is preserved");
            using (var scope = new SyncRootSearchIndex.SearchScope())
            {
                Check(scope.Contains(rootUrl).included && scope.Contains(childUrl).included, "root and child stay indexed after unowned cleanup");
                Check(!scope.Contains(SyncRootSearchIndex.FolderUrl(searchRoot + "-sibling")).included, "index rule does not include a similarly named sibling");
            }
            Check(SyncRootSearchIndex.Remove(searchRoot, Console.WriteLine), "remove only the journaled default rule");
            Check(SyncRootSearchIndex.Remove(searchRoot, Console.WriteLine), "search cleanup is repeatable");
            using (var scope = new SyncRootSearchIndex.SearchScope())
            {
                Check(scope.Contains(rootUrl) == (false, 0) && scope.Contains(childUrl) == (false, 0), "root and child search scope restored");
                exclusionAdded = true;
                scope.Add(rootUrl, include: false);
            }
            Check(!SyncRootSearchIndex.Ensure(searchRoot, Console.WriteLine), "explicit index exclusion is not overridden");
            Check(SyncRootSearchIndex.Remove(searchRoot, Console.WriteLine), "unowned exclusion cleanup is a no-op");
            using (var scope = new SyncRootSearchIndex.SearchScope())
            {
                Check(scope.Contains(rootUrl) == (false, 1), "explicit default exclusion is preserved");
                scope.Remove(rootUrl);
                exclusionAdded = false;
                Check(scope.Contains(rootUrl) == (false, 0), "fixture exclusion rule removed");
            }
        }
        finally
        {
            if (!SyncRootSearchIndex.Remove(searchRoot, Console.WriteLine))
                throw new IOException("Temporary search rule cleanup failed: " + searchRoot);
            if (exclusionAdded)
            {
                using var scope = new SyncRootSearchIndex.SearchScope();
                scope.Remove(SyncRootSearchIndex.FolderUrl(searchRoot));
            }
            var allowedPrefix = Path.EndsInDirectorySeparator(fixtureBase) ? fixtureBase : fixtureBase + Path.DirectorySeparatorChar;
            if (!fixture.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe search progress fixture cleanup path.");
            Directory.Delete(fixture, true);
        }
        return;
    }
    if (args.Contains("--shell-progress"))
    {
        Check(ShellTransferProgress.ValidateNativeAbi(out var abiResult), $"native Shell progress ABI / UInt64 counters / terminal states 0x{abiResult:X8}");
        var progressBase = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "_buildtmp", "shell-progress-regression"));
        var progressRoot = Path.Combine(progressBase, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(progressRoot);
        var progressPath = Path.Combine(progressRoot, "ordinary-upload.bin");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(progressPath, original);
        try
        {
            using (var locked = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var submitted = ShellTransferProgress.TrySet(progressPath, 10000, 5000,
                    ShellTransferProgress.Transferring, out var result);
                if (!submitted && result == unchecked((int)0x80070005))
                {
                    Console.WriteLine("BLOCKED: native ABI passed, but this process cannot access the file's volatile Shell property store (E_ACCESSDENIED). File submission/display is not verified.");
                    Environment.ExitCode = 2;
                    return;
                }
                Check(submitted, $"Shell submit progress while ordinary file is read-locked 0x{result:X8}");
                Check(ShellTransferProgress.TrySet(progressPath, 10000, 0, ShellTransferProgress.HasError, out result),
                    $"Shell submit failure state 0x{result:X8}");
                Check(ShellTransferProgress.TrySet(progressPath, 10000, 0, ShellTransferProgress.Paused, out result),
                    $"Shell submit cancelled state 0x{result:X8}");
                Check(ShellTransferProgress.TrySet(progressPath, 10000, 10000, 0, out result),
                    $"Shell submit completion state 0x{result:X8}");
            }
            Check(File.ReadAllBytes(progressPath).SequenceEqual(original), "Shell reporting leaves ordinary file contents unchanged");
        }
        finally
        {
            if (!Path.GetFullPath(progressRoot).StartsWith(progressBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe Shell progress test cleanup path.");
            Directory.Delete(progressRoot, true);
        }
        return;
    }
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
    var flagParameters = new CF_CALLBACK_PARAMETERS { ParamSize = 12, Content = [1, 0, 0, 0] };
    Check(CloudFilesInterop.TryReadCallbackFlags(flagParameters, out var flags) && flags == 1,
        "NativeAOT notification flags decoding");
    flagParameters.ParamSize = 11;
    Check(!CloudFilesInterop.TryReadCallbackFlags(flagParameters, out _), "truncated notification flags rejected");
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
    var path = Path.Combine(root, args.Contains("--pin-special") ? "固定+测试%2B & #.bin" : "pin-test.bin");
    var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
    var errors = new ConcurrentQueue<Exception>();
    var messages = new ConcurrentQueue<string>();
    var requestCount = 0;
    var rejectHydration = false;
    var hydrationDelayMilliseconds = 0;
    var hydrationChunkBytes = 0;
    var hydrationChunkDelayMilliseconds = 0;
    using var hydrationStarted = new ManualResetEventSlim(false);
    using var hydrationFinished = new ManualResetEventSlim(false);
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
            hydrationStarted.Set();
            if (hydrationDelayMilliseconds > 0) Thread.Sleep(hydrationDelayMilliseconds);
            var offset = checked((int)request.RequiredFileOffset);
            var length = checked((int)Math.Min(request.RequiredLength, payload.Length - offset));
            buffer = Marshal.AllocHGlobal(length);
            var operation = CloudFilesInterop.CreateOperationInfo(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);
            for (var sent = 0; sent < length;)
            {
                var chunk = Math.Min(hydrationChunkBytes > 0 ? hydrationChunkBytes : length, length - sent);
                Marshal.Copy(payload, offset + sent, buffer, chunk);
                var transfer = CloudFilesInterop.CreateOperationParameters(new CF_OPERATION_PARAMETERS.TRANSFERDATA
                {
                    Buffer = buffer, Offset = offset + sent, Length = chunk,
                    CompletionStatus = rejectHydration ? NTStatus.STATUS_ACCESS_DENIED : NTStatus.STATUS_SUCCESS
                });
                RequireSuccess(CfExecute(operation, ref transfer), "native hydration receipt");
                sent += chunk;
                if (rejectHydration) break;
                if (sent < length && hydrationChunkDelayMilliseconds > 0) Thread.Sleep(hydrationChunkDelayMilliseconds);
            }
        }
        catch (Exception error) { errors.Enqueue(error); }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            hydrationFinished.Set();
        }
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
        if (args.Contains("--drag-preparation"))
        {
            var blocked = false;
            try { using var prepared = DragFilePreparation.CreateAsync([path], () => false, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { blocked = true; }
            Check(blocked && requestCount == 0, "unavailable backend is rejected before hydration");
            var metadataOnly = DragFilePreparation.ReadMetadata(path);
            Check(metadataOnly.Size == payload.Length && !metadataOnly.IsReady && requestCount == 0,
                "descriptor metadata inspects placeholder size and cached bytes without hydration");
            hydrationChunkBytes = 32 * 1024;
            hydrationChunkDelayMilliseconds = 180;
            var byteProgress = new ConcurrentQueue<DragPreparationProgress>();
            VirtualFileDragSelfTest.Run(path, payload, Check,
                token => DragFilePreparation.CreateAsync([path], () => true, Console.WriteLine, token, byteProgress.Enqueue));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var prepared = DragFilePreparation.CreateAsync([path], () => true, Console.WriteLine, timeout.Token,
                byteProgress.Enqueue).GetAwaiter().GetResult())
            {
                prepared.ValidateReady();
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(payload) && requestCount > 0,
                    "asynchronous whole-file hydration completes before paths are supplied");
                bool nativePassed = false;
                Exception? nativeFailure = null;
                var thread = new Thread(() =>
                {
                    try { nativePassed = NativeFileDrag.CheckNativeResourceCleanup(path); }
                    catch (Exception error) { nativeFailure = error; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                if (nativeFailure is not null) throw nativeFailure;
                Check(nativePassed, "native Shell CF_HDROP / IDropSource ABI, image transition and GDI/USER resource cleanup");
            }
            hydrationChunkBytes = hydrationChunkDelayMilliseconds = 0;
            var byteSamples = byteProgress.ToArray();
            Check(byteSamples.Any(sample => sample.CompletedBytes > 0 && sample.CompletedBytes < payload.Length
                    && sample.TotalBytes == payload.Length && sample.Percent > 0 && sample.Percent < 100)
                && byteSamples.Last() is { IsComplete: true, CompletedFiles: 1, TotalFiles: 1, Percent: 100 }
                && byteSamples.Last().CompletedBytes == payload.Length,
                "real chunked hydration reports intermediate persisted byte counts and exact final totals");
            var requestsBeforeReuse = requestCount;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var reused = DragFilePreparation.CreateAsync([path], () => true, _ => { }, timeout.Token).GetAwaiter().GetResult())
            {
                reused.ValidateReady();
                Check(requestCount == requestsBeforeReuse && File.ReadAllBytes(path).AsSpan().SequenceEqual(payload),
                    "reusing an opened or previously dragged hydrated file does not fetch data again");
            }
            var progressFolder = Path.Combine(root, "progress-files");
            Directory.CreateDirectory(progressFolder);
            File.WriteAllBytes(Path.Combine(progressFolder, "cached.bin"), new byte[4097]);
            File.WriteAllBytes(Path.Combine(progressFolder, "empty.bin"), []);
            var mixedProgress = new List<DragPreparationProgress>();
            using (var mixed = DragFilePreparation.CreateAsync([path, progressFolder], () => true, _ => { },
                CancellationToken.None, mixedProgress.Add).GetAwaiter().GetResult())
            {
                var final = mixedProgress.Last();
                Check(final.IsComplete && final.TotalFiles == 3 && final.CompletedFiles == 3 && final.Percent == 100
                    && final.CompletedBytes == payload.Length + 4097 && final.TotalBytes == payload.Length + 4097
                    && requestCount == requestsBeforeReuse,
                    "mixed folders, cached files and zero-byte files use exact aggregate totals without refetching");
            }
            Directory.Delete(progressFolder, true);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var stopped = false;
                try { using var prepared = DragFilePreparation.CreateAsync([path], () => true, _ => { }, cancelled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "cancelled drag preparation returns without offering paths");
            }
            var handle = Native.Open(path, 0x00040080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { RequireSuccess(CfDehydratePlaceholder(new HFILE(handle), 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, IntPtr.Zero), "dehydrate drag fixture"); }
            finally { Native.CloseHandle(handle); }
            rejectHydration = true;
            var failed = false;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try { using var prepared = DragFilePreparation.CreateAsync([path], () => true, _ => { }, timeout.Token).GetAwaiter().GetResult(); }
                catch (IOException error) { Console.WriteLine(error.Message); failed = true; }
            }
            Check(failed, "native hydration failure is surfaced and cannot enter copy");
            rejectHydration = false;
            hydrationDelayMilliseconds = 500;
            hydrationStarted.Reset();
            hydrationFinished.Reset();
            using (var cancelPending = new CancellationTokenSource())
            {
                var pending = DragFilePreparation.CreateAsync([path], () => true, _ => { }, cancelPending.Token);
                Check(hydrationStarted.Wait(TimeSpan.FromSeconds(5)), "pending hydration has reached the provider callback");
                var cancellationTime = Stopwatch.StartNew();
                cancelPending.Cancel();
                var cancelledPending = false;
                try { using var prepared = pending.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelledPending = true; }
                Check(cancelledPending && cancellationTime.Elapsed < TimeSpan.FromSeconds(1),
                    "in-flight native hydration cancels promptly without blocking the caller");
                Check(hydrationFinished.Wait(TimeSpan.FromSeconds(5)), "provider completes after cancellation without a resource crash");
            }
            // Wait for the independent worker to finish its native cancellation drain before cleanup.
            Thread.Sleep(100);
            var cleanup = AccountSwitchCleanup.Run(root, (deleted, failed) => { }, Console.WriteLine);
            Check(cleanup.Failed == 0 && cleanup.Deleted == 1 && !Directory.EnumerateFileSystemEntries(root).Any(),
                "account-switch cleanup deletes the fixture without hydrating it");
            var requestCountBeforeCleanup = requestCount;
            var nested = Path.Combine(root, "子目录");
            Directory.CreateDirectory(nested);
            var readOnly = Path.Combine(nested, "只读.txt");
            File.WriteAllText(readOnly, "read only cleanup fixture");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            var lockedPath = Path.Combine(root, "locked.txt");
            using (var locked = new FileStream(lockedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                var partial = AccountSwitchCleanup.Run(root, (_, _) => { }, Console.WriteLine);
                Check(partial.Failed == 1 && partial.Errors.Length == 1 && File.Exists(lockedPath) && !Directory.Exists(nested),
                    "account cleanup reports a locked file and still removes read-only nested content");
            }
            var retry = AccountSwitchCleanup.Run(root, (_, _) => { }, Console.WriteLine);
            Check(retry.Failed == 0 && retry.Deleted == 1 && requestCount == requestCountBeforeCleanup,
                "account cleanup retry completes without downloading file data");
            var refusedRoot = false;
            try { AccountSwitchCleanup.Run(Path.GetPathRoot(root)!, (_, _) => { }, _ => { }); }
            catch (IOException) { refusedRoot = true; }
            Check(refusedRoot, "account cleanup refuses a drive root before enumeration/deletion");
            Console.WriteLine("PASS: outbound drag preparation and account cleanup.");
            return;
        }
        watcher.Start();
        Check(DragFilePreparation.ReadAvailability(path) == SyncRootAvailability.OnlineOnly,
            "availability metadata starts online-only");
        SetPin(CF_PIN_STATE.CF_PIN_STATE_PINNED);
        WaitFor(() => PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)
            && state.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED && state.OnDiskDataSize >= payload.Length,
            "Always keep on this device hydrates without callback failure");
        Check(requestCount > 0 && errors.IsEmpty, "native callback completed");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(payload), "hydrated content integrity");
        Check(DragFilePreparation.ReadAvailability(path) == SyncRootAvailability.Pinned,
            "availability metadata distinguishes a pinned copy");
        if (args.Contains("--hold-pin-lock"))
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            SetPin(CF_PIN_STATE.CF_PIN_STATE_UNPINNED);
            WaitFor(() => messages.Any(m => m.Contains("无法独占打开", StringComparison.Ordinal)),
                "a retained reader blocks the exclusive dehydration handle");
            Thread.Sleep(4_500); // Beyond the old watcher retry window; no further pin gesture after release.
            Check(PlaceholderUpdater.TryGetPlaceholderDataState(path, out var held) && held.OnDiskDataSize > 0,
                "locked content is preserved while the reader is alive");
        }
        else SetPin(CF_PIN_STATE.CF_PIN_STATE_UNPINNED);
        WaitFor(() => PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state) && state.OnDiskDataSize == 0,
            "Free up space dehydrates via production attribute watcher");
        Check(messages.Any(m => m.Contains("[PIN] 捕获占位属性变化")), "production pin watcher received attribute events");
        Check(PlaceholderUpdater.TryAllowLocalCache(path), "a new explicit download clears the previous unpin request");
        Check(PlaceholderUpdater.TryGetPlaceholderDataState(path, out var unspecified)
            && unspecified.PinState == CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED && unspecified.OnDiskDataSize == 0,
            "UNSPECIFIED does not hydrate on its own");
        using (var downloaded = await DragFilePreparation.CreateAsync([path], () => true, _ => { }, CancellationToken.None))
            downloaded.ValidateReady();
        Thread.Sleep(400); // Let the real attribute watcher see the hydration.
        Check(DragFilePreparation.ReadAvailability(path) == SyncRootAvailability.Local,
            "explicit hydration stays cached locally, without pinning or a second dehydration");
        Console.WriteLine("PASS: native pin / hydrate / unpin / dehydrate cycle.");

        void CreateCacheFixture(string parent, string name, FileIdentity identity)
        {
            var identityBytes = identity.Serialize();
            Marshal.Copy(identityBytes, 0, fileIdentityBuffer, identityBytes.Length);
            RequireSuccess(CfCreatePlaceholders(parent, [new()
            {
                RelativeFileName = name, FileIdentity = fileIdentityBuffer, FileIdentityLength = (uint)identityBytes.Length,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                FsMetadata = new()
                {
                    FileSize = identity.Type == 0 ? 0 : payload.Length,
                    BasicInfo = new() { FileAttributes = identity.Type == 0
                        ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL },
                },
            }], CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _), "create scoped-cache fixture " + name);
        }

        var cacheFolderId = new FileIdentity(1, 0, 1, 0);
        var cacheFolder = Path.Combine(root, "cache-folder");
        CreateCacheFixture(root, "cache-folder", cacheFolderId);
        CreateCacheFixture(cacheFolder, "nested", new(1, 1, 2, 0));
        var cacheNested = Path.Combine(cacheFolder, "nested");
        var directFileId = new FileIdentity(1, 1, 3, 1);
        CreateCacheFixture(cacheFolder, "direct.bin", directFileId);
        CreateCacheFixture(cacheNested, "ready.bin", new(1, 2, 4, 1));
        CreateCacheFixture(cacheNested, "dirty.bin", new(1, 2, 5, 1));
        CreateCacheFixture(cacheNested, "online-only.bin", new(1, 2, 6, 1));
        CreateCacheFixture(cacheNested, "different-parent.bin", new(1, 999, 7, 1));
        CreateCacheFixture(cacheNested, "pinned.bin", new(1, 2, 8, 1));
        var ordinary = Path.Combine(cacheNested, "ordinary.bin");
        File.WriteAllBytes(ordinary, payload);
        var direct = Path.Combine(cacheFolder, "direct.bin");
        var ready = Path.Combine(cacheNested, "ready.bin");
        var dirty = Path.Combine(cacheNested, "dirty.bin");
        var unrelated = Path.Combine(cacheNested, "different-parent.bin");
        var pinned = Path.Combine(cacheNested, "pinned.bin");
        using (var content = await DragFilePreparation.CreateAsync([direct, ready, dirty, unrelated, pinned], () => true,
                   _ => { }, CancellationToken.None))
            content.ValidateReady();
        SetPin(CF_PIN_STATE.CF_PIN_STATE_PINNED, pinned);
        var fetchesBeforeEviction = requestCount;
        var directResult = PlaceholderUpdater.RemoveCachedContent(direct, directFileId, () => { }, _ => false, Console.WriteLine);
        Check(directResult is { Removed: 1, Skipped: 0, FreedBytes: > 0 }
              && ProviderIdentity.TryReadPlaceholderIdentity(direct, out _)
              && DragFilePreparation.ReadAvailability(direct) == SyncRootAvailability.OnlineOnly
              && DragFilePreparation.ReadAvailability(ready) == SyncRootAvailability.Local,
            "removing one file drops its data but preserves its placeholder and sibling data");
        var treeResult = PlaceholderUpdater.RemoveCachedContent(cacheFolder, cacheFolderId, () => { },
            file => file == dirty, Console.WriteLine);
        Check(treeResult is { Removed: 1, Skipped: 2, FreedBytes: > 0 } && Directory.Exists(cacheNested)
              && DragFilePreparation.ReadAvailability(pinned) == SyncRootAvailability.Pinned
              && DragFilePreparation.ReadAvailability(ready) == SyncRootAvailability.OnlineOnly
              && DragFilePreparation.ReadAvailability(dirty) == SyncRootAvailability.Local
              && DragFilePreparation.ReadAvailability(unrelated) == SyncRootAvailability.Local
              && File.ReadAllBytes(ordinary).AsSpan().SequenceEqual(payload) && requestCount == fetchesBeforeEviction,
            "scoped folder removal dehydrates descendants without fetching data or touching pins, pending writes and foreign/local files");
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

    void SetPin(CF_PIN_STATE state, string? file = null)
    {
        var handle = Native.Open(file ?? path, 0x00040080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
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
