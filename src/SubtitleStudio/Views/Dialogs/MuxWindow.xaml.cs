using System.ComponentModel;
using System.Windows;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Dialogs;

/// <summary>"Add subtitles to video". View glue only: the state is in MuxViewModel.</summary>
public partial class MuxWindow : Window
{
    private readonly MuxViewModel _model;

    private MuxWindow(MuxViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Loaded += async (_, _) => await model.LoadAsync();
        Closing += OnClosing;
    }

    public static void Open(Window? owner, MuxViewModel model)
    {
        var window = new MuxWindow(model);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    // "Cancel" while it runs stops the run (and leaves nothing behind); otherwise it closes the window.
    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_model.Busy) _model.CancelRun();
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_model.Busy) _model.CancelRun();
    }
}
