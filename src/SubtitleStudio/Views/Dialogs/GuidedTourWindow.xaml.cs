using System.Collections.Generic;
using System.Windows;

namespace SubtitleStudio.Views.Dialogs;

/// <summary>
/// The guided tour as a single themed card that navigates between its own steps — Back, Next, and Skip, with the
/// last step's button reading "Finish". One window for the whole tour (not a message box per step), so it looks and
/// behaves like Video Metadata Editor's tour. Shown by GuidedTourService; replayable from the Help tab at any time.
/// </summary>
public partial class GuidedTourWindow : Window
{
    public readonly record struct TourStep(string Title, string Body);

    private readonly IReadOnlyList<TourStep> _steps;
    private int _index;

    private GuidedTourWindow(IReadOnlyList<TourStep> steps)
    {
        InitializeComponent();
        _steps = steps;
        Show(0);
    }

    /// <summary>Walks the tour in a modal window; returns when the user finishes or skips.</summary>
    public static void Run(Window? owner, IReadOnlyList<TourStep> steps)
    {
        if (steps.Count == 0) return;
        var window = new GuidedTourWindow(steps);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

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

    // The window has no title bar (WindowStyle="None"), so let it be dragged by its background.
    private void OnDragBackground(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
