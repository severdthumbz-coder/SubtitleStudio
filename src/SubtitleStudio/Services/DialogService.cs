using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace SubtitleStudio.Services;

/// <summary>Keeps OS dialogs and shell calls out of view models.</summary>
public interface IDialogService
{
    IReadOnlyList<string> PickFiles(string title, string filter);
    string? PickFile(string title, string filter, string? initialPath = null);
    string? PickFolder(string title);
    string? SaveFile(string title, string filter, int filterIndex, string? initialDirectory, string fileName);
    bool Confirm(string title, string message);
    void RevealInExplorer(string path);
    void OpenFolder(string folder);

    /// <summary>Puts text on the clipboard.</summary>
    void CopyText(string text);

    /// <summary>
    /// Asks which engine to use for <paramref name="task"/> (e.g. "Transcribe") when several can.
    /// Null: cancelled. <paramref name="remember"/>: don't ask again.
    /// </summary>
    Abstractions.EngineChoice? ChooseEngine(string task, IReadOnlyList<Abstractions.EngineChoice> choices, out bool remember);

    /// <summary>Sends a file to the Recycle Bin; true when it's gone (false: refused, or not possible).</summary>
    bool MoveToRecycleBin(string path);

    /// <summary>Shows "Add subtitles to video" until it's closed (closing stops a run that's going).</summary>
    void ShowAddToVideo(ViewModels.MuxViewModel model);
}

public sealed class WpfDialogService : IDialogService
{
    public Abstractions.EngineChoice? ChooseEngine(string task, IReadOnlyList<Abstractions.EngineChoice> choices, out bool remember)
        => Views.Dialogs.EngineChoiceWindow.Ask(Application.Current?.MainWindow, task, choices, out remember);

    public bool MoveToRecycleBin(string path) => Infrastructure.NativeMethods.MoveToRecycleBin(path);

    public void ShowAddToVideo(ViewModels.MuxViewModel model) => Views.Dialogs.MuxWindow.Open(Owner, model);

    public void CopyText(string text)
    {
        // The clipboard can be briefly locked by another program: retry a few times.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(60);
            }
        }
    }

    private static Window? Owner => Application.Current?.MainWindow;

    private static bool Show(CommonDialog dialog)
        => (Owner is { IsLoaded: true } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    public IReadOnlyList<string> PickFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true, CheckFileExists = true };
        return Show(dialog) ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFile(string title, string filter, string? initialPath = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false, CheckFileExists = true };
        var dir = string.IsNullOrWhiteSpace(initialPath) ? null : Path.GetDirectoryName(initialPath.Trim('"'));
        if (dir is not null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        return Show(dialog) ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return Show(dialog) ? dialog.FolderName : null;
    }

    public string? SaveFile(string title, string filter, int filterIndex, string? initialDirectory, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return Show(dialog) ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message)
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public void RevealInExplorer(string path)
    {
        if (File.Exists(path))
            StartExplorer($"/select,\"{path}\"");
        else if (Directory.Exists(path))
            OpenFolder(path);
    }

    public void OpenFolder(string folder)
    {
        if (Directory.Exists(folder)) StartExplorer($"\"{folder}\"");
    }

    private static void StartExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer unavailable (rare, e.g. locked-down shells). Nothing useful to do.
        }
    }
}
