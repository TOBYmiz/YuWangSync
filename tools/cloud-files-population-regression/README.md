# Cloud Files population regression

在启用 Cloud Files 的 Windows / NTFS 环境运行：

```powershell
dotnet build tools/cloud-files-population-regression/CloudFilesPopulationRegression.csproj -c Release
dotnet tools/cloud-files-population-regression/bin/Release/net10.0-windows10.0.19041.0/win-x64/CloudFilesPopulationRegression.dll
```

测试创建独立的临时 CFAPI SyncRoot 和 SQLite 缓存，不登录服务器，也不注册 Explorer 导航入口。外部子进程通过 Win32 查询和文件读取触发生产环境的 `OnFetchPlaceholders`；文件水化使用固定内存内容，不覆盖真实网络下载链路。CFAPI 不向目录发送文件打开完成通知，刷新由回执完成后延迟重开下一次枚举实现。

验证 TypeId 0 的根页解析、带盘符冒号的显示名经 SQLite 读取后创建合法本地占位，以及容器到磁盘的逐层枚举。另验证 20 轮枚举与文件打开只触发一次目录填充，并覆盖原有文件内容和时间不变、首次水化、子目录、空目录、筛选查询、不存在文件查询、加入新文件后重新填充，以及再次开启已有目录的填充。测试结束后断开、反注册并清理临时目录。

刷新回归使用本机 HTTP 响应和独立进程的原生枚举：自动重开已填充目录后，未过期的 30 分钟缓存也必须重新查询云端；验证新增、未下载文件改名、空列表删除旧占位、异常响应保留旧列表，以及本地用户文件保留。目录每次完成后约 1 秒重新允许枚举，以合并同一次操作的重复查询。此测试不驱动 Explorer 的 F5 按键或右键菜单，真实 Explorer 交互仍需复验。
