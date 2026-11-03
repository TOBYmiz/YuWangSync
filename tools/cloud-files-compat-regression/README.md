# Cloud Files 旧系统 / NativeAOT 回归

```powershell
dotnet publish tools/cloud-files-compat-regression/CloudFilesCompatRegression.csproj -c Release -o _buildtmp/cloud-files-compat-test
./_buildtmp/cloud-files-compat-test/CloudFilesCompatRegression.exe --abi-only
```

默认发布 NativeAOT；`--abi-only` 运行 12 项结构布局、回调解析和参数边界检查，不创建同步根。

在启用 Cloud Files、可注册同步根的管理员终端中，去掉 `--abi-only` 运行完整回归。程序在临时 NTFS 目录中建立独立同步根，通过 `CfSetPinState` 模拟 Explorer 的固定 / 取消固定操作，使用生产属性监视器和更新器检查水化、内容完整性、脱水和 UNSPECIFIED。不登录服务器、不注册 Explorer 导航入口；结束时断开、反注册并清理临时目录。

此测试不能替代旧系统实机、Explorer 菜单递归处理或真实网络下载验证。已验证范围见 [兼容性说明](../../docs/cloud-files-windows-compatibility.md)。
