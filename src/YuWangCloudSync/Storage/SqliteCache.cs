using Microsoft.Data.Sqlite;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Storage;

/// <summary>
/// SQLite 元数据缓存。缓存已列过的目录与文件，避免重复 HTTP 抓取。
/// 表结构见路径E-CloudFilesAPI同步引擎实现框架.md 第 6.3 节。
/// </summary>
public sealed class SqliteCache : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>
    /// **必须**：Microsoft.Data.Sqlite 的 SqliteConnection **不是线程安全的**。
    /// cfapi 的 FETCH_PLACEHOLDERS / FETCH_DATA 回调在**多个线程池线程上并发触发**，
    /// 若不加串行化，并发 ExecuteScalar / ExecuteReader 会让连接进入损坏状态，
    /// 抛 NullReferenceException（日志里大量的
    /// "FETCH_PLACEHOLDERS 失败: Object reference not set to an instance of an object."
    /// 就是这个原因），并且一旦损坏之后**所有**目录列举都会失败
    /// → 表现为"访问过几个目录之后其他目录再也打不开"。
    /// 这里用一个进程内互斥锁串行化所有 DB 操作（所有方法都是同步且无重入，不会死锁）。
    /// </summary>
    private readonly object _sync = new();

    public SqliteCache(string dbPath)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        try
        {
            _conn.Open();
            InitializeSchema();
        }
        catch
        {
            // 初始化迁移失败时也释放 native SQLite 句柄，允许调用方改用可写目录重试。
            _conn.Dispose();
            throw;
        }
    }

    private void InitializeSchema()
    {
        // 迁移：旧版 folders 以 folder_id 单列为 PRIMARY KEY。
        // 多盘映射后每个磁盘根的 folder_id 都是 0 → 单键冲突（磁盘根互相覆盖、ListCachedItems(0) 串盘）。
        // 这里检测"仅 folder_id 一列是主键"的旧结构，DROP 两张表后按新复合主键重建
        // （缓存可重建，ResetListingCache + 重新列举即可恢复，不丢真数据）。
        var oldSinglePk = false;
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(folders)";
            using var r = pragma.ExecuteReader();
            var pkCols = 0;
            while (r.Read())
            {
                // table_info 第 6 列（下标 5）为 pk 标记（1=主键列）
                if (!r.IsDBNull(5) && r.GetInt32(5) == 1) pkCols++;
            }
            oldSinglePk = pkCols == 1;
        }
        if (oldSinglePk)
        {
            Console.Error.WriteLine("[SqliteCache] 检测到 folders 旧版单主键结构，重建为 (disk_id, folder_id) 复合主键");
            using var drop = _conn.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS folders;";
            drop.ExecuteNonQuery();
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS folders (
                disk_id INTEGER NOT NULL,
                folder_id INTEGER NOT NULL,
                parent_folder_id INTEGER,
                name TEXT,
                local_path TEXT,
                item_count INTEGER,
                last_listed INTEGER,
                PRIMARY KEY (disk_id, folder_id)
            );
            CREATE TABLE IF NOT EXISTS files (
                file_id INTEGER PRIMARY KEY,
                folder_id INTEGER NOT NULL,
                disk_id INTEGER NOT NULL,
                name TEXT,
                size INTEGER,
                upload_time TEXT,
                hydrated INTEGER DEFAULT 0,
                content_hash BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_files_folder ON files(folder_id, disk_id);
            CREATE INDEX IF NOT EXISTS idx_folders_parent ON folders(parent_folder_id);

            CREATE TABLE IF NOT EXISTS disk_tree (
                user_key TEXT NOT NULL,
                node_id INTEGER NOT NULL,
                node_type TEXT NOT NULL,
                parent_id INTEGER NOT NULL,
                name TEXT,
                last_fetched INTEGER,
                PRIMARY KEY (user_key, node_id)
            );

            CREATE TABLE IF NOT EXISTS directory_permissions (
                user_key TEXT NOT NULL,
                disk_id INTEGER NOT NULL,
                folder_id INTEGER NOT NULL,
                permission INTEGER NOT NULL,
                probed_at INTEGER NOT NULL,
                PRIMARY KEY (user_key, disk_id, folder_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // 迁移：老库没有 real_size 列（育网云列表页只给向下取整到整 MB 的大小，
        // 真实大小要等整文件下载完才知道，这里持久化下来供下次创建占位使用）
        using (var mig = _conn.CreateCommand())
        {
            mig.CommandText = "ALTER TABLE files ADD COLUMN real_size INTEGER";
            try { mig.ExecuteNonQuery(); } catch { /* 列已存在 */ }
        }
        // 迁移：直链 + 探测时间比对（2026-09-29 引入真实大小探测方案）
        //   direct_url：upload_dir/file/<yyyyMM>/<hash>.<ext> 静态直链，
        //               HEAD 可得精确 Content-Length，且 Accept-Ranges: bytes 支持断点续传
        //               （留作后续分片水化优化；downSelectFile 是 chunked、忽略 Range，无法承担）
        //   probed_upload_time：探测 real_size 时的文件 upload_time 快照。
        //               之后列举时若列表的 upload_time 与之相同 → 文件没被替换 → 不再重复探测
        using (var mig2 = _conn.CreateCommand())
        {
            mig2.CommandText = """
                ALTER TABLE files ADD COLUMN direct_url TEXT;
                ALTER TABLE files ADD COLUMN probed_upload_time TEXT;
                """;
            try { mig2.ExecuteNonQuery(); } catch { /* 列已存在 */ }
        }
    }

    /// <summary>记录文件真实字节数（整文件下载完成后才知道）</summary>
    public void SetRealSize(ulong fileId, long realSize)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET real_size=@sz WHERE file_id=@fid";
            cmd.Parameters.AddWithValue("@sz", realSize);
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 用 fileId 反查本地完整路径（父目录 local_path + 文件名）。
    /// **必须**：FETCH_DATA 回调里 cfapi **不提供** NormalizedPath
    /// （实测为空 → 之前误把同步根目录的元数据改了），只能靠 FileIdentity 反查。
    /// </summary>
    public string? GetFileLocalPath(ulong fileId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT fo.local_path, f.name
                FROM files f JOIN folders fo
                  ON fo.folder_id = f.folder_id AND fo.disk_id = f.disk_id
                WHERE f.file_id=@fid
                """;
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var dir = r.IsDBNull(0) ? "" : r.GetString(0);
            var name = r.IsDBNull(1) ? "" : r.GetString(1);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            try { return Path.Combine(dir, name); }
            catch { return null; }
        }
    }

    /// <summary>取已知的真实字节数，未知返回 null</summary>
    public long? GetRealSize(ulong fileId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT real_size, probed_upload_time, upload_time
                FROM files WHERE file_id=@fid
                """;
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return null;
            var size = r.GetInt64(0);
            if (size > 0) return size;
            if (size == 0 && !r.IsDBNull(1)
                && string.Equals(r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), StringComparison.Ordinal))
                return 0;
            return null;
        }
    }

    /// <summary>
    /// 该文件是否需要（重新）探测真实大小。
    /// 规则：real_size 未知 → 探测；已知但记录的 upload_time 与当前列表不一致
    ///（文件在服务器上被替换过）→ 重新探测；否则跳过（一生只探一次，除非文件变了）。
    /// </summary>
    public bool NeedsSizeProbe(ulong fileId, string uploadTime)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT real_size, probed_upload_time FROM files WHERE file_id=@fid";
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return true;                    // 还没入库 → 探测
            if (r.IsDBNull(0)) return true;                // 大小未知 → 探测
            var probed = r.IsDBNull(1) ? null : r.GetString(1);
            if (!string.Equals(probed, uploadTime, StringComparison.Ordinal))
                return true;                               // 文件被替换过 → 重新探测
            return r.GetInt64(0) < 0;                      // 0 + 同版探测时间表示已确认没有内容对象
        }
    }

    /// <summary>
    /// 取文件下载所需信息：静态直链 + 文件名（缺一不可时返回 null）。
    /// 直链用于 Range 分片按需下载；文件名用于首次探测时拼直链扩展名。
    /// </summary>
    public (string? DirectUrl, string? Name, long? DeclaredSize, string? UploadTime, bool IsContentless)
        GetFileDownloadInfo(ulong fileId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT direct_url, name, size, real_size, probed_upload_time, upload_time
                FROM files WHERE file_id=@fid
                """;
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, null, null, null, false);
            var url = r.IsDBNull(0) ? null : r.GetString(0);
            var name = r.IsDBNull(1) ? null : r.GetString(1);
            var declaredSize = r.IsDBNull(2) ? (long?)null : r.GetInt64(2);
            var realSize = r.IsDBNull(3) ? (long?)null : r.GetInt64(3);
            var probedUploadTime = r.IsDBNull(4) ? null : r.GetString(4);
            var uploadTime = r.IsDBNull(5) ? null : r.GetString(5);
            var isContentless = realSize == 0 && probedUploadTime is not null
                && string.Equals(probedUploadTime, uploadTime ?? "", StringComparison.Ordinal);
            return (string.IsNullOrEmpty(url) ? null : url, name, declaredSize, uploadTime, isContentless);
        }
    }

    /// <summary>
    /// 保存探测结果：真实大小 + 直链 + 探测时的 upload_time 快照。
    /// 直链（upload_dir/file/...）Accept-Ranges: bytes，水化主路径据此做 Range 分片下载。
    /// </summary>
    public void SaveDirectInfo(ulong fileId, long realSize, string directUrl, string probedUploadTime)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE files SET real_size=@sz, direct_url=@url, probed_upload_time=@ut
                WHERE file_id=@fid
                """;
            cmd.Parameters.AddWithValue("@sz", realSize);
            cmd.Parameters.AddWithValue("@url", directUrl ?? "");
            cmd.Parameters.AddWithValue("@ut", probedUploadTime ?? "");
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 确保 folders 表存在该 folder 的行（(disk_id, folder_id) 复合主键），以便 files 的外键约束成立。
    /// 回源自愈时父目录行可能尚未入库：files.folder_id 引用一个不存在的 folder_id 会触发
    /// "FOREIGN KEY constraint failed"。这里用最小占位行（parent_folder_id=NULL，SQLite 外键对
    /// NULL 不校验）兜底，之后正常枚举的 SaveFolder 会用真实 parent/local_path/name 修正。
    /// </summary>
    public void EnsureFolderRow(ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO folders(folder_id, disk_id, parent_folder_id, name, local_path, item_count, last_listed)
                VALUES(@fid, @disk, NULL, '', '', 0, 0)
                ON CONFLICT(folder_id, disk_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@fid", (long)folderId);
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 仅 upsert 文件行（file_id 为主键），用于 FETCH_DATA 自愈时补全缺失的文件元数据。
    /// 不动 folders 表的层级（parent_folder_id 由正常枚举的 SaveFolder 维护），避免破坏目录树。
    /// 典型场景：占位跨运行保留、但本运行 DB 是全新的，磁盘上有旧占位而 files 表无该文件行，
    /// 此时按 fileId 落一条文件元数据即可让后续直链探测/大小修正恢复正常。
    /// </summary>
    public void SaveFileMetadata(ulong diskId, ulong folderId, ulong fileId,
        string name, string? uploadTime, long? size)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files(file_id, folder_id, disk_id, name, size, upload_time, hydrated)
                VALUES(@fid, @pfolder, @disk, @name, @size, @ut, 0)
                ON CONFLICT(file_id) DO UPDATE SET
                    folder_id=excluded.folder_id, disk_id=excluded.disk_id,
                    name=excluded.name, size=excluded.size, upload_time=excluded.upload_time;
                """;
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            cmd.Parameters.AddWithValue("@pfolder", (long)folderId);
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@name", name ?? "");
            cmd.Parameters.AddWithValue("@size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ut", uploadTime ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>保存或更新某个文件夹的列表结果（文件夹 + 文件全部入库）</summary>
    public void SaveFolder(ulong diskId, ulong folderId, ulong parentFolderId,
                           string name, string localPath, IReadOnlyList<YuWangItem> items)
    {
        lock (_sync)
        {
        using var tx = _conn.BeginTransaction();
        try
        {
            // upsert folder
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO folders(folder_id, disk_id, parent_folder_id, name, local_path, item_count, last_listed)
                    VALUES(@fid, @disk, @parent, @name, @path, @cnt, @ts)
                    ON CONFLICT(folder_id, disk_id) DO UPDATE SET
                        disk_id=excluded.disk_id, parent_folder_id=excluded.parent_folder_id,
                        name=excluded.name, local_path=excluded.local_path,
                        item_count=excluded.item_count, last_listed=excluded.last_listed;
                    """;
                cmd.Parameters.AddWithValue("@fid", (long)folderId);
                cmd.Parameters.AddWithValue("@disk", (long)diskId);
                cmd.Parameters.AddWithValue("@parent", (long)parentFolderId);
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@path", localPath ?? "");
                cmd.Parameters.AddWithValue("@cnt", items.Count);
                cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            // upsert files
            foreach (var item in items)
            {
                if (item.IsFolder) continue;  // 文件夹入库走 SaveSubFolder（由调用方遍历 items 触发）
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files(file_id, folder_id, disk_id, name, size, upload_time, hydrated)
                    VALUES(@fid, @pfolder, @disk, @name, @size, @ut, 0)
                    ON CONFLICT(file_id) DO UPDATE SET
                        folder_id=excluded.folder_id, disk_id=excluded.disk_id,
                        name=excluded.name, size=excluded.size, upload_time=excluded.upload_time;
                    """;
                cmd.Parameters.AddWithValue("@fid", (long)item.Id);
                cmd.Parameters.AddWithValue("@pfolder", (long)folderId);
                cmd.Parameters.AddWithValue("@disk", (long)diskId);
                cmd.Parameters.AddWithValue("@name", item.Name);
                cmd.Parameters.AddWithValue("@size", (object?)item.Size ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ut", item.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        }
    }

    /// <summary>记录子文件夹条目（仅元数据，无文件内容）。
    /// local_path 用于启动对账时 TryGetFolderByLocalPath 反查身份——之前不存导致未列举过的
    /// 子文件夹 local_path 恒空，对账全部"跳过（DB 无记录）"。此处补存完整本地路径。
    /// ON CONFLICT 用 CASE 保护：仅当新值非空才覆盖，避免空调用覆盖已有值。</summary>
    public void SaveSubFolder(ulong diskId, ulong folderId, ulong parentFolderId, string name, string? localPath = null)
    {
        lock (_sync)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO folders(folder_id, disk_id, parent_folder_id, name, local_path, item_count, last_listed)
            VALUES(@fid, @disk, @parent, @name, @path, 0, 0)
            ON CONFLICT(folder_id, disk_id) DO UPDATE SET
                disk_id=excluded.disk_id, parent_folder_id=excluded.parent_folder_id,
                name=excluded.name,
                local_path=CASE WHEN excluded.local_path != '' THEN excluded.local_path ELSE folders.local_path END;
            """;
        cmd.Parameters.AddWithValue("@fid", (long)folderId);
        cmd.Parameters.AddWithValue("@disk", (long)diskId);
        cmd.Parameters.AddWithValue("@parent", (long)parentFolderId);
        cmd.Parameters.AddWithValue("@name", name ?? "");
        cmd.Parameters.AddWithValue("@path", localPath ?? "");
        cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 清空列举缓存（把 folders 表的 last_listed 置 0）。
    /// 启动时调用：保证"占位文件"与"列举缓存"状态一致。
    /// 否则上次退出清理删除了占位文件、但 SQLite 缓存仍在，
    /// 下次启动会命中缓存而跳过占位创建 → 目录显示为空。
    /// </summary>
    public void ResetListingCache()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET last_listed=0";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 清空 folders.local_path（SyncRoot 换目录后旧绝对路径全部失效，必须先清）。
    /// 不这么做的话：TryGetFolderByLocalPath 会拿着旧路径反查身份，或反查不到（因为有 local_path 记录但拼的是新根为新 canal
    /// …实际表现是对账命中错行/空结果 → 目录时间对账退化成"刷新 0 个"）。
    /// 清空后下次列举/预拉取会按新路径重新写入，自愈。
    /// </summary>
    public void ClearFolderLocalPaths()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET local_path=''";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>整棵磁盘树是否已拉取并存库（供运行期判断是否走虚拟节点分支）</summary>
    public bool IsDiskTreeFetched(string userKey)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM disk_tree WHERE user_key=@uk";
            cmd.Parameters.AddWithValue("@uk", userKey ?? "");
            var r = cmd.ExecuteScalar();
            return r is long c && c > 0;
        }
    }

    /// <summary>读取某父容器（typeid）下的直接子节点（容器或磁盘），用于虚拟节点列举</summary>
    public List<DiskTreeNode> ListChildNodes(string userKey, ulong parentId)
    {
        lock (_sync)
        {
            var result = new List<DiskTreeNode>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT node_id, node_type, name FROM disk_tree WHERE user_key=@uk AND parent_id=@pid ORDER BY node_type, name";
            cmd.Parameters.AddWithValue("@uk", userKey ?? "");
            cmd.Parameters.AddWithValue("@pid", (long)parentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new DiskTreeNode
                {
                    Id = (ulong)r.GetInt64(0),
                    NodeType = r.GetString(1),
                    Name = r.IsDBNull(2) ? "" : r.GetString(2),
                    ParentId = parentId
                });
            }
            return result;
        }
    }

    /// <summary>整棵树入库（先清旧再写新，保证每次拉取都是全量快照）</summary>
    public void SaveDiskTree(string userKey, IReadOnlyList<DiskTreeNode> nodes)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                using (var del = _conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM disk_tree WHERE user_key=@uk";
                    del.Parameters.AddWithValue("@uk", userKey ?? "");
                    del.ExecuteNonQuery();
                }
                foreach (var n in nodes)
                {
                    using var ins = _conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO disk_tree(user_key, node_id, node_type, parent_id, name, last_fetched)
                        VALUES(@uk, @nid, @nt, @pid, @name, @ts)
                        ON CONFLICT(user_key, node_id) DO UPDATE SET
                            node_type=excluded.node_type, parent_id=excluded.parent_id,
                            name=excluded.name, last_fetched=excluded.last_fetched;
                        """;
                    ins.Parameters.AddWithValue("@uk", userKey ?? "");
                    ins.Parameters.AddWithValue("@nid", (long)n.Id);
                    ins.Parameters.AddWithValue("@nt", n.NodeType);
                    ins.Parameters.AddWithValue("@pid", (long)n.ParentId);
                    ins.Parameters.AddWithValue("@name", n.Name ?? "");
                    ins.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }
    }

    // ================= 目录推导时间（服务器不提供目录时间，用内容最新文件上传时间推导）=================

    /// <summary>
    /// 目录推导修改时间：该目录**全部已入库后代**（含自身直接子文件）的最大上传时间。
    /// 经 folders.parent_folder_id 递归展开子树后聚合 files.upload_time（UNION 去重防脏数据成环）。
    /// 仅覆盖已浏览过的子树（files 只在列举时入库），未浏览部分靠后续上浮收敛。
    /// </summary>
    public DateTime? GetFolderTreeMaxUploadTime(ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                WITH RECURSIVE sub(d, f) AS (
                    SELECT @d, @f
                    UNION
                    SELECT fo.disk_id, fo.folder_id FROM folders fo
                    JOIN sub ON fo.disk_id = sub.d AND fo.parent_folder_id = sub.f
                )
                SELECT MAX(fi.upload_time) FROM files fi
                JOIN sub ON fi.disk_id = sub.d AND fi.folder_id = sub.f;
                """;
            cmd.Parameters.AddWithValue("@d", (long)diskId);
            cmd.Parameters.AddWithValue("@f", (long)folderId);
            return ParseUploadTime(cmd.ExecuteScalar() as string);
        }
    }

    /// <summary>磁盘下全部已入库文件的最大上传时间（磁盘根 / 容器的推导时间）</summary>
    public DateTime? GetDiskMaxUploadTime(ulong diskId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(upload_time) FROM files WHERE disk_id=@d;";
            cmd.Parameters.AddWithValue("@d", (long)diskId);
            return ParseUploadTime(cmd.ExecuteScalar() as string);
        }
    }

    /// <summary>按本地路径反查文件夹身份（启动对账时把磁盘上的目录映射回 disk/folder；路径不区分大小写）</summary>
    public (ulong diskId, ulong folderId)? TryGetFolderByLocalPath(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return null;
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT disk_id, folder_id FROM folders WHERE local_path = @p COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("@p", localPath);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ((ulong)r.GetInt64(0), (ulong)r.GetInt64(1)) : null;
        }
    }

    public string? GetFolderLocalPath(ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT local_path FROM folders WHERE disk_id=@disk AND folder_id=@folder";
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@folder", (long)folderId);
            var value = cmd.ExecuteScalar() as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public void SetFolderLocalPath(ulong diskId, ulong folderId, string localPath)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET local_path=@path WHERE disk_id=@disk AND folder_id=@folder";
            cmd.Parameters.AddWithValue("@path", localPath ?? "");
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@folder", (long)folderId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>解析 files.upload_time（"yyyy-MM-dd HH:mm:ss"，服务器本地时刻、Kind=Unspecified）</summary>
    private static DateTime? ParseUploadTime(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null :
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : (DateTime?)null;

    /// <summary>判断 folder 是否已缓存（且未过期）。diskId 与 folderId 组成复合主键，避免多盘根 folder_id=0 串盘。</summary>
    public bool IsFolderCached(ulong diskId, ulong folderId, TimeSpan maxAge)
    {
        lock (_sync)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_listed FROM folders WHERE disk_id=@disk AND folder_id=@fid AND last_listed > 0";
        cmd.Parameters.AddWithValue("@disk", (long)diskId);
        cmd.Parameters.AddWithValue("@fid", (long)folderId);
        var result = cmd.ExecuteScalar();
        if (result is long ts)
        {
            var lastListed = DateTimeOffset.FromUnixTimeSeconds(ts);
            return DateTimeOffset.UtcNow - lastListed < maxAge;
        }
        return false;
        }
    }

    /// <summary>读取某 folder 下缓存的子项（用于 FETCH_PLACEHOLDERS 直接出占位）</summary>
    public List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)>
        ListCachedItems(ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
        var result = new List<(ulong, string, bool, long?, DateTime?)>();
        // 文件夹（排除自身：根目录 folder_id=0 时 parent_folder_id=0 自指）
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT folder_id, name FROM folders WHERE disk_id=@disk AND parent_folder_id=@pf AND folder_id<>@pf";
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@pf", (long)folderId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(((ulong)r.GetInt64(0), r.GetString(1), true, null, null));
        }
        // 文件
        using (var cmd = _conn.CreateCommand())
        {
            // 已知真实大小就直接用；否则用列表页的声明值 **+1MB 余量**
            // （声明值是向下取整到整 MB 的，偏小会导致占位把文件截断）
            cmd.CommandText = """
                SELECT file_id, name,
                       CASE
                           WHEN real_size > 0 THEN real_size
                           WHEN real_size = 0 AND probed_upload_time IS NOT NULL
                                AND probed_upload_time = COALESCE(upload_time, '') THEN 0
                           ELSE size + MAX(@pad, size / @div)
                       END,
                       upload_time
                FROM files WHERE disk_id=@disk AND folder_id=@pf
                """;
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@pf", (long)folderId);
            cmd.Parameters.AddWithValue("@pad", SizePolicy.MinPaddingBytes);
            cmd.Parameters.AddWithValue("@div", SizePolicy.PaddingDivisor);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long? size = r.IsDBNull(2) ? null : r.GetInt64(2);
                DateTime? ut = null;
                if (!r.IsDBNull(3) && DateTime.TryParse(r.GetString(3), out var dt)) ut = dt;
                result.Add(((ulong)r.GetInt64(0), r.GetString(1), false, size, ut));
            }
        }
        return result;
        }
    }

    public void MarkHydrated(ulong fileId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET hydrated=1 WHERE file_id=@fid";
            cmd.Parameters.AddWithValue("@fid", (long)fileId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Persist the latest read-only/full-access probe for one account directory.</summary>
    public void SaveDirectoryPermission(string userKey, ulong diskId, ulong folderId,
        DirectoryPermission permission)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO directory_permissions(user_key, disk_id, folder_id, permission, probed_at)
                VALUES(@uk, @disk, @folder, @permission, @at)
                ON CONFLICT(user_key, disk_id, folder_id) DO UPDATE SET
                    permission=excluded.permission, probed_at=excluded.probed_at;
                """;
            cmd.Parameters.AddWithValue("@uk", userKey ?? "");
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@folder", (long)folderId);
            cmd.Parameters.AddWithValue("@permission", (int)permission);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Unknown is returned until this exact account/directory pair has a successful probe.</summary>
    public DirectoryPermission GetDirectoryPermission(string userKey, ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT permission FROM directory_permissions
                WHERE user_key=@uk AND disk_id=@disk AND folder_id=@folder;
                """;
            cmd.Parameters.AddWithValue("@uk", userKey ?? "");
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@folder", (long)folderId);
            var value = cmd.ExecuteScalar();
            return value is long number && Enum.IsDefined(typeof(DirectoryPermission), (int)number)
                ? (DirectoryPermission)(int)number : DirectoryPermission.Unknown;
        }
    }

    public PlaceholderRestoreInfo? GetPlaceholderRestoreInfo(FileIdentity identity)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            if (identity.Type == 1)
            {
                cmd.CommandText = """
                    SELECT fo.local_path, fi.name,
                           CASE
                               WHEN fi.real_size = 0
                                    AND fi.probed_upload_time IS NOT NULL
                                    AND fi.probed_upload_time = COALESCE(fi.upload_time, '') THEN 0
                               WHEN fi.real_size > 0 THEN fi.real_size
                               ELSE fi.size + MAX(@pad, fi.size / @div)
                           END,
                           fi.upload_time
                    FROM files fi JOIN folders fo
                      ON fo.disk_id=fi.disk_id AND fo.folder_id=fi.folder_id
                    WHERE fi.disk_id=@disk AND fi.folder_id=@parent AND fi.file_id=@item;
                    """;
                cmd.Parameters.AddWithValue("@pad", SizePolicy.MinPaddingBytes);
                cmd.Parameters.AddWithValue("@div", SizePolicy.PaddingDivisor);
                cmd.Parameters.AddWithValue("@parent", (long)identity.FolderId);
            }
            else
            {
                cmd.CommandText = """
                    SELECT parent.local_path, item.name, 0, NULL
                    FROM folders item JOIN folders parent
                      ON parent.disk_id=item.disk_id AND parent.folder_id=item.parent_folder_id
                    WHERE item.disk_id=@disk AND item.folder_id=@item;
                    """;
            }
            cmd.Parameters.AddWithValue("@disk", (long)identity.DiskId);
            cmd.Parameters.AddWithValue("@item", (long)identity.FileId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var parentPath = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(name)) return null;
            var size = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            DateTime? uploadTime = null;
            if (!reader.IsDBNull(3) && DateTime.TryParse(reader.GetString(3), out var parsed)) uploadTime = parsed;
            return new PlaceholderRestoreInfo(parentPath, name, identity.Type == 0, size, uploadTime);
        }
    }

    public void InvalidateFolderListing(ulong diskId, ulong folderId)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET last_listed=0 WHERE disk_id=@disk AND folder_id=@folder";
            cmd.Parameters.AddWithValue("@disk", (long)diskId);
            cmd.Parameters.AddWithValue("@folder", (long)folderId);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveCachedItem(FileIdentity identity)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                if (identity.Type == 1)
                {
                    cmd.CommandText = "DELETE FROM files WHERE disk_id=@disk AND folder_id=@parent AND file_id=@item";
                    cmd.Parameters.AddWithValue("@parent", (long)identity.FolderId);
                }
                else
                {
                    cmd.CommandText = """
                        WITH RECURSIVE subtree(folder_id) AS (
                            SELECT @item
                            UNION
                            SELECT child.folder_id FROM folders child
                            JOIN subtree parent ON child.parent_folder_id=parent.folder_id
                            WHERE child.disk_id=@disk
                        )
                        DELETE FROM files WHERE disk_id=@disk AND folder_id IN (SELECT folder_id FROM subtree);
                        WITH RECURSIVE subtree(folder_id) AS (
                            SELECT @item
                            UNION
                            SELECT child.folder_id FROM folders child
                            JOIN subtree parent ON child.parent_folder_id=parent.folder_id
                            WHERE child.disk_id=@disk
                        )
                        DELETE FROM folders WHERE disk_id=@disk AND folder_id IN (SELECT folder_id FROM subtree);
                        """;
                }
                cmd.Parameters.AddWithValue("@disk", (long)identity.DiskId);
                cmd.Parameters.AddWithValue("@item", (long)identity.FileId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }
    }

    /// <summary>
    /// Remove account-ambiguous file/folder metadata before connecting another account.
    /// Permission probes and disk trees remain partitioned by account key and can be reused.
    /// </summary>
    public void ClearContentCache()
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM files; DELETE FROM folders;";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_sync) { _conn.Dispose(); }
    }
}
