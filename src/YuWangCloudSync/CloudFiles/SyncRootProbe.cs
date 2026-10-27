using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using YuWangCloudSync.Core;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// 挂载位置探测结果（一次 <see cref="SyncRootProbe"/> 的产物）。
///
/// 分两类：
/// - <see cref="Failures"/>：硬失败，该位置**不能**作为 SyncRoot（占位依赖重解析点，缺了就一定挂不上）；
/// - <see cref="Warnings"/>：能用但有隐患（空间紧张 / 可移动盘 / 已注册为本程序等），只提示不拦截。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SyncRootProbeResult
{
    public string Path { get; init; } = "";

    /// <summary>硬失败原因（非空即不可用）。</summary>
    public List<string> Failures { get; } = new();

    /// <summary>可用但有隐患的提示。</summary>
    public List<string> Warnings { get; } = new();

    public bool Ok => Failures.Count == 0;

    public string? FileSystem { get; init; }
    public bool SupportsReparsePoints { get; init; }
    public bool WriteDeleteOk { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>命中的已注册 SyncRoot 路径（null = 不在任何 SyncRoot 内）。</summary>
    public string? SyncRootHitPath { get; init; }

    /// <summary><see cref="SyncRootHitPath"/> 处注册的 Provider 名。</summary>
    public string? SyncRootHitProvider { get; init; }

    /// <summary>探测路径本身就已注册（hit == 自身），且 Provider 是本程序 → 切换时会先反注册，不算冲突。</summary>
    public bool IsSelfRegistration { get; init; }

    /// <summary>一行摘要：<see cref="Ok"/> 时描述能力，否则列出失败原因。</summary>
    public string Describe() => Describe(false);

    public string Describe(bool withWarnings)
    {
        if (!Ok) return string.Join("；", Failures);

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(FileSystem)) parts.Add(FileSystem);
        if (SupportsReparsePoints) parts.Add("支持重解析点");
        if (WriteDeleteOk) parts.Add("可写可删");
        if (FreeBytes > 0) parts.Add("剩余 " + FormatBytes(FreeBytes));

        var s = parts.Count == 0 ? "可用" : string.Join(" / ", parts);
        if (withWarnings && Warnings.Count > 0) s += "；注意：" + string.Join("；", Warnings);
        return s;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "未知";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.##} {units[i]}";
    }
}

