using System.Windows;
using System.Windows.Input;

namespace SubtitleStudio.Infrastructure;

/// <summary>
/// Attached behaviour: routes Explorer file drops to an ICommand (parameter = string[] of paths),
/// so views need no drag-drop code-behind.
/// Usage: inf:FileDrop.Command="{Binding DropFilesCommand}". The element needs a non-null Background
/// to receive drops over empty areas.
/// </summary>
public static class FileDrop
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FileDrop), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject d) => (ICommand?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, ICommand? value) => d.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.DragEnter -= OnDragOver;
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;

        if (e.NewValue is ICommand)
        {
            element.AllowDrop = true;
            element.DragEnter += OnDragOver;
            element.DragOver += OnDragOver;
            element.Drop += OnDrop;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var command = GetCommand(d);
        if (command?.CanExecute(paths) == true)
            command.Execute(paths);

        e.Handled = true;
    }
}
