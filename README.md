<div align="center">
  <img src="./src/YuWangCloudSync/Assets/yw_tray_256.png" alt="YuWangCloudSync" width="120">
  <h1>YuWangCloudSync</h1>
</div>

> 把局域网"育网云"资源库挂载进 Windows 资源管理器的同步引擎。

> 使用本应用，需要安装 .NET 10 Runtime x64。未安装时，启动安装程序会提示安装。

>开发信息见 [DEVELOP.md](DEVELOP.md)。
## 功能特性

- **多盘映射**：动态拉取账号下云端磁盘

- **直链下载**：使用直链，文件大小、下载进度精确
- **常驻体验**：复用登录会话，降低验证频率
- **现代界面**：使用 WinUI3 现代化界面
- **动态权限**：逐目录只读识别账户权限
- **实时进度**：适配 Windows 原生进度显示
- **账户存储**：快速添加、切换、删除账户
- **隐私保护**：手机号账户名隐藏中间四位
## 快速开始

要求：Windows 10 19041+ x64，安装 .NET 10 Runtime x64。主版本内置来自 Windows App Runtime 的必要文件。

前往 [Release 页面](https://github.com/TOBYmiz/YuWangSync/releases/latest)，下载最新版本安装包。如果已安装 Windows App Runtime 2.5.1+（对应 Windows App SDK 2.5.1+），可以使用 `WinAppSDK_Required` 版本。安装后资源管理器导航窗格出现"育网云"，双击占位文件自动下载并打开。

安装向导配置账号并加密保存密码。配置、会话、数据库和日志位于 `%LOCALAPPDATA%\YuWangCloudSync`。

`YuWangCloudSyncShellExt.dll` 提供同步状态角标，状态沿用 CFAPI / Explorer 原生显示。侧边栏入口由现有 SyncRoot 及 SyncRootManager 元数据提供，不支持固定 / 取消固定。

WinUI 3 主程序使用 `App.xaml` 创建 STA/DispatcherQueue UI 线程，并把原同步流程交给 MTA 后台运行。

> 只读权限下，不应移动同步目录内的文件。如果要另存文件，且同盘拖拽显示“移动”，请按住 `Ctrl` 拖拽以复制文件，或使用对应应用的“另存为”。

## 托盘菜单

右键托盘图标：

| 菜单项 | 行为 |
|--------|------|
| 打开同步文件夹 | 打开 SyncRoot |
| 打开日志目录 | 打开运行日志目录 |
| 清理本地缓存（脱水） | 还原占位，释放空间 |
| **设置** | 打开设置窗口，设置热生效 |
| 退出 | 退出 |

## 设置
| 分组 | 生效方式 |
|------|----------|
| 账户 | 热切换，会清空同步目录 |
| 挂载位置 | 热切换，会清空同步目录，中途失败回退旧位置 |
| 缓存周期 | 热切换 |
| 目录常驻 | 下次启动时生效 |
| TypeId、Provider 名称 | 热切换 |
| 服务器 | 只读展示，可编辑配置文件后重启 |

## 许可与安全

本研究性项目的代码采用 MIT 许可证，具体条款见 [LICENSE](LICENSE)。育网云系统和官方客户端的版权归原作者所有。

所有隐私数据保存在本地，程序仅在必要时连接你配置的服务器，以及向 GitHub 检查更新。安装时需要管理员权限，会注册资源管理器集成，某些敏感操作可能会被安全软件拦截。

