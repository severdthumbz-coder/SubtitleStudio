using System.Collections;
using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;

namespace SubtitleStudio.ViewModels;

/// <summary>Source / Files tab: import (buttons, drag-drop, VME hand-off), list, duration probing.</summary>
public sealed partial class MainViewModel
{
    private readonly HashSet<string> _filePaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _probeCts = new();
    private MediaItem? _selectedFile;

    public ObservableCollection<MediaItem> Files { get; } = new();

    public MediaItem? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public string FilesSummary
    {
        get
        {
            if (Files.Count == 0) return "No files yet";
            int videos = Files.Count(f => f.Kind == MediaKind.Video);
            int audio = Files.Count(f => f.Kind == MediaKind.Audio);
            int subs = Files.Count(f => f.Kind == MediaKind.Subtitle);
            var parts = new List<string>();
            if (videos > 0) parts.Add($"{videos} video{(videos == 1 ? "" : "s")}");
            if (audio > 0) parts.Add($"{audio} audio");
            if (subs > 0) parts.Add($"{subs} subtitle{(subs == 1 ? "" : "s")}");
            return string.Join("  ·  ", parts);
        }
    }

    public string FilesCountText => Files.Count == 1 ? "1 file" : $"{Files.Count} files";

    public bool IncludeSubfolders
    {
        get => _s.Config.IncludeSubfolders;
        set
        {
            if (_s.Config.IncludeSubfolders == value) return;
            _s.Config.IncludeSubfolders = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public AsyncRelayCommand AddFilesCommand { get; private set; } = null!;
    public AsyncRelayCommand AddFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand DropFilesCommand { get; private set; } = null!;
    public RelayCommand RemoveItemsCommand { get; private set; } = null!;
    public RelayCommand ClearFilesCommand { get; private set; } = null!;
    public RelayCommand RevealFileCommand { get; private set; } = null!;

    private void InitFiles()
    {
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        DropFilesCommand = new AsyncRelayCommand(p => p is string[] paths ? ImportAsync(paths, "drag and drop") : Task.CompletedTask);
        RemoveItemsCommand = new RelayCommand(p => RemoveItems(p as IList), p => p is IList { Count: > 0 });
        ClearFilesCommand = new RelayCommand(ClearFiles, () => Files.Count > 0);
        RevealFileCommand = new RelayCommand(p => { if (p is MediaItem m) _s.Dialogs.RevealInExplorer(m.FullPath); }, p => p is MediaItem);

        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilesSummary));
            OnPropertyChanged(nameof(FilesCountText));
        };
    }

    private Task AddFilesAsync()
    {
        var picked = _s.Dialogs.PickFiles("Add videos or subtitle files", FileImportService.OpenFileFilter);
        return picked.Count == 0 ? Task.CompletedTask : ImportAsync(picked, "Add files");
    }

    private Task AddFolderAsync()
    {
        var folder = _s.Dialogs.PickFolder(IncludeSubfolders ? "Add folder (including subfolders)" : "Add folder");
        return folder is null ? Task.CompletedTask : ImportAsync(new[] { folder }, "Add folder");
    }

    /// <summary>Single entry point for every import route (buttons, drop, command line, pipe hand-off).</summary>
    public async Task ImportAsync(IEnumerable<string> paths, string origin)
    {
        var input = paths.ToList();
        if (input.Count == 0) return;

        SetStatus($"Scanning {origin}...");
        var progress = new Progress<int>(n => SetStatus($"Scanning {origin}... {n:N0} files checked"));
        var scan = await _s.Import.ScanAsync(input, IncludeSubfolders, progress, CancellationToken.None);

        var added = new List<MediaItem>();
        foreach (var file in scan.Files)
        {
            if (!_filePaths.Add(file.Path)) continue;
            var item = new MediaItem(file.Path, file.Kind, file.SizeBytes, file.Sidecars);
            Files.Add(item);
            added.Add(item);
        }

        int duplicates = scan.Files.Count - added.Count;
        if (added.Count > 0)
        {
            SelectedFile = added[0];
            SelectedTabIndex = Tabs.Files;
        }

        var message = added.Count switch
        {
            0 when scan.Files.Count == 0 => $"Nothing to add from {origin}: no supported video, audio or subtitle files found.",
            0 => $"Already in the list ({origin}).",
            1 => $"Added {added[0].Name} ({origin}).",
            _ => $"Added {added.Count} files ({origin}).",
        };
        var extras = new List<string>();
        if (added.Count > 0 && duplicates > 0) extras.Add($"{duplicates} already listed");
        if (scan.UnsupportedCount > 0) extras.Add($"{scan.UnsupportedCount} unsupported skipped");
        if (scan.MissingCount > 0) extras.Add($"{scan.MissingCount} not found");
        if (extras.Count > 0) message += " " + string.Join(", ", extras) + ".";

        SetStatus(message, added.Count > 0 ? StatusKind.Success : StatusKind.Warning);

        await ProbeDurationsAsync(added.Where(i => i.Kind != MediaKind.Subtitle).ToList(), _probeCts.Token);
    }

    private async Task ProbeDurationsAsync(IReadOnlyList<MediaItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;

        var ffprobe = FfmpegStatus.FfprobePath;
        if (ffprobe is null)
        {
            foreach (var item in items) item.DurationText = "-";
            return;
        }

        using var gate = new SemaphoreSlim(3);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                item.Duration = await _s.Probe.ProbeDurationAsync(ffprobe, item.FullPath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                item.DurationText = "?";
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // List was cleared while probing.
        }
    }

    /// <summary>
    /// Items listed while ffprobe was missing show "-". Once ffmpeg is found (Settings, re-detect),
    /// read their durations without the user having to re-add them.
    /// </summary>
    private void ProbeMissingDurations()
    {
        if (!FfmpegStatus.HasFfprobe) return;
        var pending = Files.Where(f => f.Kind != MediaKind.Subtitle && f.Duration is null && f.DurationText == "-").ToList();
        if (pending.Count == 0) return;
        foreach (var item in pending) item.DurationText = "...";
        _ = ProbeDurationsAsync(pending, _probeCts.Token);
    }

    /// <summary>Re-detects sidecars for listed videos in a folder (after a subtitle is saved there).</summary>
    private void RefreshSidecarsIn(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        string[] siblings;
        try { siblings = Directory.GetFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var video in Files.Where(f => f.Kind == MediaKind.Video && string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase)))
            video.Sidecars = SidecarDetector.Detect(video.FullPath, siblings);
    }

    private void RemoveItems(IList? selection)
    {
        if (selection is null) return;
        var toRemove = selection.OfType<MediaItem>().ToList();
        foreach (var item in toRemove)
        {
            Files.Remove(item);
            _filePaths.Remove(item.FullPath);
        }
        if (toRemove.Count > 0)
            SetStatus(toRemove.Count == 1 ? $"Removed {toRemove[0].Name}." : $"Removed {toRemove.Count} files.");
    }

    private void ClearFiles()
    {
        _probeCts.Cancel();
        _probeCts.Dispose();
        _probeCts = new CancellationTokenSource();

        Files.Clear();
        _filePaths.Clear();
        SelectedFile = null;
        SetStatus("File list cleared.");
    }
}
