using System.Diagnostics;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Transcription;

/// <summary>What a transcription did, for the status bar and the Log.</summary>
public sealed record TranscriptionStats(TimeSpan Audio, TimeSpan Reading, TimeSpan Loading, TimeSpan Transcribing,
    string Device, string? DeviceProblem, string? Language, int Segments, int Cues, int DroppedNotSpeech, int DroppedRepeats)
{
    /// <summary>How many times faster than real time the transcription ran.</summary>
    public double Speed => Transcribing.TotalSeconds > 0 ? Audio.TotalSeconds / Transcribing.TotalSeconds : 0;
}

/// <summary>
/// Speech to subtitles on this PC with whisper.cpp: FFmpeg decodes the audio, whisper.cpp (in the app,
/// on the graphics card through Vulkan or on the processor) writes timed text, and
/// <see cref="TranscriptShaper"/> turns that into subtitle cues.
/// </summary>
public sealed class LocalWhisperEngine : ITranscriptionService, IDisposable
{
    public const string EngineId = "whisper-cpp";

    private readonly IWhisperRunner _runner;
    private readonly Func<FfmpegStatus> _ffmpeg;
    private readonly ActivityLog? _log;

    public LocalWhisperEngine(IWhisperRunner runner, Func<FfmpegStatus> ffmpeg, ActivityLog? log = null)
    {
        _runner = runner;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public string Id => EngineId;
    public string DisplayName => "On this PC (whisper.cpp)";
    public string Description => "Free and private: the audio never leaves this PC, and it works offline. Runs on the graphics card when it can, otherwise on the processor (much slower).";
    public bool IsLocal => true;
    public bool RequiresApiKey => false;
    public string? ApiKeyProviderId => null;

    /// <summary>Where to run (set from Settings before each run).</summary>
    public WhisperDevice Device { get; set; } = WhisperDevice.Processor;

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>Whisper's "condition on previous text" (see <see cref="WhisperRunOptions"/>).</summary>
    public bool CarryContext { get; set; }

    public CueShapeOptions Shape { get; set; } = new();

    public SpeechChunker.Options Chunking { get; set; } = new();

    /// <summary>Find the speech with the Silero detector before Whisper listens (off: loudness only).</summary>
    public bool UseSpeechDetector { get; set; } = true;

    /// <summary>
    /// Listen for scenes in another language (Japanese scenes in a Korean drama) and transcribe each in its
    /// own language. Costs one short language check per chunk of speech.
    /// </summary>
    public bool DetectSceneLanguages { get; set; } = true;

    public SceneLanguages.Options SceneOptions { get; set; } = new();

    public TranscriptionStats? LastStats { get; private set; }

    public IWhisperRunner Runner => _runner;

    /// <summary>Frees the Whisper model's memory (the translator needs the graphics card next).</summary>
    public void ReleaseModel() => _runner.ReleaseModel();

    public async Task<SubtitleDocument> TranscribeAsync(string mediaPath, TranscriptionOptions options, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        if (options.Model is not { Length: > 0 } modelPath || !File.Exists(modelPath))
            throw new InvalidOperationException("Choose a Whisper model first (download one in the Model section).");
        var header = WhisperModelHeader.Read(modelPath);
        if (_ffmpeg().FfmpegPath is not { } ffmpeg)
            throw new InvalidOperationException("FFmpeg is needed to read the audio. Set it in Settings > Engines and tools.");

        string? language = options.AutoDetectLanguage ? null : options.Language;
        bool translate = options.TranslateToEnglish && header.Multilingual;
        var run = new WhisperRunOptions(modelPath, language, translate, Device, Threads, CarryContext);
        _log?.Info("Transcribe", $"{Path.GetFileName(mediaPath)}: {header.Name} ({Path.GetFileName(modelPath)}), "
            + (language is null ? "language detected automatically" : $"language {WhisperLanguages.NameOf(language)}")
            + (translate ? ", translated to English" : string.Empty)
            + (options.Start is not null || options.Length is not null ? $", part from {Format(options.Start ?? TimeSpan.Zero)} for {(options.Length is { } l0 ? Format(l0) : "the rest")}" : string.Empty)
            + $", requested device: {Device.Label}.");

        // 1. Audio (the first 8% of the bar).
        var clock = Stopwatch.StartNew();
        progress?.Report(new EngineProgress(0, "Reading the audio..."));
        TimeSpan? expected = options.Length ?? (options.MediaDuration is { } d ? d - (options.Start ?? TimeSpan.Zero) : null);
        var audioProgress = new InlineProgress<double>(f => progress?.Report(new EngineProgress(f * 0.08, $"Reading the audio... {f:P0}")));
        var samples = await AudioExtractor.ReadAsync(ffmpeg, mediaPath, options.AudioTrack, options.Start, options.Length, expected, audioProgress, ct).ConfigureAwait(false);
        var audioLength = TimeSpan.FromSeconds(samples.Length / (double)AudioExtractor.SampleRate);
        var reading = clock.Elapsed;
        _log?.Detail("Transcribe", $"Audio read: {Format(audioLength)} in {reading.TotalSeconds:0.0} s.");

        // 2. Model.
        clock.Restart();
        progress?.Report(new EngineProgress(0.08, Device.UseGpu ? $"Loading the model onto {Device.Label}..." : "Loading the model..."));
        await _runner.LoadAsync(run, ct).ConfigureAwait(false);
        var loading = clock.Elapsed;
        var device = _runner.DeviceUsed ?? Device.Label;
        _log?.Info("Transcribe", $"Model loaded in {loading.TotalSeconds:0.0} s; runs on {device}.");
        if (_runner.DeviceProblem is { } problem) _log?.Warning("Transcribe", problem);

        // 3. Where the speech is (Silero speech detector; loudness if it isn't available). Only speech goes
        // to Whisper: music and silence are where it invents lines.
        progress?.Report(new EngineProgress(0.09, "Finding the speech..."));
        clock.Restart();
        var speech = UseSpeechDetector ? await _runner.DetectSpeechAsync(samples, run, ct).ConfigureAwait(false) : null;
        var plan = SpeechChunker.Plan(samples, speech, Chunking);
        if (speech is { Count: > 0 })
            _log?.Info("Transcribe", $"Speech found in {speech.Count} places ({Format(TimeSpan.FromSeconds(plan.SpeechSamples / (double)AudioExtractor.SampleRate))} of {Format(audioLength)}) "
                + $"in {clock.Elapsed.TotalSeconds:0.0} s; music, effects and silence in between ({Format(plan.SilenceSkipped)}) are skipped.");
        else
            _log?.Info("Transcribe", (speech is null ? "Speech detector not available: " : "The speech detector found no speech: ")
                + $"long silences found by loudness are skipped ({Format(plan.SilenceSkipped)}).");
        _log?.Detail("Transcribe", $"{plan.Chunks.Count} pieces for Whisper.");

        // 4. The language of each scene (when asked): chunks in another language get that language.
        clock.Restart();
        IReadOnlyList<SpeechChunk> chunks = plan.Chunks;
        string? sceneMain = null;
        double transcribeFrom = 0.1;
        if (DetectSceneLanguages && chunks.Count > 0)
        {
            var scenes = await PlanSceneLanguagesAsync(chunks, language, run, progress, ct).ConfigureAwait(false);
            if (scenes is { } found)
            {
                sceneMain = found.Main;
                chunks = found.Chunks.Select(c => c.Chunk).ToList();
                run = run with { Language = found.Main, ChunkLanguages = found.Chunks.Select(c => c.Language).ToList() };
                transcribeFrom = 0.15;
            }
        }

        // 5. Speech to text.
        var segments = new List<WhisperSegment>();
        var chunkStarts = new double[chunks.Count + 1];
        for (int c = 0; c < chunks.Count; c++) chunkStarts[c + 1] = chunkStarts[c] + chunks[c].Audio.Length;
        double totalSamples = Math.Max(1, chunkStarts[^1]);
        // Updated from whisper.cpp's thread and read by progress callbacks: plain values, no shared list.
        // whisper.cpp's progress (its own thread) and new segments (this loop) both move the bar: one lock,
        // and it only ever moves forward.
        var gate = new object();
        double done = 0;
        string? latest = null;
        void Advance(double f)
        {
            lock (gate)
            {
                if (f < done) f = done;
                done = Math.Clamp(f, 0, 1);
                progress?.Report(new EngineProgress(transcribeFrom + (1 - transcribeFrom) * done, $"Transcribing on {device}... {done:P0}", latest));
            }
        }
        var chunkProgress = new InlineProgress<(int Chunk, int Percent)>(p =>
            Advance((chunkStarts[p.Chunk] + (chunkStarts[p.Chunk + 1] - chunkStarts[p.Chunk]) * p.Percent / 100.0) / totalSamples));
        var audioChunks = chunks.Select(c => c.Audio).ToList();
        await foreach (var raw in _runner.RunAsync(audioChunks, run, chunkProgress, ct).ConfigureAwait(false))
        {
            var chunk = chunks[raw.Chunk];
            var segment = ToSource(raw, chunk);
            segments.Add(segment);
            var text = TranscriptShaper.Clean(segment.Text);
            if (text.Length > 0) lock (gate) latest = text;
            Advance((chunkStarts[raw.Chunk] + Math.Min(raw.End.TotalSeconds * AudioExtractor.SampleRate, chunk.Audio.Length)) / totalSamples);
        }
        string? detected = sceneMain ?? _runner.DetectedLanguage?.Code ?? segments.Select(x => x.Language).FirstOrDefault(l => !string.IsNullOrEmpty(l));
        if (language is null && sceneMain is null && _runner.DetectedLanguage is { } runnerFound)
            _log?.Info("Transcribe", $"Language detected: {WhisperLanguages.NameOf(runnerFound.Code)} ({runnerFound.Probability:P0} sure).");
        // 6. Lines that mix two scripts ("代사에 정보를 주장했습니다."): the language changed inside a stretch that was
        //    heard in one language. That line's audio is checked again, only between the languages its letters suggest,
        //    and written again in the language found.
        if (DetectSceneLanguages && !translate && (sceneMain ?? language ?? detected) is { } lineMain)
            await RedoMixedLinesAsync(segments, samples, run, lineMain, ct).ConfigureAwait(false);
        var transcribing = clock.Elapsed;

        var shaped = TranscriptShaper.Shape(segments, Shape, options.Start ?? TimeSpan.Zero, plan.Active);
        samples = Array.Empty<float>();
        var docLanguage = translate ? "en" : language ?? detected;
        LastStats = new TranscriptionStats(audioLength, reading, loading, transcribing, device, _runner.DeviceProblem, docLanguage,
            segments.Count, shaped.Cues.Count, shaped.DroppedNotSpeech, shaped.DroppedRepeats);

        _log?.Success("Transcribe", $"{Path.GetFileName(mediaPath)}: {shaped.Cues.Count} cues from {segments.Count} segments, "
            + $"{Format(audioLength)} of audio in {Format(transcribing)} ({LastStats.Speed:0.0}x real time) on {device}"
            + (language is null && detected is not null ? $"; language detected: {WhisperLanguages.NameOf(detected)}" : string.Empty)
            + (shaped.DroppedNotSpeech + shaped.DroppedRepeats + shaped.DroppedKnownPhrases > 0
                ? $"; left out {shaped.DroppedNotSpeech} line(s) Whisper rated as not speech, {shaped.DroppedRepeats} repeated line(s) and {shaped.DroppedKnownPhrases} of Whisper's known invented phrases"
                : string.Empty) + ".");

        var doc = new SubtitleDocument { Language = docLanguage, Format = "srt" };
        doc.Cues.AddRange(shaped.Cues);
        return doc;
    }

    /// <summary>
    /// Whisper's language check on every chunk, then on each stretch of the chunks it wasn't sure about.
    /// Null when the runner can't check languages or no chunk was clear enough to know the main language.
    /// </summary>
    private async Task<(string Main, IReadOnlyList<SceneChunk> Chunks)?> PlanSceneLanguagesAsync(IReadOnlyList<SpeechChunk> chunks, string? chosen,
        WhisperRunOptions run, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var o = SceneOptions;
        // The language was chosen: another language has to be very clear before it overrides that choice.
        if (chosen is not null) o = o with { SwitchChunk = Math.Max(o.SwitchChunk, 0.95), SurePiece = Math.Max(o.SurePiece, 0.95) };
        progress?.Report(new EngineProgress(0.1, "Listening for the language of each scene..."));
        var clock = Stopwatch.StartNew();
        var perChunk = await _runner.DetectLanguagesAsync(chunks.Select(c => c.Audio).ToList(), run,
            new InlineProgress<int>(i => progress?.Report(new EngineProgress(0.1 + 0.04 * i / chunks.Count, $"Listening for the language of each scene... {i} of {chunks.Count}"))), ct).ConfigureAwait(false);
        if (perChunk is null) return null;
        _log?.Detail("Transcribe", "Language of each chunk: " + string.Join("; ", chunks.Select((c, i) =>
            $"{Format(c.SourceStart)}–{Format(c.SourceEnd)} " + (perChunk[i] is { } d ? $"{d.Code} {d.Probability:P0}" : "?"))));
        var main = chosen ?? SceneLanguages.MainLanguage(chunks, perChunk, o);
        if (main is null)
        {
            _log?.Info("Transcribe", "Couldn't tell the language of the scenes clearly; Whisper detects it from the start of the speech instead.");
            return null;
        }
        if (chosen is null)
            _log?.Info("Transcribe", $"Language detected: {WhisperLanguages.NameOf(main)} (most of the speech).");

        // Unsure chunks: each stretch long enough to judge, in one go.
        var unsure = Enumerable.Range(0, chunks.Count).Where(i => SceneLanguages.IsUnsure(chunks[i], perChunk[i], main, o)).ToList();
        var stretches = unsure.SelectMany(i => SceneLanguages.JudgedPieces(chunks[i], o).Select(p => (Chunk: i, Piece: p))).ToList();
        var perStretch = stretches.Count == 0 ? Array.Empty<(string, float)?>()
            : await _runner.DetectLanguagesAsync(stretches.Select(s => SceneLanguages.Slice(chunks[s.Chunk], s.Piece, s.Piece).Audio).ToList(), run,
                new InlineProgress<int>(i => progress?.Report(new EngineProgress(0.14 + 0.01 * i / stretches.Count, $"Listening for the language of each scene... {chunks.Count + i} of {chunks.Count + stretches.Count}"))), ct).ConfigureAwait(false)
              ?? Array.Empty<(string, float)?>();

        if (stretches.Count > 0)
            _log?.Detail("Transcribe", "Language of each stretch in the unclear chunks: " + string.Join("; ", stretches.Select((st, k) =>
                $"{Format(SceneLanguages.Slice(chunks[st.Chunk], st.Piece, st.Piece).SourceStart)} " + (k < perStretch.Count && perStretch[k] is { } d ? $"{d.Code} {d.Probability:P0}" : "?"))));
        var result = new List<SceneChunk>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (!unsure.Contains(i))
            {
                result.Add(new SceneChunk(chunks[i], SceneLanguages.ChunkLanguage(chunks[i], perChunk[i], main, o)));
                continue;
            }
            var pieceLanguages = new (string Code, float Probability)?[chunks[i].Pieces.Count];
            for (int k = 0; k < stretches.Count && k < perStretch.Count; k++)
                if (stretches[k].Chunk == i) pieceLanguages[stretches[k].Piece] = perStretch[k];
            result.AddRange(SceneLanguages.Split(chunks[i], pieceLanguages, main, o));
        }

        var others = SceneLanguages.OtherScenes(result, main);
        _log?.Info("Transcribe", $"Languages checked for {chunks.Count} chunks" + (stretches.Count > 0 ? $" and {stretches.Count} stretches of {unsure.Count} mixed or unclear chunks" : string.Empty)
            + $" in {clock.Elapsed.TotalSeconds:0.0} s: " + (others.Count == 0
                ? $"all {WhisperLanguages.NameOf(main)}."
                : $"{WhisperLanguages.NameOf(main)}, with {others.Count} scene{(others.Count == 1 ? "" : "s")} in another language: "
                  + string.Join(", ", others.Select(x => $"{WhisperLanguages.NameOf(x.Language)} {Format(x.Start)}–{Format(x.End)}")) + "."));
        return (main, result);
    }

