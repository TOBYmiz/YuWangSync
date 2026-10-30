namespace YuWangCloudSync.Tray;

/// <summary>Desktop folder picker bound to the owning WinUI window.</summary>
internal static class FolderPicker
{
    public static async Task<string?> PickAsync(IntPtr owner, string title, string? initialPath)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner))
            {
                Title = title,
                CommitButtonText = "选择文件夹",
            };
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                picker.SuggestedFolder = initialPath;

            var result = await picker.PickSingleFolderAsync();
            return result?.Path;
        }
        catch (Exception error)
        {
            try { Serilog.Log.Warning(error, "打开文件夹选择器失败"); } catch { }
            return null;
        }
    }
}
