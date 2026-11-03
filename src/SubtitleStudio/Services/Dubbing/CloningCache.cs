using System.Security.Cryptography;
using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A persistent cache of cloned line audio, so a cloned dub that was stopped (or re-run after a change elsewhere) does
/// not redo lines it already spoke. Each line is keyed by a stable hash of what determines its audio — the speaker's
/// reference clip, the exact text, the quality (diffusion steps) and the sample rate — so the same line with the same
/// voice and settings is reused, and any change (different text, speaker, or quality) misses and is re-synthesised.
///
/// The cache lives under the cloning folder (models\tts\cloning\cache) as one wav per key. It survives app restarts,
/// which is what makes a long cloned dub resumable: stop it, start it again, and only the lines not yet done run.
/// Cloning is slow and its output is deterministic enough that reuse is a big win; the cache is bounded by pruning the
/// oldest files when it grows past a cap.
/// </summary>
public sealed class CloningCache
{
    private readonly string _dir;
    private readonly long _maxBytes;

    public CloningCache(string folder, long maxBytes = 2L * 1024 * 1024 * 1024) // 2 GB default
    {
        _dir = Path.Combine(folder, "cache");
        _maxBytes = maxBytes;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>The stable key for a line: its reference clip (by content), text, timesteps and rate.</summary>
    public string Key(string refClipPath, string text, int timesteps, int rate)
    {
        // Hash the reference clip's content (size + a sample of bytes) so a re-cut clip of the same voice still matches
        // when identical, and a different speaker's clip does not. Full-file hashing would be slower for little gain.
        long len = 0; byte[] head = Array.Empty<byte>();
        try
        {
            var fi = new FileInfo(refClipPath);
            len = fi.Exists ? fi.Length : 0;
            if (fi.Exists)
            {
                using var fs = fi.OpenRead();
                head = new byte[(int)Math.Min(64 * 1024, fi.Length)];
                _ = fs.Read(head, 0, head.Length);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        sb.Append("v1|").Append(len).Append('|').Append(timesteps).Append('|').Append(rate).Append('|').Append(text ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        if (head.Length > 0) sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private string PathFor(string key) => Path.Combine(_dir, key + ".wav");

    /// <summary>The cached samples for a key, or null if not cached. Marks the file used (for pruning).</summary>
    public float[]? Get(string key, int rate)
    {
        var p = PathFor(key);
        if (!File.Exists(p)) return null;
        try
        {
            var (samples, r) = WavFile.Read(p);
            try { File.SetLastAccessTimeUtc(p, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return r == rate ? samples : Resample.Linear(samples, r, rate);
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Stores samples for a key (best-effort). Called as each line finishes, so a stopped dub keeps its progress.</summary>
    public void Put(string key, float[] samples, int rate)
    {
        try { WavFile.Write(PathFor(key), samples, rate); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Drops the oldest files when the cache is over its cap. Cheap; run after a dub.</summary>
    public void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.wav");
            long total = files.Sum(f => f.Length);
            if (total <= _maxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                try { total -= f.Length; f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (total <= _maxBytes) break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Empties the cache (user action).</summary>
    public void Clear()
    {
        try { foreach (var f in Directory.GetFiles(_dir, "*.wav")) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The cache's size on disk, for the UI.</summary>
    public long SizeBytes()
    {
        try { return new DirectoryInfo(_dir).GetFiles("*.wav").Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
