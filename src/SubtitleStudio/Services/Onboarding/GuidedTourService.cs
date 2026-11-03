using System.Windows;
using SubtitleStudio.Views.Dialogs;

namespace SubtitleStudio.Services.Onboarding;

/// <summary>
/// The first-run (and run-anytime) guided tour. Walks through what Subtitle Studio does, one card at a time, in a
/// single themed window (GuidedTourWindow) with Back / Next / Skip navigation and a "Finish" on the last step —
/// the same look and behaviour as Video Metadata Editor's tour, not a chain of Yes/No message boxes. It's reliable on
/// first launch and can be replayed from the Help tab at any time (MainViewModel.StartTourCommand). The master on/off
/// for showing it on first run lives in Settings (ShowGuidedTourOnFirstRun).
/// </summary>
public sealed class GuidedTourService : IGuidedTourService
{
    public bool IsAvailable => true;

    private static readonly IReadOnlyList<GuidedTourWindow.TourStep> Steps = new[]
    {
        new GuidedTourWindow.TourStep("Welcome to Subtitle Studio",
            "A portable companion to Video Metadata Editor for working with subtitles: transcribe speech, translate, clean up burned-in subtitles, and dub a video with AI voices. Everything runs on your PC — nothing is uploaded. This quick tour shows the main tabs. You can skip it any time and replay it later from the Help tab."),
        new GuidedTourWindow.TourStep("Source / Files",
            "Add videos and subtitle files here, pair a subtitle with its video, and see what's loaded. Most tabs work on whatever is selected here. Files can arrive by drag and drop, the Add buttons, or a hand-off from Video Metadata Editor."),
        new GuidedTourWindow.TourStep("Subtitles",
            "Read and edit the subtitle lines — fix timing, text and splits, with a live video preview and audio-based sync. This is the text every other tool starts from."),
        new GuidedTourWindow.TourStep("Burned-in subs",
            "Find subtitles that are baked into the picture: read them into an editable subtitle with OCR, or paint them out into a new video, using the graphics card where it can."),
        new GuidedTourWindow.TourStep("AI Transcribe",
            "Turn speech into timed subtitles with Whisper, running in the app on the graphics card or the processor. It finds the speech, can listen to the voices alone to catch background talk, marks sung lines, and can detect or translate the language."),
        new GuidedTourWindow.TourStep("Translate",
            "Translate subtitles into another language with a language model in the app, keeping timings, positions and italics. Review it next to the original — lines worth a look are flagged, with alternative wordings on tap — or use an online service if you add a key in Settings."),
        new GuidedTourWindow.TourStep("Dubbing",
            "Speak the subtitles with an AI voice and mix it over the video. Pick a quick voice per speaker (Kokoro or Piper) or clone each speaker's own voice from the original audio. Find the speakers to give each character their own voice; the dub follows the original's level, side, emphasis and — for the quick voices — its intonation, and can remove the original voices under the English. Preview a stretch first, and with cloning you can see where the time will go, resume a stopped run, and re-clone a single line that came out wrong."),
        new GuidedTourWindow.TourStep("Batch",
            "Run the same job over many files at once — transcribe, translate or dub a whole folder unattended. Files that already have subtitles are skipped, so a stopped queue carries on."),
        new GuidedTourWindow.TourStep("Settings and Help",
            "Settings holds API keys, the ffmpeg path, engine choices, output defaults and stability options — including \"Use the processor instead of the graphics card\" if your machine is unstable during dubbing. Help has more on each tab, and you can replay this tour from there whenever you like. That's the tour — enjoy."),
    };

    public Task StartAsync(Window owner, CancellationToken cancellationToken = default)
    {
        GuidedTourWindow.Run(owner, Steps);
        return Task.CompletedTask;
    }
}
