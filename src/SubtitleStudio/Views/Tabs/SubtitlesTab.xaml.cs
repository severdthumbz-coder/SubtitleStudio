using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Tabs;

/// <summary>View glue only: scrolling, Delete key and Enter-to-commit. Editing logic is in the view model.</summary>
public partial class SubtitlesTab : UserControl
{
    private MainViewModel? _vm;

    public SubtitlesTab()
    {
        InitializeComponent();
    }

    // The VideoView hosts a native window; it is (re)attached whenever this tab is shown, because
    // switching tabs unloads it. The video only starts once VLC has that window's handle, otherwise
    // VLC opens its own popup window. The overlay lives in a separate transparent window owned by
    // the VideoView, so it gets the view model explicitly.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        OverlayRoot.DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;
        AttachPlayer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnViewModelChanged;
        _attachTimer?.Stop();
        _vm.DetachVideoOutput();
        VideoView.MediaPlayer = null;
        _vm = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PlayerHandle)) AttachPlayer();
    }

    private DispatcherTimer? _attachTimer;

    private void AttachPlayer()
    {
        if (_vm?.PlayerHandle is not MediaPlayer player) return;
        if (!ReferenceEquals(VideoView.MediaPlayer, player))
            VideoView.MediaPlayer = player;

        // VideoView hands its window handle to the player once its native host exists, which can be
        // a moment after Loaded. Wait for it before letting the video start (never start without it).
        _attachTimer?.Stop();
        _attachTimer = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(50) };
        _attachTimer.Tick += (_, _) =>
        {
            if (_vm is null || !ReferenceEquals(VideoView.MediaPlayer, player)) { _attachTimer?.Stop(); return; }
            if (player.Hwnd == IntPtr.Zero) return;
            _attachTimer?.Stop();
            _vm.AttachVideoOutput();
        };
        _attachTimer.Start();
    }

    private void CueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CueGrid.SelectedItem is { } item)
            CueGrid.ScrollIntoView(item);
    }

    // Delete removes the selected cues, but not while a cell is being edited (then it deletes text).
    private void CueGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.OriginalSource is TextBox || DataContext is not MainViewModel vm) return;

        var selection = CueGrid.SelectedItems;
        if (vm.DeleteCuesCommand.CanExecute(selection))
        {
            vm.DeleteCuesCommand.Execute(selection);
            e.Handled = true;
        }
    }

    // Time and frame-rate boxes update on LostFocus; Enter commits without leaving the field.
    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }
}
