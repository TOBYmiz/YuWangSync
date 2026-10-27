using System.Buffers.Binary;
using System.Text;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Models;

/// <summary>A saved account. Username and password are stored as current-user DPAPI blobs.</summary>
public sealed class AccountCredential
{
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
}

/// <summary>
/// 占位文件上下文（FileIdentity）。
/// 序列化为 byte[] 存入 Cloud Files 占位的 FileIdentity 字段。
/// FETCH_DATA / FETCH_PLACEHOLDERS 回调时从这里反序列化拿 diskid/folderid/fileid。
/// </summary>
[Serializable]
public readonly struct FileIdentity
{
    public const uint MagicExpected = 0x46435759;  // 'YWCF' 小端

    public readonly uint Magic;        // 校验位，反序列化时验证
    public readonly ulong DiskId;      // 育网云 diskid（如 2540 = TF2428 个人盘）
    public readonly ulong FolderId;    // 父 folderid（如 66411 = 历史）
    public readonly ulong FileId;      // 育网云 itemid（如 497421 = 第10课.pptx）
    public readonly byte Type;         // 0=folder, 1=file

    public FileIdentity(ulong diskId, ulong folderId, ulong fileId, byte type)
    {
        Magic = MagicExpected;
        DiskId = diskId;
        FolderId = folderId;
        FileId = fileId;
        Type = type;
    }

    public bool IsValid => Magic == MagicExpected;

    /// <summary>
    /// 虚拟节点哨兵（DiskId==0）：SyncRoot 根目录与磁盘树容器节点都用它标识，
    /// 与真实磁盘节点（DiskId!=0）彻底区分，避免三容器 FileId 全为 0 时键冲突。
    /// </summary>
    public bool IsVirtual => DiskId == 0;
    /// <summary>SyncRoot 根目录自身（DiskId==0 且 FolderId==0）</summary>
    public bool IsSyncRoot => DiskId == 0 && FolderId == 0;
    /// <summary>磁盘树容器节点（DiskId==0 且 FolderId==typeid），列举它返回其下子磁盘</summary>
    public bool IsContainer => DiskId == 0 && FolderId != 0;

    /// <summary>SyncRoot 根目录哨兵身份</summary>
    public static FileIdentity SyncRoot => new(0, 0, 0, 0);
    /// <summary>磁盘树容器节点身份（FolderId=容器 typeid）</summary>
    public static FileIdentity ForContainer(ulong typeid) => new(0, typeid, 0, 0);
    /// <summary>真实磁盘根身份（DiskId=磁盘 diskid，FolderId=0）</summary>
    public static FileIdentity ForDisk(ulong diskId) => new(diskId, 0, 0, 0);

    public byte[] Serialize()
    {
        // 固定布局：4 + 8 + 8 + 8 + 1 = 29 字节
        var buf = new byte[29];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(4, 8), DiskId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(12, 8), FolderId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(20, 8), FileId);
        buf[28] = Type;
        return buf;
    }

    public static FileIdentity Deserialize(byte[] data, int length)
    {
        if (length < 29) return default;
        var id = new FileIdentity(
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(20, 8)),
            data[28]);
        return id;
    }
}

/// <summary>
/// 占位大小策略。
///
/// 育网云列表页的"文件大小"是**向下取整到整 MB** 的（实测 23184192 → "22.00 MB"、
/// 9745278 → "9.00 MB"、76673354 → "73.00 MB"）。直接用它当占位的 FileSize，
/// cfapi 只会索取到声明长度为止 → 文件尾部被截断 → PPT 等格式损坏。
///
/// 误差实测（声明 vs 真实）：
///   22.00 MB → 23184192（22.109 MB，差 0.11 MB / 0.5%）
///   10.00 MB → 11063945（10.55 MB，差 0.55 MB / 5.2%）
///   97.00 MB → 111731049（106.55 MB，差 9.55 MB / **9.9%**）
/// 即误差**远不止 1MB**（服务端元数据本身就可能陈旧），所以余量要按比例给。
///
/// 直链 HEAD 探测成功时，占位直接使用精确值；探测失败时以余量避免截断。
/// FETCH_DATA 得到的 Content-Range 总长度只写入缓存，供后续列举校准；
/// 不在水化回调完成后修改当前占位。
/// </summary>
public static class SizePolicy
{
    /// <summary>最小余量</summary>
    public const long MinPaddingBytes = 2L * 1024 * 1024;
    /// <summary>比例余量分母（1/4 = 25%）</summary>
    public const long PaddingDivisor = 4;

