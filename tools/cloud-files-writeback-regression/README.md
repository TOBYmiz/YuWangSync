# Cloud Files 写回回归

```powershell
dotnet run --project tools/cloud-files-writeback-regression/CloudFilesWritebackRegression.csproj -c Release
```

使用内存 HTTP handler 模拟研究中确认的 Web 接口，无账号、无真实服务器写入，不注册 SyncRoot 或 Explorer 入口。临时目录仅创建在当前工作目录的 `_buildtmp/writeback-regression/<随机ID>`，结束后清理。

包含新文件的 10 MiB 原始分片及独占末端范围、中文/特殊字符名称、0B、逐片偏移确认、同名重试去重、权限/目标/部分页面保护、覆盖先备份后上传校验再删除、失败还原、丢失最终响应、清理失败重启续办、目录创建、移动同时改名、HTTP 错误保护、后代缓存路径及账号隔离。

续传回归覆盖：非分片对齐的初始偏移、HTTP 308 中间响应、已完成 token 直接校验且不 POST、越界/非整数偏移拒绝、已完成但目录缺失拒绝、旧前缀内容不符拒绝，以及覆盖续传校验失败还原旧文件名。服务端偏移只是传输提示，完整下载哈希才决定成功；同时检查进度携带复用字节数及 GET 防缓存参数。

后缀与去重回归覆盖：服务器自动追加六位数字后完整校验并恢复原名、保留原有后缀文件、最终响应丢失后复用新增 ID、部分上传中断后继续续传、目录延迟可见/多个匹配结果时不重复申请 token、改名失败后关闭并重新打开 SQLite 仍只处理原 ID，以及内容校验失败时不改名、不再上传。生产目录监视器的重启测试同样使用服务器追加后缀的响应。

目录发布回归另模拟最终字节 ACK 已成功、前两次目录查询仍无新条目，且没有防缓存参数的查询始终返回旧列表。检查等待新鲜列表后完成校验/改名，仍只有一次 token 和一次文件发送；取消目录等待时保留可能已发布的上传记录，不继续改名或重传。

另用真实 `FileSystemWatcher` 驱动生产写回队列，验证新文件自动发现、等待写入句柄关闭、上传并保留本地字节，以及在普通目录无法绑定 CFAPI 身份时保存记录、重启不重复上传。此项不验证原生占位转换成功；真实服务器及已安装 Explorer 场景仍需单独测试。

可注册 Cloud Files 同步根的 Windows/NTFS 环境中，可进一步运行原生回归：

```powershell
dotnet build tools/cloud-files-writeback-regression/CloudFilesWritebackRegression.csproj -c Release
dotnet tools/cloud-files-writeback-regression/bin/Release/net10.0-windows10.0.19041.0/win-x64/CloudFilesWritebackRegression.dll --native
```

`--native` 添加第 40 项测试，在 `_buildtmp/native-writeback/<随机ID>/root` 注册临时 SyncRoot，不注册 Explorer 导航入口。外部进程驱动新文件、编辑、重命名和删除，验证上传后绑定占位、in-sync、新旧 ID 替换、`_back`、重命名不重复上传，以及真实 CFAPI 删除完成回调。另覆盖：父级优先预建虚拟/磁盘/深层缓存树、缓存命中补建、保持未水化、普通目录冲突保护、移入未缓存权限的新目录、含子目录的文件夹移动、保留名称末尾括号、目标父目录删除失败后重试。部分续传和已完成 token 还检查原生身份绑定、in-sync、完整本地字节及复用字节日志。结束后断开、反注册、清理；网络仍使用模拟 handler。2026-11-06 完整 40 项通过。

进度回归检查 64 KiB 片内回调及单调计数、发送量与服务器确认量分离、接收器异常隔离。在原生场景中阻塞服务器 ACK，检查尚未完成/未绑定；释放 ACK 后检查完成日志和已同步身份；另验证失败和取消不会报告完成。Shell API 的原生状态提交在该 JIT 场景中成功；没有操纵 Explorer 窗口来验证可见进度。
