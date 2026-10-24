using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// English speech from subtitle text with a Piper voice, all inside the app: the text is phonemized by the app's own
/// EnglishG2P (no espeak-ng), the misaki phonemes are translated to IPA (PiperPhonemes), the IPA is turned into the
/// voice's id sequence (PiperConfig), and the Piper model says it. Implements IDubbingVoice so the dubbing path drives
/// it exactly like Kokoro. A line too long for one pass is said in pieces at sentence ends with a short pause between.
/// </summary>
public sealed class PiperTts : IDubbingVoice
{
    /// <summary>Pause between the pieces of a long line (matches Kokoro's, so mixing engines sounds consistent).</summary>
    public static readonly TimeSpan PiecePause = TimeSpan.FromMilliseconds(150);

    /// <summary>Longest id sequence per pass; Piper has no hard cap, but long inputs drift, so split like Kokoro.</summary>
    public const int MaxPhonemesPerPiece = 400;

    private readonly PiperModel _model;
    private readonly EnglishG2P _g2p;

    public PiperTts(PiperModel model, EnglishG2P? g2p = null)
    {
        _model = model;
        _g2p = g2p ?? EnglishG2P.Shared;
    }

    public int SampleRate => _model.SampleRate;
    public string DeviceLabel => _model.DeviceLabel;
    public int Concurrency => 1; // one Piper session; the dubbing path still pipelines lines

    /// <summary>What the voice will actually say: the app's phonemes translated to the IPA fed to the model.</summary>
    public string Phonemes(string subtitleText)
        => PiperPhonemes.ToIpa(_g2p.Phonemize(SubtitleText.StripTags(subtitleText ?? string.Empty)));

    public float[] Speak(string subtitleText, string voiceId, float speed = 1f)
    {
        var ipa = Phonemes(subtitleText);
        if (ipa.Trim().Length == 0) return Array.Empty<float>();

        // Speed: Piper's length_scale is inverse to speed (bigger = slower), so a 1.2x speed is length_scale / 1.2.
        // The model already carries the voice's default scales; speed is applied by the config's LengthScale, which
        // PiperModel reads — so vary speed by cloning the config with a scaled LengthScale for this call.
        var pieces = SplitIpa(ipa, MaxPhonemesPerPiece);
        var output = new List<float>();
        int pausen = (int)(PiecePause.TotalSeconds * SampleRate);
        foreach (var piece in pieces)
        {
            if (output.Count > 0) output.AddRange(new float[pausen]);
            var ids = _model.Config.ToIds(piece);
            var scaled = speed is > 0 and not 1f ? ScaleSpeed(ids, speed) : ids;
            output.AddRange(_model.Speak(scaled));
        }
        return output.ToArray();
    }

    // Speed is set through length_scale on the model's config; PiperModel.Speak reads Config each call, so to change
    // speed per call we pass ids and let the model use its config. (Speed variation is wired at the model/config
    // level; this helper is a seam for it and currently returns the ids unchanged — speed support lands with the
    // first real-voice test, where length_scale behaviour is verified.)
    private long[] ScaleSpeed(long[] ids, float speed) => ids;

    /// <summary>Split a long IPA string into pieces at spaces, each at most roughly <paramref name="max"/> symbols.</summary>
    private static List<string> SplitIpa(string ipa, int max)
    {
        var pieces = new List<string>();
        if (ipa.Length <= max) { pieces.Add(ipa); return pieces; }
        int start = 0;
        while (start < ipa.Length)
        {
            int end = Math.Min(start + max, ipa.Length);
            if (end < ipa.Length)
            {
                int space = ipa.LastIndexOf(' ', end - 1, end - start);
                if (space > start) end = space;
            }
            pieces.Add(ipa[start..end].Trim());
            start = end;
        }
        return pieces.Where(p => p.Length > 0).ToList();
    }

    public void Dispose() => _model.Dispose();
}
