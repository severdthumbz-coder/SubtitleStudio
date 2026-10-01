using System.Windows;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Views.Dialogs;

/// <summary>"Which engine?": lists the engines that can do a job, first one selected.</summary>
public partial class EngineChoiceWindow : Window
{
    public sealed class Option : ObservableObject
    {
        private bool _isSelected;

        public Option(EngineChoice choice, bool selected)
        {
            Choice = choice;
            _isSelected = selected;
        }

        public EngineChoice Choice { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    private readonly List<Option> _options;
    private bool _accepted;

    private EngineChoiceWindow(string task, IReadOnlyList<EngineChoice> choices)
    {
        InitializeComponent();
        Title = $"{task}: which engine?";
        HeadingText.Text = $"{task}: which engine?";
        _options = choices.Select((c, i) => new Option(c, i == 0)).ToList();
        ChoicesList.ItemsSource = _options;
    }

    public static EngineChoice? Ask(Window? owner, string task, IReadOnlyList<EngineChoice> choices, out bool remember)
    {
        var window = new EngineChoiceWindow(task, choices);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        remember = window._accepted && window.RememberBox.IsChecked == true;
        return window._accepted ? window._options.FirstOrDefault(o => o.IsSelected)?.Choice : null;
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
