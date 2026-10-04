using System.Windows;

namespace SubtitleStudio.Infrastructure;

/// <summary>
/// Carries a DataContext to places outside the visual tree (DataGrid columns), so a column's
/// Visibility can follow a view-model property: put one in Resources with Data="{Binding}" and
/// bind to Data.Property with Source={StaticResource Proxy}.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
