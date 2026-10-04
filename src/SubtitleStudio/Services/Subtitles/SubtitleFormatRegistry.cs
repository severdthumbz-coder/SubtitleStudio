using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Subtitles;

/// <summary>All subtitle formats, plus load/save by path. The only entry point the UI uses for files.</summary>
public sealed class SubtitleFormatRegistry
{
    private readonly List<ISubtitleFormat> _formats = new()
    {
        new SrtFormat(),
        new VttFormat(),
        new AssFormat(ssa: false),
        new AssFormat(ssa: true),
        new MicroDvdFormat(),
    };

    public IReadOnlyList<ISubtitleFormat> All => _formats;

    public ISubtitleFormat? ById(string? id)
        => _formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public ISubtitleFormat? ForExtension(string? extension)
        => _formats.FirstOrDefault(f => f.Extensions.Contains(extension ?? string.Empty, StringComparer.OrdinalIgnoreCase));

    public SubtitleDocument Load(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".idx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sup", StringComparison.OrdinalIgnoreCase))
            throw new SubtitleFormatException($"{ext.TrimStart('.').ToUpperInvariant()} subtitles are images (VobSub / Blu-ray PGS). To read them as text, select the video in Source / Files and use \"Subtitles inside\" (the file next to the video is listed there and read with Windows OCR).");

        var format = ForExtension(ext) ?? throw new SubtitleFormatException($"'{ext}' is not a subtitle format Subtitle Studio can open.");

        SubtitleDocument doc;
        using (var stream = File.OpenRead(path))
            doc = format.Read(stream);

        doc.SourcePath = path;
        return doc;
    }

    /// <summary>Writes atomically (temp file + replace) so a failed save never truncates the existing file.</summary>
    public void Save(SubtitleDocument doc, string path, string formatId)
    {
        var format = ById(formatId) ?? throw new SubtitleFormatException($"Unknown subtitle format '{formatId}'.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + ".saving");

        try
        {
            using (var stream = File.Create(temp))
                format.Write(doc, stream);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>OpenFileDialog filter for every readable subtitle format.</summary>
    public string OpenFilter
        => "Subtitles|" + string.Join(";", _formats.SelectMany(f => f.Extensions).Select(e => "*" + e))
           + "|" + string.Join("|", _formats.Select(f => $"{f.DisplayName}|{string.Join(";", f.Extensions.Select(e => "*" + e))}"))
           + "|All files|*.*";

    /// <summary>SaveFileDialog filter; returns the 1-based index of <paramref name="formatId"/>.</summary>
    public (string Filter, int Index) SaveFilter(string formatId)
    {
        var filter = string.Join("|", _formats.Select(f => $"{f.DisplayName}|{string.Join(";", f.Extensions.Select(e => "*" + e))}"));
        var index = _formats.FindIndex(f => f.Id == formatId);
        return (filter, index < 0 ? 1 : index + 1);
    }
}
