using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ComponentModel;
using YuWangCloudSync.Models;
using YuWangCloudSync.Storage;
using YuWangCloudSync.Core;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// Cloud Files API 注册与占位创建封装（基于 Vanara.PInvoke.CldApi 5.0.7）。
/// 按账号/目录权限设置文件属性，并通过 Cloud Files 通知回调处理删除、移动、重命名和脱水。
///
/// </summary>
[SupportedOSPlatform("windows")]
public static class SyncRootRegistrar
{
    public static void Register(string syncRootPath, string providerName, ulong rootContainerTypeId,
        bool update = false)
    {
        // 构造 SyncRoot 根目录的 FileIdentity。
        // **关键**：用户在 Explorer 双击进入 SyncRoot 根目录时，CldApi 会触发
        // FETCH_PLACEHOLDERS 回调，回调 info.FileIdentity 取自 registration.FileIdentity。
        // 不设的话 info.FileIdentity == IntPtr.Zero，OnFetchPlaceholders 的 ReadIdentity
        // 会立即抛"Cloud Files 回调未提供有效的 FileIdentity"，根目录"云操作不成功"。
        // 多盘映射后根为虚拟哨兵 (DiskId=0,FolderId=0)，OnFetchPlaceholders 据此从 disk_tree
        // 列举 RootContainerTypeId 的直接子节点；映射值 0 对应服务端 frame_desk 根页。
        var rootIdentity = FileIdentity.SyncRoot.Serialize();
        IntPtr identityBuffer = Marshal.AllocHGlobal(rootIdentity.Length);
        IntPtr providerBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(rootIdentity, 0, identityBuffer, rootIdentity.Length);
            Marshal.Copy(ProviderIdentity.RootIdentity, 0, providerBuffer, 16);

            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
                ProviderName = providerName,
                ProviderVersion = ProductVersion.Current,
                ProviderId = ProviderIdentity.Id,
                SyncRootIdentity = providerBuffer,
                SyncRootIdentityLength = 16,
                FileIdentity = identityBuffer,
                FileIdentityLength = (uint)rootIdentity.Length
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
                Hydration = new CF_HYDRATION_POLICY
                {
                    // Keep hydration requests flowing in the background until a pinned file is
                    // fully available locally, and allow Explorer's "Free up space" operation
                    // to invoke CfDehydratePlaceholder for in-sync placeholders.
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PROGRESSIVE,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED
                },
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_PARTIAL
                }
            };

            if (update && !ProviderIdentity.OwnsRoot(syncRootPath, allowLegacy: true, providerName: providerName))
                throw new InvalidOperationException("拒绝更新其他 Provider 的 SyncRoot。");
            var result = CfRegisterSyncRoot(syncRootPath, registration, policies,
                update ? CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE : CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE);
            if (result.Failed)
                throw new Win32Exception(result.Code, $"CfRegisterSyncRoot 失败: {result}");

            Callbacks.SyncRootPath = Path.GetFullPath(syncRootPath);
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, ProductIdentity.ExecutableFileName);
                SyncRootShellRegistration.Update(syncRootPath, providerName, iconPath);
            }
            catch (Exception ex)
            {
                Callbacks.Log?.Invoke($"更新资源管理器同步根元数据失败（忽略）: {ex.Message}");
            }
            var refreshed = CfRegisterSyncRoot(syncRootPath, registration, policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE);
            if (refreshed.Failed)
                throw new Win32Exception(refreshed.Code, $"更新 SyncRoot CFAPI 标识失败: {refreshed}");
        }
        finally
        {
            // CfRegisterSyncRoot 内部会拷贝 identity buffer，本地副本可释放
            Marshal.FreeHGlobal(identityBuffer);
            Marshal.FreeHGlobal(providerBuffer);
        }
    }

    public static void Unregister(string syncRootPath, string? providerName = null)
    {
        if (!ProviderIdentity.OwnsRoot(syncRootPath, allowLegacy: true, providerName: providerName))
            throw new InvalidOperationException("目标不是本程序的 SyncRoot，拒绝反注册。");
        SyncRootShellRegistration.Remove(syncRootPath);
        if (ProviderIdentity.OwnsRoot(syncRootPath, allowLegacy: true, providerName: providerName))
        {
            var result = CfUnregisterSyncRoot(syncRootPath);
            if (result.Failed)
                throw new Win32Exception(result.Code, $"CfUnregisterSyncRoot 失败: {result}");
        }
        ExplorerStateNotifier.NotifyItem(syncRootPath);
    }
}

[SupportedOSPlatform("windows")]
public static class PlaceholderCreator
{
    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(long value) => new()
    {
        dwLowDateTime = unchecked((int)value),
        dwHighDateTime = unchecked((int)(value >> 32))
    };

