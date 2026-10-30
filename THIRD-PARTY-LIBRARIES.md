# 第三方开源库备份清单

本文记录当前项目直接声明的 NuGet 包、主程序还原出的传递包，以及安装程序图标的图形来源。版本快照更新于 2026-10-20，依据项目文件、NuGet 包元数据和主程序的当前还原结果。升级依赖后重新盘点：

```powershell
dotnet list src/YuWangCloudSync/YuWangCloudSync.csproj package --include-transitive --format json
```

## 直接 NuGet 依赖

| NuGet 包 | 版本 | 用途 | 上游 / 许可证 | 备注 |
| --- | --- | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.5.1 | WinUI 3 窗口、控件和 Windows App Runtime | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt`、`NOTICE.txt` | 标准变体将 Windows App Runtime 随应用本地部署；Required 变体使用目标机已安装版本。主程序以 NativeAOT self-contained 发布。 |
| Vanara.PInvoke.CldApi | 5.0.7 | Cloud Files API P/Invoke | [dahall/Vanara](https://github.com/dahall/Vanara) / MIT | Core 与主程序都引用。 |
| Microsoft.Data.Sqlite | 8.0.8 | SQLite 元数据缓存 | [dotnet/efcore](https://github.com/dotnet/efcore) / MIT | 依赖 Microsoft.Data.Sqlite.Core 和 SQLitePCLRaw，见传递依赖。 |
| Serilog | 4.0.1 | 结构化日志 | [serilog/serilog](https://github.com/serilog/serilog) / Apache-2.0 |  |
| Serilog.Sinks.Console | 6.0.0 | 控制台日志接收器 | [serilog/serilog-sinks-console](https://github.com/serilog/serilog-sinks-console) / Apache-2.0 |  |
| Serilog.Sinks.File | 6.0.0 | 文件日志接收器 | [serilog/serilog-sinks-file](https://github.com/serilog/serilog-sinks-file) / Apache-2.0 |  |
| H.NotifyIcon | 2.3.2 | Win32 托盘图标和 PopupMenu | [HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) / MIT | 主程序使用。 |
| H.GeneratedIcons.System.Drawing | 2.3.2 | H.NotifyIcon 的图标生成依赖 | [HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) / MIT | 项目另有 `ExcludeAssets="all"`、`PrivateAssets="all"` 的显式引用；但 H.NotifyIcon 也依赖此包，当前还原图仍包含该包及其依赖。详见下方说明。 |

## 已还原：传递 NuGet 包

以下为 `dotnet list ... --include-transitive` 当前列出的全部 24 个传递包。Windows App SDK 子包的许可证和 NOTICE 文件随各自的 NuGet 包保存；NuGet 元数据指向 Microsoft Windows SDK 许可条款的构建工具单独列出。

### SQLite

| NuGet 包 | 版本 | 上游 / 许可证 | 来源 |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite.Core | 8.0.8 | [dotnet/efcore](https://github.com/dotnet/efcore) / MIT | Microsoft.Data.Sqlite |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.6 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) / Apache-2.0 | Microsoft.Data.Sqlite |
| SQLitePCLRaw.core | 2.1.6 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) / Apache-2.0 | SQLitePCLRaw bundle |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.6 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) / Apache-2.0；包内 SQLite 原生代码为 Public Domain | SQLitePCLRaw bundle |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.6 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) / Apache-2.0 | SQLitePCLRaw bundle |

### Windows App SDK 与 WinUI

| NuGet 包 | 版本 | 上游 / 许可证 | 来源 |
| --- | --- | --- | --- |
| Microsoft.Web.WebView2 | 1.0.3719.77 | [Microsoft WebView2](https://aka.ms/webview) / NuGet 包内 `LICENSE.txt`、`NOTICE.txt` | Microsoft.WindowsAppSDK.WinUI |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK.ML |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt`、`NOTICE.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt`、`NOTICE.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt` | Microsoft.WindowsAppSDK |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | [Microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) / NuGet 包内 `license.txt`、`NOTICE.txt` | Microsoft.WindowsAppSDK |
| System.Numerics.Tensors | 9.0.0 | [dotnet/runtime](https://github.com/dotnet/runtime) / MIT | Microsoft.WindowsAppSDK.ML |

### 托盘与图标依赖

| NuGet 包 | 版本 | 上游 / 许可证 | 来源 |
| --- | --- | --- | --- |
| System.Drawing.Common | 9.0.1 | [dotnet/winforms](https://github.com/dotnet/winforms) / MIT | H.GeneratedIcons.System.Drawing |
| Microsoft.Win32.SystemEvents | 9.0.1 | [dotnet/runtime](https://github.com/dotnet/runtime) / MIT | System.Drawing.Common |

项目中的 `H.GeneratedIcons.System.Drawing` 显式引用使用 `ExcludeAssets="all"`，但 H.NotifyIcon 自身也声明了对此包的依赖。当前 `project.assets.json` 仍解析出 `System.Drawing.Common` 和 `Microsoft.Win32.SystemEvents` 的编译、运行资产。因此不能据显式引用上的排除设置断言这些传递资产已从依赖图或最终发布物完全移除；是否进入发行包应以发布输出为准。

### Vanara

| NuGet 包 | 版本 | 上游 / 许可证 | 来源 |
| --- | --- | --- | --- |
| Vanara.Core | 5.0.7 | [dahall/Vanara](https://github.com/dahall/Vanara) / MIT | Vanara.PInvoke.CldApi |
| Vanara.PInvoke.Kernel32 | 5.0.7 | [dahall/Vanara](https://github.com/dahall/Vanara) / MIT | Vanara.PInvoke.CldApi |

### 构建工具传递包

| NuGet 包 | 版本 | 许可信息 | 来源 / 用途 |
| --- | --- | --- | --- |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | [Microsoft Windows SDK 许可条款](https://aka.ms/WinSDKLicenseURL) | Microsoft.WindowsAppSDK.Base；构建工具 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | NuGet 元数据声明包内 `sdk_license.txt`，项目页指向 [Microsoft Windows SDK](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/) | Microsoft.WindowsAppSDK.Base；MSIX 构建工具 |

## 随应用分发的 .NET Runtime

主程序以 self-contained NativeAOT 发布。构建脚本从已还原的 `Microsoft.NETCore.App.Runtime.win-x64` runtime pack 中，将 `LICENSE.TXT` 和 `THIRD-PARTY-NOTICES.TXT` 复制到载荷的 `licenses\dotnet-runtime` 目录；Runtime 包许可证为 MIT，版本由 .NET 10 SDK 选择。

这两个包是还原图中的构建工具，不是应用开源库；发行时是否需要随包分发，按实际发布内容和各自许可条款判断。

## 工具项目

安装和卸载由 `tools/nsis/Setup.nsi` 直接实现，不再包含基于 .NET 的安装器项目或其旧完成回归脚本。主程序与 Core 项目的直接包引用已在上文列出。旧的 `tools/spike-winui-host` 不在当前工作区中，故不作为当前工具依赖记录。

安装器构建还需要 NSIS 3.x 的 `makensis.exe`。`tools/nsis/Setup.nsi` 的构建组织参考 [EvilGiantMa/nsis-build-template](https://github.com/EvilGiantMa/nsis-build-template)；界面使用 NSIS 3 自带的 Modern UI 2、nsDialogs 和 `WriteUninstaller`，不复制模板附带的演示应用、皮肤、curl、7-Zip 或插件资源。NSIS 是构建工具，不随应用分发。

## 已使用：安装程序图标

| 来源 | 用途 | 上游 / 许可证 | 项目文件 |
| --- | --- | --- | --- |
| [mui/material-ui](https://github.com/mui/material-ui) | Material UI / Material Icons 图标来源 | Material UI 项目代码 / MIT | 图形资源来源，不是 .NET 安装器运行依赖。 |
| [mui/material-ui-icons](https://github.com/mui/material-ui/tree/master/packages/mui-icons-material)（`@mui/icons-material`） | `DownloadRoundedIcon`（`import DownloadRoundedIcon from '@mui/icons-material/DownloadRounded'`） | 包代码 / MIT；Material Icons 图形资源 / Apache-2.0 | 成品为 `src/YuWangCloudSync/Assets/yw_setup.ico`，多尺寸源图为同目录 `yw_setup_*.png`。 |

NSIS 安装器使用 `yw_setup.ico`；NSIS 自动生成的卸载器单独使用 `yw_uninstall.ico`。NSIS 图标通过 `MUI_ICON` / `MUI_UNICON` 在载入 Modern UI 2 前设置，避免页面宏恢复模板默认图标。Material UI 及其图标包仅作为 `yw_setup.ico` 的图形来源，不是安装器的 NuGet/npm 运行依赖。当前仓库包含 ICO 和 PNG 成品图，但没有图标 SVG、npm manifest 或锁文件；若需复刻 `yw_setup.ico` 原始图形，应另存所用 SVG/路径、图标名称、npm 包版本及许可文件。该图标名为 `DownloadRounded`。

## 备份建议

- 将本清单与项目源码、`*.csproj`、`packages.lock.json`（如后续启用锁文件）一并备份。
- 离线还原时，归档 NuGet 全局缓存中的对应 `.nupkg`，或建立内部 NuGet 源；只备份上游 URL 无法保证未来仍能取得完全相同的包。
- 发行包若分发第三方二进制，应同时保留各 NuGet 包内的许可证/NOTICE 文件，并按其许可要求随软件提供。
- 只记录本项目当前实际还原的传递包；SDK、Windows Desktop Framework、Windows SDK/系统 DLL 属于构建或平台组件，不按开源库统计。若 Windows SDK 工具通过 NuGet 传递还原，则如上单独注明。
