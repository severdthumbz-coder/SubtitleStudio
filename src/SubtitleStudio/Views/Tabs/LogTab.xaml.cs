using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SubtitleStudio.Services;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

/// <summary>View glue: follow the newest entry while the list is scrolled to the bottom; Ctrl+C copies the selection.</summary>
public partial class LogTab : UserControl
{
    private MainViewModel? _vm;

    public LogTab()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.LogEntries.CollectionChanged += OnEntriesChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.LogEntries.CollectionChanged -= OnEntriesChanged;
        _vm = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var viewer = FindScrollViewer(LogList);
        // Only follow when already at (or near) the bottom, so reading older entries isn't interrupted.
        if (viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 2) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || LogList.SelectedItems.Count == 0) return;
        var lines = LogList.SelectedItems.Cast<LogEntry>().OrderBy(x => x.Time).Select(x => x.ToString());
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch (System.Runtime.InteropServices.COMException) { }
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
