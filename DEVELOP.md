# 开发信息

## 构建

需要 .NET 10 SDK、NSIS 3.x、Visual Studio 2022 的 C++ x64 工具、ATL v143 和 Windows SDK。输出名为 `YuWangCloudSync.exe`或 `YuWangCloudSync_WinAppSDK_Required.exe`，采用自包含 NativeAOT 发布，组件版本号定义在 `src/Directory.Build.props` 。

```powershell
# 主版本
powershell -ExecutionPolicy Bypass -File .\tools\Build-Setup.ps1

# 无 Windows App SDK 子集
powershell -ExecutionPolicy Bypass -File .\tools\Build-Setup-WinAppSDKRequired.ps1

# 一并构建两版本
powershell -ExecutionPolicy Bypass -File .\tools\Build-Setup-All.ps1
```

## 命令行参数

| 参数                                   | 作用                                                                                             |
| -------------------------------------- | ------------------------------------------------------------------------------------------------ |
| （无）                                 | GUI 模式                                                                                         |
| `--console`                            | 控制台模式：控制台呈现日志与验证码输入                                                           |
| `--about`| 启动时开启关于页，用于应用内更新后|
| `--quiet`                              | 静默模式：无 UI 、控制台，仅文件日志；无有效会话直接退出                                         |
| `--open-folder` / `-open-folder`       | 启动同步并打开同步目录；同步已运行时仅打开目录                                                   |
| `--stop`                               | 请求运行中的同步进程退出并等待结束，无进程时直接返回                                             |
| `--dehydrate`                          | 清除本地已缓存内容，释放磁盘空间                                                                 |
| `--unregister`                         | 清理同步文件夹内容并取消 SyncRoot 注册                                                           |
| `--unregister --keep-downloaded-files` | 清理占位文件并保留已缓存的文件                                                                   |
| `--ui-selftest`                        | 检查 WinUI 窗口、STA/Dispatcher、托盘、设置验证、验证码提交/取消及重复打开，通过 JSON 报告后退出 |
| `--ui-selftest-shot`                   | 把设置和验证码窗口渲染成 PNG 保存到自检临时目录                                                  |
| `--ui-selftest-hold=N`                 | 窗口多留 N 秒供人工查看                                                                          |
| `--probe[=<路径>]`                     | 探测路径能否作为 SyncRoot，无路径则探当前挂载点                                                  |

## 设置

设置与验证码等窗口使用 WinUI 3。托盘与 WinUI 窗口共用 WinUI 应用的 STA 线程；同步、CFAPI 生命周期和命令行流程仍在线程池 MTA 执行。

### 挂载位置探测

热切换 SyncRoot 之前先进行探测：

| 探测项                                                        | 不通过时                 |
| ------------------------------------------------------------- | ------------------------ |
| 绝对路径 / 非 UNC                                             | 失败                     |
| 卷文件系统 + `FILE_SUPPORTS_REPARSE_POINTS`（占位是重解析点） | 失败                     |
| 已是其他提供者的 SyncRoot / 嵌在其他 SyncRoot 内              | 失败                     |
| 敏感目录（匹配环境变量）                                      | 失败                     |
| 盘符根目录（`D:\`）                                           | 不可用（见下）           |
| 目录不存在                                                    | 提示创建，无法创建时失败 |
| 临时文件测试                                                  | 删不掉时失败             |
| 剩余空间 < 1GB、可移动/网络盘                                 | 警告                     |

盘符根目录：设置窗口作普通非空目录处理，询问是否新建子目录 `YuWang`，于是 `D:\` → `D:\YuWang`。命令行 `--probe=D:\` 无交互，故"不可用"。

两次探测：① 设置窗口浏览目录后，结果呈现在状态行；
② 确定，热切换前探测。

### 系统主题与自检

WinUI 窗口跟随 Windows 应用模式的浅色 / 深色设置。自检入口为 `--ui-selftest`，不连接服务器、同步目录，以临时目录中的配置 JSON 和验证码图片检查 STA/Dispatcher、Win32 托盘、设置窗口重复打开与字段验证、验证码提交和取消，不验证导航窗格图标/配置可用性。结果保存在 `%TEMP%\YuWangCloudSync-WinUiSelfTest-*\ui-selftest-results.json`。

```powershell
YuWangCloudSync.exe --ui-selftest
YuWangCloudSync.exe --ui-selftest --ui-selftest-shot
YuWangCloudSync.exe --ui-selftest --ui-selftest-hold=10
```

设置应用逻辑位于 MTA 线程。

## 配置

模板见 [`appsettings.example.json`](./src/YuWangCloudSync/appsettings.example.json)，关键字段：

| 字段                                        | 说明                                                                             |
| ------------------------------------------- | -------------------------------------------------------------------------------- |
| `Server` / `Username` / `ProtectedPassword` | 育网云地址与账密                                                                 |
| `SyncRootPath`                              | 挂载目录（如 `D:\YuWang`）                                                       |
| `DatabasePath`                              | SQLite 元数据缓存路径，默认用当前用户 `%LOCALAPPDATA%\YuWangCloudSync`           |
| `RootContainerTypeId`                       | 目录树根容器 TypeId（通常为 100）                                                |
| `RootDiskId`                                | 兼容回落的磁盘 id                                                                |
| `PersistSyncRoot`                           | 常驻注册：启用时退出保留同步目录，关闭时退出清空内容                             |
| `FolderCacheMinutes`                        | 目录缓存与自动刷新统一周期（1—120 分钟），到期后重进目录联网刷新，激活后台预拉取 |

## 目录结构（节选）

```
src/YuWangCloudSync/
├─ Program.cs                        启动序列 / 命令行分派 / 优雅退出
├─ Crypto/RsaUtils.cs                ohdave RSA 复刻（无填充、小端分块、块间空格）
├─ Net/YuWangClient.cs               登录 / 列目录 / 磁盘树抓取解析 / 直链下载
├─ Net/HtmlParser.cs                 列表页解析
├─ Models/FileIdentity.cs            29 字节占位身份
├─ Models/DiskTree.cs                磁盘树节点模型
├─ Storage/SqliteCache.cs            folders/files/disk_tree 三表缓存
├─ CloudFiles/CloudFilesBridge.cs    SyncRoot 注册 / 占位生成 / cfapi 回调
├─ CloudFiles/PlaceholderUpdater.cs  占位安全删除 / 大小修正
├─ CloudFiles/PlaceholderFixer.cs
├─ App.xaml(.cs)                     WinUI 3 应用与 STA 入口
└─ Tray/                             Win32 托盘宿主 + WinUI 设置/验证码窗口
```

## 技术栈

C# .NET 10 x64（net10.0-windows10.0.19041.0，unpackaged WinUI 3 / Win32）· Windows App SDK 2.5.1（Windows App Runtime 主版本随载荷部署子集）· Vanara.PInvoke.CldApi 5.0.7 · HttpClient · Microsoft.Data.Sqlite · Serilog · H.NotifyIcon

## 已知约束

- 网页下载方法无文件大小、无断点续传，使用直链下载绕开

