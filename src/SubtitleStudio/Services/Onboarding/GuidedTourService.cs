using System.Windows;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Onboarding;

/// <summary>
/// The first-run (and run-anytime) guided tour. Walks through what Subtitle Studio does, one card at a time, using the
/// app's own confirm dialog: each step shows a title and a short description with "Next" to go on and "Skip the tour"
/// to stop. It's deliberately simple and self-contained — no spotlight overlay to get wrong — so it's reliable on first
/// launch and can be replayed from the Help tab at any time (MainViewModel.StartTourCommand). The master on/off for
/// showing it on first run lives in Settings (ShowGuidedTourOnFirstRun).
/// </summary>
public sealed class GuidedTourService : IGuidedTourService
{
    private readonly IDialogService _dialogs;

    public GuidedTourService(IDialogService dialogs) => _dialogs = dialogs;

    public bool IsAvailable => true;

    private sealed record Step(string Title, string Body);

    private static readonly IReadOnlyList<Step> Steps = new[]
    {
        new Step("Welcome to Subtitle Studio",
            "A portable companion to Video Metadata Editor for working with subtitles: transcribe speech, translate, clean up burned-in subtitles, and dub a video with AI voices. Everything runs on your PC — nothing is uploaded. This quick tour shows the main tabs. You can skip it any time and replay it later from the Help tab."),
        new Step("Source / Files",
            "Add videos and subtitle files here, pair a subtitle with its video, and see what's loaded. Most tabs work on whatever is selected here."),
        new Step("Subtitles",
            "Read and edit the subtitle lines — fix timing, text and splits. This is the text every other tool starts from."),
        new Step("Burned-in subs",
            "Find and paint out subtitles that are baked into the picture, using the graphics card where it can."),
        new Step("AI Transcribe",
            "Turn speech into subtitles with Whisper, running in the app. It finds the speech, skips music and silence, and can detect the language."),
        new Step("Translate",
            "Translate subtitles into another language with a language model in the app, or with an online service if you add a key in Settings."),
        new Step("Dubbing",
            "Speak the subtitles with an AI voice and mix it over the video. Choose a built-in voice (Kokoro or Piper) or clone each speaker's own voice from the original audio. Find the speakers to give each character their own voice; the dub follows the original's level, placement, emphasis and — for the quick voices — its intonation, and can remove the original voices under the English. Preview a stretch before making the whole video."),
        new Step("Batch",
            "Run the same job over many files at once — transcribe, translate or dub a whole folder."),
        new Step("Settings and Help",
            "Settings holds API keys, the ffmpeg path, engine choices and stability options. Help has more on each tab, and you can replay this tour from there whenever you like. That's the tour — enjoy."),
    };

    public Task StartAsync(Window owner, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var s = Steps[i];
            var where = $"  ({i + 1} of {Steps.Count})";
            bool last = i == Steps.Count - 1;
            // Confirm's primary button goes on; cancel stops the tour. The body says which buttons to use.
            var body = s.Body + "\n\n" + (last ? "Press OK to finish." : "Press OK for the next step, or Cancel to skip the rest.");
            bool go = _dialogs.Confirm(s.Title + where, body);
            if (!go) break;
        }
        return Task.CompletedTask;
    }
}
