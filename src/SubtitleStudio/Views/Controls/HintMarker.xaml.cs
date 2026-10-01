using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SubtitleStudio.Services.Onboarding;

namespace SubtitleStudio.Views.Controls;

/// <summary>Small (i) marker with a tooltip. Hidden globally when hints are switched off.</summary>
public partial class HintMarker : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HintMarker), new PropertyMetadata(string.Empty));

    public HintMarker()
    {
        InitializeComponent();
        SetBinding(VisibilityProperty, new Binding(nameof(HintService.IsEnabled))
        {
            Source = HintService.Current,
            Converter = new BooleanToVisibilityConverter(),
        });
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
