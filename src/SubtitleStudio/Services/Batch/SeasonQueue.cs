using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Subtitles;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.Services.Batch;

public enum QueueStep { Waiting, Transcribing, Translating, Done, Skipped, Failed, Cancelled }

/// <param name="SpokenLanguage">Language spoken (null: detect it in each file).</param>
/// <param name="TranslateTo">Language to translate into (null: transcribe only).</param>
/// <param name="OutputFormat">Format id for the files written (srt, vtt, ass, ssa, sub).</param>
/// <param name="RedoExisting">Make the subtitles again even when the file already has them (otherwise they are used / left alone).</param>
public sealed record QueueOptions(string? SpokenLanguage, string? TranscribeModel, string? TranslateTo, string OutputFormat = "srt", bool RedoExisting = false);

/// <summary>What happened to one file (sent as it changes).</summary>
/// <param name="Fraction">Of this file's work (0..1).</param>
public sealed record QueueUpdate(int Index, QueueStep Step, double Fraction, string Text, string? TranscriptPath = null, string? TranslationPath = null);

public sealed record QueueResult(int Transcribed, int Translated, int Reused, int Skipped, int Failed, bool Cancelled,
    IReadOnlyList<string> TranslatedFiles, IReadOnlyList<NameGroup> NameSuggestions, int NameChanges);

/// <summary>
/// Many files, unattended: each one transcribed to "Name.ko.srt" (or the language detected) next to it,
/// then translated to "Name.en.srt". All files are transcribed first and then all translated, so each
/// model is loaded once. Subtitles a file already has are used (transcripts) or left alone (translations)
/// unless "redo" is chosen, so a stopped run carries on where it ended. A file that fails is noted and the
/// queue moves on. Names follow each show's names list and matching, as in the Translate tab.
/// </summary>
public sealed class SeasonQueue
{
    private readonly SubtitleFormatRegistry _formats;
    private readonly ActivityLog? _log;

    public SeasonQueue(SubtitleFormatRegistry formats, ActivityLog? log = null)
    {
        _formats = formats;
        _log = log;
    }

    /// <param name="files">Media files, in order.</param>
    /// <param name="durations">Their lengths when known (for progress).</param>
    /// <param name="namesFor">The names list of a show (by its name).</param>
    /// <param name="beforeTranslating">Called between the two halves (frees Whisper's graphics memory).</param>
    public async Task<QueueResult> RunAsync(IReadOnlyList<string> files, IReadOnlyList<TimeSpan?> durations, QueueOptions options,
        ITranscriptionService transcriber, ITranslationService? translator, Func<string, NameList> namesFor, bool nameMatching,
        Action? beforeTranslating, IProgress<QueueUpdate>? progress, CancellationToken ct)
    {
        var transcripts = new string?[files.Count];
        var failed = new bool[files.Count];
        int transcribed = 0, translated = 0, reused = 0, skipped = 0, failures = 0, nameChanges = 0;
        var translatedFiles = new List<string>();
        var ext = (_formats.ById(options.OutputFormat)?.Extensions.FirstOrDefault() ?? ".srt").TrimStart('.');
        _log?.Info("Batch", $"{files.Count} file(s): transcribe {(options.SpokenLanguage is null ? "(language detected in each file)" : "from " + WhisperLanguages.NameOf(options.SpokenLanguage))}"
            + (options.TranslateTo is { } t ? $", then translate into {WhisperLanguages.NameOf(t)}" : string.Empty) + $"; .{ext} files next to each one"
            + (options.RedoExisting ? "; existing subtitles made again." : "; subtitles already there are used or left alone."));

        // 1. Transcripts.
        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var file = files[i];
            var name = Path.GetFileName(file);
            try
            {
                var existing = options.RedoExisting ? null : FindTranscript(file, options.SpokenLanguage, options.TranslateTo);
                if (existing is not null)
                {
                    transcripts[i] = existing;
                    reused++;
                    _log?.Info("Batch", $"{name}: transcript already there ({Path.GetFileName(existing)}), used as it is.");
                    progress?.Report(new QueueUpdate(i, options.TranslateTo is null ? QueueStep.Done : QueueStep.Waiting, options.TranslateTo is null ? 1 : 0.5,
                        $"Transcript already there: {Path.GetFileName(existing)}", existing));
                    continue;
                }
                progress?.Report(new QueueUpdate(i, QueueStep.Transcribing, 0, "Transcribing..."));
                var fileProgress = new InlineProgress<EngineProgress>(p =>
                    progress?.Report(new QueueUpdate(i, QueueStep.Transcribing, (options.TranslateTo is null ? 1 : 0.5) * p.Fraction, p.Message)));
                var doc = await transcriber.TranscribeAsync(file, new TranscriptionOptions(options.SpokenLanguage, options.SpokenLanguage is null, options.TranscribeModel,
                    MediaDuration: i < durations.Count ? durations[i] : null), fileProgress, ct).ConfigureAwait(false);
                if (doc.Cues.Count == 0)
                {
                    failed[i] = true;
                    skipped++;
                    _log?.Warning("Batch", $"{name}: no speech found; nothing written.");
                    progress?.Report(new QueueUpdate(i, QueueStep.Skipped, 1, "No speech found"));
                    continue;
                }
                var path = Path.Combine(Path.GetDirectoryName(file)!, SidecarDetector.BuildSidecarName(Path.GetFileNameWithoutExtension(file), doc.Language, false, false, false, ext));
                Save(doc, path, options.OutputFormat);
                transcripts[i] = path;
                transcribed++;
                _log?.Success("Batch", $"{name}: {doc.Cues.Count} cues written to {Path.GetFileName(path)}.");
                progress?.Report(new QueueUpdate(i, options.TranslateTo is null ? QueueStep.Done : QueueStep.Waiting, options.TranslateTo is null ? 1 : 0.5,
                    $"{doc.Cues.Count} cues: {Path.GetFileName(path)}", path));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                progress?.Report(new QueueUpdate(i, QueueStep.Cancelled, 0, "Cancelled"));
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed[i] = true;
                failures++;
                _log?.Error("Batch", $"{name}: transcription failed: {ex.Message}");
                progress?.Report(new QueueUpdate(i, QueueStep.Failed, 0, "Transcription failed: " + ex.Message));
            }
        }

