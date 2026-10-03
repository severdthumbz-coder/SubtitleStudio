using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

public partial class BatchTab : UserControl
{
    public BatchTab()
    {
        InitializeComponent();
    }

    // View glue only: double-clicking a finished file opens its subtitles in the editor.
    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ItemsControl.ContainerFromElement(QueueGrid, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;
        if (vm.BatchOpenResultCommand.CanExecute(row.Item))
        {
            vm.BatchOpenResultCommand.Execute(row.Item);
            e.Handled = true;
        }
    }

    // View glue only: DataGrid.SelectedItems is not bindable, so the Delete key forwards it to the command.
    private void QueueGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainViewModel vm) return;
        var selection = QueueGrid.SelectedItems;
        if (vm.BatchRemoveCommand.CanExecute(selection))
        {
            vm.BatchRemoveCommand.Execute(selection);
            e.Handled = true;
        }
    }
}
