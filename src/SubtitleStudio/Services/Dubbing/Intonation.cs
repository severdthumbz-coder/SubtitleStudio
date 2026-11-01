namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Within-line intonation: the rise and fall of the original voice's PITCH across a line — a question lifting at the
/// end, a statement settling, a stressed word peaking — carried onto the English voice that replaces it. It is the
/// pitch analogue of <see cref="Emphasis"/> (which carries loudness): measured from the separated original voice as a
/// short curve of semitone offsets relative to the line's own median pitch (so it's the melody shape, not the speaker's
/// absolute pitch, which the chosen voice already sets), then applied to the English speech as a gentle, capped,
/// slowly-gliding pitch shift that keeps the line's length.
///
/// For the quick engines (Kokoro, Piper) only: cloning already re-synthesises from the original and carries its
/// intonation, so applying this on top would double it. Kept subtle on purpose (±a few semitones): a small contour
/// adds life and makes a question sound like a question; a large one sounds like a vocoder or seasick.
/// </summary>
public static class Intonation
{
    /// <summary>Points in the stored contour (start to end of the line). Matches Emphasis for the same smoothing.</summary>
    public const int Points = 16;

    /// <summary>The most the pitch moves from the line's median, in semitones (±). Conservative: expressive, not cartoonish.</summary>
    public const double MaxSemitones = 3.0;

    /// <summary>At least this many voiced pitch readings are needed to trust a contour (else the line is left as-is).</summary>
    public const int MinVoiced = 6;

    /// <summary>
    /// The pitch shape of a line, as <see cref="Points"/> semitone offsets from the line's own median pitch (0 = the
    /// median). Taken from the original voice's tracked pitch (<see cref="Pitch.Track"/>), expressed in semitones (pitch
    /// is heard in ratios, so log/semitone is the natural unit), resampled to the fixed points, and smoothed the same
    /// heavy way as Emphasis so it's the arc of the sentence, not octave flicker on separation artifacts. A line with
    /// too little voiced pitch, or an essentially flat contour, returns null (the speech is left as the engine made it).
    /// </summary>
    public static float[]? Measure(ReadOnlySpan<float> voice, int rate)
    {
        var pitches = Pitch.Track(voice, rate);
        if (pitches.Count < MinVoiced) return null;

        // Median of the line's own pitch is the centre; the contour is each reading's distance from it, in semitones.
        var sorted = new List<double>(pitches);
        sorted.Sort();
        double median = sorted[sorted.Count / 2];
        if (median <= 0) return null;

        var semis = new double[pitches.Count];
        for (int i = 0; i < pitches.Count; i++)
            semis[i] = pitches[i] > 0 ? 12.0 * Math.Log2(pitches[i] / median) : 0;

        // Resample the voiced sequence to the fixed number of points (the sequence is treated as spanning the line;
        // unvoiced gaps aren't located, which is fine at the sentence-arc level this works on, like Emphasis's energy).
        var raw = new double[Points];
        for (int p = 0; p < Points; p++)
        {
            double pos = (double)p / (Points - 1) * (semis.Length - 1);
            int a = (int)pos; double f = pos - a;
            raw[p] = a + 1 < semis.Length ? semis[a] * (1 - f) + semis[a + 1] * f : semis[a];
        }

        // Heavy smoothing (as Emphasis): the melody arc, not syllable-by-syllable jitter or a tracking glitch.
        var s = (double[])raw.Clone();
        for (int pass = 0; pass < 4; pass++)
        {
            var t = (double[])s.Clone();
            for (int p = 0; p < Points; p++)
                s[p] = (t[Math.Max(0, p - 1)] + 2 * t[p] + t[Math.Min(Points - 1, p + 1)]) / 4;
        }

        // Cap to the musical range (an octave error from tracking is clipped here), and null out a flat contour.
        double swing = 0;
        var contour = new float[Points];
        for (int p = 0; p < Points; p++)
        {
            double c = Math.Clamp(s[p], -MaxSemitones, MaxSemitones);
            contour[p] = (float)c;
            swing = Math.Max(swing, Math.Abs(c));
        }
        return swing < 0.25 ? null : contour; // under ~a quarter tone of movement: nothing worth following
    }

    /// <summary>The pitch glides with a one-pole smoother of about this long, so it never steps and is heard as melody.</summary>
    public const double SlewMs = 70;

    /// <summary>Analysis window for the time-varying shift (overlap-added); ~43 ms at 24 kHz, like TimeStretch.</summary>
    private const int Window = 1024;