/// <summary>
/// 挂载位置（SyncRoot）**切换前**的轻量探测。
///
/// 为什么要有这个东西：热切换 <c>Program.SwitchSyncRootAsync</c> 是"先拆旧、再挂新" ——
/// 停 Fixer → 清空旧占位 → CfDisconnectSyncRoot → 反注册旧路径 → 才去注册新路径。
/// 一旦新路径根本挂不上（FAT32/exFAT 卷、无写权限、嵌在别的 SyncRoot 里…），
/// 旧挂载点已经被拆掉，用户会掉进"两边都没有"的窗口期，只能靠回滚兜底（回滚本身也可能失败）。
/// 探测把这类失败**提前到拆旧之前**：不通过就压根不动旧根。
///
/// 刻意保持"轻量"：**不写注册表、不做试注册**（CfRegisterSyncRoot 试挂会在系统里留下注册项，
/// 万一探测后用户取消、或该路径是别人的 Provider，反注册反而会破坏别人的同步根）。
/// 唯一的副作用是在目标目录里建一个 1 字节临时文件再删掉（证明"能建也能删"——
/// 后续创建/清理占位走的正是这两条能力）。顺序上**先做冲突检测再做写删自测**，
/// 避免往当前同步根里写临时文件触发 cldflt。
///
/// 探测项与判定：
///   1. 路径合法性：绝对路径、非 UNC；盘符根目录本身不能直接挂（见下条的"跟着既有设定"）；
///   2. 卷能力：文件系统名 + FILE_SUPPORTS_REPARSE_POINTS（占位是重解析点，没有它 cfapi 一定失败）；
///   3. 冲突：自下而上查 CfGetSyncRootInfoByPath —— 嵌在别人的 SyncRoot 内 / 已是别人的 Provider → 硬失败；
///      已是本程序自己的注册 → 警告（切换流程会先反注册，属正常）；
///   4. 敏感目录：按**环境变量**匹配的高危去向（见 <see cref="SensitiveRoots"/>），**一律硬失败**
///      （不看是否为空）；
///   5. 目录：不存在则创建；
///   6. 写删自测：建临时文件 → 写入 → 删除（删不掉的话以后清理占位会卡住）；
///   7. 空间 & 驱动器类型：剩余 &lt; 1GB、可移动盘 → 警告。
///
/// 盘符根目录的处理（与设置窗口保持同一口径）：
///   挂上去的一定不能是盘符根本身（<c>Program.CleanupSyncRootContents</c> 也有同款守卫），
///   所以**探测**层面仍判不可用；但设置窗口不再一票否决，而是把它当普通非空目录处理 ——
///   走既有的"目录非空 → 询问是否在其下新建子目录 YuWang"流程，于是 D:\ → D:\YuWang。
///   命令行 <c>--probe=D:\</c> 没有交互可问，报不可用正是预期。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SyncRootProbe
{
    /// <summary>FILE_SUPPORTS_REPARSE_POINTS（GetVolumeInformation 的 lpFileSystemFlags）。</summary>
    private const uint FileSupportsReparsePoints = 0x00000080;

    private const long WarnFreeBytesThreshold = 1024L * 1024 * 1024;   // 1 GB

    /// <summary>已知支持重解析点、可承载 cfapi 占位的文件系统。</summary>
    private static readonly string[] KnownGoodFileSystems = ["NTFS", "ReFS"];

    /// <summary>
    /// 探测 <paramref name="path"/> 能否作为 SyncRoot。
    /// </summary>
    /// <param name="path">目标路径（会被规范化）。</param>
    /// <param name="currentSyncRootPath">当前挂载点；用于识别"未改动"与"嵌在当前同步根内部"。</param>
    /// <param name="providerName">本程序的显示名；所有权由稳定的 SyncRoot 身份判定，显示名可在升级时变化。</param>
    public static SyncRootProbeResult Probe(string path, string? currentSyncRootPath = null,
        string? providerName = null)
    {
        // ---- 1. 路径合法性 ----
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            return Fail(path, $"路径不合法：{ex.Message}");
        }

        if (full.StartsWith("\\\\", StringComparison.Ordinal))
            return Fail(full, "不支持 UNC 网络路径");

        var driveRoot = Path.GetPathRoot(full) ?? "";
        if (driveRoot.Length == 0)
            return Fail(full, "必须使用绝对路径");
        if (IsDriveRoot(full))
            return Fail(full, "不能直接使用磁盘根目录（设置窗口会询问是否新建子目录）");

        // ---- 2. 卷能力（文件系统 + 是否支持重解析点）----
        var (fsName, flags, volOk) = QueryVolume(driveRoot);
        var supportsReparse = volOk && (flags & FileSupportsReparsePoints) != 0;

        var warnings = new List<string>();
        if (!volOk)
            warnings.Add("无法读取卷信息");
        else if (!supportsReparse)
            return Fail(full, $"卷 {driveRoot} 不支持重解析点");
        else if (fsName.Length > 0 && !KnownGoodFileSystems.Contains(fsName, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"文件系统为 {fsName}，支持重解析点，但非 NTFS 可能不稳定");

        var result = new SyncRootProbeResult
        {
            Path = full,
            FileSystem = fsName.Length == 0 ? null : fsName,
            SupportsReparsePoints = supportsReparse,
        };

        // ---- 3. 冲突检测（必须在写删自测之前！避免往同步根里写文件触发 cldflt）----
        var (hitPath, hitProvider) = FindContainingSyncRoot(full);
        if (hitPath != null)
        {
            result = new SyncRootProbeResult
            {
                Path = result.Path, FileSystem = result.FileSystem,
                SupportsReparsePoints = result.SupportsReparsePoints,
                SyncRootHitPath = hitPath, SyncRootHitProvider = hitProvider,
                IsSelfRegistration = string.Equals(hitPath.TrimEnd('\\'), full.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase),
            };

            // ProviderName is a localized display value and may change during an upgrade.
            // Decide ownership from the stable SyncRoot identity, with the guarded legacy fallback.
            var mine = result.IsSelfRegistration
                && ProviderIdentity.OwnsRoot(hitPath, allowLegacy: true, providerName: providerName);
            // 自己的注册：切换流程本来就会先反注册，属正常，只是提示一下
            if (mine)
            {
                warnings.Add(string.Equals(hitProvider, providerName, StringComparison.OrdinalIgnoreCase)
                    ? "本程序已注册该位置"
                    : $"本程序已注册该位置（旧 Provider 名称：{DescribeProvider(hitProvider)}）");
            }
            else if (result.IsSelfRegistration)
            {
                return Finish(result, warnings,
                    $"该位置已被其它云 Provider 占用（{DescribeProvider(hitProvider)}）");
            }
            else if (currentSyncRootPath != null
                && string.Equals(hitPath.TrimEnd('\\'), Path.GetFullPath(currentSyncRootPath).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Finish(result, warnings, "不能选择当前同步根目录内部");
            }
            else
            {
                return Finish(result, warnings,
                    $"该位置位于另一个已有同步根目录内：{hitPath}"
                    + $"（Provider：{DescribeProvider(hitProvider)}）");
            }
        }

        // ---- 4. 目录存在性 ----
        if (!Directory.Exists(full))
        {
            try { Directory.CreateDirectory(full); }
            catch (Exception ex)
            {
                return Finish(result, warnings, $"无法创建目录：{ex.Message}");
            }
        }

        // ---- 4b. 敏感目录（按环境变量匹配）----
        // 一律拒绝，**不看目录是否为空**：这些位置是系统/其它程序的私有数据区，
        // 挂上去后一旦触发清理（切换、退出清理都会清 SyncRoot 内容）就是不可逆的破坏，
        // 判"空就放行"只是把风险延后 —— 目录随时会被别的程序填满。
        // 敏感环境变量的路径及其所有子目录都禁止挂载。
        if (InSensitiveRoot(full))
            return Finish(result, warnings, DescribeSensitiveRoot(full));

        // ---- 5. 写删自测 ----
        // 与当前挂载点相同就跳过：它现在正跑着，本来就能建能删，没必要再写一次
        var isCurrent = currentSyncRootPath != null
            && string.Equals(Path.GetFullPath(currentSyncRootPath).TrimEnd('\\'), full.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        bool writeDeleteOk;
        if (isCurrent)
        {
            writeDeleteOk = true;
        }
        else
        {
            var tmp = Path.Combine(full, $".ywprobe_{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tmp, [0x59, 0x57]);   // "YW"
                if (!File.Exists(tmp))
                    return Finish(result, warnings, "写入测试失败：临时文件丢失");
            }
            catch (Exception ex)
            {
                // Double backslashes in display text so Markdown renders literal path separators.
                var displayMessage = ex.Message.Replace(@"\", @"\\");
                return Finish(result, warnings, $"目录不可写入：{displayMessage}");
            }
            try
            {
                File.Delete(tmp);
                writeDeleteOk = !File.Exists(tmp);
                if (!writeDeleteOk)
                    return Finish(result, warnings, "目录内文件不可删除");
            }
            catch (Exception ex)
            {
                return Finish(result, warnings, $"目录内文件不可删除：{ex.Message}");
            }
        }

        result = Clone(result, writeDeleteOk: writeDeleteOk);

        // ---- 6. 空间与驱动器类型 ----
        long free = -1;
        try
        {
            var di = new DriveInfo(driveRoot);
            free = di.AvailableFreeSpace;
            if (di.DriveType is DriveType.Removable or DriveType.Network or DriveType.CDRom)
                warnings.Add($"驱动器类型为 {di.DriveType}，建议选择固定磁盘");
        }
        catch (Exception ex)
        {
            warnings.Add($"无法读取驱动器信息：{ex.Message}");
        }
        if (free >= 0 && free < WarnFreeBytesThreshold)
            warnings.Add($"剩余空间仅 {SyncRootProbeResult.FormatBytes(free)}，可能不足以缓存文件");

        result = Clone(result, writeDeleteOk: writeDeleteOk, freeBytes: free);
        return Finish(result, warnings);
    }

    // ================== 敏感目录（按环境变量匹配）==================

    /// <summary>
    /// 挂载目录的高危去向清单（按**环境变量**匹配，避免硬编码具体盘符/用户名）。
    /// </summary>
    public static IEnumerable<(string Name, string Path)> SensitiveRoots()
    {
        // TEMP/TMP 也保留：部分环境的临时目录不在 LOCALAPPDATA 下。
        string[] names =
        [
            "APPDATA", "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
            "CommonProgramFiles", "CommonProgramFiles(x86)", "PROGRAMDATA", "PUBLIC",
            "SYSTEMROOT", "WINDIR", "TEMP", "TMP",
        ];
        foreach (var n in names)
        {
            var v = Environment.GetEnvironmentVariable(n);
            if (string.IsNullOrWhiteSpace(v)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(v);
            }
            catch { continue; }   // 异常取值（如残留的非法路径）直接跳过；yield 不能放在带 catch 的 try 里
            yield return (n, full);
        }
    }

    /// <summary>目标路径是否落在敏感目录（含其任意层级子目录）内。</summary>
    public static bool InSensitiveRoot(string fullPath) => FindSensitiveRoot(fullPath) != null;

    /// <summary>生成敏感目录错误，指出命中的环境变量和对应绝对路径。</summary>
    public static string DescribeSensitiveRoot(string fullPath)
    {
        var match = FindSensitiveRoot(fullPath);
        if (match == null)
            return $"禁止使用系统敏感目录：\n{fullPath}";
        return $"禁止使用系统敏感目录：\n{fullPath}\n"
            + $"（位于 {match.Value.Path} 下）";
    }

    private static (string Name, string Path)? FindSensitiveRoot(string fullPath)
    {
        foreach (var (name, root) in SensitiveRoots())
        {
            var r = root.TrimEnd('\\');
            var rPath = r.Length == 0 ? root : r + "\\";
            if (string.Equals(fullPath.TrimEnd('\\'), r, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(rPath, StringComparison.OrdinalIgnoreCase))
                return (name, root);
        }
        return null;
    }

    /// <summary>
    /// 是否是盘符根目录（如 <c>D:\</c>）。
    /// 它不能直接挂（探测判不可用），但设置窗口会走"非空目录"流程在其下新建 YuWang 子目录，
    /// 所以界面需要一个独立判断来给出正确提示，而不是直接显示"不可用"。
    /// </summary>
    public static bool IsDriveRoot(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        var root = Path.GetPathRoot(full) ?? "";
        return root.Length > 0
            && string.Equals(root.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目录里是否有任何条目；枚举不了就当"非空"（后面的可访问性检查会给更准确的错误）。</summary>
    public static bool HasAnyEntry(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return true; }
    }

    // ================== 内部 ==================

    private static SyncRootProbeResult Fail(string path, string reason) => new()
    {
        Path = path,
        Failures = { reason },
    };

    /// <summary>把 result / warnings 合成最终结果（result 是不可变风格，靠 Clone 携带新字段）。</summary>
    private static SyncRootProbeResult Finish(SyncRootProbeResult result,
        List<string> warnings, string? failure = null)
    {
        var r = Clone(result);
        foreach (var w in warnings) r.Warnings.Add(w);
        if (failure != null) r.Failures.Add(failure);
        return r;
    }

    private static SyncRootProbeResult Clone(SyncRootProbeResult src,
        bool? writeDeleteOk = null, long? freeBytes = null) => new()
    {
        Path = src.Path,
        FileSystem = src.FileSystem,
        SupportsReparsePoints = src.SupportsReparsePoints,
        WriteDeleteOk = writeDeleteOk ?? src.WriteDeleteOk,
        FreeBytes = freeBytes ?? src.FreeBytes,
        SyncRootHitPath = src.SyncRootHitPath,
        SyncRootHitProvider = src.SyncRootHitProvider,
        IsSelfRegistration = src.IsSelfRegistration,
    };

    private static string DescribeProvider(string? p) =>
        string.IsNullOrWhiteSpace(p) ? "未知 Provider" : p;

    /// <summary>
    /// 自下而上查路径落在哪个已注册 SyncRoot 内。
    /// CfGetSyncRootInfoByPath 对"同步根本身"和"根内任意子路径"都返回成功，
    /// 所以一直往上走到首次失败为止 —— **最后一个成功的目录就是那个 SyncRoot**。
    /// </summary>
    private static (string? Path, string? Provider) FindContainingSyncRoot(string full)
    {
        string? hit = null;
        string? provider = null;
        try
        {
            for (var d = full.TrimEnd('\\'); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d) ?? "")
            {
                string? prov;
                try
                {
                    var info = CfGetSyncRootInfoByPath<CF_SYNC_ROOT_PROVIDER_INFO>(d);
                    prov = info.ProviderName;
                }
                catch
                {
                    break;   // 不在任何 SyncRoot 内了（Vanara 的泛型重载失败即抛）
                }
                hit = d;
                provider = prov;
                var root = Path.GetPathRoot(d) ?? "";
                if (string.Equals(root.TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    break;   // 已经到卷根
            }
        }
        catch
        {
            // 探测本身失败不应阻断整个流程：按"未命中"处理，后面还有注册步骤兜底
        }
        // 命中自身所在目录也算（下面按 hit == full 判定）
        return (hit, provider);
    }

    private static (string FileSystem, uint Flags, bool Ok) QueryVolume(string driveRoot)
    {
        var root = driveRoot.EndsWith('\\') ? driveRoot : driveRoot + "\\";
        var nameBuf = new StringBuilder(256);
        var fsBuf = new StringBuilder(256);
        try
        {
            var ok = GetVolumeInformation(root, nameBuf, (uint)nameBuf.Capacity,
                out _, out _, out var flags, fsBuf, (uint)fsBuf.Capacity);
            return ok ? (fsBuf.ToString(), flags, true) : ("", 0, false);
        }
        catch
        {
            return ("", 0, false);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);
}
