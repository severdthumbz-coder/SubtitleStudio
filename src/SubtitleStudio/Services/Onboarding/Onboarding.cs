using System.Windows;
using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Services.Onboarding;

/// <summary>
/// Global on/off switch for the contextual (i) hint markers. HintMarker controls bind to
/// <see cref="Current"/> directly so no view has to thread the setting through.
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

/// <summary>
/// Hook for the optional first-run guided tour (VME pattern). Build 1 ships the null implementation;
/// a real tour plugs in here without touching the shell. The master on/off toggle already lives in
/// Settings > Behaviour (ShowGuidedTourOnFirstRun).
/// </summary>
public interface IGuidedTourService
{
    /// <summary>False until a tour implementation exists.</summary>
    bool IsAvailable { get; }

    Task StartAsync(Window owner, CancellationToken cancellationToken = default);
}

public sealed class NullGuidedTourService : IGuidedTourService
{
    public bool IsAvailable => false;

    public Task StartAsync(Window owner, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
