using System.Windows;
using System.Windows.Controls;
using SubtitleStudio.Models;
using System.Windows.Input;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

public partial class FilesTab : UserControl
{
    public FilesTab()
    {
        InitializeComponent();
    }

    // View glue only: double-clicking a subtitle row opens it in the editor.
    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ItemsControl.ContainerFromElement(FilesGrid, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;
        if (row.Item is MediaItem { Kind: MediaKind.Subtitle } item && vm.OpenInEditorCommand.CanExecute(item))
        {
            vm.OpenInEditorCommand.Execute(item);
            e.Handled = true;
        }
    }

    // View glue only: DataGrid.SelectedItems is not bindable, so the Delete key forwards it to the command.
    private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainViewModel vm) return;

        var selection = FilesGrid.SelectedItems;
        if (vm.RemoveItemsCommand.CanExecute(selection))
        {
            vm.RemoveItemsCommand.Execute(selection);
            e.Handled = true;
        }
    }
}
