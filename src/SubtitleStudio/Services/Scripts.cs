namespace SubtitleStudio.Services;

/// <summary>The writing systems the app tells apart. Latin covers English words that turn up in any language.</summary>
public enum Script
{
    Other, Latin, Hangul, Kana, Han, Cyrillic, Greek, Arabic, Hebrew, Thai, Devanagari, Bengali, Gurmukhi, Gujarati,
    Tamil, Telugu, Kannada, Malayalam, Sinhala, Georgian, Armenian, Khmer, Lao, Myanmar, Ethiopic, Tibetan,
}

/// <summary>
/// Which script a letter belongs to, which scripts a language is written in, and which language a script
/// points to. Used to catch words left in the original script after translating, lines that mix two
/// languages, a "From" language that the letters contradict, and to compare lines of the same kind.
/// </summary>
public static class Scripts
{
    public static Script Of(char c)
    {
        if (!char.IsLetter(c)) return Script.Other;
        return c switch
        {
            <= 'ɏ' or >= 'Ḁ' and <= 'ỿ' => Script.Latin, // Basic Latin, Latin-1, Extended A/B, Extended Additional (Vietnamese)
            >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿' => Script.Greek,
            >= 'Ѐ' and <= 'ԯ' => Script.Cyrillic,
            >= '԰' and <= '֏' => Script.Armenian,
            >= '֐' and <= '׿' => Script.Hebrew,
            >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ࢠ' and <= 'ࣿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Arabic,
            >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
            >= 'ঀ' and <= '৿' => Script.Bengali,
            >= '਀' and <= '੿' => Script.Gurmukhi,
            >= '઀' and <= '૿' => Script.Gujarati,
            >= '஀' and <= '௿' => Script.Tamil,
            >= 'ఀ' and <= '౿' => Script.Telugu,
            >= 'ಀ' and <= '೿' => Script.Kannada,
            >= 'ഀ' and <= 'ൿ' => Script.Malayalam,
            >= '඀' and <= '෿' => Script.Sinhala,
            >= '฀' and <= '๿' => Script.Thai,
            >= '຀' and <= '໿' => Script.Lao,
            >= 'ༀ' and <= '࿿' => Script.Tibetan,
            >= 'က' and <= '႟' => Script.Myanmar,
            >= 'Ⴀ' and <= 'ჿ' => Script.Georgian,
            >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' or >= '가' and <= '힯' => Script.Hangul,
            >= 'ሀ' and <= '᎟' => Script.Ethiopic,
            >= 'ក' and <= '៿' => Script.Khmer,
            >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ' => Script.Kana,
            >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿' => Script.Han,
            _ => Script.Other,
        };
    }

    /// <summary>The scripts a language is written in (Whisper's codes). Unknown codes and Latin-alphabet languages: Latin.</summary>
    public static IReadOnlyList<Script> Of(string? language) => (language ?? string.Empty).Split('-', '_')[0].ToLowerInvariant() switch
    {
        "ko" => new[] { Script.Hangul, Script.Han },
        "ja" => new[] { Script.Kana, Script.Han },
        "zh" or "yue" => new[] { Script.Han },
        "ru" or "uk" or "be" or "bg" or "mk" or "kk" or "ky" or "tg" or "mn" or "ba" or "tt" => new[] { Script.Cyrillic },
        "sr" => new[] { Script.Cyrillic, Script.Latin },
        "el" => new[] { Script.Greek },
        "ar" or "fa" or "ur" or "ps" or "sd" or "ug" => new[] { Script.Arabic },
        "he" or "yi" => new[] { Script.Hebrew },
        "th" => new[] { Script.Thai },
        "hi" or "mr" or "ne" or "sa" => new[] { Script.Devanagari },
        "bn" or "as" => new[] { Script.Bengali },
        "pa" => new[] { Script.Gurmukhi },
        "gu" => new[] { Script.Gujarati },
        "ta" => new[] { Script.Tamil },
        "te" => new[] { Script.Telugu },
        "kn" => new[] { Script.Kannada },
        "ml" => new[] { Script.Malayalam },
        "si" => new[] { Script.Sinhala },
        "ka" => new[] { Script.Georgian },
        "hy" => new[] { Script.Armenian },
        "km" => new[] { Script.Khmer },
        "lo" => new[] { Script.Lao },
        "my" => new[] { Script.Myanmar },
        "am" => new[] { Script.Ethiopic },
        "bo" => new[] { Script.Tibetan },
        _ => new[] { Script.Latin },
    };

