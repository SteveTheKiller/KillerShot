using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace KillerShot.Files;

public partial class FilesView
{
    private void DetailsHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DetailsHeader, Placement = PlacementMode.Bottom };
        foreach (var (key, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("Str_Col_Size", () => ViewState.SizeVisible, value => ViewState.SizeVisible = value),
            ("Str_Col_Modified", () => ViewState.ModifiedVisible, value => ViewState.ModifiedVisible = value),
            ("Str_Col_Type", () => ViewState.TypeVisible, value => ViewState.TypeVisible = value),
            ("Str_Col_Created", () => ViewState.CreatedVisible, value => ViewState.CreatedVisible = value)
        })
        {
            var item = new MenuItem { IsCheckable = true, IsChecked = get() };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => set(item.IsChecked);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }
    private int _resizeColumn;
    private double _resizeStartX, _resizeStartWidth;
    private void ColGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip || !int.TryParse(Convert.ToString(grip.Tag), out int column)) return;
        if (e.ClickCount == 2) ViewState.SetColumnWidth(column, ResultsViewState.DefaultColumnWidth(column));
        else
        {
            _resizeColumn = column;
            _resizeStartX = e.GetPosition(this).X;
            _resizeStartWidth = ViewState.GetColumnWidth(column);
            grip.CaptureMouse();
        }
        e.Handled = true;
    }
    private void ColGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeColumn == 0 || sender is not FrameworkElement { IsMouseCaptured: true }) return;
        ViewState.SetColumnWidth(_resizeColumn, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
        e.Handled = true;
    }
    private void ColGrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _resizeColumn = 0;
        if (sender is FrameworkElement grip) grip.ReleaseMouseCapture();
        e.Handled = true;
    }
    private void Header_Click(object sender, RoutedEventArgs e)
    {
        int column = Grid.GetColumn((UIElement)sender);
        SetSort(column, Navigation.SortIndex == column ? !Navigation.SortAsc : true);
    }
}
