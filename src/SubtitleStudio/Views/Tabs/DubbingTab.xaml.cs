using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

/// <summary>
/// View glue for the dub preview player. The VideoView hosts a native window; the player may only start
/// once it has that window's handle (otherwise VLC opens its own popup), so the view waits for the handle
/// and then tells the player the surface is attached. Hidden or unloaded: detached.
/// </summary>
public partial class DubbingTab : UserControl
{
    private MainViewModel? _vm;
    private DispatcherTimer? _attachTimer;

    public DubbingTab()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        if (_vm.HasDubPreview) AttachPlayer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnViewModelChanged;
        DetachPlayer();
        _vm = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.HasDubPreview) || _vm is null) return;
        if (_vm.HasDubPreview) AttachPlayer();
        else DetachPlayer();
    }

    private void AttachPlayer()
    {
        if (_vm?.DubPlayer is not { } clip || clip.Handle is not MediaPlayer player) return;
        if (!ReferenceEquals(PreviewView.MediaPlayer, player)) PreviewView.MediaPlayer = player;
        _attachTimer?.Stop();
        _attachTimer = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(50) };
        _attachTimer.Tick += (_, _) =>
        {
            if (_vm?.DubPlayer is null || !_vm.HasDubPreview) { _attachTimer?.Stop(); return; }
            if (player.Hwnd == IntPtr.Zero) return;
            _attachTimer?.Stop();
            clip.Attach();
        };
        _attachTimer.Start();
    }

    private void DetachPlayer()
    {
        _attachTimer?.Stop();
        _vm?.DubPlayer?.Detach();
        if (PreviewView.MediaPlayer is not null) PreviewView.MediaPlayer = null;
    }
}
