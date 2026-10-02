using System.Diagnostics;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>What a translation did, for the status bar and the Log.</summary>
public sealed record TranslationStats(int Cues, int Translated, int KeptAsIs, int Failed, TimeSpan Loading, TimeSpan Translating, string Device, string? DeviceProblem,
    int LeftoversFixed = 0, int LeftoversRemaining = 0)
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
            progress?.Report(new EngineProgress(0.02 + 0.98 * fraction, $"Translating on {device}... {done} of {todo.Count}", latest));
        }

        for (int start = 0; start < todo.Count; start += BatchSize)
            await TranslateBatch(todo.Skip(start).Take(BatchSize).ToList()).ConfigureAwait(false);
        var translating = clock.Elapsed;

        // 3. Back into cues: same times, tags and positions.
        var result = new List<SubtitleCue>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i].Clone();
            if (translations[i] is { } t) cue.Text = SubtitleTranslationPrompt.Restore(prepared[i], t, MaxColumns);
            result.Add(cue);
        }

        int translated = translations.Count(t => t is not null);
        LastStats = new TranslationStats(cues.Count, translated, cues.Count - todo.Count, failed, loading, translating, device, _runner.DeviceProblem,
            leftoversFixed, leftoversRemaining);
        _log?.Success("Translate", $"{translated} of {cues.Count} cues translated to {targetName} in {Format(translating)} on {device}"
            + (LastStats.KeptAsIs > 0 ? $"; {LastStats.KeptAsIs} with nothing to translate (music, symbols) kept as they were" : string.Empty)
            + (failed > 0 ? $"; {failed} couldn't be translated and kept the original text" : string.Empty)
            + (leftoversFixed + leftoversRemaining > 0 ? $"; {leftoversFixed + leftoversRemaining} came back with words in the original script and were asked again ({leftoversRemaining} still have some, listed above)" : string.Empty) + ".");
        return result;
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public void Dispose() => _runner.Dispose();
}