    private static readonly float[] Hann =
        Enumerable.Range(0, Window).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Window))).ToArray();

    /// <summary>
    /// Apply a pitch contour to a line's speech: the line is covered by overlapping windows, and each window is shifted
    /// by the contour's semitone offset at that position (interpolated and slew-limited, so pitch glides rather than
    /// steps), then overlap-added back. A pitch shift that keeps duration is a time-stretch by f followed by a resample
    /// by 1/f (f = 2^(semitones/12)); WSOLA (<see cref="TimeStretch.Faster"/>) does the stretch cleanly. The output is
    /// the same length as the input — intonation only moves pitch, timing is handled by fitting/anchoring. A null or
    /// flat contour, or speech too short, returns the same samples.
    /// </summary>
    public static float[] Apply(float[] speech, float[]? contour, int rate)
    {
        if (contour is null || contour.Length < 2 || speech.Length < Window * 2) return speech;

        int hop = Window / 2;
        var output = new float[speech.Length + Window];
        var weight = new float[speech.Length + Window];
        double a = Math.Exp(-1.0 / (SlewMs / 1000.0 * rate / hop)); // one-pole over hops, so the shift glides
        double slew = 0; // current semitone offset, slew-limited
        bool moved = false;

        for (int start = 0; start < speech.Length; start += hop)
        {
            int len = Math.Min(Window, speech.Length - start);
            if (len < 16) break;

            // Contour value at this window's centre, interpolated along the line.
            double centre = (start + len / 2.0) / Math.Max(1, speech.Length - 1);
            double posf = centre * (contour.Length - 1);
            int ci = (int)posf; double cf = posf - ci;
            double target = ci + 1 < contour.Length ? contour[ci] * (1 - cf) + contour[ci + 1] * cf : contour[ci];
            slew = a * slew + (1 - a) * target;
            if (Math.Abs(slew) > 0.05) moved = true;

            // Shift this window by `slew` semitones, keeping its length: stretch then resample.
            var win = new float[len];
            for (int i = 0; i < len; i++) win[i] = speech[start + i];
            var shifted = ShiftSemitones(win, slew);

            // Overlap-add the (same-length) shifted window with a Hann taper.
            for (int i = 0; i < len; i++)
            {
                float h = i < Window ? Hann[(int)((long)i * Window / Math.Max(1, len))] : 0f;
                output[start + i] += shifted[i] * h;
                weight[start + i] += h;
            }
        }

        if (!moved) return speech;
        var result = new float[speech.Length];
        for (int i = 0; i < speech.Length; i++) result[i] = weight[i] > 1e-3f ? output[i] / weight[i] : speech[i];
        return result;
    }

    /// <summary>
    /// Shift a short window by <paramref name="semitones"/> without changing its length: time-stretch by f (WSOLA) then
    /// resample by 1/f, where f = 2^(semitones/12). A tiny shift (near zero) is returned unchanged.
    /// </summary>
    private static float[] ShiftSemitones(float[] win, double semitones)
    {
        if (Math.Abs(semitones) < 0.02) return win;
        double f = Math.Pow(2, semitones / 12.0);          // >1 = higher
        // Time-stretch to length*f (Faster shortens by its factor, so to LENGTHEN by f we pass 1/f), then resample the
        // result back down to the original length — the net is a pitch shift of f with the duration preserved.
        var stretched = TimeStretch.Faster(win, 1.0 / f);   // length ≈ win.Length * f
        return ResampleTo(stretched, win.Length);
    }

    /// <summary>Linear-resample <paramref name="input"/> to exactly <paramref name="outLength"/> samples.</summary>
    private static float[] ResampleTo(float[] input, int outLength)
    {
        if (input.Length == outLength || input.Length == 0) return input.Length == outLength ? input : Pad(input, outLength);
        var result = new float[outLength];
        double step = (input.Length - 1) / (double)Math.Max(1, outLength - 1);
        for (int i = 0; i < outLength; i++)
        {
            double src = i * step;
            int a = (int)src; double t = src - a;
            float x0 = a < input.Length ? input[a] : 0f, x1 = a + 1 < input.Length ? input[a + 1] : x0;
            result[i] = (float)(x0 + (x1 - x0) * t);
        }
        return result;
    }

    private static float[] Pad(float[] input, int outLength)
    {
        var r = new float[outLength];
        Array.Copy(input, r, Math.Min(input.Length, outLength));
        return r;
    }
}
