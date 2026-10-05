using System.Diagnostics;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>What a translation did, for the status bar and the Log.</summary>
public sealed record TranslationStats(int Cues, int Translated, int KeptAsIs, int Failed, TimeSpan Loading, TimeSpan Translating, string Device, string? DeviceProblem,
    int LeftoversFixed = 0, int LeftoversRemaining = 0, int Mismatched = 0, int Realigned = 0)
{
    public double CuesPerMinute => Translating.TotalMinutes > 0 ? Translated / Translating.TotalMinutes : 0;
}

/// <summary>
/// Subtitles to another language with a language model on this PC (llama.cpp in the app). Cues go in
/// numbered batches with the lines before them (and their translations) as context; a grammar makes the
/// model answer with exactly one numbered line per cue. Timings, positions and italics are kept.
/// </summary>
public sealed class LocalLlmTranslator : ITranslationService, IDisposable
{
    public const string EngineId = "llama-cpp";

    private readonly ILlmRunner _runner;
    private readonly ActivityLog? _log;

    public LocalLlmTranslator(ILlmRunner runner, ActivityLog? log = null)
    {
        _runner = runner;
        _log = log;
    }

    public string Id => EngineId;
    public string DisplayName => "On this PC (language model)";
    public string Description => "Free and private: the subtitles never leave this PC, and it works offline. Runs on the graphics card when it can, otherwise on the processor (much slower).";
    public bool IsLocal => true;
    public bool RequiresApiKey => false;
    public string? ApiKeyProviderId => null;

    /// <summary>The GGUF model to use (set before each run).</summary>
    public string? ModelPath { get; set; }

    public WhisperDevice Device { get; set; } = WhisperDevice.Processor;

    /// <summary>Cues per question to the model.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>Earlier cues (with their translations) sent along for context.</summary>
    public int ContextLines { get; set; } = 8;

    /// <summary>The show's names, spelled the way the user wants (from the names list): given to the model.</summary>
    public IReadOnlyList<string> Names { get; set; } = Array.Empty<string>();

    /// <summary>Ask again, line by line, when a translation still has words in the original script.</summary>
    public bool RetryLeftovers { get; set; } = true;

    /// <summary>
    /// After translating, ask the model whether each translation says what its own line says, and translate
    /// the lines that don't again one at a time (catches lines shifted by one within a batch).
    /// </summary>
    public bool CheckAlignment { get; set; } = true;

    /// <summary>Pairs per question in the check.</summary>
    public int CheckBatchSize { get; set; } = 16;

    public int MaxColumns { get; set; } = 42;

    public TranslationStats? LastStats { get; private set; }

    public ILlmRunner Runner => _runner;

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(IReadOnlyList<SubtitleCue> cues, string? sourceLanguage, string targetLanguage,
        IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        if (ModelPath is not { Length: > 0 } modelPath || !File.Exists(modelPath))
            throw new InvalidOperationException("Choose a language model first (download one in the Model section).");
        string targetName = WhisperLanguages.NameOf(targetLanguage);
        string? sourceName = string.IsNullOrWhiteSpace(sourceLanguage) ? null : WhisperLanguages.NameOf(sourceLanguage);
        _log?.Info("Translate", $"{cues.Count} cues from {sourceName ?? "the language they are in"} to {targetName} with {Path.GetFileName(modelPath)}, requested device: {Device.Label}.");

        // 1. Model.
        var clock = Stopwatch.StartNew();
        progress?.Report(new EngineProgress(0, Device.UseGpu ? $"Loading the language model onto {Device.Label}..." : "Loading the language model..."));
        await _runner.LoadAsync(modelPath, Device, ct).ConfigureAwait(false);
        var loading = clock.Elapsed;
        var device = _runner.DeviceUsed ?? Device.Label;
        _log?.Info("Translate", $"Model loaded in {loading.TotalSeconds:0.0} s; runs on {device}.");
        if (_runner.DeviceProblem is { } problem) _log?.Warning("Translate", problem);
        bool noThinking = _runner.Architecture is { } arch && arch.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase);

        // 2. Lines.
        var prepared = cues.Select(c => SubtitleTranslationPrompt.Prepare(c.Text)).ToList();
        var todo = Enumerable.Range(0, cues.Count).Where(i => !prepared[i].IsEmpty).ToList();
        var translations = new string?[cues.Count];
        var history = new List<(string Source, string Translation)>();
        var system = SubtitleTranslationPrompt.SystemMessage(sourceName, targetName, Names);
        int done = 0, failed = 0, leftoversFixed = 0, leftoversRemaining = 0;
        double translateShare = CheckAlignment ? 0.88 : 0.98;
        clock.Restart();
        progress?.Report(new EngineProgress(0.02, $"Translating on {device}... 0 of {todo.Count}"));

