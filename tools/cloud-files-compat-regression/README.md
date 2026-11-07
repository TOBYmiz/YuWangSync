# Cloud Files 旧系统 / NativeAOT 回归

```powershell
dotnet publish tools/cloud-files-compat-regression/CloudFilesCompatRegression.csproj -c Release -o _buildtmp/cloud-files-compat-test
./_buildtmp/cloud-files-compat-test/CloudFilesCompatRegression.exe --abi-only
```

默认发布 NativeAOT；`--abi-only` 运行 14 项结构布局、回调解析和参数边界检查（含通知标志的安全解析），不创建同步根。

在启用 Cloud Files、可注册同步根的管理员终端中，去掉 `--abi-only` 运行完整回归。程序在临时 NTFS 目录中建立独立同步根，通过 `CfSetPinState` 模拟 Explorer 的固定 / 取消固定操作，使用生产属性监视器和更新器检查水化、内容完整性、脱水和 UNSPECIFIED。不登录服务器、不注册 Explorer 导航入口；结束时断开、反注册并清理临时目录。

此测试不能替代旧系统实机、Explorer 菜单递归处理或真实网络下载验证。已验证范围见 [兼容性说明](../../docs/cloud-files-windows-compatibility.md)。

上传进度的 NativeAOT 检查：

```powershell
./_buildtmp/cloud-files-compat-test/CloudFilesCompatRegression.exe --shell-progress
```

通过实际原生内存属性存储验证 UInt64 `(completed,total)`、大于 4 GiB 的计数和传输/失败/暂停/完成状态，并在独立临时普通文件上检查 Shell 属性提交及内容不变。文件属性访问若被系统以 `E_ACCESSDENIED` 拒绝，输出 `BLOCKED`、返回 2；这不算文件提交或可见 UI 通过。此模式不注册同步根，不更改搜索索引规则，不依赖普通文件回读来冒充视觉验证。

若出现 `E_ACCESSDENIED`，先用 `icacls` 检查测试可执行文件的完整性标记。本工作区的测试输出曾继承 `Low Mandatory Level`；确认属于此情况时，仅修正测试输出目录，再启动新进程复测：

```powershell
icacls _buildtmp/cloud-files-compat-test
icacls _buildtmp/cloud-files-compat-test/CloudFilesCompatRegression.exe
icacls _buildtmp/cloud-files-compat-test /setintegritylevel '(OI)(CI)M' /T
./_buildtmp/cloud-files-compat-test/CloudFilesCompatRegression.exe --shell-progress
```

2026-11-05：同一 NativeAOT 二进制在修正测试产物标记后，上述 ABI、文件进度和终态提交、内容不变检查全部通过，退出码为 0；安装版 Explorer 的可见显示仍需单独验证。
