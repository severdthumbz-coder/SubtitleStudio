using System.ComponentModel;
using System.Windows;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio;

/// <summary>
/// Shell window. Code-behind is limited to window-level concerns: size restore/remember,
/// native title-bar theming and bring-to-front on hand-off.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, ThemeService theme, WindowPlacement? placement)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        ApplyPlacement(placement);
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        Closing += OnClosing;
    }

    /// <summary>Restore if minimised and bring to the foreground (used for pipe hand-off).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();

        // Topmost flip is the reliable way to raise a window that Windows would otherwise just flash.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ApplyPlacement(WindowPlacement? placement)
    {
        if (placement is null) return;

        var area = SystemParameters.WorkArea;
        if (placement.Width >= MinWidth && placement.Height >= MinHeight)
        {
            Width = Math.Min(placement.Width, area.Width);
            Height = Math.Min(placement.Height, area.Height);
        }
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.ConfirmCloseEditor())
        {
            e.Cancel = true;
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0) return;
        _viewModel.RememberWindow(bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }
}
