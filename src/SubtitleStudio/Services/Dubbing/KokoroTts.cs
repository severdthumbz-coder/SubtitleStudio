using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// English speech from subtitle text with Kokoro, all inside the app: the text is turned into phonemes
/// (EnglishG2P), the phonemes into the model's numbers, and the model says them in the chosen voice.
/// Long lines are said in pieces of at most 510 phonemes with a short pause between.
/// </summary>
public sealed class KokoroTts : ITtsService, IDubbingVoice, IDisposable
{
    public const int SampleRate = OnnxKokoroModel.SampleRate;

    /// <summary>The engine's sample rate, as an instance member for IDubbingVoice (the static SampleRate stays for callers that have it).</summary>
    int IDubbingVoice.SampleRate => SampleRate;

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

    /// <summary>Lines best spoken at the same time on the device the model runs on.</summary>
    public int Concurrency => _model.Concurrency;

    /// <summary>With the card and the processor together: how many pieces each spoke since last asked (null otherwise).</summary>
    public string? TakeUsage() => (_model as SharedKokoroModel)?.TakeUsage();

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
            output.AddRange(Trim(DeRing(Sanitize(audio))));
        }
        return output.ToArray();
    }

    /// <summary>
    /// Kokoro's vocoder leaves a thin metallic sizzle high in the band. It is not one pure tone: a narrow line sits
    /// near 9.6 kHz (RingHz), but the bulk is a sustained fuzzy band spread across ~6.5–8 kHz, strongest on brighter
    /// voices. So there are two steps: a narrow notch at the 9.6 kHz line, and a high-shelf that pulls the whole top
    /// end down (ShelfHz / ShelfGainDb). A shelf, not a low-pass: 6.5–8 kHz is also where the voice's sibilance ("s",
    /// "sh") lives, so the top is softened rather than cut off — enough to kill the sizzle, not so much as to lisp.
    /// </summary>
    public const double RingHz = 9600, RingQ = 24, ShelfHz = 6500, ShelfGainDb = -9;

    /// <summary>
    /// Takes out Kokoro's vocoder sizzle: a narrow notch at the 9.6 kHz line, then a high-shelf cut above 6.5 kHz for
    /// the broad fuzzy band above it. Tuned so the voice (below ~4 kHz) is untouched and sibilance is only softened,
    /// while the metallic ring is brought down into the noise. Applied to each piece as it's made.
    /// </summary>
    public static float[] DeRing(float[] audio)
    {
        if (audio.Length < 4) return audio;
        // Notch (RBJ band-stop) at the 9.6 kHz line.
        double w0 = 2 * Math.PI * RingHz / SampleRate, cw = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * RingQ);
        Biquad(audio, 1, -2 * cw, 1, 1 + alpha, -2 * cw, 1 - alpha);
        // High-shelf (RBJ) cut above ShelfHz for the broad sizzle band — softens the top without cutting it off.
        double A = Math.Pow(10, ShelfGainDb / 40), w = 2 * Math.PI * ShelfHz / SampleRate, c = Math.Cos(w), s = Math.Sin(w);
        double a2g = s / 2 * Math.Sqrt((A + 1 / A) * (1.0 / 0.9 - 1) + 2), sa = 2 * Math.Sqrt(A) * a2g;
        Biquad(audio,
            A * ((A + 1) + (A - 1) * c + sa), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - sa),
            (A + 1) - (A - 1) * c + sa, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - sa);
        return audio;
    }

    /// <summary>One direct-form-I biquad pass over the buffer in place (coefficients are normalised by a0 here).</summary>
    private static void Biquad(float[] audio, double b0, double b1, double b2, double a0, double a1, double a2)
    {
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            double x = audio[i];
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            audio[i] = (float)y;
        }
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
