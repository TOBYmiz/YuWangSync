using System.Runtime.InteropServices;

namespace YuWangCloudSync.Tray;

/// <summary>
/// 文件夹选择对话框（IFileOpenDialog + FOS_PICKFOLDERS）。
///
/// 文件夹选择使用 COM 的 IFileDialog（Vista+ 通用对话框），避免额外 UI 框架依赖。
///
/// 纯 COM 互操作声明，不新增任何 NuGet 依赖。调用方必须在 STA 线程（WinUI DispatcherQueue 线程满足）。
/// </summary>
internal static class FolderPicker
{
    // CLSID_FileOpenDialog
    private const string ClsidFileOpenDialog = "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7";

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;

    /// <summary>SIGDN_FILESYSPATH（0x80058000，作为 int 是负数）</summary>
    private const int SigdnFilesysPath = unchecked((int)0x80058000);

    /// <summary>
    /// 弹出文件夹选择对话框。
    /// </summary>
    /// <param name="owner">父窗口句柄（用于模态归属与居中）；可为 <see cref="IntPtr.Zero"/>。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="initialPath">初始目录（不存在则忽略）。</param>
    /// <returns>选中的目录路径；用户取消或失败时返回 null。</returns>
    public static string? Pick(IntPtr owner, string title, string? initialPath)
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(ClsidFileOpenDialog));
            if (type == null) return null;
            com = Activator.CreateInstance(type);
            if (com is not IFileOpenDialog dlg) return null;

            _ = dlg.GetOptions(out var options);
            _ = dlg.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            if (!string.IsNullOrWhiteSpace(title)) _ = dlg.SetTitle(title);

            // 初始目录：需要把路径包装成 IShellItem（SHCreateItemFromParsingName）
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                var riid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");   // IID_IShellItem
                if (SHCreateItemFromParsingName(initialPath!, IntPtr.Zero, ref riid, out var item) >= 0
                    && item != null)
                {
                    _ = dlg.SetFolder(item);
                }
            }

            // 用户取消时 Show 返回 HRESULT_FROM_WIN32(ERROR_CANCELLED)（负值），按"未选择"处理
            if (dlg.Show(owner) < 0) return null;

            if (dlg.GetResult(out var result) < 0 || result == null) return null;
            if (result.GetDisplayName(SigdnFilesysPath, out var psz) < 0 || psz == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(psz); }
            finally { Marshal.FreeCoTaskMem(psz); }
        }
        catch
        {
            return null;   // 对话框不可用不应影响设置窗口其它功能
        }
        finally
        {
            if (com != null && Marshal.IsComObject(com))
            {
                try { Marshal.ReleaseComObject(com); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>
    /// 把路径解析成 IShellItem。out 参数声明成接口类型（而非 IntPtr）：
    /// 互操作层会自动把返回的 IUnknown 包成 RCW 并接管其引用计数，省掉手工 GetObjectForIUnknown/Release
    /// 的引用计数归属争议。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>
    /// IFileOpenDialog（把 IModalWindow / IFileDialog 的方法**平铺**声明在同一个接口里）。
    /// 平铺而非用接口继承：COM 互操作的 vtable 顺序必须与方法声明顺序严格一致，
    /// 平铺写法最不容易搞错；未用到的方法也要按序声明，否则后面的方法会错位。
    /// </summary>
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // ===== IModalWindow =====
        [PreserveSig] int Show(IntPtr hwndOwner);

        // ===== IFileDialog =====
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetDefaultFolder(IShellItem psi);
        [PreserveSig] int SetFolder(IShellItem psi);
        [PreserveSig] int GetFolder(out IShellItem ppsi);
        [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName(out IntPtr pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        [PreserveSig] int AddPlace(IShellItem psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr pFilter);

        // ===== IFileOpenDialog =====
        [PreserveSig] int GetResults(out IntPtr ppenum);
        [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
    }

    /// <summary>IShellItem：用于 SetFolder / GetResult 的路径传递。</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
