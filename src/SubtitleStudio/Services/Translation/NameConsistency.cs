using System.Text;
using System.Text.RegularExpressions;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Translation;

/// <summary>A name on the show's list: the spelling to use and the spellings to replace with it.</summary>
public sealed record NameEntry(string Name, IReadOnlyList<string> Variants);

/// <summary>
/// The show's names list, as typed: one name per line, optionally followed by "=" and the spellings to
/// replace with it, separated by commas ("Choi Deok-gi = Choi Deok-hee, Choi Da-ki").
/// </summary>
public sealed class NameList
{
    public NameList(IEnumerable<NameEntry> entries) => Entries = entries.ToList();

    public IReadOnlyList<NameEntry> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    public static NameList Parse(string? text)
    {
        var entries = new List<NameEntry>();
        foreach (var raw in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            var name = (eq < 0 ? line : line[..eq]).Trim();
            if (name.Length == 0) continue;
            var variants = eq < 0 ? new List<string>()
                : line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(v => !v.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            entries.Add(new NameEntry(name, variants));
        }
        return new NameList(entries);
    }

    public static string Format(NameEntry entry) => entry.Variants.Count == 0 ? entry.Name : $"{entry.Name} = {string.Join(", ", entry.Variants)}";

    /// <summary>The list with a group added (or merged into the entry it shares a spelling with), as text.</summary>
    public static string Add(string? text, NameEntry group)
    {
        var list = Parse(text).Entries.ToList();
        var all = new[] { group.Name }.Concat(group.Variants).ToList();
        int at = list.FindIndex(e => all.Any(a => a.Equals(e.Name, StringComparison.OrdinalIgnoreCase) || e.Variants.Contains(a, StringComparer.OrdinalIgnoreCase)));
        if (at < 0) list.Add(group);
        else
        {
            var e = list[at];
            var variants = e.Variants.Concat(all).Where(v => !v.Equals(e.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list[at] = e with { Variants = variants };
        }
        return string.Join(Environment.NewLine, list.Select(Format));
    }
}

/// <summary>One spelling changed in a translation, for the Log.</summary>
public sealed record NameChange(string From, string To, int Cues, bool FromList);

/// <summary>Similar spellings that may be one person: offered to the user, not changed.</summary>
public sealed record NameGroup(IReadOnlyList<(string Name, int Cues)> Spellings)
{
    /// <summary>The most used spelling first.</summary>
    public string Suggested => Spellings[0].Name;

    public NameEntry AsEntry() => new(Suggested, Spellings.Skip(1).Select(s => s.Name).ToList());

    public string Label => string.Join("  ·  ", Spellings.Select(s => $"{s.Name} ({s.Cues})"));
}

public sealed record NameReport(IReadOnlyList<NameChange> Changes, IReadOnlyList<NameGroup> Suggestions)
{
    public int CuesChanged { get; init; }
}

/// <summary>
/// Keeps names spelled the same way through a translation. The show's list always wins: its variants (and,
/// with matching on, spellings one letter away from a listed name) become the listed spelling. Matching
/// also merges spellings of the same name that differ by a letter or two when one is clearly the usual one
/// (Ichida 8 times, Ichiza once). Looser look-alikes (Choi Deok-gi, Choi Da-ki) are only suggested: they
/// could be two people.
/// </summary>
public static partial class NameConsistency
{
    private const string Titles = "Professor|Doctor|Miss|Nurse|Detective|Chairwoman|Chairman|Director|Teacher|President|Captain|Officer|Attorney|Chief|Manager|Sergeant|Inspector|Pharmacist|Lady|Lord|Sensei";
    private const string Abbreviations = "Prof|Dr|Mr|Mrs|Ms|Det|Capt|Sgt";

    /// <summary>Hyphenated words that aren't names.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety", "self", "well", "long", "short", "high", "low",
        "so", "half", "all", "ex", "co", "non", "re", "pre", "post", "anti", "over", "under", "x", "t", "e", "uh", "um", "oh", "ah",
    };

    /// <summary>Korean family names as they are usually romanized (a "Given-name" after one of these is a full name).</summary>
    private const string Families = "Kim|Gim|Lee|Yi|Rhee|Ri|Park|Pak|Bak|Choi|Choe|Jung|Jeong|Chung|Kang|Gang|Cho|Jo|Yoon|Yun|Jang|Chang|Lim|Im|Han|Oh|Seo|Suh|Shin|Sin|Kwon|Gwon|Hwang|Ahn|An|Song|Jeon|Jun|Chun|Cheon|Hong|Ko|Go|Moon|Mun|Yang|Son|Bae|Baek|Paek|Heo|Hur|Yoo|Yu|Ryu|Noh|Roh|No|Nam|Ha|Kwak|Gwak|Sung|Seong|Cha|Joo|Ju|Woo|Wu|Min|Na|Jin|Ji|Uhm|Eom|Byun|Byeon|Chae|Won|Bang|Pyo|Do|Gil|Ma|Tak|Ryeo|Yeo|Sim|Shim|Gu|Koo|Ku|Ok|Kong|Gong|Hyun|Hyeon|Seok|Suk|Pyeon|Ra|Maeng|Myung|Myeong|Yeon|Ye|Jeong|Jegal|Namgung|Sunwoo|Seonu|Hwangbo|Dokgo";

    [GeneratedRegex(@"(?<![\p{L}-])(" + Families + @") ([A-Z][a-z]{0,8}-[a-z]{1,8})(?![\p{L}-])")]
    private static partial Regex FamilyGiven();

    [GeneratedRegex(@"(?<![\p{L}-])([A-Z][a-z]{0,8}-[a-z]{1,8})(?![\p{L}-])")]
    private static partial Regex GivenOnly();

    // A full-word title is followed by the name directly ("Doctor. This" is two sentences); an abbreviation may have its period.
    [GeneratedRegex(@"\b(?:(?:" + Titles + @")|(?:" + Abbreviations + @")\.?) ([A-Z][a-z]{3,14})(?![\p{L}-])")]
    private static partial Regex Titled();

    /// <summary>Names that look like names: Korean-style "Family Given-name", "Given-name", and a title with a name.</summary>
    public static Dictionary<string, int> FindNames(IEnumerable<string> texts)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var titles = new HashSet<string>(Titles.Split('|').Concat(Abbreviations.Split('|')), StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            var covered = new List<(int, int)>();
            foreach (Match m in FamilyGiven().Matches(text))
            {
                if (titles.Contains(m.Groups[1].Value) || NotNames.Contains(m.Groups[2].Value.Split('-')[0])) continue;
                found.Add(m.Value);
                covered.Add((m.Index, m.Index + m.Length));
            }
            foreach (Match m in GivenOnly().Matches(text))
            {
                if (covered.Any(c => m.Index >= c.Item1 && m.Index < c.Item2)) continue;
                var parts = m.Value.Split('-');
                if (NotNames.Contains(parts[0]) || NotNames.Contains(parts[1])) continue;
                found.Add(m.Value);
            }
            foreach (Match m in Titled().Matches(text))
                if (!titles.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
            foreach (var name in found) counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    /// <summary>Letters only, lower case: "Deok-gi" → "deokgi".</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// How a romanized name sounds, roughly: vowels dropped, letters that romanize the same Korean or
    /// Japanese sound made equal (g/k, d/t, b/p, j/ch, r/l). "Deok-gi" and "Da-ki" both give "tk".
    /// </summary>
    public static string Sound(string name)
    {
        var key = Key(name).Replace("ch", "j").Replace("sh", "s");
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            char d = c switch { 'g' => 'k', 'd' => 't', 'b' => 'p', 'r' => 'l', 'z' => 'j', 'f' => 'p', _ => c };
            if ("aeiouyw".IndexOf(d) >= 0) continue;
            if (d == 'h' && sb.Length > 0) continue; // aspiration in the middle of a name
            if (sb.Length > 0 && sb[^1] == d) continue;
            sb.Append(d);
        }
        return sb.ToString();
    }

    public static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Spellings this close are the same name written slightly differently (one letter, two for long names).</summary>
    public static bool NearlySame(string a, string b)
    {
        string ka = Key(a), kb = Key(b);
        if (ka == kb) return true;
        int shorter = Math.Min(ka.Length, kb.Length);
        if (shorter < 5) return false;
        return Distance(ka, kb) <= (shorter >= 10 ? 2 : 1) && SameFamily(a, b);
    }

    /// <summary>Two-word names must share the family name; single words are compared whole.</summary>
    private static bool SameFamily(string a, string b)
    {
        var pa = a.Split(' ');
        var pb = b.Split(' ');
        if (pa.Length != pb.Length) return false;
        return pa.Length == 1 || pa[0] == pb[0];
    }

    /// <summary>
    /// Could be the same name: near-identical, or written alike (same shape: words and hyphen), sounding the
    /// same (same family name) and not too different in letters. Korean given names share syllables
    /// ("-young"), so sound alone isn't enough: Gi-young and Kyung-hwa are two people.
    /// </summary>
    public static bool LookAlike(string a, string b)
    {
        if (NearlySame(a, b)) return true;
        var pa = a.Split(' ');
        var pb = b.Split(' ');
        if (pa.Length != pb.Length || (pa.Length == 2 && pa[0] != pb[0]) || pa[^1].Contains('-') != pb[^1].Contains('-')) return false;
        string sa = Sound(pa[^1]), sb = Sound(pb[^1]);
        if (sa.Length < 2 || sa != sb) return false;
        string ka = Key(pa[^1]), kb = Key(pb[^1]);
        return Distance(ka, kb) <= (Math.Max(ka.Length, kb.Length) + 1) / 2;
    }

    /// <summary>
    /// Makes the names in <paramref name="cues"/> consistent (changing their text in place) and reports what
    /// changed and which look-alikes are left for the user to decide.
    /// </summary>
    public static NameReport Apply(IList<SubtitleCue> cues, NameList list, bool matching)
    {
        var changes = new List<NameChange>();
        var changedCues = new HashSet<SubtitleCue>();

        // 1. The list: its variants become the listed spelling.
        foreach (var entry in list.Entries)
            foreach (var variant in entry.Variants)
            {
                int n = Replace(cues, variant, entry.Name, ignoreCase: true, changedCues);
                if (n > 0) changes.Add(new NameChange(variant, entry.Name, n, FromList: true));
            }

        var suggestions = new List<NameGroup>();
        if (!matching) return new NameReport(changes, suggestions) { CuesChanged = changedCues.Count };

        var counts = FindNames(cues.Select(c => c.Text));
        var listed = list.Entries.SelectMany(e => new[] { e.Name }.Concat(e.Variants)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2. A spelling one letter away from a listed name is that name.
        foreach (var (name, _) in counts.OrderByDescending(kv => kv.Value).ToList())
        {
            if (listed.Contains(name)) continue;
            var entry = list.Entries.FirstOrDefault(e => NearlySame(name, e.Name));
            if (entry is null) continue;
            int n = Replace(cues, name, entry.Name, ignoreCase: false, changedCues);
            if (n > 0) changes.Add(new NameChange(name, entry.Name, n, FromList: true));
            counts.Remove(name);
        }

        // 3. Near-identical spellings, one clearly the usual one: the rare ones become the usual one.
        var names = counts.Keys.Where(n => !listed.Contains(n)).OrderByDescending(n => counts[n]).ThenBy(n => n, StringComparer.Ordinal).ToList();
        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var main in names)
        {
            if (merged.Contains(main)) continue;
            foreach (var other in names)
            {
                if (other == main || merged.Contains(other) || counts[other] * 2 > counts[main] || !NearlySame(main, other)) continue;
                int n = Replace(cues, other, main, ignoreCase: false, changedCues);
                if (n > 0) changes.Add(new NameChange(other, main, n, FromList: false));
                merged.Add(other);
                counts[main] += counts[other];
            }
        }

        // 4. What's left that looks alike: suggested, not changed.
        var left = names.Where(n => !merged.Contains(n)).OrderByDescending(n => counts[n]).ThenBy(n => n, StringComparer.Ordinal).ToList();
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var main in left)
        {
            if (grouped.Contains(main)) continue;
            var group = new List<string> { main };
            foreach (var other in left)
                if (other != main && !grouped.Contains(other) && group.Any(g => LookAlike(g, other))) group.Add(other);
            if (group.Count < 2) continue;
            foreach (var g in group) grouped.Add(g);
            suggestions.Add(new NameGroup(group.Select(g => (g, counts[g])).OrderByDescending(s => s.Item2).ToList()));
        }
        return new NameReport(changes, suggestions) { CuesChanged = changedCues.Count };
    }

    private static int Replace(IList<SubtitleCue> cues, string from, string to, bool ignoreCase, HashSet<SubtitleCue> changed)
    {
        var pattern = new Regex(@"(?<![\p{L}-])" + Regex.Escape(from) + @"(?![\p{L}-])", ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
        int n = 0;
        foreach (var cue in cues)
        {
            var text = pattern.Replace(cue.Text, to);
            if (text == cue.Text) continue;
            cue.Text = text;
            changed.Add(cue);
            n++;
        }
        return n;
    }

    /// <summary>The show a file belongs to: "Hyper Knife - S01E03 - Episode 3.ko.srt" → "Hyper Knife".</summary>
    public static string ShowOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var file = fileName.Trim();
        file = file[(file.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        var name = Path.GetFileNameWithoutExtension(file);
        var m = ShowEpisode().Match(name);
        if (m.Success && m.Groups[1].Value.Trim(' ', '-', '.', '_').Length > 0) name = m.Groups[1].Value;
        else
        {
            // No episode number: the name without language and other tags at the end.
            var parts = name.Split('.').ToList();
            while (parts.Count > 1 && parts[^1].Length is >= 2 and <= 7) parts.RemoveAt(parts.Count - 1);
            name = string.Join('.', parts);
        }
        name = name.Replace('.', ' ').Replace('_', ' ');
        return Regex.Replace(name, @"\s+", " ").Trim(' ', '-');
    }

    [GeneratedRegex(@"^(.*?)[\s._-]*(?:[Ss]\d{1,2}[\s._-]*[Ee]\d{1,3}|\d{1,2}x\d{2,3}|[Ee][Pp]?\d{1,3}\b)")]
    private static partial Regex ShowEpisode();
}
