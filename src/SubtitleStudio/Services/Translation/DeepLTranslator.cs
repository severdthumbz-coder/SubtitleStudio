using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>
/// Online translation with DeepL (optional, needs the user's own API key). The subtitles are stripped to their
/// translatable text with the same helper the local engine uses (<see cref="SubtitleTranslationPrompt.Prepare"/>),
/// translated by DeepL, then put back as cues with their tags, italics, speaker dashes and line wrapping restored
/// (<see cref="SubtitleTranslationPrompt.Restore"/>) — so formatting is handled identically to the local engine and
/// DeepL only does the wording. A Free key (ending ":fx") uses the api-free.deepl.com endpoint; a Pro key uses
/// api.deepl.com. The key is read fresh from the key store on each run so a newly saved key takes effect at once.
/// </summary>
public sealed class DeepLTranslator : ITranslationService
{
    public const string EngineId = "deepl";

    private const string FreeHost = "https://api-free.deepl.com";
    private const string ProHost = "https://api.deepl.com";

    // DeepL translates in batches of many texts per request; keep requests well under its size limits.
    private const int BatchSize = 50;

    private readonly Func<string?> _keyProvider;
    private readonly ActivityLog? _log;

    /// <param name="keyProvider">Returns the current DeepL key (null when none is saved).</param>
    public DeepLTranslator(Func<string?> keyProvider, ActivityLog? log = null)
    {
        _keyProvider = keyProvider;
        _log = log;
    }

    public string Id => EngineId;
    public string DisplayName => "DeepL (online)";
    public string Description => "High-quality online translation with your DeepL key. Sends the subtitle text to DeepL's servers (so it isn't private or offline), but is fast and often reads more naturally. The Free plan covers 500,000 characters a month.";
    public bool IsLocal => false;
    public bool RequiresApiKey => true;
    public string? ApiKeyProviderId => ProviderIds.DeepL;

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(IReadOnlyList<SubtitleCue> cues, string? sourceLanguage, string targetLanguage,
        IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var key = _keyProvider()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("No DeepL key is set. Add it in Settings > API keys.");

        string host = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? FreeHost : ProHost;
        string? target = DeepLTarget(targetLanguage);
        if (target is null)
            throw new InvalidOperationException($"DeepL doesn't translate into {WhisperLanguages.NameOf(targetLanguage)}. Use the on-this-PC engine for that language.");
        string? source = DeepLSource(sourceLanguage);

        _log?.Info("Translate", $"{cues.Count} cues to {WhisperLanguages.NameOf(targetLanguage)} with DeepL ({(host == FreeHost ? "Free" : "Pro")} plan)"
            + (source is null ? ", source language detected by DeepL." : $", from {WhisperLanguages.NameOf(sourceLanguage!)}."));

        // Separate the translatable text from tags/prefix/italics, exactly as the local engine does.
        var prepared = cues.Select(c => SubtitleTranslationPrompt.Prepare(c.Text)).ToList();
        var todo = Enumerable.Range(0, cues.Count).Where(i => !prepared[i].IsEmpty).ToList();
        var translations = new string?[cues.Count];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);

        int done = 0;
        progress?.Report(new EngineProgress(0.02, $"Translating with DeepL... 0 of {todo.Count}"));
        for (int start = 0; start < todo.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = todo.Skip(start).Take(BatchSize).ToList();
            var texts = batch.Select(i => prepared[i].Text).ToList();
            var results = await RequestAsync(http, host, texts, source, target, ct).ConfigureAwait(false);
            for (int k = 0; k < batch.Count && k < results.Count; k++)
                translations[batch[k]] = results[k];
            done += batch.Count;
            progress?.Report(new EngineProgress(0.02 + 0.96 * done / Math.Max(1, todo.Count), $"Translating with DeepL... {done} of {todo.Count}"));
        }

        var output = new List<SubtitleCue>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i].Clone();
            cue.Text = SubtitleTranslationPrompt.Restore(prepared[i], translations[i] ?? string.Empty);
            output.Add(cue);
        }
        progress?.Report(new EngineProgress(1, $"Translated {todo.Count} lines with DeepL."));
        _log?.Success("Translate", $"DeepL translated {todo.Count} of {cues.Count} lines.");
        return output;
    }

    private async Task<IReadOnlyList<string>> RequestAsync(HttpClient http, string host, IReadOnlyList<string> texts, string? source, string target, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("target_lang", target),
            new("tag_handling", "xml"),       // leave any markup we didn't strip untouched
            new("split_sentences", "nonewlines"),
            new("preserve_formatting", "1"),
        };
        if (source is not null) form.Add(new("source_lang", source));
        foreach (var t in texts) form.Add(new("text", t));

        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"{host}/v2/translate", content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Couldn't reach DeepL (check your internet connection): " + ex.Message, ex);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("DeepL rejected the key (403). Check the key in Settings > API keys — a Free key must end in \":fx\".");
        if (response.StatusCode == (HttpStatusCode)456)
            throw new ServiceQuotaException(ProviderIds.DeepL, "DeepL's character limit for this plan has been reached (the Free plan allows 500,000 characters a month). It resets at the start of your billing month.");
        if (response.StatusCode == (HttpStatusCode)429)
            throw new ServiceQuotaException(ProviderIds.DeepL, "DeepL is rate-limiting the requests (429). Try again in a moment.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"DeepL returned {(int)response.StatusCode}: {Trim(body)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = new List<string>(texts.Count);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("translations", out var arr))
            foreach (var t in arr.EnumerateArray())
                list.Add(t.TryGetProperty("text", out var txt) ? txt.GetString() ?? string.Empty : string.Empty);
        return list;
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200] + "...";

    /// <summary>This month's character use for a DeepL key: characters used and the plan's limit.</summary>
    public readonly record struct Usage(long Used, long Limit)
    {
        public double Fraction => Limit <= 0 ? 0 : Math.Clamp((double)Used / Limit, 0, 1);
    }

    /// <summary>
    /// Asks DeepL how much of the character allowance this key has used this month (the /v2/usage endpoint).
    /// Returns null when there's no key or the request fails. Picks the Free or Pro host from the key, like translation.
    /// </summary>
    public static async Task<Usage?> GetUsageAsync(string? key, CancellationToken ct = default)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        string host = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? FreeHost : ProHost;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
            using var response = await http.GetAsync($"{host}/v2/usage", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            long used = doc.RootElement.TryGetProperty("character_count", out var c) ? c.GetInt64() : 0;
            long limit = doc.RootElement.TryGetProperty("character_limit", out var l) ? l.GetInt64() : 0;
            return new Usage(used, limit);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>App language code (ISO 639-1) to a DeepL target code; null when DeepL can't translate into it.</summary>
    public static string? DeepLTarget(string code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "en" => "EN-US",
        "pt" => "PT-PT",
        "zh" => "ZH",
        _ => Supported.Contains((code ?? string.Empty).ToLowerInvariant()) ? code!.ToUpperInvariant() : null,
    };

    /// <summary>App language code to a DeepL source code; null means let DeepL detect it.</summary>
    public static string? DeepLSource(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.ToLowerInvariant();
        if (c is "en") return "EN";
        if (c is "pt") return "PT";
        return Supported.Contains(c) ? c.ToUpperInvariant() : null;
    }

    // DeepL's supported languages (ISO 639-1, lower case). en/pt/zh are mapped above to their variant codes.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hu", "id", "it", "ja", "ko",
        "lt", "lv", "nb", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "zh",
    };
}
