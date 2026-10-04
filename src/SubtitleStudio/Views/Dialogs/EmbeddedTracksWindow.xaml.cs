using System.ComponentModel;
using System.Windows;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Dialogs;

/// <summary>"Subtitles inside this video". View glue only: the state is in EmbeddedTracksViewModel.</summary>
public partial class EmbeddedTracksWindow : Window
{
    private readonly EmbeddedTracksViewModel _model;

    private EmbeddedTracksWindow(EmbeddedTracksViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.CloseRequested += Close;
        Loaded += async (_, _) => await model.LoadAsync();
        Closing += OnClosing;
    }

    public static void Open(Window? owner, EmbeddedTracksViewModel model)
    {
        var window = new EmbeddedTracksWindow(model);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _model.CloseRequested -= Close;
        if (_model.Busy) _model.CancelRun();
    }
}
