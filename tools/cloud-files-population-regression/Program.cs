using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangCloudSync.Storage;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

if (args is ["--cleanup-test", var abandonedTest])
{
    ValidateTestPath(abandonedTest);
    if (Directory.Exists(Path.Combine(abandonedTest, "root")))
        RequireSuccess(CfUnregisterSyncRoot(Path.Combine(abandonedTest, "root")), "unregister abandoned test");
    DeleteTestDirectory(abandonedTest);
    return;
}

// Provider-process directory IO deliberately bypasses population. Use a separate
// process to exercise the same native path as an application opening a file.
if (args is ["--enumerate", var childPath, var childPattern, var expected])
{
    for (var i = 0; i < 20; i++)
    {
        var actual = NativeQueries.CountFiles(childPath, childPattern);
        Check(actual == int.Parse(expected), $"enumeration expected={expected}, actual={actual}");
        var hydratedFile = Path.Combine(childPath, "existing.pptx");
        if (File.Exists(hydratedFile))
        {
            Check(File.ReadAllText(hydratedFile) == "Keep locally hydrated content.", "repeated file open");
            Check(File.ReadAllText(Path.Combine(childPath, "new.pptx")) == "Keep cloud file content.", "placeholder hydration and repeated open");
        }
    }
    return;
}

// Exercise the production callback against cldflt, without a client, account or Explorer registration.
var testBase = Path.Combine(Path.GetTempPath(), "YuWangPopulation-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(testBase, "root");
Directory.CreateDirectory(root);
var registered = false;
var connected = false;
CF_CONNECTION_KEY connection = default;
var counts = new ConcurrentDictionary<ulong, int>();
var messages = new ConcurrentQueue<string>();
var hydrationRequests = 0;
using var cache = new SqliteCache(Path.Combine(testBase, "cache.db"));
Callbacks.Cache = cache;
Callbacks.Client = null;
Callbacks.SyncRootPath = root;
Callbacks.UserKey = "population-regression";
Callbacks.Log = message => { messages.Enqueue(message); Console.WriteLine(message); };
Callbacks.DisablePopulationAfterPopulate = true;
CF_CALLBACK callback = (in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters) =>
{
    var bytes = new byte[info.FileIdentityLength];
    Marshal.Copy(info.FileIdentity, bytes, 0, bytes.Length);
    var identity = FileIdentity.Deserialize(bytes, bytes.Length);
    counts.AddOrUpdate(identity.FileId, 1, (_, value) => value + 1);
    Callbacks.OnFetchPlaceholders(info, parameters);
};
CF_CALLBACK fetchData = (in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters) =>
{
    Interlocked.Increment(ref hydrationRequests);
    var fetch = parameters.GetParam<CF_CALLBACK_PARAMETERS.FETCHDATA>();
    var data = System.Text.Encoding.UTF8.GetBytes("Keep cloud file content.");
    var buffer = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, buffer, data.Length);
        var operation = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            RequestKey = info.RequestKey
        };
        var transfer = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.TRANSFERDATA
        {
            CompletionStatus = NTStatus.STATUS_SUCCESS,
            Buffer = buffer + checked((int)fetch.RequiredFileOffset),
            Offset = fetch.RequiredFileOffset,
            Length = Math.Min(fetch.RequiredLength, data.Length - fetch.RequiredFileOffset)
        });
        RequireSuccess(CfExecute(operation, ref transfer), "fixture hydration");
    }
    finally { Marshal.FreeHGlobal(buffer); }
};
CF_CALLBACK_REGISTRATION[] callbacks =
[
    new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = callback },
    new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = fetchData },
    new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION, Callback = Callbacks.OnNotifyFileOpenCompletion },
    CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
];

