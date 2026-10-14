using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Dubbing;

namespace SubtitleStudio.ViewModels;

/// <summary>A gender to choose for a speaker.</summary>
public sealed record GenderOption(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One speaker in the Dubbing tab's Speakers list: name, gender, voice, and merging into another.</summary>
public sealed class SpeakerRowViewModel : ObservableObject
{
    public static readonly IReadOnlyList<GenderOption> Genders = new[] { new GenderOption("female", "Female"), new GenderOption("male", "Male") };

    private readonly Action<SpeakerRowViewModel> _changed;
    private readonly Action<SpeakerRowViewModel, SpeakerRowViewModel> _merge;
    private SpeakerRowViewModel? _sameAs;

    public SpeakerRowViewModel(Speaker speaker, Action<SpeakerRowViewModel> changed, Action<SpeakerRowViewModel, SpeakerRowViewModel> merge,
        Func<SpeakerRowViewModel, Task> hearOriginal, Func<SpeakerRowViewModel, Task> hearVoice)
    {
        Speaker = speaker;
        _changed = changed;
        _merge = merge;
        HearOriginalCommand = new AsyncRelayCommand(() => hearOriginal(this));
        HearVoiceCommand = new AsyncRelayCommand(() => hearVoice(this));
    }

    public Speaker Speaker { get; }

    public string Name
    {
        get => Speaker.Name;
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0 || v == Speaker.Name) return;
            Speaker.Name = v;
            OnPropertyChanged();
            _changed(this);
        }
    }

    /// <summary>"142 lines, 6.3 min · 118 Hz · remembered".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>
            {
                (Speaker.Lines.Count == 1 ? "1 line" : $"{Speaker.Lines.Count} lines") + $", {Speaker.SpeechTime.TotalMinutes:0.0} min",
            };
            if (Speaker.PitchHz is { } hz) parts.Add($"voice around {hz:0} Hz");
            if (Speaker.Remembered) parts.Add("remembered from an earlier episode");
            return string.Join(" · ", parts);
        }
    }

    public GenderOption Gender
    {
        get => Genders.FirstOrDefault(g => g.Id == Speaker.Gender) ?? Genders[0];
        set
        {
            if (value is null || value.Id == Speaker.Gender) return;
            Speaker.Gender = value.Id;
            Speaker.GenderSetByHand = true;
            Speaker.GenderUncertain = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GenderUncertain));
            _changed(this);
        }
    }

    /// <summary>The pitch is in the band where male and female overlap: worth confirming the gender by hand.</summary>
    public bool GenderUncertain => Speaker.GenderUncertain;

    public IReadOnlyList<KokoroVoiceInfo> Voices => KokoroVoices.English;

    public KokoroVoiceInfo Voice
    {
        get => KokoroVoices.Find(Speaker.Voice) ?? KokoroVoices.English[0];
        set
        {
            if (value is null || value.Id == Speaker.Voice) return;
            Speaker.Voice = value.Id;
            Speaker.VoiceSetByHand = true;
            OnPropertyChanged();
            _changed(this);
        }
    }

    /// <summary>The other speakers, to say "this is the same person as...".</summary>
    public IReadOnlyList<SpeakerRowViewModel> Others { get; private set; } = Array.Empty<SpeakerRowViewModel>();

    public SpeakerRowViewModel? SameAs
    {
        get => _sameAs;
        set
        {
            if (value is null || value == this) return;
            _sameAs = value;
            _merge(this, value);
        }
    }

    public AsyncRelayCommand HearOriginalCommand { get; }
    public AsyncRelayCommand HearVoiceCommand { get; }

    internal void SetOthers(IReadOnlyList<SpeakerRowViewModel> all)
    {
        Others = all.Where(r => r != this).ToList();
        OnPropertyChanged(nameof(Others));
    }

    /// <summary>After a change made elsewhere (voice reassigned, lines moved).</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Voice));
        OnPropertyChanged(nameof(Gender));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(GenderUncertain));
    }

    public override string ToString() => Speaker.Name;
}
