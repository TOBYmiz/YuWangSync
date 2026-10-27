using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// 占位元数据就地修正。
///
/// 文件大小只在 FETCH_PLACEHOLDERS 阶段由直链 HEAD 获得精确值后、文件访问前校准；
/// FETCH_DATA 期间不修改当前正在水化的占位。
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlaceholderUpdater
{
    public readonly record struct PlaceholderDataState(
        long OnDiskDataSize, long ValidatedDataSize, CF_PIN_STATE PinState,
        CF_IN_SYNC_STATE InSyncState, long FileId);

    // 只申请"改元数据"所需的最小权限：
    // CfUpdatePlaceholder 要求 WRITE_DATA 或 WRITE_DAC。
    // 这里刻意**不**要 WRITE_DATA —— 对未完全水化的占位申请写数据权限会触发
    // 整文件水化（嵌套 FETCH_DATA），而我们现在就在 FETCH_DATA 里。
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint WRITE_DAC = 0x00040000;

    private const uint FILE_SHARE_ALL = 0x00000007;   // READ|WRITE|DELETE
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 剥离云文件重解析标记用：打开重解析点本身 + 设备控制删除。
    private const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// 在回调之外手动关闭某目录的按需填充，仅适用于子占位已在磁盘上的目录。
    /// FETCH_PLACEHOLDERS 的正常完成路径直接在 TRANSFER_PLACEHOLDERS 回执里设置
    /// DISABLE_ON_DEMAND_POPULATION，不依赖此方法或后台队列完成回执。
    /// 这只影响该目录自身，其子目录仍可独立按需填充。
    /// </summary>
    public static bool TryDisableOnDemandPopulation(string dirPath, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return false;

        var h = CreateFileW(dirPath,
            FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | WRITE_DAC,
            FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[POP] 打开目录失败: {dirPath} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            // 只改标志，不动元数据（全 0 表示该部分不更新）
            var metadata = new CF_FS_METADATA();
            long usn = 0;
            var hr = CfUpdatePlaceholder(new HFILE(h), in metadata, IntPtr.Zero, 0,
                null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION, ref usn, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[POP] 关闭按需填充失败: {hr} dir={dirPath}");
                return false;
            }
            log?.Invoke($"[POP] 已关闭按需填充（cfapi 不再重复枚举）: {dirPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[POP] 关闭按需填充异常: {ex.Message} dir={dirPath}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 重开目录的按需填充（与 TryDisableOnDemandPopulation 对称，flag 取反）。
    /// 用途：填充后关闭按需的目录，用户重新进入/刷新时若缓存过期，在此重开 →
    ///   cfapi 下次列举触发 FETCH_PLACEHOLDERS → OnFetchPlaceholders 刷新（HTTP 重新拉取）
    ///   → 填充完成回执直接关闭按需。闭环实现"关闭按需下仍可刷新"。
    /// </summary>
    public static bool TryEnableOnDemandPopulation(string dirPath, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return false;

        var h = CreateFileW(dirPath,
            FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | WRITE_DAC,
            FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[POP] 重开按需填充-打开目录失败: {dirPath} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var metadata = new CF_FS_METADATA();
            long usn = 0;
            var hr = CfUpdatePlaceholder(new HFILE(h), in metadata, IntPtr.Zero, 0,
                null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION, ref usn, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[POP] 重开按需填充失败: {hr} dir={dirPath}");
                return false;
            }
            log?.Invoke($"[POP] 已重开按需填充（cfapi 将重新触发列举刷新）: {dirPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[POP] 重开按需填充异常: {ex.Message} dir={dirPath}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 把占位的 FileSize 改为 realSize。成功返回 true。
    /// 失败只记录日志、不抛异常（不影响本次数据传输）。
    /// </summary>
    public static bool TryUpdateFileSize(string localPath, long realSize, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(localPath) || realSize < 0) return false;

        var h = CreateFileW(localPath,
            FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | WRITE_DAC,
            FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[SIZE] 打开占位失败（无法修正大小）: {localPath} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            // 只更新 FileSize：BasicInfo 全 0 表示"该部分不更新"
            var metadata = new CF_FS_METADATA { FileSize = realSize };
            // UpdateUsn 是 ref：入参 0 表示不做 USN 校验，出参接收更新后的 USN
            long usn = 0;
            var hr = CfUpdatePlaceholder(new HFILE(h), in metadata, IntPtr.Zero, 0,
                null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, ref usn, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[SIZE] CfUpdatePlaceholder 失败: {hr} path={localPath} newSize={realSize}");
                return false;
            }
            log?.Invoke($"[SIZE] 占位大小已修正 → {realSize}: {localPath}");
            ExplorerStateNotifier.NotifyItem(localPath);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[SIZE] CfUpdatePlaceholder 异常: {ex.Message} path={localPath}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool TryGetPlaceholderDataState(string localPath, out PlaceholderDataState state)
    {
        state = default;
        var h = CreateFileW(localPath, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;

        var capacity = Marshal.SizeOf<CF_PLACEHOLDER_STANDARD_INFO>() + 4096;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var hr = CfGetPlaceholderInfo(new HFILE(h), CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD,
                buffer, (uint)capacity, out var returned);
            var dataOffset = Marshal.OffsetOf<CF_PLACEHOLDER_STANDARD_INFO>(
                nameof(CF_PLACEHOLDER_STANDARD_INFO.FileIdentity)).ToInt32();
            if (hr.Failed || returned < dataOffset) return false;
            var info = Marshal.PtrToStructure<CF_PLACEHOLDER_STANDARD_INFO>(buffer);
            state = new PlaceholderDataState(info.OnDiskDataSize, info.ValidatedDataSize,
                info.PinState, info.InSyncState, info.FileId);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(h);
        }
    }

    public static bool TryHydratePlaceholder(string localPath, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return false;
        var h = CreateFileW(localPath, WRITE_DAC, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[PIN] 打开占位以水化失败: Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var hr = CfHydratePlaceholder(new HFILE(h), 0, -1,
                CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[PIN] CfHydratePlaceholder 失败: {hr}");
                return false;
            }
            ExplorerStateNotifier.NotifyItem(localPath);
            log?.Invoke($"[PIN] 已完成强制水化: {Path.GetFileName(localPath)}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[PIN] 水化异常: {ex.Message}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 把目录占位的修改时间改为 fileTimeUtc。
    /// 走 CfUpdatePlaceholder 官方路径：BasicInfo 非零字段更新、FileSize=0 表示不动（目录无大小语义）。
    /// 供 File.SetLastWriteTimeUtc 被 cldflt 拦截时的兜底（占位目录时间修正）。
    /// </summary>
    public static bool TryUpdateDirTimes(string localPath, long fileTimeUtc, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(localPath)) return false;

        var h = CreateFileW(localPath,
            FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | WRITE_DAC,
            FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[DIRTIME] 打开目录失败: {localPath} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var ft = new System.Runtime.InteropServices.ComTypes.FILETIME
            {
                dwLowDateTime = unchecked((int)fileTimeUtc),
                dwHighDateTime = unchecked((int)(fileTimeUtc >> 32))
            };
            // FileSize=0 = 不更新；只提供 LastWriteTime，避免改变创建/访问时间。
            var metadata = new CF_FS_METADATA
            {
                BasicInfo = new Kernel32.FILE_BASIC_INFO
                {
                    LastWriteTime = ft
                }
            };
            long usn = 0;
            var hr = CfUpdatePlaceholder(new HFILE(h), in metadata, IntPtr.Zero, 0,
                null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, ref usn, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[DIRTIME] CfUpdatePlaceholder 失败: {hr} path={localPath}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[DIRTIME] CfUpdatePlaceholder 异常: {ex.Message} path={localPath}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 剥离文件/目录上的云文件重解析标记（IO_REPARSE_TAG_CLOUD_*），令其变回普通对象。
    ///
    /// 为什么需要：引擎被强杀（TaskStop / 任务管理器）后，SyncRoot 注册项残留，
    /// D:\YuWang 内的占位变成"孤儿"云重解析点。此时 Provider 未连接，普通
    /// File.Delete / Directory.Delete 会被 cldflt 拒绝（SAFE_DELETE_FAIL_CLOSED /
    /// 0x80070194）。通过 FSCTL_DELETE_REPARSE_POINT 把重解析标记剥掉后，
    /// 它就只是个普通（可能为空）的文件/目录，可正常删除。
    ///
    /// 必须在 CfUnregisterSyncRoot 之后调用（已与 Provider 解绑）成功率最高。
    /// </summary>
    public static bool TryStripReparsePoint(string path, bool isDirectory, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var h = CreateFileW(path,
            FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | (isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0), IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log?.Invoke($"[CLEAN] 打开重解析点失败(无法剥离): {path} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            // REPARSE_DATA_BUFFER 头部：ReparseTag=0 / ReparseDataLength=0 / Reserved=0
            // 表示"删除任意已有的重解析点"。
            var inBuf = new byte[8];
            var pinned = GCHandle.Alloc(inBuf, GCHandleType.Pinned);
            try
            {
                var ok = DeviceIoControl(h, FSCTL_DELETE_REPARSE_POINT,
                    pinned.AddrOfPinnedObject(), (uint)inBuf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!ok)
                {
                    log?.Invoke($"[CLEAN] FSCTL_DELETE_REPARSE_POINT 失败: {path} Win32={Marshal.GetLastWin32Error()}");
                    return false;
                }
                log?.Invoke($"[CLEAN] 已剥离云文件重解析标记(变普通对象): {path}");
                return true;
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CLEAN] 剥离重解析点异常: {ex.Message} path={path}");
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // ================= 脱水（清除本地已下载数据，还原为占位）=================

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    // FILE_INFO_BY_HANDLE_CLASS.FileStandardInfo = 1
    private const int FileStandardInfo = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int fileInformationClass,
        out FILE_STANDARD_INFO lpFileInformation, uint dwBufferSize);

    /// <summary>
    /// 查文件在磁盘上的**实际占用**（AllocationSize）。
    /// 已水化的占位会真实占用磁盘；纯占位（未下载）占用为 0 或极小。
    /// 用它判断"这个文件值不值得脱水"，避免对未水化文件白操作。
    /// </summary>
    public static long GetAllocationSize(string path)
    {
        var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;
        try
        {
            return GetFileInformationByHandleEx(h, FileStandardInfo, out var info,
                (uint)Marshal.SizeOf<FILE_STANDARD_INFO>())
                ? info.AllocationSize
                : -1;
        }
        catch { return -1; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// 把一个**已水化**的占位脱水：清掉本地已下载的数据，还原成纯占位（释放磁盘空间），
    /// 下次打开时再按需重新下载。
    ///
    /// 实现要点（顺序不能改）：
    /// 1. 先只读属性打开查 AllocationSize —— 磁盘上没数据的直接返回（无需脱水），
    ///    也避免对未水化文件申请数据权限（那会触发整文件水化，与本意相反）；
    /// 2. 确有数据才用 <see cref="CfOpenFileWithOplock"/> 取**独占句柄**再脱水。
    ///
    /// **为什么必须用 CfOpenFileWithOplock 而不是 CreateFileW**：
    /// CfDehydratePlaceholder 的官方 Remarks 要求"调用方必须取得独占句柄，
    /// 且强烈建议用 CfOpenFileWithOplock 拿 oplock，而不是 share-none 句柄"。
    /// 实测用普通 CreateFileW(GENERIC_READ|GENERIC_WRITE) 的共享句柄调用，
    /// CfDehydratePlaceholder 会**卡住不返回**（cldflt 与句柄状态机互等，
    /// 日志停在"开始清除"后再无输出）。改用 oplock 路径后恢复正常。
    ///
    /// 返回是否脱水成功；freedBytes 为释放的磁盘字节数（失败时为 0）。
    /// </summary>
    public static bool TryDehydrate(string localPath, out long freedBytes, Action<string>? log = null)
    {
        freedBytes = 0;
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return false;

        var alloc = GetAllocationSize(localPath);
        if (alloc <= 0)
        {
            // 磁盘上没有数据（已是占位 / 空文件），无需脱水
            return false;
        }

        var open = CfOpenFileWithOplock(localPath,
            CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, out var h);
        if (open.Failed)
        {
            // 拿不到独占（正被其它进程打开、或正在水化中）→ 跳过，不算错误
            log?.Invoke($"[DEHY] 跳过（无法独占打开）: {Path.GetFileName(localPath)} {open}");
            return false;
        }

        try
        {
            var hr = CfDehydratePlaceholder(h, 0, -1,
                CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, IntPtr.Zero);
            if (hr.Failed)
            {
                log?.Invoke($"[DEHY] CfDehydratePlaceholder 失败: {hr} path={localPath}");
                return false;
            }
            freedBytes = alloc;
            ExplorerStateNotifier.NotifyItem(localPath);
            log?.Invoke($"[DEHY] 已还原为占位: {Path.GetFileName(localPath)}（释放 {alloc / 1024.0:F1} KB）");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[DEHY] 脱水异常: {ex.Message} path={localPath}");
            return false;
        }
        finally
        {
            // 只能用 CfCloseHandle 关闭 CfOpenFileWithOplock 返回的句柄，
            // 混用 Win32 CloseHandle 会破坏 cfapi 内部状态。
            CfCloseHandle(h);
        }
    }

    /// <summary>
    /// 递归脱水目录下所有已水化文件，返回 (处理数, 成功数, 释放字节, 跳过数)。
    /// 跳过两类：磁盘上本就没数据的（已是占位）、正在被其它进程占用的（打不开）。
    /// </summary>
    public static (int Total, int Dehydrated, long FreedBytes, int Skipped) DehydrateTree(
        string rootDir, Action<string>? log = null)
    {
        var total = 0;
        var dehydrated = 0;
        var freed = 0L;
        var skipped = 0;
        if (!Directory.Exists(rootDir)) return (0, 0, 0, 0);

        string[] files;
        try { files = Directory.GetFiles(rootDir); }
        catch (Exception ex)
        {
            log?.Invoke($"[DEHY] 无法枚举: {rootDir} ({ex.Message})");
            files = [];
        }

        foreach (var f in files)
        {
            total++;
            if (TryDehydrate(f, out var freedOne, log))
            {
                dehydrated++;
                freed += freedOne;
            }
            else
            {
                skipped++;
            }
        }

        string[] subs;
        try { subs = Directory.GetDirectories(rootDir); }
        catch { subs = []; }

        foreach (var d in subs)
        {
            var r = DehydrateTree(d, log);
            total += r.Total;
            dehydrated += r.Dehydrated;
            freed += r.FreedBytes;
            skipped += r.Skipped;
        }

        return (total, dehydrated, freed, skipped);
    }

    /// <summary>
    /// 安全删除一个占位（文件或目录）。先尝试普通删除；若被 cldflt 拒绝
    /// （孤儿重解析点 / 未连接 Provider），自动剥离重解析标记后重试。
    /// 返回是否删除成功。
    /// </summary>
    public static bool DeletePlaceholderSafe(string path, bool isDirectory, Action<string>? log = null)
    {
        if (isDirectory && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            return false;
        try
        {
            if (isDirectory) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CLEAN] 直接删除失败，尝试剥离重解析点: {path} ({ex.GetType().Name}: {ex.Message})");
        }

        if (TryStripReparsePoint(path, isDirectory, log))
        {
            try
            {
                if (isDirectory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
                log?.Invoke($"[CLEAN] 剥离后删除成功: {path}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[CLEAN] 剥离后删除仍失败: {path} ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return false;
    }

    public static bool RevertDirectory(string path, Action<string>? log = null)
    {
        var handle = CreateFileW(path, FILE_READ_ATTRIBUTES | WRITE_DAC, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return false;
        try
        {
            var result = CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE, IntPtr.Zero);
            if (result.Failed) log?.Invoke($"[CLEAN] 保留用户文件时还原目录失败: {path} {result}");
            return !result.Failed;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>保留已完整水化文件的数据，并移除其 Cloud Files 占位状态。</summary>
    public static bool RevertFile(string path, Action<string>? log = null)
    {
        var handle = CreateFileW(path, FILE_READ_ATTRIBUTES | WRITE_DAC, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            log?.Invoke($"[CLEAN] 打开已下载文件失败: {path} Win32={Marshal.GetLastWin32Error()}");
            return false;
        }
        try
        {
            var result = CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE, IntPtr.Zero);
            if (result.Failed) log?.Invoke($"[CLEAN] 保留已下载文件时还原占位失败: {path} {result}");
            return !result.Failed;
        }
        finally { CloseHandle(handle); }
    }
}