    /// <summary>
    /// 目录时间未知时的哨兵（1601-01-01 = FILETIME 0）。
    /// **绝不能用"现在"**：目录占位出生时盖当前时刻，会永久压制后续"只增不减"的
    /// 上浮修正（占位创建时刻几乎总晚于内容文件的上传时间）→ 目录时间永远显示程序运行时间。
    /// 哨兵早于一切真实数据，列举/上浮修正总能把它抬到正确值。
    /// </summary>
    private static readonly DateTime UnknownFolderTime = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// 构造 CF_PLACEHOLDER_CREATE_INFO 数组 + 待释放的 identity buffer 列表。
    /// 不调用 CfCreatePlaceholders，调用方负责：
    ///   1. 用返回的数组调 CfCreatePlaceholders 或传给 TRANSFER_PLACEHOLDERS
    ///   2. 在 CfExecute 完成后释放 identities 列表里每个 IntPtr
    /// </summary>
    public static CF_PLACEHOLDER_CREATE_INFO[] Build(
        ulong diskId, ulong folderId,
        IEnumerable<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items,
        out List<IntPtr> identities, bool readOnly = true)
    {
        var list = items.ToList();
        identities = new List<IntPtr>(list.Count);
        if (list.Count == 0) return Array.Empty<CF_PLACEHOLDER_CREATE_INFO>();

        var placeholders = new CF_PLACEHOLDER_CREATE_INFO[list.Count];
        // 同级文件的最大上传时间：目录子项的创建期近似值（服务器不给目录时间）。
        // 例如"历史"列表（4 子目录 + 48 PPT）→ 子目录占位盖 48 个 PPT 的最新上传时间。
        var siblingMax = list.Where(i => !i.isFolder && i.uploadTime is not null)
            .Select(i => i.uploadTime!.Value)
            .DefaultIfEmpty(DateTime.MinValue).Max();
        var siblingMaxKnown = siblingMax != DateTime.MinValue;
        for (var index = 0; index < list.Count; index++)
        {
            var item = list[index];
            var identity = new FileIdentity(diskId, folderId, item.id, item.isFolder ? (byte)0 : (byte)1).Serialize();
            var identityBuffer = Marshal.AllocHGlobal(identity.Length);
            identities.Add(identityBuffer);
            Marshal.Copy(identity, 0, identityBuffer, identity.Length);

            // 时间戳：文件用上传时间（缺失才兜底现在）；目录用 推导值（列举方已用 DB enrich）
            // → 同级文件最大上传时间（近似）→ 低位哨兵（绝不用"现在"，见 UnknownFolderTime 注释）
            DateTime stamp;
            if (item.uploadTime is { } ut)
                stamp = ut;
            else if (item.isFolder)
                stamp = siblingMaxKnown ? siblingMax : UnknownFolderTime;
            else
                stamp = DateTime.UtcNow;
            var timestamp = ToFileTime(stamp.ToFileTimeUtc());

            // MARK_IN_SYNC：占位元数据取自云端目录响应，创建时与云端状态一致
            //   → Explorer 显示正常图标（不是"正在同步"），并减少 cfapi 的重复状态查询
            //
            // **不要加 CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE**：
            // 该标志会让 cfapi 每次 TRANSFER_PLACEHOLDERS 都**重写已存在的子占位**
            // → 产生创建/删除变更通知 → Explorer 刷新 → 重新枚举 → 回调 → 又重写……
            // 实测自激循环到 42 次/秒、累计 8400 次。
            // （占位不存在时 SUPERSEDE 本来也没用；CreatePlaceholders 里已有预检查跳过已存在项）
            var flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;

            placeholders[index] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = item.name,
                FileIdentity = identityBuffer,
                FileIdentityLength = (uint)identity.Length,
                Flags = flags,
                FsMetadata = new CF_FS_METADATA
                {
                    BasicInfo = new Kernel32.FILE_BASIC_INFO
                    {
                        CreationTime = timestamp,
                        LastAccessTime = timestamp,
                        LastWriteTime = timestamp,
                        ChangeTime = timestamp,
                        FileAttributes = (FileFlagsAndAttributes)(uint)(item.isFolder
                            ? FileAttributes.Directory
                            : FileAttributes.Normal | (readOnly ? FileAttributes.ReadOnly : 0))
                    },
                    FileSize = item.isFolder ? 0 : Math.Max(0, item.size ?? 0)
                }
            };
        }
        return placeholders;
    }

    /// <summary>
    /// 把 CF_PLACEHOLDER_CREATE_INFO 数组 marshal 到非托管连续内存，返回 IntPtr。
    /// 调用方必须在 CfExecute 返回后用 Marshal.FreeHGlobal 释放。
    /// </summary>
    public static IntPtr MarshalPlaceholderArray(CF_PLACEHOLDER_CREATE_INFO[] placeholders)
    {
        if (placeholders.Length == 0) return IntPtr.Zero;
        var elemSize = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
        var totalSize = elemSize * placeholders.Length;
        var ptr = Marshal.AllocHGlobal(totalSize);
        try
        {
            for (var i = 0; i < placeholders.Length; i++)
                Marshal.StructureToPtr(placeholders[i], ptr + i * elemSize, false);
            return ptr;
        }
        catch
        {
            Marshal.FreeHGlobal(ptr);
            throw;
        }
    }

    /// <summary>释放 MarshalPlaceholderArray 分配的非托管数组内存</summary>
    public static void FreePlaceholderArray(IntPtr ptr, int count)
    {
        if (ptr == IntPtr.Zero || count == 0) return;
        var elemSize = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
        for (var i = 0; i < count; i++)
            Marshal.DestroyStructure(ptr + i * elemSize, typeof(CF_PLACEHOLDER_CREATE_INFO));
        Marshal.FreeHGlobal(ptr);
    }

    /// <summary>
    /// 调 CfCreatePlaceholders 创建占位（预检查跳过已存在的，避免 ALREADY_EXISTS）。
    /// </summary>
    public static int CreatePlaceholders(string syncRootPath, CF_PLACEHOLDER_CREATE_INFO[] placeholders)
    {
        if (placeholders.Length == 0) return 0;

        // 预检查：跳过已存在的占位
        var toCreate = new List<CF_PLACEHOLDER_CREATE_INFO>();
        var skippedNames = new List<string>();
        foreach (var ph in placeholders)
        {
            var fullPath = Path.Combine(syncRootPath, ph.RelativeFileName);
            var isFolder = ((uint)ph.FsMetadata.BasicInfo.FileAttributes & (uint)FileAttributes.Directory) != 0;
            var exists = isFolder ? Directory.Exists(fullPath) : File.Exists(fullPath);
            if (exists) skippedNames.Add(ph.RelativeFileName);
            else toCreate.Add(ph);
        }
        // 诊断：跳过多少、要创建多少（写入 Callbacks.Log 让 .log 文件捕获）
        Callbacks.Log?.Invoke($"[CreatePlaceholders] total={placeholders.Length} skipped={skippedNames.Count} toCreate={toCreate.Count} syncRootPath={syncRootPath}");
        if (skippedNames.Count > 0)
            Callbacks.Log?.Invoke($"[CreatePlaceholders] skipped: {string.Join(", ", skippedNames.Take(5))}{(skippedNames.Count > 5 ? "..." : "")}");
        if (toCreate.Count == 0)
        {
            // 所有占位已存在，无需创建
            return 0;
        }

        var subArray = toCreate.ToArray();
        var result = CfCreatePlaceholders(syncRootPath, subArray, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var entriesProcessed);
        // 诊断：CfCreatePlaceholders 返回结果 + 每项 Result
        var perItemResults = subArray
            .Select((ph, i) => (name: ph.RelativeFileName, result: ph.Result, isFolder: ((uint)ph.FsMetadata.BasicInfo.FileAttributes & (uint)FileAttributes.Directory) != 0))
            .ToArray();
        var failedCount = perItemResults.Count(x => x.result.Failed);
        Callbacks.Log?.Invoke($"[CfCreatePlaceholders] api={result} entriesProcessed={entriesProcessed}/{subArray.Length} failedItems={failedCount}");
        if (failedCount > 0)
            Callbacks.Log?.Invoke($"[CfCreatePlaceholders] 失败项: {string.Join(", ", perItemResults.Where(x => x.result.Failed).Take(5).Select(x => $"'{x.name}'(isFolder={x.isFolder}): {x.result}"))}");
        if (result.Failed)
            throw new Win32Exception(result.Code, $"CfCreatePlaceholders 失败: {result}");

        var errors = subArray
            .Select((ph, i) => (ph, i))
            .Where(entry => entry.ph.Result.Failed)
            .Select(entry => $"{toCreate[entry.i].RelativeFileName}: {entry.ph.Result}")
            .ToArray();
        if (errors.Length > 0 || entriesProcessed != subArray.Length)
            throw new InvalidOperationException(
                $"占位符创建不完整（已处理 {entriesProcessed}/{subArray.Length}）：{string.Join("; ", errors)}");

        return toCreate.Count;
    }

    /// <summary>旧的便捷入口：Build + CreatePlaceholders + 释放 identities（不传给 TRANSFER_PLACEHOLDERS 的场景）</summary>
    public static int CreateBatch(string syncRootPath, ulong diskId, ulong folderId,
        IEnumerable<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items,
        bool readOnly = true)
    {
        var placeholders = Build(diskId, folderId, items, out var identities, readOnly);
        try
        {
            return CreatePlaceholders(syncRootPath, placeholders);
        }
        finally
        {
            foreach (var id in identities) Marshal.FreeHGlobal(id);
        }
    }

    /// <summary>
    /// 由 disk_tree 子节点构建占位数组（虚拟节点分支专用）。
    /// 容器子节点 → 身份 (0, typeid, 0, 0)；磁盘子节点 → 身份 (diskid, 0, 0, 0)。
    /// 所有子节点都是目录（虚拟层只到"磁盘根"，磁盘内的真实文件夹/文件走真实分支枚举）。
    /// </summary>
    public static CF_PLACEHOLDER_CREATE_INFO[] BuildFromDiskTree(
        List<DiskTreeNode> nodes, out List<IntPtr> identities)
    {
        identities = new List<IntPtr>(nodes.Count);
        if (nodes.Count == 0) return Array.Empty<CF_PLACEHOLDER_CREATE_INFO>();

        var placeholders = new CF_PLACEHOLDER_CREATE_INFO[nodes.Count];
        // 虚拟节点（容器/磁盘）无服务器时间：低位哨兵，待列举磁盘根/启动对账时上浮到真实推导值
        var timestamp = ToFileTime(UnknownFolderTime.ToFileTimeUtc());
        var flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;

        for (var index = 0; index < nodes.Count; index++)
        {
            var nd = nodes[index];
            // 容器 → (0, typeid, 0, 0)；磁盘 → (diskid, 0, 0, 0)
            var id = nd.IsContainer ? FileIdentity.ForContainer(nd.Id) : FileIdentity.ForDisk(nd.Id);
            var identity = id.Serialize();
            var identityBuffer = Marshal.AllocHGlobal(identity.Length);
            identities.Add(identityBuffer);
            Marshal.Copy(identity, 0, identityBuffer, identity.Length);

            placeholders[index] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = nd.LocalName,
                FileIdentity = identityBuffer,
                FileIdentityLength = (uint)identity.Length,
                Flags = flags,
                FsMetadata = new CF_FS_METADATA
                {
                    BasicInfo = new Kernel32.FILE_BASIC_INFO
                    {
                        CreationTime = timestamp,
                        LastAccessTime = timestamp,
                        LastWriteTime = timestamp,
                        ChangeTime = timestamp,
                        FileAttributes = (FileFlagsAndAttributes)(uint)FileAttributes.Directory
                    },
                    FileSize = 0
                }
            };
        }
        return placeholders;
    }

    /// <summary>虚拟节点便捷入口：构建 disk_tree 子节点占位并创建（启动期建顶层三组容器时用）</summary>
    public static void CreateVirtualBatch(string localDir, List<DiskTreeNode> nodes)
    {
        if (nodes.Count == 0) return;
        var phs = BuildFromDiskTree(nodes, out var identities);
        try
        {
            CreatePlaceholders(localDir, phs);
        }
        finally
        {
            foreach (var id in identities) Marshal.FreeHGlobal(id);
        }
    }
}

[SupportedOSPlatform("windows")]
public static class CallbackTable
{
    public static CF_CALLBACK_REGISTRATION[] BuildForCleanup() =>
    [
        new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = Callbacks.OnCleanupFetchData },
        new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = Callbacks.OnCleanupFetchPlaceholders },
        CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
    ];

    public static CF_CALLBACK_REGISTRATION[] Build()
    {
        return
        [
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = Callbacks.OnFetchData },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = Callbacks.OnFetchPlaceholders },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION, Callback = Callbacks.OnNotifyFileCloseCompletion },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE, Callback = Callbacks.OnNotifyDehydrate },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE_COMPLETION, Callback = Callbacks.OnNotifyDehydrateCompletion },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = Callbacks.OnNotifyDelete },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION, Callback = Callbacks.OnNotifyDeleteCompletion },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = Callbacks.OnNotifyRename },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION, Callback = Callbacks.OnNotifyRenameCompletion },
            // 文件/目录打开完成通知：用于"关闭按需填充后仍可刷新"。
            // 填充后关闭按需的目录，用户重新进入时此回调触发 → 缓存过期则重开按需 →
            // cfapi 重新触发 FETCH_PLACEHOLDERS 刷新 → 填充后再关闭。闭环实现刷新。
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION, Callback = Callbacks.OnNotifyFileOpenCompletion },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        ];
    }
}

