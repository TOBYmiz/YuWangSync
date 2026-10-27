using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// 后台修正占位目录修改时间的工作队列。
/// 目录修改时间修正（[DIRTIME]）：
/// 育网云列表页对目录只给"创建者+名称"、无任何时间（实证 folderid=0 列表全页 0 个日期），
/// 创建目录占位时只能用当前时刻 → Explorer 修改日期显示"程序运行时间"。
/// 修正语义：目录修改时间 = 内容中最新文件的上传时间（由列举回调推导、含祖先链上浮），
/// 只增不减；目录占位的时间戳是本地 NTFS 元数据，SetLastWriteTimeUtc 不触发水化。
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlaceholderFixer
{
    private sealed record DirTimeJob(string Path, long TimeUtc, bool Force);

    /// <summary>待修正修改时间的目录（只增不减）</summary>
    private static readonly ConcurrentQueue<DirTimeJob> DirTimeQueue = new();
    /// <summary>目录 → 上次已入队的时间（去重：仅当新时间更大才再入队）</summary>
    private static readonly ConcurrentDictionary<string, long> DirTimeEnqueued = new();
    private static readonly ManualResetEventSlim Wake = new(false);
    private static Action<string>? _log;
    private static Thread? _worker;
    private static volatile bool _stopping;

    public static void Start(Action<string>? log)
    {
        _log = log;
        _stopping = false;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PlaceholderFixer"
        };
        _worker.Start();
    }

    public static void Stop()
    {
        _stopping = true;
        Wake.Set();
    }

    /// <summary>
    /// 入队：修正目录占位的修改时间。
    /// - raise-only（默认，force=false）：只增不减，目标 ≤ 当前则跳过——浏览期上浮语义，
    ///   保证目录时间收敛到"全部后代文件的最大上传时间"，与处理顺序无关。
    /// - force=true：无条件设为目标值（仅启动对账用，把历史版本"出生盖当时时刻"的污染值归位）。
    /// 同一目录重复入队时仅当时间比上次入队的更新才再入（force 不受此限），
    /// 避免 Explorer 反复枚举把队列刷爆。
    /// </summary>
    public static void EnqueueDirTime(string dirPath, long fileTimeUtc, bool force = false)
    {
        if (string.IsNullOrEmpty(dirPath) || fileTimeUtc <= 0) return;
        // **盘符根守卫**：祖先链上浮不应溢出到盘符（D:\、C:\）。
        // 即便 SyncRootPath 设置正确（已在 Program 统一设置），这里仍是最后一道防线——
        // CfUpdatePlaceholder 对盘符报 ERROR_NOT_A_CLOUD_FILE，File.GetLastWriteTimeUtc 报
        // "Path must not be a drive"（日志 line 137-139 的根因之一）。
        // 盘符根 TrimEnd('\\') 后长度为 2（"X:"），普通目录路径远长于 2。
        try
        {
            var full = Path.GetFullPath(dirPath).TrimEnd('\\');
            if (full.Length <= 2) return;   // 盘符根，不入队
        }
        catch { return; }   // 路径非法，不入队
        if (!force)
        {
            while (true)
            {
                if (DirTimeEnqueued.TryGetValue(dirPath, out var last))
                {
                    if (fileTimeUtc <= last) return;
                    if (!DirTimeEnqueued.TryUpdate(dirPath, fileTimeUtc, last)) continue;
                }
                else if (!DirTimeEnqueued.TryAdd(dirPath, fileTimeUtc))
                {
                    continue;
                }
                break;
            }
        }
        else
        {
            DirTimeEnqueued[dirPath] = fileTimeUtc;
        }
        DirTimeQueue.Enqueue(new DirTimeJob(dirPath, fileTimeUtc, force));
        Wake.Set();
    }

    private static void WorkerLoop()
    {
        while (!_stopping)
        {
            Wake.Wait(500);
            Wake.Reset();

            // 目录修改时间修正（占位目录时间戳是本地 NTFS 元数据，不触发水化）
            // - 普通任务只增不减（浏览期上浮语义）；Force 任务无条件设为目标值（启动对账归位）
            while (DirTimeQueue.TryDequeue(out var job))
            {
                if (_stopping) return;
                try
                {
                    // 对账和浏览期推导可能交错入队；旧目标不能覆盖同一路径较新的推导值。
                    if (DirTimeEnqueued.TryGetValue(job.Path, out var latest) && job.TimeUtc < latest)
                        continue;
                    if (!Directory.Exists(job.Path)) continue;
                    var target = DateTime.FromFileTimeUtc(job.TimeUtc);
                    var current = File.GetLastWriteTimeUtc(job.Path);
                    if (job.Force ? current == target : current >= target) continue;
                    try
                    {
                        File.SetLastWriteTimeUtc(job.Path, target);
                    }
                    catch (Exception ex)
                    {
                        // cldflt 极端情况下可能拦截普通属性写 → 走 CfUpdatePlaceholder 官方路径兜底
                        if (!PlaceholderUpdater.TryUpdateDirTimes(job.Path, job.TimeUtc, _log))
                        {
                            _log?.Invoke($"[DIRTIME] 修正失败（忽略）: {ex.Message} path={job.Path}");
                            continue;
                        }
                    }
                    _log?.Invoke($"[DIRTIME] 目录修改时间已修正{(job.Force ? "（对账）" : "")} → {target:yyyy-MM-dd HH:mm}: {job.Path}");
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[DIRTIME] 修正异常（忽略）: {ex.Message} path={job.Path}");
                }
            }

        }
    }
}
