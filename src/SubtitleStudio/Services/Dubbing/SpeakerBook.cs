using System.Text.Json;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>A person remembered for a show: their name, voice, gender and voiceprint.</summary>
public sealed class RememberedSpeaker
{
    public string Name { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public bool GenderSetByHand { get; set; }
    public string Voice { get; set; } = string.Empty;
    public float[] Voiceprint { get; set; } = Array.Empty<float>();
    /// <summary>Episodes they were found in (to keep the regulars when the book is full).</summary>
    public int Episodes { get; set; }
}

/// <summary>
/// The speakers of one show, remembered across its episodes (speakers\&lt;show&gt;.json next to the app), so
/// the same person keeps the same name and voice through a season. A speaker found in a new episode is
/// taken for a remembered one when their voiceprints are alike enough.
/// </summary>
public sealed class SpeakerBook
{
    /// <summary>Voiceprints this alike (cosine, averaged over a speaker's lines) are the same person.</summary>
    public const double SamePerson = 0.5;

    /// <summary>Speakers with fewer lines than this in an episode aren't remembered (unless named by hand).</summary>
    public const int RememberFrom = 5;

    public const int MostRemembered = 40;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SpeakerBook(string path, List<RememberedSpeaker> people)
    {
        Path = path;
        People = people;
    }

    public string Path { get; }
    public List<RememberedSpeaker> People { get; }

    /// <summary>The book of a show ("hyper knife" → speakers\hyper knife.json); empty when there's none yet.</summary>
    public static SpeakerBook Open(string folder, string show)
    {
        var safe = string.Concat((show.Length > 0 ? show : "untitled").Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = System.IO.Path.Combine(folder, safe + ".json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<RememberedSpeaker>>(File.ReadAllText(path)) is { } people)
                return new SpeakerBook(path, people.Where(p => p.Voiceprint.Length > 0).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new SpeakerBook(path, new List<RememberedSpeaker>());
    }

    /// <summary>
    /// Gives the speakers that match someone remembered their name, voice and (if set by hand) gender,
    /// most alike pairs first, each person at most once. Returns how many were recognised.
    /// </summary>
    public int Recognise(IReadOnlyList<Speaker> speakers)
    {
        var pairs = new List<(double Sim, Speaker Speaker, RememberedSpeaker Person)>();
        foreach (var s in speakers.Where(s => s.Voiceprint is { Length: > 0 }))
            foreach (var p in People)
            {
                var sim = Voiceprints.Similarity(s.Voiceprint!, p.Voiceprint);
                if (sim >= SamePerson) pairs.Add((sim, s, p));
            }
        var taken = new HashSet<Speaker>();
        var used = new HashSet<RememberedSpeaker>();
        foreach (var (_, s, p) in pairs.OrderByDescending(x => x.Sim))
        {
            if (taken.Contains(s) || used.Contains(p)) continue;
            taken.Add(s);
            used.Add(p);
            s.Name = p.Name;
            s.Voice = p.Voice;
            if (p.GenderSetByHand || s.Gender is null) { s.Gender = p.Gender; s.GenderSetByHand = p.GenderSetByHand; }
            s.Remembered = true;
        }
        // New speakers numbered after the remembered names ("Speaker 3" isn't given twice).
        var names = new HashSet<string>(speakers.Where(s => s.Remembered).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        int next = 1;
        foreach (var s in speakers.Where(s => !s.Remembered))
        {
            while (names.Contains($"Speaker {next}")) next++;
            s.Name = $"Speaker {next}";
            names.Add(s.Name);
        }
        return taken.Count;
    }

    /// <summary>
    /// Keeps the episode's speakers: remembered ones updated (name, voice, gender, voiceprint averaged in),
    /// new ones added when they have a few lines or were named by hand. Saved at once.
    /// </summary>
    public void Remember(IReadOnlyList<Speaker> speakers)
    {
        foreach (var s in speakers.Where(s => s.Voiceprint is { Length: > 0 }))
        {
            var person = People.Select(p => (p, sim: Voiceprints.Similarity(s.Voiceprint!, p.Voiceprint)))
                .Where(x => x.p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase) || x.sim >= SamePerson)
                .OrderByDescending(x => x.sim).Select(x => x.p).FirstOrDefault();
            bool named = !s.Name.StartsWith("Speaker ", StringComparison.Ordinal);
            if (person is null)
            {
                if (s.Lines.Count < RememberFrom && !named) continue;
                person = new RememberedSpeaker { Voiceprint = s.Voiceprint! };
                People.Add(person);
            }
            else
            {
                // Earlier episodes count more, so one noisy episode doesn't move the voiceprint far.
                int w = Math.Min(person.Episodes, 4);
                person.Voiceprint = Voiceprints.Normalize(person.Voiceprint.Zip(s.Voiceprint!, (a, b) => a * w + b).ToArray());
            }
            person.Name = s.Name;
            person.Voice = s.Voice;
            person.Gender = s.Gender;
            person.GenderSetByHand = s.GenderSetByHand;
            person.Episodes++;
        }
        if (People.Count > MostRemembered)
        {
            var keep = People.OrderByDescending(p => p.Episodes).Take(MostRemembered).ToList();
            People.Clear();
            People.AddRange(keep);
        }
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(People, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