[SupportedOSPlatform("windows")]
public static class Callbacks
{
    public static void OnCleanupFetchPlaceholders(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
            {
                CompletionStatus = NTStatus.STATUS_SUCCESS,
                Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION,
                PlaceholderTotalCount = 0,
                PlaceholderCount = 0,
                PlaceholderArray = IntPtr.Zero,
                EntriesProcessed = 0
            });
    }

    public static void OnCleanupFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var fetch = parameters.GetParam<CF_CALLBACK_PARAMETERS.FETCHDATA>();
        Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                CompletionStatus = NTStatus.STATUS_UNSUCCESSFUL,
                Buffer = IntPtr.Zero,
                Offset = fetch.RequiredFileOffset,
                Length = fetch.RequiredLength
            });
    }

    public static Net.YuWangClient? Client;
    public static Storage.SqliteCache? Cache;
    public static Action<string>? Log;
    public static string SyncRootPath = Environment.CurrentDirectory;
    public static volatile bool SuppressDeleteRecovery;

    /// <summary>磁盘树入口映射值（来自 SyncRootContext.RootContainerTypeId）</summary>
    public static ulong ContainerTypeId { get; set; } = 0;
    /// <summary>多用户隔离键（来自 SyncRootContext.UserKey，回落到登录名）</summary>
    public static string UserKey { get; set; } = "";

    /// <summary>
    /// 节点复合键（DiskId, FolderId, FileId）。
    /// **关键修复**：原先所有缓存字典以 identity.FileId 为键——但虚拟容器节点与磁盘根的
    /// FileId 恒为 0（SyncRoot 根、24腾飞班级/年级共享/校级共享 三容器、各磁盘根都是 0），
    /// 多盘映射后会严重串键。复合键能唯一区分每个节点，同时解决"多磁盘根 folder_id=0 互相覆盖"。
    /// </summary>
    private readonly struct NodeKey : IEquatable<NodeKey>
    {
        public readonly ulong DiskId, FolderId, FileId;
        public NodeKey(ulong disk, ulong folder, ulong file) { DiskId = disk; FolderId = folder; FileId = file; }
        public NodeKey(FileIdentity id) : this(id.DiskId, id.FolderId, id.FileId) { }
        public bool Equals(NodeKey o) => DiskId == o.DiskId && FolderId == o.FolderId && FileId == o.FileId;
        public override bool Equals(object? o) => o is NodeKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(DiskId, FolderId, FileId);
        public override string ToString() => $"(D{DiskId},F{FolderId},I{FileId})";
    }

    /// <summary>
    /// 列举并发节流。Explorer 读取目录元信息时会连锁触发子目录的 FETCH_PLACEHOLDERS，
    /// 若不限制会同时打出几十个 HTTP 请求（拖慢整体响应，甚至触发 cfapi 60s 超时）。
    /// 这里限制同时进行的育网云列举请求数，超出的排队等待。
    /// 注意：不能设太小，否则排队过久反而触发 cfapi 超时。
    /// </summary>
    private static readonly SemaphoreSlim ListThrottle = new(3, 3);

    /// <summary>
    /// 目录列表的**进程内**缓存（替代反复查 SQLite）。
    /// Explorer 会对同一个目录触发成百上千次 FETCH_PLACEHOLDERS 回调，
    /// 即使 SQLite 已加锁，每次都开 Reader 扫描两张表也是浪费。
    /// 命中后连 SQLite 都不碰。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, (DateTime expires, List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)> MemFolders = new();

    /// <summary>同一目录的诊断日志节流（key=NodeKey，value=上次打印时刻）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, DateTime> LastDiagLog = new();

    /// <summary>每个目录的 FETCH_PLACEHOLDERS 回调累计次数（暴露 Explorer 的重复枚举风暴）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, int> PlaceholderCallbackCount = new();

    /// <summary>
    /// 已成功完成填充的目录（NodeKey → 条目数），仅用于诊断，不代替 CFAPI 完成回执。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, int> PopulatedFolders = new();

    /// <summary>单个文件的水化活跃度（用于在"安全时刻"改占位大小，见 PlaceholderFixer）</summary>
    private sealed class HydrationActivity
    {
        public int InFlight;
        public long LastTick;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, HydrationActivity> Hydration = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, string> PendingRenameTargets = new();

    private static readonly NTStatus CloudFileAccessDenied = new(unchecked((int)0xC000CF18));

    public static void OnNotifyFileCloseCompletion(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        try
        {
            var identity = ReadIdentity(info);
            Log?.Invoke($"[WRITE] close-completed identity={identity.DiskId}/{identity.FolderId}/{identity.FileId}");
        }
        catch (Exception error) { Log?.Invoke($"[WRITE] close completion 解析失败: {error.Message}"); }
    }

    public static void OnNotifyDehydrate(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        // Explorer's "Free up space" only removes the local data stream. It does not write to
        // the cloud, so both read-only and writable directories may dehydrate successfully.
        ulong fileId = 0;
        try { fileId = ReadIdentity(info).FileId; } catch { }
        Log?.Invoke($"[HYDRATION] dehydrate-request file={fileId} identityLength={info.FileIdentityLength} size={info.FileSize}");
        var ack = new CF_OPERATION_PARAMETERS.ACKDEHYDRATE
        {
            Flags = CF_OPERATION_ACK_DEHYDRATE_FLAGS.CF_OPERATION_ACK_DEHYDRATE_FLAG_NONE,
            CompletionStatus = NTStatus.STATUS_SUCCESS,
            FileIdentity = info.FileIdentity,
            FileIdentityLength = info.FileIdentityLength,
        };
        var result = Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DEHYDRATE, ack);
        Log?.Invoke($"[HYDRATION] dehydrate-ack file={fileId} result={result}");
    }

    public static void OnNotifyDehydrateCompletion(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        try
        {
            var identity = ReadIdentity(info);
            Log?.Invoke($"[HYDRATION] dehydrate-completed file={identity.FileId}");
        }
        catch (Exception error) { Log?.Invoke($"[HYDRATION] dehydrate completion 解析失败: {error.Message}"); }
    }

    public static void OnNotifyDelete(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var status = CloudFileAccessDenied;
        FileIdentity identity = default;
        try
        {
            identity = ReadIdentity(info);
            var permission = Cache?.GetDirectoryPermission(UserKey, identity.DiskId, identity.FolderId)
                ?? DirectoryPermission.Unknown;
            // Read-only delete and drag-out operations are allowed to finish locally. The
            // completion callback restores the cloud placeholder metadata without hydrating data.
            // Full-access deletions are confirmed against the server after local completion.
            if (SuppressDeleteRecovery || permission is DirectoryPermission.ReadOnly or DirectoryPermission.FullAccess)
                status = NTStatus.STATUS_SUCCESS;
            Log?.Invoke($"[WRITE] delete-request file={identity.FileId} directory={identity.Type == 0} permission={permission} ack={status}");
        }
        catch (Exception error)
        {
            Log?.Invoke($"[WRITE] delete notification failed closed: {error.Message}");
        }

        Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            new CF_OPERATION_PARAMETERS.ACKDELETE
            {
                Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
                CompletionStatus = status,
            });
    }

    public static void OnNotifyDeleteCompletion(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        if (SuppressDeleteRecovery) return;
        try
        {
            var identity = ReadIdentity(info);
            var pathHint = GetLocalPath(info);
            var permission = Cache?.GetDirectoryPermission(UserKey, identity.DiskId, identity.FolderId)
                ?? DirectoryPermission.Unknown;
            if (permission != DirectoryPermission.FullAccess)
            {
                _ = Task.Run(() => RestoreDeletedPlaceholder(identity, pathHint));
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var deleted = await (Client ?? throw new InvalidOperationException("Cloud client 尚未初始化"))
                        .DeleteRemoteItemAsync(identity.DiskId, identity.FolderId, identity.FileId, identity.Type == 0)
                        .ConfigureAwait(false);
                    if (!deleted)
                    {
                        Log?.Invoke($"[WRITE] 云端删除未确认，恢复本地占位 file={identity.FileId}");
                        RestoreDeletedPlaceholder(identity, pathHint);
                        return;
                    }
                    Cache?.RemoveCachedItem(identity);
                    Cache?.InvalidateFolderListing(identity.DiskId, identity.FolderId);
                    InvalidateFolderListing(identity.DiskId, identity.FolderId);
                    Log?.Invoke($"[WRITE] 云端删除已确认 file={identity.FileId}");
                }
                catch (Exception error)
                {
                    Log?.Invoke($"[WRITE] 云端删除失败，恢复本地占位 file={identity.FileId}: {error.Message}");
                    RestoreDeletedPlaceholder(identity, pathHint);
                }
            });
        }
        catch (Exception error) { Log?.Invoke($"[WRITE] delete completion 处理失败: {error.Message}"); }
    }

    public static void OnNotifyRename(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var status = CloudFileAccessDenied;
        try
        {
            var identity = ReadIdentity(info);
            var permission = Cache?.GetDirectoryPermission(UserKey, identity.DiskId, identity.FolderId)
                ?? DirectoryPermission.Unknown;
            if (permission == DirectoryPermission.FullAccess)
            {
                var rename = parameters.GetParam<CF_CALLBACK_PARAMETERS.RENAME>();
                PendingRenameTargets[new NodeKey(identity)] = ResolveTargetPath(info, rename.TargetPath);
                status = NTStatus.STATUS_SUCCESS;
            }
            Log?.Invoke($"[WRITE] rename-request file={identity.FileId} permission={permission} ack={status}");
        }
        catch (Exception error) { Log?.Invoke($"[WRITE] rename notification failed closed: {error.Message}"); }

        Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            new CF_OPERATION_PARAMETERS.ACKRENAME
            {
                Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE,
                CompletionStatus = status,
            });
    }

    public static void OnNotifyRenameCompletion(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        try
        {
            var identity = ReadIdentity(info);
            if (!PendingRenameTargets.TryRemove(new NodeKey(identity), out var targetPath)) return;
            _ = Task.Run(() => SyncCompletedRenameAsync(identity, targetPath));
        }
        catch (Exception error) { Log?.Invoke($"[WRITE] rename completion 处理失败: {error.Message}"); }
    }

    private static async Task SyncCompletedRenameAsync(FileIdentity identity, string targetPath)
    {
        try
        {
            var targetFolderId = (ulong?)null;
            var root = Path.GetFullPath(SyncRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var isInsideRoot = targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (isInsideRoot)
            {
                var targetParent = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidDataException("移动目标没有父目录。");
                targetFolderId = Cache?.TryGetFolderByLocalPath(targetParent)?.folderId;
                if (targetFolderId is null)
                    throw new InvalidDataException($"移动目标目录尚未映射到云端: {targetParent}");
            }

            var newName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar));
            var ok = await (Client ?? throw new InvalidOperationException("Cloud client 尚未初始化"))
                .RenameRemoteItemAsync(identity.DiskId, identity.FolderId, identity.FileId,
                    identity.Type == 0, targetFolderId, newName).ConfigureAwait(false);
            if (!ok) throw new IOException("服务器未确认重命名或移动。");

            Cache?.InvalidateFolderListing(identity.DiskId, identity.FolderId);
            InvalidateFolderListing(identity.DiskId, identity.FolderId);
            if (targetFolderId is { } target)
            {
                Cache?.InvalidateFolderListing(identity.DiskId, target);
                InvalidateFolderListing(identity.DiskId, target);
            }
            Log?.Invoke($"[WRITE] 云端重命名/移动已确认 file={identity.FileId}");
        }
        catch (Exception error)
        {
            Log?.Invoke($"[WRITE] 云端重命名/移动失败，尝试还原源占位 file={identity.FileId}: {error.Message}");
            RestoreDeletedPlaceholder(identity);
        }
    }

    private static string ResolveTargetPath(CF_CALLBACK_INFO info, string? targetPath)
    {
        var target = targetPath ?? "";
        var volume = info.VolumeDosName ?? "";
        if (!string.IsNullOrWhiteSpace(volume) && target.StartsWith('\\'))
            target = volume.TrimEnd('\\') + target;
        return Path.GetFullPath(target);
    }

    private static void RestoreDeletedPlaceholder(FileIdentity identity, string? pathHint = null)
    {
        if (SuppressDeleteRecovery) return;
        var cache = Cache;
        if (cache is null) return;
        try
        {
            var restore = cache.GetPlaceholderRestoreInfo(identity);
            if (restore is null)
            {
                var client = Client;
                if (client is not null)
                {
                    if (!string.IsNullOrWhiteSpace(pathHint))
                    {
                        var parentPath = Path.GetDirectoryName(pathHint);
                        if (!string.IsNullOrWhiteSpace(parentPath))
                        {
                            cache.EnsureFolderRow(identity.DiskId, identity.FolderId);
                            cache.SetFolderLocalPath(identity.DiskId, identity.FolderId, parentPath);
                        }
                    }
                    var listing = client.ListFolderWithPermissionsAsync(identity.DiskId, identity.FolderId)
                        .GetAwaiter().GetResult();
                    if (listing.IsValidPage)
                    {
                        cache.SaveDirectoryPermission(UserKey, identity.DiskId, identity.FolderId, listing.Permission);
                        cache.EnsureFolderRow(identity.DiskId, identity.FolderId);
                        foreach (var item in listing.Items)
                        {
                            if (item.IsFolder)
                            {
                                cache.SaveSubFolder(identity.DiskId, item.Id, identity.FolderId, item.Name,
                                    Path.Combine(cache.GetFolderLocalPath(identity.DiskId, identity.FolderId) ?? "", item.Name));
                            }
                            else
                            {
                                cache.SaveFileMetadata(identity.DiskId, identity.FolderId, item.Id, item.Name,
                                    item.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss"), item.Size);
                            }
                        }
                    }
                }
                restore = cache.GetPlaceholderRestoreInfo(identity);
            }
            if (restore is null || !Directory.Exists(restore.ParentPath))
            {
                Log?.Invoke($"[WRITE] 无法恢复 placeholder 元数据 file={identity.FileId}（父目录或缓存缺失）");
                return;
            }

            var permission = cache.GetDirectoryPermission(UserKey, identity.DiskId, identity.FolderId);
            var count = PlaceholderCreator.CreateBatch(restore.ParentPath, identity.DiskId, identity.FolderId,
                [(identity.FileId, restore.Name, restore.IsFolder, restore.Size, restore.UploadTime)],
                readOnly: permission != DirectoryPermission.FullAccess);
            if (count > 0)
            {
                ExplorerStateNotifier.NotifyItem(Path.Combine(restore.ParentPath, restore.Name));
                ExplorerStateNotifier.NotifyDirectory(restore.ParentPath);
            }
            Log?.Invoke($"[WRITE] placeholder 已恢复（不水化）file={identity.FileId} created={count}");
        }
        catch (Exception error) { Log?.Invoke($"[WRITE] placeholder 恢复失败 file={identity.FileId}: {error}"); }
    }

    /// <summary>
    /// 该文件当前是否处于"水化静默"：没有在途的 FETCH_DATA 回调，且已静默 quietMs 毫秒。
    /// 只有静默时才能安全地 CfUpdatePlaceholder 改大小——
    /// 水化途中把文件**缩小**会让在途/后续的 TRANSFER_DATA 落到新长度之外，
    /// cfapi 直接报 ERROR_CLOUD_FILE_INVALID_REQUEST，整次水化失败（实测复制损坏）。
    /// </summary>
    public static bool IsHydrationQuiet(ulong fileId, long quietMs)
    {
        if (!Hydration.TryGetValue(fileId, out var a)) return true;
        if (Volatile.Read(ref a.InFlight) > 0) return false;
        return Environment.TickCount64 - Volatile.Read(ref a.LastTick) >= quietMs;
    }

    private static bool TryMem(NodeKey key,
        out List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)
    {
        if (MemFolders.TryGetValue(key, out var e) && DateTime.UtcNow < e.expires)
        {
            items = e.items;
            return true;
        }
        items = new List<(ulong, string, bool, long?, DateTime?)>();
        return false;
    }

    private static void SetMem(NodeKey key,
        List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)
        => MemFolders[key] = (DateTime.UtcNow.AddMinutes(10), items);

    /// <summary>诊断日志节流：同一目录 10 秒内只打一次</summary>
    private static bool DiagAllowed(NodeKey key)
    {
        var now = DateTime.UtcNow;
        if (LastDiagLog.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(10))
            return false;
        LastDiagLog[key] = now;
        return true;
    }

    public static void OnFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var fetch = parameters.GetParam<CF_CALLBACK_PARAMETERS.FETCHDATA>();
        var offset = fetch.RequiredFileOffset;
        IntPtr buffer = IntPtr.Zero;
        HydrationActivity? activity = null;
        try
        {
            var identity = ReadIdentity(info);
            if (identity.Type != 1) throw new InvalidDataException("FETCH_DATA 收到的身份不是文件");
            var pinState = "unknown";
            try
            {
                var localFile = Cache?.GetFileLocalPath(identity.FileId);
                if (!string.IsNullOrWhiteSpace(localFile))
                {
                    var attributes = (uint)File.GetAttributes(localFile);
                    pinState = $"pinned={(attributes & 0x00080000) != 0},unPinned={(attributes & 0x00100000) != 0}," +
                               $"recall={(attributes & 0x00440000) != 0}";
                }
            }
            catch { /* 仅诊断属性，不影响水化回调。 */ }
            Log?.Invoke($"[HYDRATION] fetch-request file={identity.FileId} offset={offset} " +
                        $"length={fetch.RequiredLength} size={info.FileSize} state={pinState}");

            // 登记水化活跃度：目录枚举校准旧占位大小时需避开正在进行的 FETCH_DATA。
            activity = Hydration.GetOrAdd(identity.FileId, _ => new HydrationActivity());
            Interlocked.Increment(ref activity.InFlight);
            Volatile.Write(ref activity.LastTick, Environment.TickCount64);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // FETCH_DATA 完成前，Explorer 看不到网络下载阶段的进度。报告当前传输键的提供方进度，
            // 让 Windows 在 HTTP 分片读取期间也能更新自动文件下载的进度 UI。
            var connectionKey = info.ConnectionKey;
            var transferKey = info.TransferKey;
            long reportedTotal = 0;
            long reportedCompleted = 0;
            Action<long, long> reportProgress = (totalProgress, completedProgress) =>
            {
                // Range 请求重试会从头读取；同一总量下保留已报告的高水位，避免系统进度倒退。
                if (totalProgress == reportedTotal)
                    completedProgress = Math.Max(completedProgress, reportedCompleted);
                else
                {
                    reportedTotal = totalProgress;
                    reportedCompleted = 0;
                }
                reportedCompleted = Math.Clamp(completedProgress, 0, totalProgress);
                ReportProviderProgress(connectionKey, transferKey, totalProgress, reportedCompleted);
            };
            var (data, total, viaDirect) = FetchRange(identity, offset, fetch.RequiredLength, reportProgress);
            Log?.Invoke($"[FETCH_DATA] file={identity.FileId} offset={offset} 请求={fetch.RequiredLength} " +
                        $"实得={data.Length} 文件总长={total} 占位声明={info.FileSize} " +
                        $"来源={(viaDirect ? "直链Range" : "整文件缓存")} 用时={sw.Elapsed.TotalSeconds:F1}s");

            // 精确大小应在 FETCH_PLACEHOLDERS 期间由直链 HEAD 探测，并在文件访问前对齐旧占位。
            // FETCH_DATA 期间只把 Range 返回的真实大小记入缓存，绝不对正在水化的文件调用
            // CfUpdatePlaceholder；改大小会丢失/重置 CFAPI 已记录的本地数据范围。
            var sizeNeedsRecording = total > 0 && total != info.FileSize
                && (!RealSizeRecorded.TryGetValue(identity.FileId, out var recordedSize) || recordedSize != total);
            if (sizeNeedsRecording)
            {
                RealSizeRecorded[identity.FileId] = total;
                Log?.Invoke($"[SIZE] 当前占位大小与直链真实大小不一致: file={identity.FileId} " +
                            $"占位={info.FileSize} 真实={total}；仅记录缓存，水化回调不改当前占位");
                try { Cache?.SetRealSize(identity.FileId, total); }
                catch (Exception ex) { Log?.Invoke($"[SIZE] 落库真实大小失败: {ex.Message}"); }

                try { InvalidateFolderListing(identity.DiskId, identity.FolderId); }
                catch (Exception ex) { Log?.Invoke($"[SIZE] 失效父目录缓存失败（忽略）: {ex.Message}"); }
            }

            // 旧占位在重新列举父目录前可能仍大于真实大小；本次按 CFAPI 要求补齐请求，
            // 后续列举会先把该占位大小与已缓存的真实值对齐，再交给 Explorer 继续访问。
            if (info.FileSize > total && data.Length < fetch.RequiredLength && total > 0)
            {
                var need = (int)Math.Min(fetch.RequiredLength, info.FileSize - offset);
                if (need > data.Length)
                {
                    var padded = new byte[need];
                    Array.Copy(data, 0, padded, 0, data.Length);
                    Log?.Invoke($"[FETCH_DATA] file={identity.FileId} offset={offset} 尾部补零 {need - data.Length} 字节（占位余量）");
                    data = padded;
                }
            }

            if (data.Length > 0)
            {
                buffer = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, buffer, data.Length);
            }

            var transfer = Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                new CF_OPERATION_PARAMETERS.TRANSFERDATA
                {
                    CompletionStatus = NTStatus.STATUS_SUCCESS,
                    Buffer = buffer,
                    Offset = offset,
                    Length = (uint)data.Length
                });
            Log?.Invoke($"[HYDRATION] fetch-transfer file={identity.FileId} offset={offset} " +
                        $"requested={fetch.RequiredLength} transferred={data.Length} result={transfer}");
            if (!transfer.Failed)
            {
                var updatedPath = ResolveFilePath(identity, info);
                if (updatedPath != null) ExplorerStateNotifier.NotifyItem(updatedPath);
            }
            if (offset + data.Length >= total && total > 0)
            {
                try { Cache?.MarkHydrated(identity.FileId); }
                catch (Exception ex) { Log?.Invoke($"更新 hydration 缓存状态失败: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            // 输出完整堆栈：只打 Message 无法定位（本次就是靠堆栈才找到 SQLite 并发）
            Log?.Invoke($"FETCH_DATA 失败: {ex}");
            Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                new CF_OPERATION_PARAMETERS.TRANSFERDATA
                {
                    CompletionStatus = NTStatus.STATUS_UNSUCCESSFUL,
                    Buffer = IntPtr.Zero,
                    Offset = offset,
                    Length = 0
                });
        }
        finally
        {
            if (activity != null)
            {
                Interlocked.Decrement(ref activity.InFlight);
                Volatile.Write(ref activity.LastTick, Environment.TickCount64);
            }
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 把目录子项的 uploadTime 用 DB 推导值补齐（该目录全部已入库后代文件的最大上传时间），
    /// 供建占位时目录时间戳使用——服务器不给目录时间，未 enrich 的目录占位只能盖同级近似值/哨兵。
    /// 仅补 uploadTime 为 null 的目录项，文件项不动；全都不需要补时原样返回（零分配）。
    /// </summary>
    private static List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> EnrichFolderTimes(
        SqliteCache cache, ulong diskId,
        List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)
    {
        if (items.All(i => !i.isFolder)) return items;
        var result = new List<(ulong, string, bool, long?, DateTime?)>(items.Count);
        foreach (var i in items)
            if (i.isFolder)
            {
                // The API does not expose folder timestamps. Always refresh this value;
                // a previously cached null can become known after the child is enumerated.
                var derived = cache.GetFolderTreeMaxUploadTime(diskId, i.id) ?? i.uploadTime;
                result.Add((i.id, i.name, i.isFolder, i.size, derived));
            }
            else
                result.Add(i);
        return result;
    }

    /// <summary>
    /// 文件/目录打开完成通知回调。
    /// 用于实现"关闭按需填充后仍可刷新"：
    ///   填充后关闭按需的目录，用户重新进入时此回调触发 → 检查统一缓存周期 →
    ///   周期已过则清除进程快照并重开按需填充 → cfapi 重新触发 FETCH_PLACEHOLDERS 联网刷新 → 填充后再关闭。
    /// 非目录打开直接 return（文件水化的 CreateFile 不触发刷新）。
    /// </summary>
    public static void OnNotifyFileOpenCompletion(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        try
        {
            var identity = ReadIdentity(info);
            // 非目录（文件打开）不处理——FileIdentity.Type=0 是文件夹，!=0 是文件
            if (identity.Type != 0) return;
            // 虚拟节点（SyncRoot 根/容器）不走 HTTP 刷新（disk_tree 启动时已拉取，无 last_listed 过期概念）
            if (identity.IsVirtual) return;
            var localDir = GetLocalPath(info);
            var cache = Cache;
            if (cache == null) return;
            // 缓存有效则不刷新——避免每次进入都 HTTP
            if (cache.IsFolderCached(identity.DiskId, identity.FileId, FolderCache)) return;
            // 到期后先清除内存清单和占位创建快照；否则后续回调可能命中旧快照而不联网。
            InvalidateFolderListing(identity.DiskId, identity.FileId);
            // 重开按需填充，cfapi 下次列举触发 FETCH_PLACEHOLDERS 联网刷新，填充后再关闭。
            PlaceholderUpdater.TryEnableOnDemandPopulation(localDir, Log);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[POP] OnNotifyFileOpenCompletion 异常（忽略）: {ex.Message}");
        }
    }

    public static void OnFetchPlaceholders(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var identity = default(FileIdentity);
        var completionStatus = NTStatus.STATUS_SUCCESS;
        uint totalCount = 0;
        string populatedDir = "";
        try
        {
            identity = ReadIdentity(info);
            if (identity.Type != 0) throw new InvalidDataException("FETCH_PLACEHOLDERS 收到的身份不是文件夹");
            var nodeKey = new NodeKey(identity);

            // 回调风暴计数：按复合键，避免虚拟/真实节点串计
            var n = PlaceholderCallbackCount.AddOrUpdate(nodeKey, 1, (_, v) => v + 1);
            if (n == 1 || n % 200 == 0)
                Log?.Invoke($"[STAT] FETCH_PLACEHOLDERS folder={nodeKey} 累计 {n} 次" +
                            (n == 1 ? "" : "（目录填充回调重复，需检查填充完成状态）"));

            var localDir = GetLocalPath(info);
            populatedDir = localDir;
            var cache = Cache ?? throw new InvalidOperationException("元数据缓存尚未初始化");
            List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items;

            if (identity.IsVirtual)
            {
                // ===== 虚拟节点（SyncRoot 根 / 磁盘树容器）=====
                // 子节点来自 disk_tree 表（启动期已全量拉取并入库），不触达育网云。
                // SyncRoot 根列举 RootContainerTypeId 的直接子节点；
                // 容器节点列举其下的子磁盘（与可能的子容器）。
                var parentId = identity.IsSyncRoot ? ContainerTypeId : identity.FolderId;
                bool memHit = TryMem(nodeKey, out items);
                List<DiskTreeNode> vnodes;
                if (memHit)
                {
                    vnodes = new List<DiskTreeNode>();   // 已准备过占位时无需重建
                }
                else
                {
                    vnodes = cache.ListChildNodes(UserKey, parentId);
                    items = vnodes.Select(nd => ((ulong)nd.Id, nd.LocalName, true, (long?)null, (DateTime?)null)).ToList();
                    SetMem(nodeKey, items);
                    if (DiagAllowed(nodeKey))
                        Log?.Invoke($"[DEBUG] 虚拟列举 node={nodeKey} 来自 disk_tree 子节点={vnodes.Count}（parentId={parentId}）");
                }
                var count = EnsurePlaceholders(nodeKey, localDir, memHit, out var built, () =>
                {
                    var phs = PlaceholderCreator.BuildFromDiskTree(vnodes, out var ids);
                    return (phs, ids);
                });
                if (built && DiagAllowed(nodeKey))
                    Log?.Invoke($"[DEBUG] 虚拟占位已准备 node={nodeKey} count={count}");
            }
            else
            {
                // ===== 真实磁盘节点：SQLite 时间戳判定后复用进程内存 → SQLite → HTTP =====
                // cacheHit=true 表示之前已列举过该目录、占位文件已存在 → 后面跳过创建
                bool cacheHit;
                if (cache.IsFolderCached(identity.DiskId, identity.FileId, FolderCache))
                {
                    cacheHit = true;
                    if (!TryMem(nodeKey, out items))
                    {
                        items = EnrichFolderTimes(cache, identity.DiskId,
                            cache.ListCachedItems(identity.DiskId, identity.FileId));
                        ProbeCachedRealSizes(identity.FileId, items);
                        items = EnrichFolderTimes(cache, identity.DiskId,
                            cache.ListCachedItems(identity.DiskId, identity.FileId));
                        SetMem(nodeKey, items);
                        if (DiagAllowed(nodeKey))
                            Log?.Invoke($"[DEBUG] 列举 folder={nodeKey} SQLITE_HIT items={items.Count} dir='{localDir}'");
                    }
                }
                else
                {
                    cacheHit = false;
                    Log?.Invoke($"[DEBUG] 列举 folder={nodeKey} MISS → HTTP（dir='{localDir}' " +
                                $"identity(disk={identity.DiskId},folder={identity.FolderId},file={identity.FileId})）");
                    // 并发节流：限制同时打出的育网云列举请求数（Explorer 连锁触发时避免 HTTP 风暴）
                    ListThrottle.Wait();
                    try
                    {
                        // 再查一次：排队期间可能已被其他线程填充（避免重复 HTTP）
                        if (cache.IsFolderCached(identity.DiskId, identity.FileId, FolderCache))
                        {
                            cacheHit = true;
                            if (!TryMem(nodeKey, out items))
                            {
                                items = EnrichFolderTimes(cache, identity.DiskId,
                                    cache.ListCachedItems(identity.DiskId, identity.FileId));
                                ProbeCachedRealSizes(identity.FileId, items);
                                items = EnrichFolderTimes(cache, identity.DiskId,
                                    cache.ListCachedItems(identity.DiskId, identity.FileId));
                                SetMem(nodeKey, items);
                            }
                        }
                        else
                        {
                            var listing = (Client ?? throw new InvalidOperationException("Cloud client 尚未初始化"))
                                .ListFolderWithPermissionsAsync(identity.DiskId, identity.FileId)
                                .GetAwaiter().GetResult();
                            if (!listing.IsValidPage)
                                throw new InvalidDataException("目录响应不是目标目录页面，拒绝缓存或变更占位。");
                            var remoteItems = listing.Items.ToList();
                            cache.SaveDirectoryPermission(UserKey, identity.DiskId, identity.FileId,
                                listing.Permission);
                            cache.SaveFolder(identity.DiskId, identity.FileId, identity.FolderId,
                                Path.GetFileName(localDir), localDir, remoteItems);
                            foreach (var directory in remoteItems.Where(item => item.IsFolder))
                                cache.SaveSubFolder(identity.DiskId, directory.Id, identity.FileId, directory.Name,
                                    System.IO.Path.Combine(localDir, directory.Name));
                            // 列表页大小向下取整到整 MB；创建占位前通过直链 HEAD 取得精确字节数，
                            // 避免在 FETCH_DATA 期间修改正在水化的文件。
                            //   PicPlayer 页 → sketch hash → upload_dir/file/<yyyyMM>/<hash>.<ext>
                            //   直链 → HEAD Content-Length
                            // 探测结果写 real_size + direct_url + probed_upload_time；
                            // 之后列举时 upload_time 不变 → 不再重复探测。
                            // 探测失败仍降级用声明值+余量，不阻塞列举。
                            ProbeRealSizes(identity.FileId, remoteItems);

                            items = EnrichFolderTimes(cache, identity.DiskId, cache.ListCachedItems(identity.DiskId, identity.FileId));
                            SetMem(nodeKey, items);
                        }
                    }
                    finally
                    {
                        ListThrottle.Release();
                    }
                }

                // Memory cache can predate enumeration of a child folder. Refresh folder
                // timestamps from SQLite before deriving this directory's timestamp.
                items = EnrichFolderTimes(cache, identity.DiskId, items);
                ReconcileExistingPlaceholderSizes(localDir, items);

                var count = EnsurePlaceholders(nodeKey, localDir, cacheHit, out var built, () =>
                {
                    var permission = cache.GetDirectoryPermission(UserKey, identity.DiskId, identity.FileId);
                    var phs = PlaceholderCreator.Build(identity.DiskId, identity.FileId, items, out var ids,
                        readOnly: permission != DirectoryPermission.FullAccess);
                    return (phs, ids);
                });
                if (built && DiagAllowed(nodeKey))
                    Log?.Invoke($"[DEBUG] 占位已准备 folder={nodeKey} count={count}");
            }

            // === 目录占位修改时间推导（2026-10-05）===
            // 育网云列表页对目录只给"创建者+名称"、无任何时间（实证 folderid=0 列表全页 0 个日期），
            // PlaceholderCreator.Build 建目录占位时 uploadTime 为 null → 兜底当前时刻
            // → Explorer 修改日期显示"程序运行时间"，观感异常。
            // 修正语义：目录修改时间 = 直接子文件的最大上传时间；并沿祖先链上浮，
            // 让纯子目录的父目录（磁盘根、三组容器）也能显示"后代最新文件时间"。
            // 只增不减；经后台队列执行（回调线程内直接改时间戳有互等风险，同改占位大小的死锁教训）。
            // 虚拟节点（容器/磁盘）的 items 全为目录、无 uploadTime → 自然跳过。
            DateTime? derivedTime = null;
            foreach (var it in items)
                if (it.uploadTime is { } ut && (derivedTime == null || ut > derivedTime))
                    derivedTime = ut;
            if (derivedTime == null)
            {
                // 无直接子文件（纯子目录列表 / 磁盘根 / 容器）时的兜底推导：
                // 磁盘根 → 全盘已入库文件的最大上传时间；容器 → 其下各磁盘的最大上传时间
                if (!identity.IsVirtual && identity.FolderId == 0)
                    derivedTime = cache.GetDiskMaxUploadTime(identity.DiskId);
                else if (identity.IsVirtual && !identity.IsSyncRoot)
                    derivedTime = GetVirtualNodeMaxUploadTime(cache, identity.FolderId);
            }
            if (derivedTime is { } dt)
            {
                var ft = dt.ToFileTimeUtc();   // 与 PlaceholderCreator.Build 相同的时间语义（本地时刻 → FILETIME）
                // self + 祖先链均用 raise-only（force=false）：
                // 浏览期 derivedTime 是"直接子文件最大上传时间"（≤ 对账用的"全后代最大"），
                // 用 raise-only 只增不减——若对账已设更大的全后代最大值，浏览不覆盖（current≥target 跳过）；
                // 若预拉取未完成/对账未跑，浏览 raise 把哨兵(1601)/污染值抬升到直接子文件最大（比哨兵好）。
                // 对账（PreFetchAllDisksAsync 完成后）用 Force 设全后代最大值（最准），覆盖浏览的小值。
                PlaceholderFixer.EnqueueDirTime(localDir, ft);
                // 祖先链上浮：逐级向上直到 SyncRoot 根（不含根本身）
                var rootPath = Path.GetFullPath(SyncRootPath).TrimEnd('\\');
                for (var anc = Directory.GetParent(localDir); anc != null; anc = anc.Parent)
                {
                    if (string.Equals(anc.FullName.TrimEnd('\\'), rootPath, StringComparison.OrdinalIgnoreCase))
                        break;
                    PlaceholderFixer.EnqueueDirTime(anc.FullName, ft);
                }
            }

            // FETCH_PLACEHOLDERS 允许填充 Pattern 以外的条目。上面已通过
            // CfCreatePlaceholders 补齐整个目录，筛选请求也按完整填充完成。
            totalCount = checked((uint)items.Count);
        }
        catch (Exception ex)
        {
            completionStatus = NTStatus.STATUS_UNSUCCESSFUL;
            // 输出完整堆栈：只打 Message 无法定位（SQLite 并发 NRE 就是靠堆栈确认的）
            Log?.Invoke($"FETCH_PLACEHOLDERS 失败: {ex}");
        }

        // 占位已由 CfCreatePlaceholders 创建，不能再回传同一数组制造重复创建。
        // 这里是零剩余条目的完成回执，TotalCount 也必须是 0；填实际目录条目数会让
        // CFAPI 继续等待尚未通过 TRANSFER_PLACEHOLDERS 交付的条目。
        // 在同一个回执里关闭按需填充，包含空目录和筛选查询，避免后台关闭的时序窗口。
        var hr = Execute(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
            {
                Flags = completionStatus == NTStatus.STATUS_SUCCESS && DisablePopulationAfterPopulate
                    ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION
                    : CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                CompletionStatus = completionStatus,
                PlaceholderTotalCount = 0,
                PlaceholderArray = IntPtr.Zero,
                PlaceholderCount = 0,
                EntriesProcessed = 0
            });

        if (!hr.Failed && completionStatus == NTStatus.STATUS_SUCCESS)
        {
            var nodeKey = new NodeKey(identity);
            var firstTime = PopulatedFolders.TryAdd(nodeKey, (int)totalCount);
            PopulatedFolders[nodeKey] = (int)totalCount;
            if (firstTime)
                Log?.Invoke($"[POP] 已填充 folder={identity.FileId} 共 {totalCount} 项，关闭按需填充={DisablePopulationAfterPopulate} （dir='{populatedDir}'）");
        }
    }

    /// <summary>
    /// 计算虚拟容器节点下已入库磁盘或子容器的最大上传时间。
    /// disk_tree 本身没有文件时间，实际值来自对应磁盘的 files 缓存。
    /// </summary>
    private static DateTime? GetVirtualNodeMaxUploadTime(SqliteCache cache, ulong parentId)
    {
        DateTime? best = null;
        foreach (var node in cache.ListChildNodes(UserKey, parentId))
        {
            var time = node.IsDisk
                ? cache.GetDiskMaxUploadTime(node.Id)
                : GetVirtualNodeMaxUploadTime(cache, node.Id);
            if (time is { } value && (best == null || value > best))
                best = value;
        }
        return best;
    }

    /// <summary>已补齐占位的目录快照；仅保存条目数，不跨回调持有非托管数组。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeKey, int> PreparedListings = new();
    private static readonly object _populationSync = new();

    /// <summary>
    /// 为某目录的完整清单补齐占位。
    /// 以 NodeKey 为键，彻底消除"多虚拟/真实节点 FileId 同为 0"的串键冲突。
    /// 同一快照只补建一次；需要重建时调用 onMiss，创建器会跳过已存在项。
    /// </summary>
    private static int EnsurePlaceholders(
        NodeKey nodeKey, string localDir, bool cacheHit, out bool built,
        Func<(CF_PLACEHOLDER_CREATE_INFO[] phs, List<IntPtr> ids)> onMiss)
    {
        built = false;
        if (cacheHit && PreparedListings.TryGetValue(nodeKey, out var hit)) return hit;

        lock (_populationSync)
        {
            // 双重检查：排队期间可能已被别的线程建好
            if (cacheHit && PreparedListings.TryGetValue(nodeKey, out var hit2)) return hit2;

            var (phs, ids) = onMiss();
            try
            {
                PlaceholderCreator.CreatePlaceholders(localDir, phs);
                PreparedListings[nodeKey] = phs.Length;
                built = true;
                return phs.Length;
            }
            finally
            {
                foreach (var id in ids) Marshal.FreeHGlobal(id);
            }
        }
    }

    // ================= 关于"读块缓存（read-ahead）"：已实测并否决 =================
    //
    // 背景：同一个 15.9MB PPTX，PowerPoint 双击时 cfapi **一次就要全量**（5.2s）；
    // 而 cp/Explorer 复制时 cfapi 打出 233 个 4096/126976 交错的小分片 → 6.755s。
    // 曾尝试"小分片对齐 512KB 整块下载 + 块内命中内存"，把回源次数 233 降到 ~31。
    //
    // 实测三版（同一文件、同一冷启动流程）：
    //   无块缓存（逐片精确请求）        6.755s
    //   块缓存（未做单飞，并发重复下载）  8.375s   ← 更慢：cfapi 回调高并发，同一块被
    //                                               N 个线程各下载一遍，下载量远超文件
    //   块缓存（Lazy 单飞修复后）        6.515s   ← 仅快 0.24s（3.5%），噪声级别
    //
    // 结论：总下载字节数没变，瓶颈始终是服务器 ~3.5MB/s 带宽；局域网 RTT 极低，
    // 减少请求数收益本就有限。且对 PowerPoint 双击（一次要全量 15927636 ≥ 块大小，
    // 根本不触发块缓存）毫无帮助。为 3.5% 付 32MB 内存 + 单飞 + 淘汰逻辑不划算 → 否决。
    //
    // 若将来走公网（RTT 高）或服务器解除限速，可重新评估；届时务必先做单飞，
    // 否则会退化成上面 8.375s 那版。

    /// <summary>
    /// 取一个文件分片，返回 (data, total, viaDirect)。
    ///
    /// **主路径 = 直链 Range 分片**（2026-09-29 迁移）：
    ///   upload_dir/file/&lt;yyyyMM&gt;/&lt;hash&gt;.ext 是静态资源，实测 Accept-Ranges: bytes，
    ///   本次回调要哪一段就只下哪一段 —— 打开 95MB PPTX 只读前几段时不必先整文件落
    ///   D:\YuWangCache 再切分（旧路径 42MB 复制实测 16.4s 即源于此）。
    ///     直链来自探测时入库的 direct_url；若还没探测过，这里顺手探一次（并落库供后续回调复用）。
    ///
    /// **自愈（2026-09-30 修复 "占位存在但 DB 无该文件行 → 双击打开失败"）**：
    ///   占位跨运行保留，而元数据 SQLite 是可清的缓存。DB 全新 / 懒枚举未覆盖某文件夹时，
    ///   GetFileDownloadInfo 对旧占位返回 (null,null) —— 连文件名都没有，re-probe 分支都进不去，
    ///   旧代码直接 throw → 文件永久打不开。现改为：文件名缺失时先查 SQLite 文件夹缓存、
    ///   不行再回源列举父目录按 fileId 找回文件名并落库，拿到名后照常探测直链。
    ///   这是与枚举期同源的"正确机制"的按需调用，并非回退到已移除的整文件下载路径。
    /// </summary>
    private static (byte[] data, long total, bool viaDirect) FetchRange(
        FileIdentity identity, long offset, long length, Action<long, long>? reportProgress = null)
    {
        var client = Client ?? throw new InvalidOperationException("Cloud client 尚未初始化");
        var cache = Cache;

        // 1) 缓存优先：直链 + 文件名
        var (url, name) = cache?.GetFileDownloadInfo(identity.FileId) ?? (null, null);

        // 2) 文件名缺失（文件元数据不在 DB）→ 自愈：回源补齐元数据后再探测直链
        if (string.IsNullOrEmpty(name))
        {
            name = ResolveMissingName(identity);
            if (!string.IsNullOrEmpty(name))
                url = TryProbe(client, cache, identity.FileId, name);
        }

        // 3) 有文件名但还没有直链（枚举期探测失败被降级，或自愈刚拿到名）→ 现探一次
        if (url == null && !string.IsNullOrEmpty(name))
            url = TryProbe(client, cache, identity.FileId, name);

        if (url != null)
        {
            try
            {
                var r = client.DownloadRangeDirectAsync(url, offset, length, reportProgress).GetAwaiter().GetResult();
                if (r.HasValue) return (r.Value.Data, r.Value.Total, true);
                Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 直链分片返回 null（忽略 Range 后的整文件切片仍为空），无兜底下载路径");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[FETCH_DATA] file={identity.FileId} offset={offset} 请求={length} " +
                            $"直链分片异常({ex.GetType().Name}): {ex.Message}");
                throw new IOException("直链 Range 下载失败，无法完成水化。", ex);
            }
        }

        throw new IOException(
            $"file={identity.FileId} 无法取得直链（文件名={(string.IsNullOrEmpty(name) ? "缺失且回源失败" : name)}，" +
            "探测失败或请求失败），水化中止；该文件在 Explorer 中会显示打开失败");
    }

    /// <summary>
    /// 探测直链；成功则落库并返回直链，任何失败返回 null（不抛，交由上层决定兜底）。
    /// </summary>
    private static string? TryProbe(Net.YuWangClient client, Storage.SqliteCache? cache, ulong fileId, string name)
    {
        try
        {
            var probe = client.ProbeRealSizeAsync(fileId, name).GetAwaiter().GetResult();
            if (probe.HasValue)
            {
                cache?.SaveDirectInfo(fileId, probe.Value.Size, probe.Value.DirectUrl, "");
                return probe.Value.DirectUrl;
            }
            Log?.Invoke($"[FETCH_DATA] file={fileId} 直链探测返回 null（页面无 hash / 直链 404 / 无 Content-Length）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[FETCH_DATA] file={fileId} 直链探测失败({ex.Message})");
        }
        return null;
    }

    /// <summary>
    /// 文件名缺失时的自愈：先从 SQLite 文件夹缓存里按 fileId 找（文件夹行在、只是 files 行丢了），
    /// 找不到再回源列举父目录，命中后把该文件元数据落库（仅 files 行）并返回文件名。
    /// 修复根因：DB 与磁盘占位可能因跨运行重置而发散，FETCH_DATA 不应因 DB 缺行而无条件失败。
    /// </summary>
    private static string? ResolveMissingName(FileIdentity identity)
    {
        var cache = Cache;
        var client = Client;
        if (cache == null || client == null) return null;

        // 2a) SQLite 里文件夹行在、只是 files 行丢了 → 直接命中
        try
        {
            var cached = cache.ListCachedItems(identity.DiskId, identity.FolderId);
            foreach (var it in cached)
                if (!it.isFolder && it.id == identity.FileId && !string.IsNullOrEmpty(it.name))
                    return it.name;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 查 SQLite 文件夹缓存失败（忽略）: {ex.Message}");
        }

        // 2b) 回源列举父目录（与 OnFetchPlaceholders 同款阻塞调用，等价懒枚举）
        try
        {
            Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 元数据缺失，回源列举父目录 folder={identity.FolderId} 自愈");
            var listing = client.ListFolderWithPermissionsAsync(identity.DiskId, identity.FolderId).GetAwaiter().GetResult();
            if (!listing.IsValidPage) throw new InvalidDataException("回源目录页无效。");
            var remote = listing.Items;
            cache.SaveDirectoryPermission(UserKey, identity.DiskId, identity.FolderId, listing.Permission);
            // 先确保父目录行存在（files.folder_id 外键目标），否则 SaveFileMetadata 触发
            // "FOREIGN KEY constraint failed"
            cache.EnsureFolderRow(identity.DiskId, identity.FolderId);
            // 一次性把整个文件夹的文件元数据都落库：避免同文件夹多个旧占位各触发一次回源列举
            string? found = null;
            foreach (var it in remote)
            {
                if (it.IsFolder) continue;
                cache.SaveFileMetadata(identity.DiskId, identity.FolderId, it.Id, it.Name,
                    it.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss"), it.Size);
                if (it.Id == identity.FileId) found = it.Name;
            }
            if (!string.IsNullOrEmpty(found))
            {
                Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 自愈成功，已落库元数据 name={found}");
                return found;
            }
            Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 父目录列举未含该文件（可能已删除/移动）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[FETCH_DATA] file={identity.FileId} 回源列举父目录失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 并发探测一批文件的精确大小（并发 4，避免拖垮列举耗时）。
    /// 每文件 2 个轻量请求（PicPlayer 页 + HEAD 直链），局域网约 100ms；
    /// 已探测过且 upload_time 未变的文件直接跳过（NeedsSizeProbe 判断）。
    /// 任何失败静默降级——该文件继续用声明值+余量，不阻塞整个目录的列举。
    /// </summary>
    private static void ProbeRealSizes(ulong folderId, List<YuWangCloudSync.Net.YuWangItem> items)
    {
        var cache = Cache;
        var client = Client;
        if (cache == null || client == null) return;

        var files = items.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0) return;

        var probed = 0;
        var skipped = 0;
        var failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.ForEach(files,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
            () => (probed: 0, skipped: 0, failed: 0),
            (item, _, local) =>
            {
                try
                {
                    var ut = item.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                    if (!cache.NeedsSizeProbe(item.Id, ut))
                    {
                        local.skipped++;
                        return local;
                    }
                    var r = client.ProbeRealSizeAsync(item.Id, item.Name).GetAwaiter().GetResult();
                    if (r.HasValue)
                    {
                        cache.SaveDirectInfo(item.Id, r.Value.Size, r.Value.DirectUrl, ut);
                        local.probed++;
                    }
                    else
                    {
                        local.failed++;
                    }
                }
                catch
                {
                    local.failed++;
                }
                return local;
            },
            local =>
            {
                System.Threading.Interlocked.Add(ref probed, local.probed);
                System.Threading.Interlocked.Add(ref skipped, local.skipped);
                System.Threading.Interlocked.Add(ref failed, local.failed);
            });

        if (probed > 0 || failed > 0)
            Log?.Invoke($"[PROBE] folder={folderId} 大小探测完成：成功 {probed} / 跳过(已探测且未变) {skipped} / " +
                        $"失败 {failed}（失败者降级用声明值+余量），用时 {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static void ProbeCachedRealSizes(ulong folderId,
        List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)
    {
        var probeItems = items.Where(item => !item.isFolder)
            .Select(item => new YuWangCloudSync.Net.YuWangItem
            {
                Type = 'F',
                Id = item.id,
                Name = item.name,
                Size = item.size,
                UploadTime = item.uploadTime
            })
            .ToList();
        ProbeRealSizes(folderId, probeItems);
    }

    private static void ReconcileExistingPlaceholderSizes(string localDir,
        List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items)
    {
        foreach (var item in items)
        {
            if (item.isFolder || item.size is not > 0) continue;
            var path = Path.Combine(localDir, item.name);
            try
            {
                if (!File.Exists(path) || !ProviderIdentity.OwnsPlaceholder(path)
                    || !Callbacks.IsHydrationQuiet(item.id, quietMs: 200))
                    continue;
                if (!PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)
                    || state.OnDiskDataSize != 0)
                    continue;
                if (new FileInfo(path).Length == item.size.Value) continue;
                if (PlaceholderUpdater.TryUpdateFileSize(path, item.size.Value, Log))
                    Log?.Invoke($"[SIZE] 列举前已按直链探测结果校准占位大小 file={item.id} size={item.size.Value}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[SIZE] 列举前校准占位大小失败 file={item.id}: {ex.Message}");
            }
        }
    }

    /// <summary>退出时打印回调统计，用于量化 Explorer 的重复枚举程度</summary>
    public static void LogCallbackStats()
    {
        if (PlaceholderCallbackCount.IsEmpty) return;
        var parts = PlaceholderCallbackCount
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"folder={kv.Key}:{kv.Value}次");
        Log?.Invoke($"[STAT] FETCH_PLACEHOLDERS 总计 {PlaceholderCallbackCount.Values.Sum()} 次（{string.Join(", ", parts)}）");
    }

    /// <summary>清空占位创建快照（保留原生命周期 API 名称）。</summary>
    public static void ReleasePlaceholderArrays()
    {
        lock (_populationSync)
        {
            PreparedListings.Clear();
        }
    }

    /// <summary>
    /// 使某个父目录的占位创建快照失效，迫使下次列举用最新列表补建占位。
    ///
    /// 文件真实大小落库后调用：按 (diskId, folderFileId) 精确匹配父目录节点
    /// （folderFileId 即该父目录自身的 id，diskId 限定所属磁盘，避免多盘根 folder_id=0 串键），
    /// 新建占位读取 SQLite 中的真实大小；已有占位在 FETCH_PLACEHOLDERS 阶段、开始访问前校准。
    /// 不在 FETCH_DATA 完成后通过后台线程修改正在使用的文件。
    /// </summary>
    public static void InvalidatePlaceholderArray(ulong diskId, ulong folderFileId)
    {
        lock (_populationSync)
        {
            foreach (var k in PreparedListings.Keys.ToArray())
            {
                if (k.DiskId == diskId && k.FileId == folderFileId)
                {
                    if (PreparedListings.TryRemove(k, out _))
                    {
                        Log?.Invoke($"[POP] 父目录 disk={diskId} folder={folderFileId} 的占位创建快照已失效");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 目录清单在 SQLite 外部更新（例如启动预拉取）后，使进程内快照失效，
    /// 让下次 FETCH_PLACEHOLDERS 从数据库重建并向 CFAPI 回传最新条目。
    /// </summary>
    public static void InvalidateFolderListing(ulong diskId, ulong folderId)
    {
        foreach (var key in MemFolders.Keys.ToArray())
            if (key.DiskId == diskId && key.FileId == folderId)
                MemFolders.TryRemove(key, out _);

        InvalidatePlaceholderArray(diskId, folderId);
    }

    /// <summary>
    /// 目录填充完成后是否关闭其按需填充。Program 固定设为 true，避免 Explorer 自激枚举。
    /// </summary>
    public static bool DisablePopulationAfterPopulate { get; set; } = true;

    /// <summary>
    /// 目录缓存与自动刷新共用的周期（来自 appsettings 的 FolderCacheMinutes）。
    /// 过期判断统一基于 SQLite last_listed；到期后打开目录会清理进程快照并触发联网刷新。
    /// 取值由 Program 归一化到 **1~120 分钟**（无效值 ≤0 或 &gt;120 重置为 30）。
    /// </summary>
    public static TimeSpan FolderCache { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>已从 Range 响应记录真实大小的文件（避免每个 FETCH_DATA 分片重复写 SQLite）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> RealSizeRecorded = new();

    /// <summary>
    /// 解析 FETCH_DATA 目标文件的本地路径。
    /// 首选：用 FileIdentity 里的 fileId 反查 SQLite（父目录路径 + 文件名）——
    ///       因为 **FETCH_DATA 回调的 NormalizedPath 实测是空的**，
    ///       之前直接用它会退化成 SyncRootPath，把同步根目录的元数据改掉（严重 bug）。
    /// 兜底：GetLocalPath(info)，但必须校验是**已存在的文件**、且不是同步根目录。
    /// </summary>
    private static string? ResolveFilePath(FileIdentity identity, CF_CALLBACK_INFO info)
    {
        try
        {
            var fromDb = Cache?.GetFileLocalPath(identity.FileId);
            if (!string.IsNullOrEmpty(fromDb) && File.Exists(fromDb))
            {
                if (!Directory.Exists(fromDb)) return fromDb;
            }
        }
        catch { /* 落到兜底 */ }

        var fromInfo = GetLocalPath(info);
        if (string.IsNullOrEmpty(fromInfo)) return null;
        // 校验：必须是存在的文件；绝不能是目录（尤其不能是 SyncRoot 本身）
        if (Directory.Exists(fromInfo)) return null;
        if (!File.Exists(fromInfo)) return null;
        if (string.Equals(Path.GetFullPath(fromInfo).TrimEnd('\\'),
                Path.GetFullPath(SyncRootPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return null;
        return fromInfo;
    }

    private static FileIdentity ReadIdentity(CF_CALLBACK_INFO info)
    {
        if (info.FileIdentity == IntPtr.Zero || info.FileIdentityLength < 29)
            throw new InvalidDataException("Cloud Files 回调未提供有效的 FileIdentity");
        var bytes = new byte[info.FileIdentityLength];
        Marshal.Copy(info.FileIdentity, bytes, 0, bytes.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != FileIdentity.MagicExpected)
            throw new InvalidDataException("FileIdentity 魔数无效");
        var identity = FileIdentity.Deserialize(bytes, bytes.Length);
        if (!identity.IsValid) throw new InvalidDataException("FileIdentity 魔数无效");
        return identity;
    }

    /// <summary>
    /// 回调 info → 本地完整路径。
    /// FETCH_PLACEHOLDERS 时 NormalizedPath 指向**目录**，
    /// FETCH_DATA 时指向**文件**，两者换算方式相同。
    /// </summary>
    private static string GetLocalPath(CF_CALLBACK_INFO info)
    {
        // Vanara 文档：NormalizedPath 是"从卷根开始的绝对路径"，
        // 例如 "\Users\<user>\YuWangCloud\历史"
        // VolumeDosName 是 "X:" 形式（如 "C:"）
        // 直接拼即可：C: + \Users\<user>\... = C:\Users\<user>\...
        var normalizedPath = info.NormalizedPath ?? string.Empty;
        var dosName = info.VolumeDosName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dosName) && normalizedPath.StartsWith('\\'))
            return dosName.TrimEnd('\\') + normalizedPath;

        // 兜底：若 NormalizedPath 已经是完全限定 DOS/NT 路径，直接用
        if (Path.IsPathFullyQualified(normalizedPath)) return normalizedPath;

        // 最后兜底：拼 SyncRootPath（极少走到这里）
        return Path.GetFullPath(Path.Combine(SyncRootPath,
            normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    private static HRESULT Execute<T>(in CF_CALLBACK_INFO callbackInfo, CF_OPERATION_TYPE type, T parameters)
        where T : struct
    {
        var operationInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = type,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            CorrelationVector = callbackInfo.CorrelationVector,
            RequestKey = callbackInfo.RequestKey
        };
        var operationParameters = CF_OPERATION_PARAMETERS.Create(parameters);
        var result = CfExecute(in operationInfo, ref operationParameters);
        if (result.Failed)
            Log?.Invoke($"CfExecute({type}) 失败: {result}");
        return result;
    }

    private static void ReportProviderProgress(CF_CONNECTION_KEY connectionKey, CF_TRANSFER_KEY transferKey,
        long total, long completed)
    {
        if (total <= 0) return;
        try
        {
            var result = CfReportProviderProgress(connectionKey, transferKey, total, Math.Clamp(completed, 0, total));
            if (result.Failed)
                Log?.Invoke($"CfReportProviderProgress 失败: {result}");
        }
        catch (Exception ex)
        {
            // 进度提示不应让本身成功的文件水化失败。
            Log?.Invoke($"CfReportProviderProgress 异常（忽略）: {ex.Message}");
        }
    }
}