        async Task TranslateBatch(List<int> batch)
        {
            ct.ThrowIfCancellationRequested();
            var lines = batch.Select(i => prepared[i].Text).ToList();
            var context = history.Skip(Math.Max(0, history.Count - ContextLines)).ToList();
            var user = SubtitleTranslationPrompt.UserMessage(context, lines, noThinking);
            var grammar = SubtitleTranslationPrompt.Grammar(lines.Count);
            int maxTokens = 32 + lines.Sum(l => Math.Max(48, l.Length * 3));
            string output;
            try
            {
                output = await _runner.CompleteAsync(system, user, grammar, maxTokens, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && batch.Count > 1)
            {
                _log?.Detail("Translate", $"A batch of {batch.Count} lines failed ({ex.Message}); trying in smaller pieces.");
                output = string.Empty;
            }
            var parsed = SubtitleTranslationPrompt.Parse(output, lines.Count);
            if (parsed.Any(p => p.Length == 0))
            {
                if (batch.Count > 1)
                {
                    // Cut short or a line missing: the two halves separately.
                    int half = batch.Count / 2;
                    await TranslateBatch(batch.Take(half).ToList()).ConfigureAwait(false);
                    await TranslateBatch(batch.Skip(half).ToList()).ConfigureAwait(false);
                    return;
                }
                failed++;
                _log?.Warning("Translate", $"Cue {cues[batch[0]].Index}: no translation came back; the original text is kept.");
                parsed = new[] { string.Empty };
            }
            // Words left in the original script ("Han 선생님"): that line once more, on its own, with a reminder.
            if (RetryLeftovers)
                for (int k = 0; k < batch.Count; k++)
                {
                    if (!SubtitleTranslationPrompt.HasLeftoverScript(parsed[k], targetLanguage)) continue;
                    var retryContext = history.Skip(Math.Max(0, history.Count - ContextLines)).ToList();
                    var retryUser = SubtitleTranslationPrompt.UserMessage(retryContext, new[] { lines[k] }) + "\n\n" + SubtitleTranslationPrompt.LeftoverReminder(targetName)
                                    + (noThinking ? "\n/no_think" : string.Empty);
                    string again;
                    try
                    {
                        again = SubtitleTranslationPrompt.Parse(await _runner.CompleteAsync(system, retryUser, SubtitleTranslationPrompt.Grammar(1), 32 + Math.Max(48, lines[k].Length * 3), null, ct).ConfigureAwait(false), 1)[0];
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        again = string.Empty;
                    }
                    if (again.Length > 0 && !SubtitleTranslationPrompt.HasLeftoverScript(again, targetLanguage))
                    {
                        parsed = parsed.Select((p, j) => j == k ? again : p).ToList();
                        leftoversFixed++;
                    }
                    else
                    {
                        leftoversRemaining++;
                        _log?.Warning("Translate", $"Cue {cues[batch[k]].Index}: still has untranslated words: \"{parsed[k]}\".");
                    }
                }
            for (int k = 0; k < batch.Count; k++)
            {
                int i = batch[k];
                if (parsed[k].Length > 0)
                {
                    translations[i] = parsed[k];
                    history.Add((prepared[i].Text, parsed[k]));
                }
                done++;
            }
            var lastIndex = batch[^1];
            var latest = translations[lastIndex] is { } t ? $"{prepared[lastIndex].Text}  →  {t}" : null;
            double fraction = todo.Count == 0 ? 1 : done / (double)todo.Count;
            progress?.Report(new EngineProgress(0.02 + translateShare * fraction, $"Translating on {device}... {done} of {todo.Count}", latest));
        }

        for (int start = 0; start < todo.Count; start += BatchSize)
            await TranslateBatch(todo.Skip(start).Take(BatchSize).ToList()).ConfigureAwait(false);

        // 3. The check: does each translation say what its own line says? (A model can carry a sentence
        //    split over two cues into the first one, and every later line in the batch then slides up by one.)
        int checkedCount = 0, mismatched = 0, realigned = 0;
        if (CheckAlignment)
            (checkedCount, mismatched, realigned) = await CheckAndRealignAsync(cues, prepared, todo, translations, system, sourceName, targetName, targetLanguage,
                noThinking, device, progress, ct).ConfigureAwait(false);
        var translating = clock.Elapsed;

        // 4. Back into cues: same times, tags and positions.
        var result = new List<SubtitleCue>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i].Clone();
            if (translations[i] is { } t) cue.Text = SubtitleTranslationPrompt.Restore(prepared[i], t, MaxColumns);
            result.Add(cue);
        }

        int translated = translations.Count(t => t is not null);
        LastStats = new TranslationStats(cues.Count, translated, cues.Count - todo.Count, failed, loading, translating, device, _runner.DeviceProblem,
            leftoversFixed, leftoversRemaining, mismatched, realigned);
        _log?.Success("Translate", $"{translated} of {cues.Count} cues translated to {targetName} in {Format(translating)} on {device}"
            + (LastStats.KeptAsIs > 0 ? $"; {LastStats.KeptAsIs} with nothing to translate (music, symbols) kept as they were" : string.Empty)
            + (failed > 0 ? $"; {failed} couldn't be translated and kept the original text" : string.Empty)
            + (leftoversFixed + leftoversRemaining > 0 ? $"; {leftoversFixed + leftoversRemaining} came back with words in the original script and were asked again ({leftoversRemaining} still have some, listed above)" : string.Empty)
            + (CheckAlignment ? $"; checked {checkedCount} lines against their originals: {(mismatched == 0 ? "all matched" : $"{mismatched} didn't match their own line, {realigned} translated again on their own (listed above)")}" : string.Empty) + ".");
        return result;
    }

    /// <summary>
    /// Other ways to translate one cue, for reviewing: the model sees the lines before it (with their
    /// translations), the lines after it, the current translation and the reviewer's note, and writes
    /// <paramref name="count"/> differently worded translations. Tags, italics and position are kept.
    /// The model is loaded if it isn't already (and stays loaded for the next line).
    /// </summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(IReadOnlyList<(string Source, string Translation)> before, string cueText, string? currentTranslation,
        IReadOnlyList<string> after, string? sourceLanguage, string targetLanguage, string? hint, int count, CancellationToken ct)
    {
        if (ModelPath is not { Length: > 0 } modelPath || !File.Exists(modelPath))
            throw new InvalidOperationException("Choose a language model first (download one in the Model section of Translate).");
        await _runner.LoadAsync(modelPath, Device, ct).ConfigureAwait(false);
        bool noThinking = _runner.Architecture is { } arch && arch.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase);
        string targetName = WhisperLanguages.NameOf(targetLanguage);
        string? sourceName = string.IsNullOrWhiteSpace(sourceLanguage) ? null : WhisperLanguages.NameOf(sourceLanguage);
        var prepared = SubtitleTranslationPrompt.Prepare(cueText);
        if (prepared.IsEmpty) return Array.Empty<string>();
        var currentLine = currentTranslation is null ? null : SubtitleTranslationPrompt.Prepare(currentTranslation).Text;
        var system = SubtitleTranslationPrompt.SystemMessage(sourceName, targetName, Names);
        var user = SubtitleTranslationPrompt.AlternativesMessage(before.Skip(Math.Max(0, before.Count - ContextLines)).ToList(), prepared.Text, currentLine,
            after.Select(a => SubtitleTranslationPrompt.Prepare(a).Text).Where(a => a.Length > 0).Take(3).ToList(), hint, count, targetName, noThinking);
        var output = await _runner.CompleteAsync(system, user, SubtitleTranslationPrompt.Grammar(count), 32 + count * Math.Max(64, prepared.Text.Length * 3), null, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var leftovers = new List<string>();
        foreach (var line in SubtitleTranslationPrompt.Parse(output, count))
        {
            if (line.Length == 0) continue;
            var restored = SubtitleTranslationPrompt.Restore(prepared, line, MaxColumns);
            if (!seen.Add(restored)) continue;
            // Words left in the original script ("Han 선생님") go last, and only when there aren't two clean ones.
            if (SubtitleTranslationPrompt.HasLeftoverScript(restored, targetLanguage)) leftovers.Add(restored);
            else result.Add(restored);
        }
        if (result.Count < 2) result.AddRange(leftovers);
        _log?.Info("Translate", $"Suggestions for \"{prepared.Text}\"" + (string.IsNullOrWhiteSpace(hint) ? "" : $" (note: {hint.Trim()})") + ": "
            + string.Join(" | ", result.Select(r => r.Replace("\n", " "))));
        return result;
    }

    private async Task<(int Checked, int Mismatched, int Realigned)> CheckAndRealignAsync(IReadOnlyList<SubtitleCue> cues, List<PreparedLine> prepared,
        List<int> todo, string?[] translations, string system, string? sourceName, string targetName, string targetLanguage, bool noThinking, string device,
        IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var translated = todo.Where(i => translations[i] is not null).ToList();
        if (translated.Count == 0) return (0, 0, 0);
        progress?.Report(new EngineProgress(0.90, $"Checking the translation on {device}... 0 of {translated.Count}"));
        var checkSystem = SubtitleTranslationPrompt.CheckSystemMessage(sourceName, targetName);
        var flagged = new List<int>();
        int checkedCount = 0;
        int size = Math.Max(1, CheckBatchSize);
        for (int start = 0; start < translated.Count; start += size)
        {
            ct.ThrowIfCancellationRequested();
            var batch = translated.Skip(start).Take(size).ToList();
            var pairs = batch.Select(i => (prepared[i].Text, translations[i]!)).ToList();
            try
            {
                var output = await _runner.CompleteAsync(checkSystem, SubtitleTranslationPrompt.CheckMessage(pairs, noThinking),
                    SubtitleTranslationPrompt.CheckGrammar(batch.Count), 16 + batch.Count * 8, null, ct).ConfigureAwait(false);
                var wrong = SubtitleTranslationPrompt.ParseCheck(output, batch.Count);
                for (int k = 0; k < batch.Count; k++)
                    if (wrong[k]) flagged.Add(batch[k]);
                checkedCount += batch.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Detail("Translate", $"Checking {batch.Count} lines failed ({ex.Message}); they are left as they are.");
            }
            double share = flagged.Count > 0 ? 0.05 : 0.10;
            progress?.Report(new EngineProgress(0.90 + share * Math.Min(1, (start + batch.Count) / (double)translated.Count),
                $"Checking the translation on {device}... {Math.Min(translated.Count, start + batch.Count)} of {translated.Count}"));
        }
        if (flagged.Count == 0)
        {
            _log?.Info("Translate", $"Check: all {checkedCount} translations match their own lines.");
            return (checkedCount, 0, 0);
        }
        _log?.Info("Translate", $"Check: {flagged.Count} translation{(flagged.Count == 1 ? "" : "s")} didn't match their own line (cue{(flagged.Count == 1 ? "" : "s")} "
            + string.Join(", ", flagged.Select(i => cues[i].Index)) + "); translating them again one at a time.");

        // Each flagged line on its own: the accepted lines before it (with the ones fixed just now) and the next lines as context.
        int realigned = 0, n = 0;
        var position = todo.Select((cue, pos) => (cue, pos)).ToDictionary(p => p.cue, p => p.pos);
        foreach (var i in flagged)
        {
            ct.ThrowIfCancellationRequested();
            int pos = position[i];
            var before = new List<(string, string)>();
            for (int p = pos - 1; p >= 0 && before.Count < ContextLines; p--)
                if (translations[todo[p]] is { } t) before.Insert(0, (prepared[todo[p]].Text, t));
            var after = todo.Skip(pos + 1).Take(3).Select(j => prepared[j].Text).ToList();
            var user = SubtitleTranslationPrompt.UserMessage(before, new[] { prepared[i].Text }, false, after)
                       + "\n\n" + SubtitleTranslationPrompt.FragmentReminder(targetName) + (noThinking ? "\n/no_think" : string.Empty);
            string again;
            try
            {
                again = SubtitleTranslationPrompt.Parse(await _runner.CompleteAsync(system, user, SubtitleTranslationPrompt.Grammar(1),
                    32 + Math.Max(48, prepared[i].Text.Length * 3), null, ct).ConfigureAwait(false), 1)[0];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                again = string.Empty;
            }
            var old = translations[i]!;
            // Kept as it was when nothing came back, or the new one has words in the original script and the old one didn't.
            if (again.Length == 0 || (SubtitleTranslationPrompt.HasLeftoverScript(again, targetLanguage) && !SubtitleTranslationPrompt.HasLeftoverScript(old, targetLanguage)))
                _log?.Info("Translate", $"Cue {cues[i].Index}: translating again gave nothing better; kept \"{old}\".");
            else
            {
                translations[i] = again;
                if (!string.Equals(again, old, StringComparison.Ordinal)) realigned++;
                _log?.Info("Translate", $"Cue {cues[i].Index}: \"{prepared[i].Text}\": \"{old}\" → \"{again}\".");
            }
            n++;
            progress?.Report(new EngineProgress(0.95 + 0.05 * n / flagged.Count, $"Translating again the lines that didn't match... {n} of {flagged.Count}",
                $"{prepared[i].Text}  →  {translations[i]}"));
        }
        return (checkedCount, flagged.Count, realigned);
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public void Dispose() => _runner.Dispose();
}
