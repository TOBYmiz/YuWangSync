using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace YuWangCloudSync.Tray;

/// <summary>Let XAML own the cursor for the entire hit area, including pointer capture.</summary>
internal sealed partial class ResizeGrip : Grid
{
    internal ResizeGrip(InputSystemCursorShape shape)
    {
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