try
{
    var identity = FileIdentity.SyncRoot.Serialize();
    var identityPtr = Marshal.AllocHGlobal(identity.Length);
    try
    {
        Marshal.Copy(identity, 0, identityPtr, identity.Length);
        var registration = new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = "YuWang population regression",
            ProviderVersion = "1.0",
            ProviderId = Guid.NewGuid(),
            FileIdentity = identityPtr,
            FileIdentityLength = (uint)identity.Length
        };
        var policies = new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new() { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PARTIAL },
            Population = new() { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_PARTIAL }
        };
        RequireSuccess(CfRegisterSyncRoot(root, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE), "register");
        registered = true;
    }
    finally { Marshal.FreeHGlobal(identityPtr); }
    RequireSuccess(CfConnectSyncRoot(root, callbacks, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out connection), "connect");
    connected = true;

    // frame_desk labels include drive markers such as (I:). Exercise the full
    // parser -> SQLite -> native placeholder -> application enumeration path.
    using (var client = new YuWangClient("https://example.invalid", "", "", _ => { }))
    {
        var parsed = client.ParseDiskTree(
            """<a ondblclick="showDisk('100','1')"><span id="100">Shared(I:)</span></a>""",
            rootListing: true);
        Check(parsed is [{ id: 100, type: "container", name: "Shared(I:)" }], "root mapping preserves server identity and label");
        cache.SaveDiskTree(Callbacks.UserKey,
        [
            new() { Id = 100, NodeType = "container", Name = parsed[0].name, ParentId = 0 },
            new() { Id = 101, NodeType = "container", Name = "Class", ParentId = 100 },
            new() { Id = 2540, NodeType = "disk", Name = "Disk", ParentId = 101 }
        ]);
        // Start at the old configured container and populate the disk. Its identity
        // stays the same when TypeId 0 introduces another directory above it.
        Callbacks.ContainerTypeId = 100;
        var oldDiskPath = Path.Combine(root, "Class", "Disk");
        cache.SaveFolder(2540, 0, 0, "Disk", oldDiskPath, [FileItem(2001, "probe.txt")]);
        EnumerateFromApplication(root, "*", 0);
        EnumerateFromApplication(Path.Combine(root, "Class"), "*", 0);
        EnumerateFromApplication(oldDiskPath, "*", 1);

        Callbacks.ContainerTypeId = 0;
        Callbacks.InvalidateFolderListing(0, 0);
        Callbacks.ReleasePlaceholderArrays();
        var topNodes = cache.ListChildNodes(Callbacks.UserKey, 0);
        Check(topNodes is [{ Id: 100, Name: "Shared(I:)" }], "cached server label retained");
        Check(topNodes[0].LocalName == "Shared(I_)", "drive label maps to a valid local name");
        Check(new DiskTreeNode { Id = 9, Name = "... " }.LocalName == "9", "empty local name falls back to identity");
        Check(new DiskTreeNode { Name = "Plain. " }.LocalName == "Plain", "local name excludes trailing dots and spaces");
        PlaceholderCreator.CreateVirtualBatch(root, topNodes);
        EnumerateFromApplication(Path.Combine(root, "Shared(I_)"), "*", 0);
        EnumerateFromApplication(Path.Combine(root, "Shared(I_)", "Class"), "*", 0);
        var diskPath = Path.Combine(root, "Shared(I_)", "Class", "Disk");
        cache.SaveFolder(2540, 0, 0, "Disk", diskPath, [FileItem(2001, "probe.txt")]);
        EnumerateFromApplication(diskPath, "*", 1);
        Check(Directory.Exists(Path.Combine(root, "Shared(I_)")), "root label is a valid local directory");
        Console.WriteLine("PASS: TypeId 100 -> 0 switch, cached drive label and nested native enumeration.");
    }

    Seed(101, "mixed", [FileItem(1001, "existing.pptx"), FileItem(1002, "new.pptx")]);
    var mixed = Path.Combine(root, "mixed");
    cache.SetRealSize(1002, System.Text.Encoding.UTF8.GetByteCount("Keep cloud file content."));
    cache.SaveFolder(1, 105, 101, "child", Path.Combine(mixed, "child"), []);
    var existing = Path.Combine(mixed, "existing.pptx");
    File.WriteAllText(existing, "Keep locally hydrated content.");
    var existingStamp = File.GetLastWriteTimeUtc(existing);
    RepeatEnumeration(101, "mixed", "*", 2);
    Check(File.ReadAllText(existing) == "Keep locally hydrated content.", "existing file data retained");
    Check(File.GetLastWriteTimeUtc(existing) == existingStamp, "existing file timestamp retained");
    Check(hydrationRequests > 0, "native file hydration exercised");
    RepeatEnumeration(105, Path.Combine("mixed", "child"), "*", 0);

    Seed(102, "empty", []);
    RepeatEnumeration(102, "empty", "*", 0);

    Seed(103, "filtered", [FileItem(1003, "slides.pptx"), FileItem(1004, "notes.txt")]);
    RepeatEnumeration(103, "filtered", "*.pptx", 1);
    EnumerateFromApplication(Path.Combine(root, "filtered"), "*", 2);
    Check(counts[103] == 1, "full query after filtered query stays populated");

    Seed(104, "absent", [FileItem(1005, "slides.pptx")]);
    RepeatEnumeration(104, "absent", "~$missing.pptx", 0);

    // Reopening population after a cache refresh must complete again, rather than being
    // suppressed by a process-lifetime 'already queued' marker.
    cache.SaveFolder(1, 101, 0, "mixed", mixed,
        [FileItem(1001, "existing.pptx"), FileItem(1002, "new.pptx"), FileItem(1006, "added.txt")]);
    Callbacks.InvalidateFolderListing(1, 101);
    Check(PlaceholderUpdater.TryEnableOnDemandPopulation(mixed, Callbacks.Log), "re-enable population");
    var before = counts[101];
    EnumerateFromApplication(mixed, "*", 3);
    Check(counts[101] == before + 1, $"refresh completes once (callbacks={counts[101] - before})");

    Check(PlaceholderUpdater.TryEnableOnDemandPopulation(mixed, Callbacks.Log), "re-enable already prepared listing");
    before = counts[101];
    EnumerateFromApplication(mixed, "*", 3);
    Check(counts[101] == before + 1, "existing listing completes again without a background queue");

    Check(!messages.Any(message => message.Contains("失败") || message.Contains("异常")), "no provider errors");
    Console.WriteLine("PASS: native CFAPI population, repeated file opens, empty/filtered/absent queries and refresh.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    if (connected) RequireSuccess(CfDisconnectSyncRoot(connection), "disconnect");
    GC.KeepAlive(callbacks);
    GC.KeepAlive(callback);
    GC.KeepAlive(fetchData);
    Callbacks.ReleasePlaceholderArrays();
    if (registered) RequireSuccess(CfUnregisterSyncRoot(root), "unregister");
    cache.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    // Only delete this freshly generated temporary directory, after unregistering it.
    DeleteTestDirectory(testBase);
}