    /// <summary>给声明大小加余量后的占位大小</summary>
    public static long Pad(long declared)
    {
        if (declared <= 0) return MinPaddingBytes;
        return declared + Math.Max(MinPaddingBytes, declared / PaddingDivisor);
    }
}

/// <summary>SyncRoot 全局上下文，进程级单例</summary>
public sealed class SyncRootContext
{
    public string SyncRootPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "YuWangCloud");
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Selected profile name. Kept separately from Username for legacy config compatibility.</summary>
    public string ActiveAccount { get; set; } = "";
    /// <summary>Saved account credentials; usernames and passwords use current-user DPAPI on disk.</summary>
    public List<AccountCredential> Accounts { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string Password { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public ulong RootDiskId { get; set; } = 2540;
    public ulong RootFolderId { get; set; } = 0;
    /// <summary>
    /// 项目内的磁盘树入口映射值：0 使用服务端 frame_desk 根目录；正数使用对应容器 typeid。
    /// 动态拉取的磁盘树全部挂在此入口下，按 user_key 隔离。
    /// </summary>
    public ulong RootContainerTypeId { get; set; } = 0;
    /// <summary>多用户隔离键（默认用登录名；为空时回落到 Username）</summary>
    public string UserKey { get; set; } = "";
    public string ProviderName { get; set; } = ProductIdentity.SyncRootDefaultName;
    public string DatabasePath { get; set; } = "yunpan.db";

    /// <summary>
    /// 日志输出**目录**（相对当前用户的数据目录）。每次运行在此目录下生成独立文件
    /// YuWangCloudSync_yyyyMMdd_HHmmss.log，不再追加/覆盖同一个文件。
    /// 旧值 "YuWangCloudSync.log" 会被自动当作目录名处理（取目录部分）。
    /// </summary>
    public string LogPath { get; set; } = "logs";
    /// <summary>保留最近多少个日志文件（0 = 不清理）</summary>
    public int LogsToKeep { get; set; } = 20;
    public int SessionCheckIntervalMinutes { get; set; } = 30;
    /// <summary>登录/列目录等轻量请求的超时（秒）</summary>
    public int HttpRequestTimeoutSeconds { get; set; } = 30;
    /// <summary>单文件下载超时（分钟）。直链 Range 分片下载，大文件需要较长时间。</summary>
    public int DownloadTimeoutMinutes { get; set; } = 10;
    /// <summary>
    /// 启动期尝试反注册的**旧** SyncRoot 路径列表。
    /// 换过 SyncRootPath 之后，旧路径仍注册在系统里、会残留在 Explorer 的导航窗格，
    /// 这里在启动时尽力清掉（失败不影响主流程）。
    /// </summary>
    public string[] LegacySyncRootPaths { get; set; } = [];
    /// <summary>
    /// 目录缓存与自动刷新的统一周期（分钟）。缓存未过期时复用本地数据；到期后，重进目录会触发云端刷新，
    /// 后台预拉取也使用同一周期去重。合法区间 1~120；无效值启动时重置为 30 分钟并告警。
    /// </summary>
    public int FolderCacheMinutes { get; set; } = 30;
    /// <summary>
    /// 是否**常驻** SyncRoot 注册（退出时保留注册与占位内容，下次启动直接复用）。
    ///
    /// true（推荐）：跨会话保留占位与**已水化状态** —— 重启程序后再次打开已下载过的
    ///   文件是瞬时的（在 3.5MB/s 的服务器限速下这是体验关键）。启动时若检测到已有
    ///   注册且目录非空，直接 CfConnectSyncRoot 复用，跳过"反注册→清空→重注册→列根"。
    /// false：维持旧行为（每次退出反注册并清空 D:\YuWang，下次启动全量重建）。
    ///
    /// **前提**：常驻注册要求引擎随系统启动（开机常驻）。若注册常驻而引擎没运行，
    /// Explorer 里双击**未水化**的占位会等待 provider 回调直至 cfapi 超时（约 60s）；
    /// 已水化完成的文件不受影响（本地已有完整数据，离线也能打开）。
    /// 需要卸载时用命令行参数 --unregister（反注册并清理占位内容）。
    /// </summary>
    public bool PersistSyncRoot { get; set; } = true;

    /// <summary>是否每日自动检查 GitHub Release 更新。</summary>
    public bool AutomaticUpdateCheck { get; set; } = true;

    /// <summary>有更新时，是否在托盘图标显示角标。</summary>
    public bool TrayUpdateHintEnabled { get; set; } = true;

}
