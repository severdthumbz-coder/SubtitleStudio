using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Services.Onboarding;

/// <summary>
/// Global on/off switch for the contextual (i) hint markers. HintMarker controls bind to
/// <see cref="Current"/> directly so no view has to thread the setting through.
/// Kept in step with the OnboardingKit master switch (OnboardingController.ShowHelpers) so the
/// hints, the ❔ legend and the first-run tour all turn on and off together.
/// </summary>
public sealed class HintService : ObservableObject
{
    private bool _isEnabled = true;

    public static HintService Current { get; } = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}
