using System.Windows;
using System.Windows.Controls;

namespace VMCreate.Manager
{
    /// <summary>
    /// WPF-UI's row template puts the selected row's accent pill (3 px) in its own Auto column next to
    /// the cells. Star-sized columns already fill the viewport, so a selected row is 3 px too wide and
    /// the grid shows a horizontal scrollbar. A negative margin lays the pill over the first cell instead.
    /// </summary>
    public static class DataGridRowFix
    {
        // Loaded never reaches class handlers; SizeChanged does and comes with a row's first layout.
        public static void Register()
            => EventManager.RegisterClassHandler(typeof(DataGridRow), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnRowSizeChanged));

        private static void OnRowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is DataGridRow row && row.Template?.FindName("ActiveRectangle", row) is FrameworkElement { Margin.Right: 0 } pill)
                pill.Margin = new Thickness(0, 0, -pill.Width, 0);
        }
    }
}
