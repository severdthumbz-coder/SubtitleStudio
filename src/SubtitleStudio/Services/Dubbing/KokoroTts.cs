using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// English speech from subtitle text with Kokoro, all inside the app: the text is turned into phonemes
/// (EnglishG2P), the phonemes into the model's numbers, and the model says them in the chosen voice.
/// Long lines are said in pieces of at most 510 phonemes with a short pause between.
/// </summary>
public sealed class KokoroTts : ITtsService, IDisposable
{
    public const int SampleRate = OnnxKokoroModel.SampleRate;

    /// <summary>Pause between the pieces of a line too long for one pass.</summary>
    public static readonly TimeSpan PiecePause = TimeSpan.FromMilliseconds(150);

    private readonly IKokoroModel _model;
    private readonly KokoroVoices _voices;
    private readonly EnglishG2P _g2p;

    public KokoroTts(IKokoroModel model, KokoroVoices voices, EnglishG2P? g2p = null)
    {
        _model = model;
        _voices = voices;
        _g2p = g2p ?? EnglishG2P.Shared;
    }

    public string Id => "kokoro";
    public string DisplayName => "Kokoro (in the app)";
    public bool IsLocal => true;
    public bool RequiresApiKey => false;
    public string? ApiKeyProviderId => null;
    public string DeviceLabel => _model.DeviceLabel;

    /// <summary>The English voices the voices file has.</summary>
    public IReadOnlyList<KokoroVoiceInfo> Voices => KokoroVoices.English.Where(v => _voices.Contains(v.Id)).ToList();

    public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TtsVoice>>(Voices.Select(v => v.ToTtsVoice()).ToList());

    /// <summary>What is said for a subtitle line: tags removed, sound labels and speaker dashes left out by the phonemizer.</summary>
    public string Phonemes(string subtitleText) => _g2p.Phonemize(SubtitleText.StripTags(subtitleText ?? string.Empty));

    /// <summary>The line as 24 kHz samples, silence trimmed from both ends; empty when there's nothing to say.</summary>
    public float[] Speak(string subtitleText, string voiceId, float speed = 1f)
    {
        var phonemes = Phonemes(subtitleText);
        var pieces = KokoroVocab.Split(phonemes).Select(KokoroVocab.Tokenize).Where(t => t.Length > 0 && t.Any(IsSound)).ToList();
        var output = new List<float>();
        foreach (var tokens in pieces)
        {
            if (output.Count > 0) output.AddRange(new float[(int)(PiecePause.TotalSeconds * SampleRate)]);
            var audio = _model.Speak(tokens, _voices.Style(voiceId, tokens.Length), Math.Clamp(speed, 0.5f, 2f));
            output.AddRange(Trim(Sanitize(audio)));
        }
        return output.ToArray();
    }

    /// <summary>Invalid samples (never seen with the full model) made silent rather than left to break the track.</summary>
    private static float[] Sanitize(float[] audio)
    {
        for (int i = 0; i < audio.Length; i++)
            if (!float.IsFinite(audio[i])) audio[i] = 0;
            else if (audio[i] is > 1 or < -1) audio[i] = Math.Clamp(audio[i], -1f, 1f);
        return audio;
    }

    /// <summary>Tokens that aren't only punctuation or spaces (ids 1 to 16 are punctuation and the space).</summary>
    private static bool IsSound(long id) => id > 16;

    public Task<TimeSpan> SynthesizeAsync(string text, TtsVoice voice, string outputPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = Speak(text, voice.Id);
            WavFile.Write(outputPath, samples, SampleRate);
            return TimeSpan.FromSeconds(samples.Length / (double)SampleRate);
        }, cancellationToken);

    /// <summary>
    /// Cuts the near-silence Kokoro leaves before and after speech (frames 60 dB below the loudest, as
    /// kokoro-onnx does with librosa's trim), keeping a few milliseconds so no sound is clipped.
    /// </summary>
    public static float[] Trim(float[] audio, double topDb = 60)
    {
        const int frame = 512, hop = 128;
        if (audio.Length < frame) return audio;
        int frames = (audio.Length - frame) / hop + 1;
        var power = new double[frames];
        double max = 0;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < frame; i++) { double v = audio[f * hop + i]; sum += v * v; }
            power[f] = sum / frame;
            max = Math.Max(max, power[f]);
        }
        if (max <= 0) return Array.Empty<float>();
        double threshold = max * Math.Pow(10, -topDb / 10);
        int first = Array.FindIndex(power, p => p > threshold), last = Array.FindLastIndex(power, p => p > threshold);
        if (first < 0) return Array.Empty<float>();
        int margin = SampleRate / 100; // 10 ms
        int start = Math.Max(0, first * hop - margin), end = Math.Min(audio.Length, last * hop + frame + margin);
        return audio[start..end];
    }

    public void Dispose() => _model.Dispose();
}
