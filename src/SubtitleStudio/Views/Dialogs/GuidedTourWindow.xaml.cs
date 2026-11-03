using System;
using System.Collections.Generic;
using System.Windows;

namespace SubtitleStudio.Views.Dialogs;

/// <summary>
/// The guided tour as a single themed card over a translucent backdrop that fills the owner window to focus attention.
/// The card navigates between its own steps — Back, Next, and Skip, with the last step's button reading "Finish" — so
/// it looks and behaves like Video Metadata Editor's tour (one window for the whole tour, not a message box per step).
/// Each step may name a tab to bring forward behind the backdrop, via <see cref="TourStep.Tab"/> and the onStep callback.
/// Shown by GuidedTourService; replayable from the Help tab at any time.
/// </summary>
public partial class GuidedTourWindow : Window
{
    /// <summary>A step. <paramref name="Tab"/> is the main-tab index to select while this step shows, or null to leave it.</summary>
    public readonly record struct TourStep(string Title, string Body, int? Tab = null);

    private readonly IReadOnlyList<TourStep> _steps;
    private readonly Action<int?>? _onStep;
    private readonly Window? _owner;
    private int _index;

    private GuidedTourWindow(Window? owner, IReadOnlyList<TourStep> steps, Action<int?>? onStep)
    {
        InitializeComponent();
        _steps = steps;
        _onStep = onStep;
        _owner = owner;
        Show(0);
    }

    /// <summary>
    /// Walks the tour in a modal window over a dim backdrop; returns when the user finishes or skips.
    /// <paramref name="onStep"/> is called with each step's tab index (or null) so the caller can bring that tab forward.
    /// </summary>
    public static void Run(Window? owner, IReadOnlyList<TourStep> steps, Action<int?>? onStep = null)
    {
        if (steps.Count == 0) return;
        var window = new GuidedTourWindow(owner, steps, onStep);
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
            window.CoverOwner();
            owner.LocationChanged += window.OnOwnerChanged;
            owner.SizeChanged += window.OnOwnerChanged;
            window.Closed += (_, _) =>
            {
                owner.LocationChanged -= window.OnOwnerChanged;
                owner.SizeChanged -= window.OnOwnerChanged;
            };
        }
        else
        {
            // No owner to cover: fall back to a centred card on a full-screen dim layer.
            window.WindowState = WindowState.Maximized;
        }
        window.ShowDialog();
    }

    // Keep the backdrop exactly over the owner window as it moves or resizes.
    private void CoverOwner()
    {
        if (_owner is null) return;
        Left = _owner.Left;
        Top = _owner.Top;
        Width = _owner.ActualWidth > 0 ? _owner.ActualWidth : _owner.Width;
        Height = _owner.ActualHeight > 0 ? _owner.ActualHeight : _owner.Height;
    }

    private void OnOwnerChanged(object? sender, EventArgs e) => CoverOwner();

    private void Show(int index)
    {
        _index = index;
        var step = _steps[index];
        bool last = index == _steps.Count - 1;

        StepLabel.Text = $"Step {index + 1} of {_steps.Count}";
        TitleText.Text = step.Title;
        BodyText.Text = step.Body;

        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = last ? "Finish ✓" : "Next →";

        // Bring this step's tab forward behind the backdrop (if it names one).
        _onStep?.Invoke(step.Tab);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index >= _steps.Count - 1) { Close(); return; }
        Show(_index + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_index > 0) Show(_index - 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    // Clicking the dim backdrop (outside the card) does nothing — the card stays put, so a stray click can't dismiss it.
    private void OnBackdropClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    // Swallow clicks on the card so they don't bubble to the backdrop handler.
    private void OnCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;
}
