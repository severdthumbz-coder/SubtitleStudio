# Subtitle Studio

Portable Windows app for creating, editing, syncing and translating subtitles, and generating dubbed audio with AI.
Companion to Video Metadata Editor (VME).

## Build

Requirements: Windows 10 (1809+) / 11 x64 and the .NET 10 SDK, pinned by global.json (only for building; the app targets .NET 10 and the published EXE is self-contained, so nothing needs to be installed to run it).

- `build_and_launch.bat` restores, publishes `publish\SubtitleStudio.exe` (single-file, self-contained) and offers to launch it.
- `publish-release.ps1` reads `<FullVersion>` from the csproj, checks CHANGELOG.md has an entry for it, publishes, and creates a GitHub release `v<FullVersion>` with the EXE attached (needs `gh` logged in).
- CI (`.github/workflows/build.yml`) builds every push and PR and uploads the EXE as an artifact. Green CI is the definition of "it builds".

## Versioning

Bump only `<FullVersion>` in `src/SubtitleStudio/SubtitleStudio.csproj` and add a matching `## [x.y.z.w] - date` entry at the top of `CHANGELOG.md`.

## Portable data

Everything lives next to the EXE:

- `subt_settings.json` - all settings. API keys are stored only as DPAPI-encrypted blobs (they decrypt only for the same Windows account on the same PC).
- `subt_errors.log` - written only if something unexpected goes wrong. Starts over after 2 MB (the previous one is kept as `subt_errors.old.log`).
- `subt_session.json` - only while the app is open: the working session (files, Detect results, settings, unsaved subtitle edits, a running Create video or Extract), saved every few seconds for crash recovery and deleted on a normal close. Settings > "Offer to restore my session after a crash" turns it off.
- `subt_activity.log` - what the app did (same as the Log tab): settings, speed and timing of each step, encoder and graphics card used, ffmpeg commands. Starts over after 2 MB (the previous one is kept as `subt_activity.old.log`).
- `models\` - the AI fill model (LaMa from the OpenCV model zoo, Apache-2.0, about 90 MB), downloaded by the app on request and checked with SHA-256. Only needed for AI fill. `models\directml-setup.txt` remembers which graphics-card setup was measured fastest on this PC (delete it to measure again).
- `%TEMP%\.net\SubtitleStudio\` - the libraries the EXE unpacks on first start (not next to the EXE). Folders left there by older builds are removed automatically when no other copy of the app is running.
- Optional: put `ffmpeg.exe` and `ffprobe.exe` next to the EXE (or in `.\ffmpeg`, `.\ffmpeg\bin`, `.\tools`) and they are found automatically.

## Hand-off contract (for VME and other tools)

```
SubtitleStudio.exe "D:\Movies\Film (2024)\Film (2024).mkv" ["another path" ...]
```

- Every argument that is an existing file or folder is imported into Source / Files. Arguments starting with `-` are reserved.
- If a path with spaces arrives unquoted (split into several arguments), the arguments are rejoined and tried as one path.
- Only one window runs per Windows session. A second launch forwards its paths to the running window over the named pipe `SubtitleStudio.Handoff.<user>.<session>` (UTF-8, one absolute path per line) and exits. An empty message just brings the window forward.

## Layout (no god-file)

```
src/SubtitleStudio/
  App.xaml(.cs)            startup and composition only
  MainWindow.xaml(.cs)     shell: toolbar, tabs, status bar
  Themes/                  Tokens.Dark.xaml, Tokens.Light.xaml, Controls.xaml
  Views/Tabs/              one UserControl per tab
  Views/Controls/          HintMarker, ComingSoonPanel
  ViewModels/              MainViewModel.cs + one partial per area, ApiKeyEntryViewModel
  Services/                settings, DPAPI keys, theme, hand-off, single instance, import, sidecars, ffmpeg, dialogs
  Services/Abstractions/   ITranscriptionService, ITranslationService, ITtsService, ISubtitleFormat
  Models/                  AppSettings, MediaItem, SidecarInfo, SubtitleCue/Document
```
