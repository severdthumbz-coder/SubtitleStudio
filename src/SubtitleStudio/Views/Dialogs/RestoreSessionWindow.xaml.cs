using System.Windows;
using SubtitleStudio.Services;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Views.Dialogs;

public enum RestoreChoice { StartFresh, Restore, RestoreAndRestart }

/// <summary>
/// Shown at start-up when the previous session didn't close normally: lists what will come back and,
/// if a Create video or Extract was running, offers to start it again (the default button).
/// </summary>
public partial class RestoreSessionWindow : Window
{
    private readonly bool _hasJob;

    public RestoreChoice Choice { get; private set; } = RestoreChoice.StartFresh;

    private RestoreSessionWindow(SessionSnapshot snapshot)
    {
        InitializeComponent();
        var saved = snapshot.SavedUtc == default ? string.Empty : $" Last saved {snapshot.SavedUtc.ToLocalTime():g}.";
        IntroText.Text = "Subtitle Studio didn't close properly last time (the app or the PC stopped unexpectedly)." + saved + " This can come back:";
        ItemsList.ItemsSource = snapshot.Describe();

        if (snapshot.Job is { } job)
        {
            _hasJob = true;
            string what = MainViewModel.JobName(job.Kind);
            JobText.Text = $"{what} of {Path.GetFileName(job.VideoPath)} was running when it stopped. It can't continue where it left off, "
                         + (job.Kind == SessionJobKind.CreateVideo && job.OutputPath is { } o
                             ? $"but it can start again straight away, to the same file ({Path.GetFileName(o)}; the unfinished one is replaced)."
                             : "but it can start again straight away.");
            JobText.Visibility = Visibility.Visible;
            RestoreButton.Content = $"Restore and restart {what}";
            RestoreOnlyButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Asks; closing the window counts as Start fresh.</summary>
    public static RestoreChoice Ask(Window? owner, SessionSnapshot snapshot)
    {
        var window = new RestoreSessionWindow(snapshot);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Choice;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        Choice = _hasJob ? RestoreChoice.RestoreAndRestart : RestoreChoice.Restore;
        Close();
    }

    private void OnRestoreOnly(object sender, RoutedEventArgs e)
    {
        Choice = RestoreChoice.Restore;
        Close();
    }

    private void OnStartFresh(object sender, RoutedEventArgs e)
    {
        Choice = RestoreChoice.StartFresh;
        Close();
    }
}