    /// <summary>The language a script points to when there's one obvious one (Cyrillic, Arabic and Devanagari are written for several: null).</summary>
    public static string? LanguageOf(Script script) => script switch
    {
        Script.Hangul => "ko",
        Script.Kana => "ja",
        Script.Han => "zh",
        Script.Greek => "el",
        Script.Hebrew => "he",
        Script.Thai => "th",
        Script.Bengali => "bn",
        Script.Gurmukhi => "pa",
        Script.Gujarati => "gu",
        Script.Tamil => "ta",
        Script.Telugu => "te",
        Script.Kannada => "kn",
        Script.Malayalam => "ml",
        Script.Sinhala => "si",
        Script.Georgian => "ka",
        Script.Armenian => "hy",
        Script.Khmer => "km",
        Script.Lao => "lo",
        Script.Myanmar => "my",
        Script.Ethiopic => "am",
        Script.Tibetan => "bo",
        _ => null,
    };

    /// <summary>Languages Whisper may be hearing when a line is written in this script (for re-checking a line that mixes scripts).</summary>
    public static IReadOnlyList<string> CandidatesFor(Script script) => script switch
    {
        Script.Kana => new[] { "ja" },
        Script.Han => new[] { "ja", "zh" },
        Script.Cyrillic => new[] { "ru", "uk" },
        Script.Arabic => new[] { "ar", "fa", "ur" },
        Script.Devanagari => new[] { "hi" },
        _ => LanguageOf(script) is { } l ? new[] { l } : Array.Empty<string>(),
    };

    /// <summary>Letters of each script in the text (non-letters left out).</summary>
    public static Dictionary<Script, int> Count(string? text)
    {
        var counts = new Dictionary<Script, int>();
        foreach (var c in text ?? string.Empty)
        {
            var s = Of(c);
            if (s != Script.Other) counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        return counts;
    }

    /// <summary>
    /// The script most of the letters are in, with Japanese counted together (kana with kanji). Latin only when
    /// there's nothing else. Other: no letters.
    /// </summary>
    public static Script Dominant(string? text)
    {
        var counts = Count(text);
        if (counts.GetValueOrDefault(Script.Kana) > 0) counts[Script.Kana] += counts.GetValueOrDefault(Script.Han);
        if (counts.GetValueOrDefault(Script.Kana) > 0) counts.Remove(Script.Han);
        var nonLatin = counts.Where(c => c.Key != Script.Latin).ToList();
        if (nonLatin.Count > 0) return nonLatin.MaxBy(c => c.Value).Key;
        return counts.ContainsKey(Script.Latin) ? Script.Latin : Script.Other;
    }

    /// <summary>Letters that don't belong in <paramref name="language"/> (Latin is always allowed: names, brands, English words).</summary>
    public static bool HasForeignLetters(string? text, string language)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var allowed = Of(language);
        foreach (var c in text)
        {
            var s = Of(c);
            if (s is Script.Other or Script.Latin) continue;
            if (!allowed.Contains(s)) return true;
        }
        return false;
    }

    /// <summary>
    /// The non-Latin scripts in a line, with Japanese (kana and kanji) as one and kanji or hanja without kana as
    /// Han. Two or more means the line mixes languages.
    /// </summary>
    public static IReadOnlyList<Script> NonLatinGroups(string? text)
    {
        var counts = Count(text);
        bool kana = counts.ContainsKey(Script.Kana);
        return counts.Keys.Where(s => s != Script.Latin && !(kana && s == Script.Han)).OrderBy(s => s).ToList();
    }
}