    private async Task RedoMixedLinesAsync(List<WhisperSegment> segments, float[] samples, WhisperRunOptions run, string main, CancellationToken ct)
    {
        int redone = 0, kept = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = segments[i];
            var heardIn = run.ChunkLanguages is { } langs && s.Chunk < langs.Count && langs[s.Chunk] is { } l ? l : run.Language ?? main;
            if (SceneLanguages.MixedScriptCandidates(TranscriptShaper.Clean(s.Text), heardIn) is not { } candidates) continue;
            long from = Math.Max(0, (long)((s.Start.TotalSeconds - 0.25) * AudioExtractor.SampleRate));
            long to = Math.Min(samples.Length, (long)((s.End.TotalSeconds + 0.25) * AudioExtractor.SampleRate));
            if (to - from < AudioExtractor.SampleRate / 2) continue;
            var audio = new ReadOnlyMemory<float>(samples, (int)from, (int)(to - from));
            var found = await _runner.DetectLanguagesAsync(new[] { audio }, run, null, ct, candidates).ConfigureAwait(false);
            if (found is not [{ } f] || f.Code == heardIn)
            {
                kept++;
                _log?.Detail("Transcribe", $"{Format(s.Start)}: \"{s.Text.Trim()}\" mixes scripts, but it sounds like {WhisperLanguages.NameOf(heardIn)}; kept.");
                continue;
            }
            var piece = new SpeechChunk(audio, new[] { new AudioPiece(0, from, (int)(to - from)) });
            var redo = new List<WhisperSegment>();
            await foreach (var r in _runner.RunAsync(new[] { audio }, run with { Language = f.Code, ChunkLanguages = null }, null, ct).ConfigureAwait(false))
                redo.Add(ToSource(r, piece) with { Chunk = s.Chunk });
            if (!redo.Any(r => TranscriptShaper.Clean(r.Text).Any(char.IsLetter)))
            {
                kept++;
                continue;
            }
            segments.RemoveAt(i);
            segments.InsertRange(i, redo);
            i += redo.Count - 1;
            redone++;
            _log?.Info("Transcribe", $"{Format(s.Start)}: \"{s.Text.Trim()}\" mixed two scripts; written again in {WhisperLanguages.NameOf(f.Code)}: \"{string.Join(" ", redo.Select(r => r.Text.Trim()))}\".");
        }
        if (redone + kept > 0)
            _log?.Info("Transcribe", $"{redone + kept} line(s) mixed two scripts: {redone} written again in the language they sound like, {kept} kept.");
    }

    /// <summary>Segment and token times from chunk time to source time.</summary>
    private static WhisperSegment ToSource(WhisperSegment s, SpeechChunk chunk)
    {
        if (chunk.Pieces.Count == 1 && chunk.Pieces[0].ChunkOffset == 0 && chunk.Pieces[0].SourceOffset == 0) return s;
        var tokens = s.Tokens.Select(t => t with { Start = chunk.ToSource(t.Start, false), End = chunk.ToSource(t.End, true) }).ToList();
        var start = chunk.ToSource(s.Start, false);
        var end = chunk.ToSource(s.End, true);
        return s with { Start = start, End = end > start ? end : start + (s.End - s.Start), Tokens = tokens };
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public void Dispose() => _runner.Dispose();
}

/// <summary>Reports on the calling thread, in order (<see cref="Progress{T}"/> posts to the thread pool, out of order).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Which engine to use when there may be several (local, and online ones with a saved key).</summary>
public sealed record EngineDecision<T>(T? Engine, IReadOnlyList<T> Choices) where T : class, IEngineInfo
{
    /// <summary>Several can be used and none was remembered: ask.</summary>
    public bool MustAsk => Engine is null && Choices.Count > 1;
}

public static class EngineChooser
{
    /// <summary>
    /// Usable engines: local ones, and online ones whose API key is saved. One: use it. Several: the
    /// remembered choice if it's still usable, otherwise ask.
    /// </summary>
    public static EngineDecision<T> Decide<T>(IReadOnlyList<T> engines, Func<string, bool> hasKey, string? remembered) where T : class, IEngineInfo
    {
        var usable = engines.Where(e => !e.RequiresApiKey || (e.ApiKeyProviderId is { } p && hasKey(p))).ToList();
        if (usable.Count == 1) return new EngineDecision<T>(usable[0], usable);
        var chosen = usable.FirstOrDefault(e => e.Id == remembered);
        return new EngineDecision<T>(chosen, usable);
    }
}
