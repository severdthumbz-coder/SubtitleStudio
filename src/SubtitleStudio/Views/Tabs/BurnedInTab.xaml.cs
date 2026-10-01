using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

/// <summary>
/// View glue for the before / after players. Each VideoView hosts a native window; a player may only
/// start once it has that window's handle (otherwise VLC opens its own popup), so the view waits for
/// the handle and then tells the view model the surface is attached. Hidden or unloaded: detached.
/// </summary>
public partial class BurnedInTab : UserControl
{
    private MainViewModel? _vm;
    private DispatcherTimer? _attachTimer;

    public BurnedInTab()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        if (_vm.ShowCompare) AttachPlayers();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnViewModelChanged;
        DetachPlayers(stopPlayback: true);
        _vm = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowCompare) || _vm is null) return;
        if (_vm.ShowCompare) AttachPlayers();
        else DetachPlayers(stopPlayback: false); // the view model already stopped them
    }

    private void AttachPlayers()
    {
        if (_vm?.Compare is not { } compare) return;
        if (compare.BeforeHandle is not MediaPlayer before || compare.AfterHandle is not MediaPlayer after) return;
        if (!ReferenceEquals(BeforeView.MediaPlayer, before)) BeforeView.MediaPlayer = before;
        if (!ReferenceEquals(AfterView.MediaPlayer, after)) AfterView.MediaPlayer = after;

        bool beforeDone = false, afterDone = false;
        _attachTimer?.Stop();
        _attachTimer = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(50) };
        _attachTimer.Tick += (_, _) =>
        {
            if (_vm?.Compare is null || !_vm.ShowCompare) { _attachTimer?.Stop(); return; }
            if (!beforeDone && before.Hwnd != IntPtr.Zero) { beforeDone = true; compare.AttachBefore(); }
            if (!afterDone && after.Hwnd != IntPtr.Zero) { afterDone = true; compare.AttachAfter(); }
            if (beforeDone && afterDone) _attachTimer?.Stop();
        };
        _attachTimer.Start();
    }

    private void DetachPlayers(bool stopPlayback)
    {
        _attachTimer?.Stop();
        if (stopPlayback) _vm?.Compare?.Detach();
        DetachView(BeforeView);
        DetachView(AfterView);
    }

    private static void DetachView(VideoView view)
    {
        if (view.MediaPlayer is not null) view.MediaPlayer = null;
    }
}
