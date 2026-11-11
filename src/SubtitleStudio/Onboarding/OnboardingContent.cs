using OnboardingKit;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio.Onboarding;

/// <summary>
/// The app-specific tour steps and legend rows for the OnboardingKit. One step per main tab in workflow order, with a
/// Welcome first and a wrap-up last; the Settings step says the helpers can be turned off, and the last step says where
/// to replay. Tab indices come from <see cref="Tabs"/>. The legend lists the status glyphs and colours the app uses.
/// </summary>
public static class OnboardingContent
{
    public static readonly TourStep[] TourSteps =
    {
        new("Welcome to Subtitle Studio",
            "A portable companion to Video Metadata Editor for working with subtitles: transcribe speech, translate, clean up burned-in subtitles, and dub a video with AI voices. Everything runs on your PC — nothing is uploaded. Press → or Enter for the next step, Esc to skip. You can replay this tour any time from the Help tab."),
        new("Source / Files",
            "Add videos and subtitle files here, pair a subtitle with its video, and see what's loaded. Most tabs work on whatever is selected here. Files can arrive by drag and drop, the Add buttons, or a hand-off from Video Metadata Editor.",
            Tabs.Files),
        new("Subtitles",
            "Read and edit the subtitle lines — fix timing, text and splits, with a live video preview and audio-based sync. This is the text every other tool starts from.",
            Tabs.Subtitles),
        new("Burned-in subs",
            "Find subtitles that are baked into the picture: read them into an editable subtitle with OCR, or paint them out into a new video, using the graphics card where it can.",
            Tabs.BurnedIn),
        new("AI Transcribe",
            "Turn speech into timed subtitles with Whisper, running in the app on the graphics card or the processor. It finds the speech, can listen to the voices alone to catch background talk, marks sung lines, and can detect or translate the language.",
            Tabs.Transcribe),
        new("Translate",
            "Translate subtitles into another language on this PC with a language model, keeping timings, positions and italics. Review it next to the original — lines worth a look are flagged, with alternative wordings on tap. Add a DeepL key in Settings for a fast online option too; when both can run, an Engine box here lets you pick which to use.",
            Tabs.Translate),
        new("Dubbing",
            "Speak the subtitles with an AI voice and mix it over the video. Pick a quick voice per speaker (Kokoro or Piper) or clone each speaker's own voice from the original audio. The dub follows the original's level, side, emphasis and — for the quick voices — intonation, and can remove the original voices under the English. Preview a stretch first, and with cloning you can see where the time will go, resume a stopped run, and re-clone a single line that came out wrong.",
            Tabs.Dubbing),
        new("Batch",
            "Run the same job over many files at once — transcribe, translate or dub a whole folder unattended. Files that already have subtitles are skipped, so a stopped queue carries on.",
            Tabs.Batch),
        new("Settings",
            "API keys, the ffmpeg path, engine choices, output defaults and stability options — including \"Use the processor instead of the graphics card\" if your machine is unstable during dubbing. You can turn these onboarding helpers (the ⓘ hints, the Legend button — the grid icon in the toolbar — and this first-run tour) off here under Behaviour.",
            Tabs.Settings),
        new("You're all set",
            "That's the tour. The Legend button — the grid icon in the toolbar — explains the icons and colours, and the ⓘ markers explain each section on hover. Replay this tour any time from the Help tab. Enjoy."),
    };

    public static readonly LegendItem[] LegendItems =
    {
        new("", "Video file", Swatch: null),
        new("", "Audio file"),
        new("", "Subtitle file, or subtitles found next to a video"),
        new("", "Hint: hover the ⓘ marker for an explanation (can be hidden in Settings)"),
        new("", "Done / ready"),
        new("", "Warning: works, but something needs attention"),
        new("", "Error: the action did not complete"),
        new("", "API key saved and encrypted"),
    };
}
