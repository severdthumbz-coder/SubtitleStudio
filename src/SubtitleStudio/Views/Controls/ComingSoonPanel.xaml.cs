using System.Windows;
using System.Windows.Controls;

namespace SubtitleStudio.Views.Controls;

/// <summary>Placeholder card for a tab whose feature arrives in a later build.</summary>
public partial class ComingSoonPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(string.Empty));

    public ComingSoonPanel()
    {
        InitializeComponent();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Summary { get => (string)GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    /// <summary>Filled from XAML with &lt;sys:String&gt; children.</summary>
    public List<string> Planned { get; } = new();
}