void Seed(ulong folder, string name, IReadOnlyList<YuWangItem> items)
{
    cache.SaveFolder(1, folder, 0, name, Path.Combine(root, name), items);
    PlaceholderCreator.CreateBatch(root, 1, 0,
        [(folder, name, true, (long?)null, (DateTime?)null)]);
}

void RepeatEnumeration(ulong folder, string name, string pattern, int expectedFiles)
{
    var path = Path.Combine(root, name);
    EnumerateFromApplication(path, pattern, expectedFiles);
    Check(counts.GetValueOrDefault(folder) == 1, $"{name} completes once (callbacks={counts.GetValueOrDefault(folder)})");
    Console.WriteLine($"PASS: {name}, 20 enumerations, callbacks={counts[folder]}");
}

static void EnumerateFromApplication(string path, string pattern, int expectedFiles)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in new[] { "--enumerate", path, pattern, expectedFiles.ToString() })
        start.ArgumentList.Add(argument);
    using var application = Process.Start(start) ?? throw new InvalidOperationException("Cannot start application IO probe.");
    if (!application.WaitForExit(30_000))
    {
        application.Kill(entireProcessTree: true);
        throw new TimeoutException("Application IO probe timed out.");
    }
    Check(application.ExitCode == 0, "application IO probe");
}

static YuWangItem FileItem(ulong id, string name) => new() { Id = id, Type = 'F', Name = name, Size = 0 };
static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
}
static void RequireSuccess(HRESULT result, string operation) => Check(!result.Failed, operation + ": " + result);

static void ValidateTestPath(string path)
{
    var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    var name = Path.GetFileName(fullPath);
    Check(string.Equals(Path.GetDirectoryName(fullPath), temp, StringComparison.OrdinalIgnoreCase)
        && name.StartsWith("YuWangPopulation-") && Guid.TryParseExact(name[17..], "N", out _), "cleanup path guard");
}

static void DeleteTestDirectory(string path)
{
    ValidateTestPath(path);
    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
    Directory.Delete(path, recursive: true);
}

static class NativeQueries
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string pattern, out FindData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr handle, out FindData data);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    public static int CountFiles(string path, string pattern)
    {
        var handle = FindFirstFileW(Path.Combine(path, pattern), out var data);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return 0;
            throw new System.ComponentModel.Win32Exception(error);
        }
        try
        {
            var count = 0;
            do { if (!data.Attributes.HasFlag(FileAttributes.Directory)) count++; }
            while (FindNextFileW(handle, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new System.ComponentModel.Win32Exception(error);
            return count;
        }
        finally { FindClose(handle); }
    }
}
