using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace YuWangCloudSync.Tray;

// Compiled bindings in a resource dictionary need a code-behind class.
public sealed partial class NavigationTemplates : ResourceDictionary
{
    public NavigationTemplates() => InitializeComponent();

    private void OnNavigationRowLoaded(object sender, RoutedEventArgs args)
    {
        for (var parent = VisualTreeHelper.GetParent((DependencyObject)sender); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            TreeViewItem item;
            try { item = WinRT.CastExtensions.As<TreeViewItem>(parent); }
            catch (InvalidCastException) { continue; }
            // Preserve the stock template/selection indicator, changing only the content indentation.
            // WinUI calculates Indentation from the node's current Depth (16 DIP per level in the
            // stock template). Scale that live value to 8 DIP per level, including nested units
            // and folders, rather than assigning a fixed padding to any particular node kind.
            // The binding follows expansion, recycling and changes of parent automatically.
            if (FindContentGrid(item) is { } grid)
                grid.SetBinding(Grid.PaddingProperty, new Binding
                {
                    Source = item.TreeViewItemTemplateSettings,
                    Path = new PropertyPath("Indentation"),
                    Converter = new CompactIndentationConverter(),
                    Mode = BindingMode.OneWay,
                });
            break;
        }
    }

    internal static Grid? FindContentGrid(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            try
            {
                var grid = WinRT.CastExtensions.As<Grid>(child);
                if (grid.Name == "MultiSelectGrid") return grid;
            }
            catch (InvalidCastException) { }
            if (FindContentGrid(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed partial class CompactIndentationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is Thickness indent ? new Thickness(indent.Left / 2, indent.Top, indent.Right, indent.Bottom)
                : new Thickness(0);

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