        // 2. Translations.
        var seasonCues = new List<SubtitleCue>();
        var shows = new List<string>();
        if (options.TranslateTo is { } target && translator is not null && !ct.IsCancellationRequested)
        {
            bool released = false;
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (transcripts[i] is not { } transcript || failed[i]) continue;
                var file = files[i];
                var name = Path.GetFileName(file);
                var outPath = Path.Combine(Path.GetDirectoryName(file)!, SidecarDetector.BuildSidecarName(Path.GetFileNameWithoutExtension(file), target, false, false, false, ext));
                try
                {
                    if (!options.RedoExisting && FindLanguage(file, target) is { } there)
                    {
                        skipped++;
                        _log?.Info("Batch", $"{name}: {WhisperLanguages.NameOf(target)} subtitles already there ({Path.GetFileName(there)}), left alone.");
                        progress?.Report(new QueueUpdate(i, QueueStep.Done, 1, $"{WhisperLanguages.NameOf(target)} subtitles already there: {Path.GetFileName(there)}", transcript, there));
                        continue;
                    }
                    var source = _formats.Load(transcript);
                    var sourceLanguage = source.Language ?? SidecarDetector.ParseStandalone(transcript).Language
                                         ?? SubtitleTranslationPrompt.ScriptLanguage(source.Cues.Select(c => c.Text));
                    if (sourceLanguage == target)
                    {
                        skipped++;
                        progress?.Report(new QueueUpdate(i, QueueStep.Done, 1, $"Already in {WhisperLanguages.NameOf(target)}", transcript));
                        continue;
                    }
                    if (!released)
                    {
                        beforeTranslating?.Invoke();
                        released = true;
                    }
                    var show = NameConsistency.ShowOf(file);
                    var names = namesFor(show);
                    if (translator is LocalLlmTranslator local) local.Names = names.Entries.Select(e => e.Name).ToList();
                    progress?.Report(new QueueUpdate(i, QueueStep.Translating, 0.5, "Translating...", transcript));
                    var fileProgress = new InlineProgress<EngineProgress>(p =>
                        progress?.Report(new QueueUpdate(i, QueueStep.Translating, 0.5 + 0.5 * p.Fraction, p.Message, transcript)));
                    var result = (await translator.TranslateAsync(source.Cues, sourceLanguage, target, fileProgress, ct).ConfigureAwait(false)).ToList();
                    var report = NameConsistency.Apply(result, names, nameMatching);
                    nameChanges += report.CuesChanged;
                    foreach (var c in report.Changes)
                        _log?.Info("Names", $"{name}: \"{c.From}\" → \"{c.To}\" in {c.Cues} cue{(c.Cues == 1 ? "" : "s")}" + (c.FromList ? " (names list)." : " (near-identical spelling)."));
                    var doc = new SubtitleDocument
                    {
                        Language = target, Format = source.Format, FormatHeader = source.FormatHeader, FormatTrailer = source.FormatTrailer,
                        FrameRate = source.FrameRate, Forced = source.Forced, HearingImpaired = source.HearingImpaired, Sdh = source.Sdh,
                    };
                    doc.Cues.AddRange(result);
                    Save(doc, outPath, options.OutputFormat);
                    translated++;
                    translatedFiles.Add(outPath);
                    seasonCues.AddRange(result.Select(c => c.Clone()));
                    if (!shows.Contains(show)) shows.Add(show);
                    _log?.Success("Batch", $"{name}: {result.Count} cues translated to {Path.GetFileName(outPath)}.");
                    progress?.Report(new QueueUpdate(i, QueueStep.Done, 1, $"Done: {Path.GetFileName(outPath)}", transcript, outPath));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    progress?.Report(new QueueUpdate(i, QueueStep.Cancelled, 0.5, "Cancelled", transcript));
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    _log?.Error("Batch", $"{name}: translation failed: {ex.Message}");
                    progress?.Report(new QueueUpdate(i, QueueStep.Failed, 0.5, "Translation failed: " + ex.Message, transcript));
                }
            }
        }

        // Look-alike names across the whole run (what one episode alone may not show).
        var suggestions = shows.Count == 1 && seasonCues.Count > 0
            ? NameConsistency.Apply(seasonCues, namesFor(shows[0]), nameMatching).Suggestions
            : Array.Empty<NameGroup>();
        bool cancelled = ct.IsCancellationRequested;
        _log?.Info("Batch", (cancelled ? "Stopped: " : "Finished: ") + $"{transcribed} transcribed, {reused} transcript(s) already there, {translated} translated, {skipped} skipped, {failures} failed."
            + (suggestions.Count > 0 ? $" {suggestions.Count} group(s) of look-alike names to check." : string.Empty));
        return new QueueResult(transcribed, translated, reused, skipped, failures, cancelled, translatedFiles, suggestions, nameChanges);
    }

    /// <summary>The names list (and matching) on files already written; returns the cues changed per file.</summary>
    public int ApplyNames(IEnumerable<string> subtitleFiles, Func<string, NameList> namesFor, bool nameMatching)
    {
        int total = 0;
        foreach (var path in subtitleFiles)
        {
            if (!File.Exists(path)) continue;
            var doc = _formats.Load(path);
            var report = NameConsistency.Apply(doc.Cues, namesFor(NameConsistency.ShowOf(path)), nameMatching);
            if (report.CuesChanged == 0) continue;
            var format = _formats.ForExtension(Path.GetExtension(path))?.Id ?? "srt";
            Save(doc, path, format);
            total += report.CuesChanged;
            _log?.Info("Names", $"{Path.GetFileName(path)}: names made consistent in {report.CuesChanged} cue(s).");
        }
        return total;
    }

    private void Save(SubtitleDocument doc, string path, string formatId)
    {
        if (formatId == "sub") doc.FrameRate ??= MicroDvdFormat.DefaultFrameRate;
        _formats.Save(doc, path, formatId);
    }

    /// <summary>
    /// A transcript the file already has: subtitles next to it in the spoken language (when chosen), or with
    /// "detect", in any language other than the one to translate into.
    /// </summary>
    public static string? FindTranscript(string file, string? spoken, string? translateTo)
    {
        foreach (var s in Sidecars(file))
        {
            if (s.Forced || s.Language is null) continue;
            if (spoken is not null ? Same(s.Language, spoken) : !Same(s.Language, translateTo)) return s.FilePath;
        }
        return null;
    }

    /// <summary>Subtitles next to the file in this language (not forced-only ones).</summary>
    public static string? FindLanguage(string file, string language)
        => Sidecars(file).FirstOrDefault(s => !s.Forced && Same(s.Language, language))?.FilePath;

    private static IReadOnlyList<SidecarInfo> Sidecars(string file)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (!Directory.Exists(folder)) return Array.Empty<SidecarInfo>();
        return SidecarDetector.Detect(file, Directory.EnumerateFiles(folder))
            .Where(s => s.Format is not ("idx" or "sup") && !s.FilePath.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) && !s.FilePath.EndsWith(".sup", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool Same(string? a, string? b)
        => a is not null && b is not null && a.Split('-', '_')[0].Equals(b.Split('-', '_')[0], StringComparison.OrdinalIgnoreCase);
}
