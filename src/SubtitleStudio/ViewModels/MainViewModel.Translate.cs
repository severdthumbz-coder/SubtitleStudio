using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.ViewModels;

/// <summary>Translate tab. Build 1: engine registry only; batch translation + review arrive later.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<ITranslationService> TranslationEngines => _s.TranslationEngines;

    public bool HasTranslationEngines => _s.TranslationEngines.Count > 0;

    private void InitTranslate()
    {
    }
}
