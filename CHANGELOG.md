# Changelog

All notable changes to Subtitle Studio, newest first. One entry per build.
The version is the `<FullVersion>` value in `src/SubtitleStudio/SubtitleStudio.csproj`.
This file is embedded in the app and shown under Help > Revision history, and CI fails if the
current FullVersion has no entry here.

## [1.0.0.43] - 2026-10-03

### Added
- Add subtitles to a video as tracks. "Add subtitles to video" opens from three places: a video in Source / Files, "Add to video" in the Subtitles tab (the open subtitle, saved first, into its paired video), and right-clicking a file in the Batch tab.
  - Every subtitle file next to the video is offered (SRT, VTT, ASS, SSA, MicroDVD SUB; a VobSub .sub/.idx pair is not), and "Add a subtitle file..." adds others.
  - Each track gets a language, a name players show ("Korean", "English (Forced)", "English (SDH)", or your own), and the Default, Forced and SDH flags. The language, forced and SDH are read from the file name. The full track in your usual language (Settings > Output defaults) starts as the default, and only one track can be the default.
  - Uses FFmpeg without re-encoding. The picture and sound are copied as they are (checked bit for bit in testing), so it takes about as long as copying the file.
  - MKV keeps subtitles as they are: SRT as SubRip, and ASS/SSA with their styling and positions. Fonts and picture-based tracks (PGS, VobSub) already in the video are kept too. MP4 stores subtitles as plain text (mov_text). When the video or audio can't go into MP4 without re-encoding (Vorbis audio, for example), the window says so before you start, and suggests MKV.
  - The video's own subtitle tracks are kept unless you untick "Keep the subtitle tracks it already has". When a new track is the default, the old ones lose that flag.
  - The new video is saved beside the original ("Episode 1 (subtitles).mkv", or a name you choose), or it replaces the original, which then goes to the Recycle Bin. On a drive without a Recycle Bin, Windows asks before deleting it.
  - The new video is written under a temporary name and checked (all tracks there, nothing cut short) before it gets its real name. Only then does a replaced original go to the Recycle Bin. A failed or cancelled run leaves nothing behind.
  - The window lists what the video already has, and says what will be left out (for example, fonts in MP4).

### Fixed
- Batch: the include checkboxes were clipped. A checkbox without a label no longer reserves room for one, and the column is wider.
- Batch: a file that has been transcribed and is waiting for its translation now shows "Transcribed" instead of "Waiting".

## [1.0.0.42] - 2026-10-02

### Added
- Batch tab (after Translate): a season queue. Point it at a folder ("Add folder", drop it on the tab, or take the files listed in Source / Files) and it transcribes, then translates, every video or audio file in turn. Each file gets its subtitles saved next to it, named for Plex and Jellyfin ("Hyper Knife S01E03.ko.srt" and "Hyper Knife S01E03.en.srt", in the default output format from Settings). Files are queued in episode order (Episode 2 before Episode 10), and each one can be ticked or unticked.
- The queue uses the same spoken language, Whisper model, language model, device and names lists as AI Transcribe and Translate. "Translate into" can also be "Don't translate", for transcripts only.
- All files are transcribed first and then all translated, so each model is loaded once. Whisper's graphics memory is freed before the language model loads.
- Subtitles a file already has are not made again: an existing transcript is used, and an existing translation is left alone. The list shows this for each file before you start. Cancel (or closing the app) stops after the current step, and pressing Start again carries on with what isn't done. "Redo files that already have subtitles" makes them all again.
- A file that fails (unreadable audio, a translation error) is marked Failed with the reason, written to the Log, and the queue moves on. A file with no speech is marked Skipped and nothing is written.
- Names: each file uses its own show's names list (editable in the tab when all the files are from one show), with matching as set in Settings. When the queue finishes, look-alike names across the whole season are suggested, each with "Add to list". "Apply names to the translated files" then fixes the saved translations without translating again.
- Each file shows its step, its own progress and what it wrote, and the tab shows overall progress with the time left. Double-click a finished file (or right-click) to open its subtitles in the editor, or show it in its folder.

### Changed
- AI Transcribe and Translate wait while the Batch queue runs, and the queue waits for them, so only one model uses the graphics card at a time.
- The tab order is now Source / Files, Subtitles, Burned-in subs, AI Transcribe, Translate, Batch, Dubbing, Settings, Log, Help.

## [1.0.0.41] - 2026-10-02

### Fixed
- Translate: the last run on the Korean episode was sent as "from Assamese" (the Log says "516 cues from Assamese to English"). "From" had been changed, probably by the mouse wheel passing over the list. The model still read the Korean, but the Korean-only instructions (romanized names, 선생님 as Doctor or Teacher) were left out, which is why "Han 선생님" stayed in three lines. When the letters plainly contradict "From" (Hangul, Japanese kana, or Chinese characters only), the app now translates from the language the text is written in, and says so in the status bar and the Log. With "From" left as "As the subtitle says" and no language tag, the letters decide too.
- Help: Burned-in subtitles showed "Partial" and "Removal comes next", from before removal was added. It now shows "Available" and describes reading, removal and the keep list.

## [1.0.0.40] - 2026-10-02

### Added
- Translate: a names list for each show. One name per line, spelled the way you want it, and after "=" the other spellings to replace with it ("Choi Deok-gi = Choi Deok-hee, Choi Da-ki"). The show is taken from the file name ("Hyper Knife - S01E03 …" belongs to Hyper Knife), so every episode uses the same list. Listed names are given to the language model, so it uses those spellings from the start, and after translating every listed variant is replaced (upper or lower case, whole names only).
- Translate: name matching, on by default (Settings > Translate). After translating:
  - a spelling one letter away from a listed name becomes the listed name;
  - a spelling one letter away from another, much more common one (Ichiza once, Ichida three times) becomes the common one, when both have the same family name and are at least 5 letters long;
  - looser look-alikes are only suggested, never changed, because they could be two people. These are names with the same family name that sound the same in romanization (Choi Deok-gi / Choi Deok-hee / Choi Da-ki, Kim Myeong-jin / Kim Myung-joon). Names that only share a syllable (Gi-young, Kyung-hwa) are kept apart. Each suggestion has an "Add to list" button that adds it to the show's list.
  - Every change is listed in the Log ("Ichiza" → "Ichida" in 1 cue) and summed up in the tab.
  With matching off, only the list is used.
- Translate: "Apply names to the open subtitles" uses the list (and matching) on the subtitles in the editor without translating again, for example after adding a suggestion. One Undo puts them back.

## [1.0.0.39] - 2026-10-02

### Changed
- Translate: words left in the original script are caught. On the Korean test episode a few lines came back as "Han 선생님, it's me.", "Ichida 하루" or with Japanese katakana in them. A translated line that still has Korean, Japanese or Chinese letters (when translating into a language that doesn't use them) is asked again on its own, with a reminder to translate every word and write names in the target alphabet. If it is still not clean, the answer is kept and listed in the Log.
- Translate: clearer instructions for Korean: names in standard romanization and the same every time, forms of address translated (선생님: Doctor, Teacher or Sir by context; 교수님: Professor). The 8 lines before each batch now go along as context (was 6).
- Translate: what a language model adds and subtitles don't have is removed: markdown emphasis ("*you*"), and a speaker dash or "/" at the start of a line when the original line had none. Dashes the original had are kept.

### Fixed
- Translate: about 40 seconds of an episode's translation went on setting up the model's working memory twice for each batch. It is now set up once per batch.
- Translate: the Log no longer repeats the same two llama.cpp notices for every batch (86 lines for one episode). Each notice is logged once per run.

## [1.0.0.38] - 2026-10-02

### Fixed
- AI Transcribe: sentences came out one syllable per cue, spread over a minute or more (on the Korean test episode: "감 / 사 / 합 / 니다. / LA에 / 서 / …" across 52 seconds, "수 / 술 / 은 / 잘 / 끝 / 냈 / 어 / 요." across 45 seconds, about 15 places in all). Whisper sometimes gives one short line a time span that covers long stretches of music it never heard as speech. Its word times for Korean often can't be used (a syllable split across two tokens garbles both), so the text was shared out over the whole span. Then the 7-second limit kept cutting it, down to single syllables. Now:
  - without usable word times, the text is laid over the speech found from the segment's start, at a speaking pace (about 5 Korean syllables or 12 letters a second), stopping at the first long pause, instead of being stretched over everything the segment spans;
  - a cue that is only too long in time is split only between words, never into scraps of fewer than 8 columns; text is split between characters only when it is too wide for two lines (Chinese, Japanese);
  - a cue is held at most about as long as it takes to read: 1 second plus 0.12 seconds per column, within the 0.8 to 7 second range.
- AI Transcribe: a cue stayed on screen for 70 seconds ("도", 9:02 to 10:12) and another for 24. Whisper stamped the first word of the line at the end of the previous one. A first word more than 2 seconds ahead of the second is now moved up to it, and a cue ends where its last word ends (within the reading time above).
- Burned-in removal, keep list: "A.D. 24" in The Chosen wasn't kept (the Log showed "text kept in 0 frames"). Its thin serif strokes break apart in the cleaned-up image that OCR read, so nothing could be recognised. The keep list now reads both the picture itself (enlarged to the usual reading size) and the cleaned-up text, and a line found by either counts. Check a frame does the same, and reads the frame again when the list is changed.

## [1.0.0.37] - 2026-10-02

### Added
- Burned-in, step 4: a keep list. Text typed there, one entry per line, stays in the picture when the subtitles are removed: a title card, a channel name, a sign, a translator's note. Everything else in the subtitle area is removed as before. How it works:
  - Whenever the text in the area changes, the app reads that frame with Windows OCR (in the language chosen under Hardware and text recognition). While the text stays the same, it reuses that answer, so it takes about one reading per subtitle.
  - A line of text that contains an entry is left alone, with a small margin for its outline and shadow; the rest of the frame's text is still removed. If a subtitle and a kept caption are on screen together, only the subtitle goes.
  - Matching ignores upper and lower case, spaces and punctuation (OCR spacing varies, especially in Korean and Japanese), and forgives one misread letter per six in longer entries. Entries shorter than six letters must match exactly.
  - Check a frame shows whether the frame on screen has text from the list, so an entry can be tried before rendering. The list is saved with the settings.
  - The preview and the finished video say how many frames kept text and which entries were found; the Log lists each reading where something was kept.
  - It works when the text is found per frame (white or yellow subtitles, the clean-up option). With coloured subtitles the whole area is blurred, so the list can't apply, and the Log says so.

## [1.0.0.36] - 2026-10-01

### Fixed
- Build warning CS8602 (possibly null reference) in App.xaml.cs, where the app starts listening for files handed over by VME. The single-instance check is created before that point, so it never was null; the code now says so in a way the compiler can check.
- CI: the GitHub Actions steps moved to the versions that run on Node.js 24 (checkout v7, setup-dotnet v6, upload-artifact v7). GitHub is retiring Node.js 20 and was already forcing these steps onto Node.js 24 with a warning. Same steps, same inputs; nothing in the app changes.

## [1.0.0.35] - 2026-10-01

### Added
- Translate: the subtitles open in the editor into another language, with a language model running inside the app through llama.cpp (LLamaSharp 0.27.0). Nothing is uploaded and no other program is needed. The translation opens in the Subtitles tab as a new, unsaved subtitle with the same timings and the new language in its suggested file name, so it can be reviewed before saving. If the original isn't saved yet, the app offers to save it first, so a fresh transcription isn't lost. Progress shows the time left and the latest lines with their translations, and Cancel stops it.
- How it translates: cues go to the model 16 at a time, numbered, with the 6 lines before them and their translations as context, so names and tone stay the same through an episode. A grammar makes the model answer with exactly one numbered line per cue: it can't skip, merge or add commentary. A batch that comes back short is retried in halves, then line by line; a line that still fails keeps its original text and is listed in the Log. Positions ({\an8}), italics and two-speaker dash lines are kept; long lines are wrapped to two lines of 42 columns; music notes and lines with no words are left as they are.
- Language models: a list to download from Hugging Face. Gemma 3 12B is recommended (natural dialogue, good with Korean and Japanese, fits a 12 GB graphics card); Qwen3 8B and Gemma 3 4B are also offered. Downloads are checked against the size and SHA-256 Hugging Face publishes and continue where they stopped, like the Whisper models. "Use a model file" adds a .gguf you already have, after reading its header to make sure it is a chat model. Models are kept in models\llm next to the app.
- Runs on the graphics card through Vulkan, the same device chosen for AI Transcribe, otherwise on the processor. If the model doesn't fit in the graphics memory, part of it (then all of it) runs on the processor, and the app says so. The Log says which device was used, how many layers went on it, and how many cues a minute it translated. Only one model is kept in graphics memory at a time: Whisper's is freed before translating and the language model before transcribing.
- Which engine: the same "ask, or remember the answer" as AI Transcribe, for when an online translation engine is added; "Ask again" for it is in Settings, with the language models folder.

### Changed
- Help: AI Transcribe and Translate are now marked as available.

### Notes
- The EXE is larger: llama.cpp's Vulkan library alone is about 62 MB. The avx512 and multimodal builds that come with LLamaSharp are left out.
- When the translator starts, LLamaSharp asks Windows' "vulkaninfo" once if it is installed (it comes with some graphics drivers). The answer isn't used: the app chooses the library and the device itself.

## [1.0.0.34] - 2026-09-30

### Fixed
- AI Transcribe: finding the speech took almost 3 minutes on a 65-minute episode (167 s), longer than Whisper itself (87 s on the graphics card). The speech detector was given 8 processor threads, but it is a tiny network run tens of thousands of times one step after another, and sharing each step across threads cost far more than the step. Measured on the same audio: 8 threads 18 times slower than 1. It now runs on one thread: about 15 s for an episode, with exactly the same result.
- The Log no longer lists every stretch of speech the detector found (632 lines for one episode). The summary line ("Speech found in 632 places, 25:04 of 1:04:59") stays.

## [1.0.0.33] - 2026-09-30

### Fixed
- AI Transcribe wrote lines over music that nobody says. On the first full episode, the opening music came out as "다음 / 영상 / 에서 / 만나 / 요." (Whisper's "see you in the next video", learned from the end of online videos, one word per cue, twice), with a caption credit ("한글자막 by …") further on and a first cue held on screen for almost 10 seconds. Two changes:
  - A speech detector is now built into the app: Silero VAD, 885 KB, MIT licence, the version whisper.cpp converts. It finds where people are actually speaking and only that goes to Whisper. Build 32 judged by loudness, which counts music as sound. On a test clip of 20 s of music followed by speech over music, nothing is written over the music any more, and the speech under the music is all there and correctly timed. It takes about a second per hour-long episode. Cue edges are also trimmed to the detected speech. If the detector can't run, loudness is used as before, and the Log says which was used.
  - Whisper's well-known invented lines are left out, also when they come one word per segment: "다음 영상에서 만나요", "시청해주셔서 감사합니다", "구독과 좋아요", caption credits ("한글자막 by", "자막 제공", "Subtitles by the Amara.org community"), "Thanks for watching", and the same in Japanese and Chinese. Everyday words on their own ("감사합니다", "Thank you") are not touched. A cycle of lines repeated over and over is kept once. The Log says how many lines were left out.

## [1.0.0.32] - 2026-09-30

### Added
- AI Transcribe: turns the speech in a video or audio file into timed subtitles with Whisper, running inside the app through whisper.cpp (Whisper.net 1.9.1). Nothing is uploaded and no other program is needed except FFmpeg. The result opens in the Subtitles tab, unsaved, to review and save. Progress shows the time left and the last lines heard, and Cancel stops it straight away. You can:
  - pick the file and, when there are several, the audio track;
  - choose the language spoken, or let it be detected;
  - write the speech out in that language, or translate it into English subtitles as Whisper listens;
  - try just the first 2, 5 or 15 minutes before a whole episode.
- Whisper models: a list to download from whisper.cpp's page on Hugging Face. Large v3 Turbo is recommended for writing out speech, and Large v3 for English subtitles of other languages; compressed versions, Medium, Small and Base are also offered. Each download is checked against the exact size and SHA-256 that Hugging Face publishes, and a damaged one is deleted rather than installed. An interrupted download continues where it stopped. "Use a model file" adds a ggml-*.bin you already have, after reading its header to make sure it is a Whisper model. Models are kept in models\whisper next to the app. The tab warns when a model doesn't suit the task (Turbo for translating, English-only models, very small models).
- Runs on the graphics card through Vulkan (AMD, NVIDIA and Intel), otherwise on the processor. The app asks the Vulkan driver for the devices itself and points whisper.cpp at the graphics card, so on a laptop it won't pick the slower GPU built into the processor. The choice is in the tab and in Settings; a change takes effect after restarting the app. The Log says which device was actually used and how many times faster than real time it ran.
- Fewer Whisper slips (the Log says how much was left out):
  - long silences (2 s or more) are cut out before Whisper hears the audio, then the times are mapped back, so it no longer drifts across a pause, skips the next sentence or invents lines over silence;
  - lines Whisper itself rates as probably not speech (and isn't confident about) are left out;
  - a line repeated over and over when Whisper gets stuck is kept at most twice;
  - the language is detected once and kept for the whole file.
- Subtitle-shaped cues: long sentences are split at sentence ends or commas near the middle, each cue is at most two balanced lines of 42 columns (Korean, Chinese and Japanese characters count double), and no cue lasts more than 7 seconds. Splits are timed from the words' own timestamps where they can be trusted, cue edges are trimmed to where the sound is, and there are no overlaps.
- Which engine: when an online transcription engine can be used (its API key saved in Settings), the app asks whether to run on this PC or online, and can remember the answer ("Ask again" in Settings forgets it). No online engine is included yet, so for now it always runs on this PC without asking.
- Crash recovery also covers Transcribe: an interrupted one can be restarted from the restore prompt.

### Changed
- Settings > Engines and tools: the "Local Whisper (offline transcription)" box for an outside Whisper program is replaced by the AI Transcribe card (device, models folder, remembered engine), since whisper.cpp is now built in.

## [1.0.0.31] - 2026-09-30

### Fixed
- AI fill left faint ghost letters where bold text was removed from a dark, smooth background (the yellow "HollyMovieHD.Com" ad line at 34:29 in Episode 1). The model was given the text as letter-shaped holes, and on a dark, plain background it echoed those shapes back faintly. Where the picture around the text is smooth, each line is now repainted as one solid shape: the gaps between letters and words are closed, and the shape is grown by a few pixels so the text's soft rim isn't left behind (the model drew that out as a thin line). Over detailed pictures (faces, clothing, streets) nothing changes, and the real picture between the letters is still kept, because repainting it there makes the result less faithful. Where the two kinds of patch meet along one line, they now fade into each other instead of leaving a short vertical edge. On the saved 34:29 frame, the fine detail left behind the ad dropped from about twice the picture's own level to the same level as the picture above it. The two saved dialogue frames come out exactly as before.
- The Log's AI timing line says how many patches were repainted with the gaps closed.

## [1.0.0.30] - 2026-09-29

### Fixed
- A second line in another colour survived removal: for example, a website ad burned in with the subtitles, a white line ("Watch Movie, TV-Drama, Anime Online With English Subtitles") with a bold yellow web address under it ("HollyMovieHD.Com | GOHD.CC"). The text finder assumed one letter colour, took the most common one, and dropped anything else as bright background, so the other line was never found, read or removed. It now also accepts a second letter colour when that is clearly a subtitle colour (white or a strong yellow, not beige or skin), clearly different from the first, and a real share of the letters; the same letter-shape checks still apply. On a test frame with that layout, both lines are now found completely and removed with nothing left, and nothing outside the subtitle area changes. The real frames saved from the episode read exactly as before.

### Added
- Burned-in subs, step 3: "Leave out website ads (lines with a web address)", on by default. A reading with a web address in it (like HollyMovieHD.Com, GOHD.CC, www..., https://...) is left out of the extracted subtitle, so the ad's other line doesn't become a cue. The Log says how many were left out. Dialogue with dots ("5 p.m.", "3.5 million", "I want to. In fact...") is not affected. Detect's "Text found in the samples" leaves ads out too. The ads are still removed from the picture by step 4.

## [1.0.0.29] - 2026-09-29

### Added
- Crash recovery. While the app runs, the session is saved every few seconds whenever something has changed, and straight away when a Create video or Extract starts. It goes to subt_session.json next to the app, written to a temporary file, flushed to disk and then swapped in, so a crash or power cut mid-save leaves the previous copy intact. It includes:
  - the file list, the selected file and the open tab
  - the Burned-in video with its Detect result (no need to Detect again), subtitle area, clean-up, extraction speed, removal method and the frame being checked
  - the subtitle editor's document, cue by cue (times to the tick, tags, ASS styles), with its unsaved changes
  - a Create video or Extract that was running
- A normal close deletes the file. If it's still there at the next start, the app or the PC stopped unexpectedly, and a window lists what can come back: "Restore", or, when a Create video or Extract was running, "Restore and restart Create video" (the default), "Restore only" or "Start fresh". A job can't continue mid-way, but it restarts on its own; Create video goes to the same file, replacing the unfinished one, without asking for a name again. Files that no longer exist are reported. Hand-off files are imported after the restore.
- Settings > General: "Offer to restore my session after a crash" (on by default). Off, nothing is saved and nothing is offered.

## [1.0.0.28] - 2026-09-29

### Fixed
- AI fill quality, from the first full episode:
  - A kerb or a car door that "jumped" partway along the text. The text is covered by overlapping square patches, and each invents the hidden picture a little differently; the later patch simply overwrote the earlier one, leaving a straight vertical step. They now hand over gradually, but only in a narrow band (16 px at 256) at the middle of the overlap. A wide 50/50 blend would show both inventions at once, like a doubled passer-by. On frames saved from the episode, the step where one patch took over dropped from 3.7 to 0.6 and from 1.9 to 0.7 brightness levels, below the picture's own column-to-column detail (0.7 and 0.9). The hand-over adds under 0.1 to the picture's own detail.
  - A faint lighter rectangle, for example on a road. In a frame, patches with a smooth background got the ordinary fill while their neighbours got the AI, so the method changed at a patch edge. Now if any of a frame's patches needs the AI, all of them get it. This means somewhat more AI work on such frames; frames with only smooth backgrounds still skip it.
  - A faint outline of the letters. The AI's result can be slightly brighter or darker than the real picture right next to the text. That difference is now measured along the edge of the text, spread smoothly across the repainted area and mostly removed (60%: removing all of it made the edge smoother than the picture itself). On the saved frames, the step at the edge dropped from 2.6 to 1.2 and from 3.4 to 2.1, against 1.6 and 1.5 between neighbouring pixels of real picture.
- Not fixed: when there is detail the AI can't see (small figures walking behind the text), it still invents something, which can look smeared. That is the limit of the model.

## [1.0.0.27] - 2026-09-29

### Fixed
- "Text found in the samples" showed garbled lines ("T e surgery as as c ss", "Öoyjrul"). Detect reads its sample frames whole, before it knows where the subtitles are or what colour they are (that is how it finds them), and the list showed those first readings, background and all. Once Detect has found the area and the colour, each listed moment is now read again the way extraction reads it: the subtitle area at the reading scale, with the background clean-up for white or yellow text. The list shows one line per moment. Moments that don't read as a subtitle that way (a sign, a stray reading) are left out. On the test video, every line now matches the real subtitle at 97% or better. Detect takes a few seconds longer: one extra reading per listed moment.

### Changed
- AI fill: the sessions for 320- and 384-pixel patches (two-line subtitles) are built in the background right after the model loads. The first preview no longer pauses about 7 seconds for one partway through. Patches at other sizes keep running while they are built.

## [1.0.0.26] - 2026-09-29

### Fixed
- The before / after players (and any video opened paused at a time) showed black until Play was pressed. Each player started the video, jumped to the time and paused at once, before VLC had drawn a single frame. Now a player starts right at the time and plays silently until VLC reports a frame there, then pauses and settles on the exact time with the sound back on. That usually takes a fraction of a second. If VLC never reports a frame, it pauses anyway after 2 seconds, so it is never left playing. Pressing Play or moving the slider meanwhile works as expected.
- "Check a frame": the black bars on both sides of the enlarged text strip are gone. They were only the strip's backdrop, but looked like missing pictures.

## [1.0.0.25] - 2026-09-28

### Changed
- AI fill keeps the graphics card busier. Per the Log of the first full episode on an RX 6850M XT (71 minutes), about 13 of those minutes went to work the card waited for: text search, preparing and pasting patches, and reading frames.
  - The AI now runs as its own stage. Text search and the ordinary fill for the next batch of frames happen while the card repaints the current one. There are four batches in flight (reading, text, AI, writing), up from three.
  - Inside the AI stage, the patches for the next model call are prepared while the card runs the current call, and results are pasted back on all processor threads (one frame per thread, in order within a frame).
  - AI batches hold up to 32 frames (was 16), limited by memory rather than by the number of processor threads, so fewer model calls are padded with blank patches.
  - A 320-pixel patch size sits between 256 and 384, for text a little too tall for 256, at about 60% of the 384 cost.
  - On graphics cards with 10 GB or more, the first load also measures 16 patches per call. It's kept only if it's at least 10% faster per patch. The remembered choice is measured again once because of this.
- The Timing line in the Log shows the stages separately (text stage: waiting for frames, text search, waiting for the AI; AI stage: busy, graphics card time, waiting for the encoder), and how many patches ran at each size.

## [1.0.0.24] - 2026-09-28

### Fixed
- 1.0.0.23 fell back to the processor ("Non-zero status code returned while running Sub node ... The parameter is incorrect"), so a 10-second preview took 8 minutes. Besides its inputs and output, the model file records the shape of every intermediate step (18,077 of them), all worked out for 512 × 512. The processor ignores those records, but DirectML checks each step's result against them, so at 256 pixels the very first step failed. With a flexible size the records are now dropped; ONNX Runtime works every shape out from the inputs. The result is unchanged (checked against the model edited with the onnx library).
- When the plain setup failed, the loader went straight to the processor without trying the other setups. It now tries every setup before making the batch smaller, and stops halving when every failure has the same cause, because then it isn't the card's memory. If the graphics card can't take a flexible size at all, the model is used at a fixed 512 pixels, as up to 1.0.0.22; the processor is the last resort.
- The remembered choice now includes the patch size (256 or 512). The choice saved by 1.0.0.23 is measured again.

## [1.0.0.23] - 2026-09-28

### Changed
- AI fill runs each patch at its own size instead of always enlarging it to 512 × 512 pixels. The model file declares a fixed 512 input, but the model works out its sizes from the picture it's given, so the app marks height and width as flexible when it loads the model (the file on disk is unchanged). Patches run at 256, 384 or 512 pixels, whichever is the smallest that fits. For subtitles up to about 116 pixels high, which covers most videos including 1440 × 604, that's 256: a quarter of the pixels. It measured 4.1 times faster on the processor. On frames saved from an episode, the text is gone just as cleanly and nothing outside it changes; the result is, if anything, slightly crisper, because the patch is no longer enlarged and shrunk back. How much faster it is on a graphics card will show in the Log.
- The graphics-card setups are now measured on 256-pixel patches, and the "fixed" setups build one session per patch size, as needed. The choice made by 1.0.0.22 is measured again once, because the setup file's version changed.

### Fixed
- "Profile failed: Could not find file ...\system32\onnxruntime_profile_...json": the profile's location was set after profiling was switched on, which is too late (ONNX Runtime reads it at that moment), so it went to the default relative name. The profile now goes to the app's temp folder and is summarised into the Log.

## [1.0.0.22] - 2026-09-28

### Added
- AI fill reuses results across frames. A subtitle stays on screen for a second or more, and in many shots the picture behind it hardly changes. When a patch's surroundings still match a patch the model repainted in the last 2 seconds (same place, text within the repainted area), the repainted pixels are reused instead of running the model again. The comparison is always against the frame the model actually ran on, so small changes never add up. It needs a mean difference of 2 brightness levels or less around the text, with almost no pixels changing sharply; a camera pan, a lighting change or someone walking behind the text runs the model again. How much this saves depends on the scene. The status line and the Log report how many patches were reused.
- The first time the AI model loads on a PC, the app measures three ways of setting it up on the graphics card and keeps the fastest. The three are: 1.0.0.20's plain setup, a fixed batch size, and 1.0.0.21's fixed batch with the weights expanded, which turned out slower than 1.0.0.20 on an RX 6850M XT (about 310 ms per patch against about 190). The choice is remembered in models\directml-setup.txt, so later starts skip the measuring; delete that file to measure again. The first load takes about a minute.
- One call of the chosen setup is profiled into the Log: how many steps ran on DirectML, how many were handed to the processor, and which kinds of step took the time.

### Changed
- At start-up, the libraries unpacked by older builds of the EXE in %TEMP%\.net\SubtitleStudio are deleted, several hundred MB per build. This only happens when no other copy of Subtitle Studio is running, and only to folders holding Subtitle Studio's own files. The Log says how much was freed.
- subt_errors.log starts over after 2 MB, keeping the previous one as subt_errors.old.log, like the activity log.

## [1.0.0.21] - 2026-09-28

### Changed
- AI fill should be faster on the graphics card (1.0.0.20 managed about 2 frames per second, around 16 hours for an episode):
  - The model's session is built for a fixed number of patches per call. With every size known up front, ONNX Runtime works out the Fourier matrices and all the size arithmetic once at load time, instead of on every call: about 8,300 steps per call become about 2,900. Most of the removed ones are small steps that DirectML hands back to the processor and waits for. The model's compressed (int8) weights are also expanded once at load instead of on every call, which uses about 210 MB of graphics memory. A short last call is padded with blank patches. The results are the same as before: checked against the unmodified model, the largest difference is 0.003 of a brightness level.
  - Reading frames from ffmpeg, removing the text (including the AI) and writing to the encoder now run at the same time, with three batches of frames in flight, instead of taking turns. The graphics card no longer waits while frames are decoded and encoded.
  - AI fill takes up to 16 frames per batch (was 4), so the model gets full calls.
  - Preparing patches for the model is about 3x faster, and runs on all processor threads.
- The Log records how fast the AI model is when it loads (milliseconds per patch, after the first run compiles the shaders). At the end of each preview or video it also records where the time went: waiting for decoded frames, text search and fill, AI (model time, calls, milliseconds per patch), and waiting for the encoder.

## [1.0.0.20] - 2026-09-28

### Fixed
- AI fill failed on the graphics card with "Non-zero status code returned while running MatMul node ... rttn/MatMul_5 ... The parameter is incorrect". The LaMa model does its Fourier transforms as 144 five-dimensional matrix multiplies, and DirectML only multiplies up to four dimensions. When the model loads for DirectML, those 144 steps are now rewritten in memory into the equivalent four-dimensional form (swap the multiply's operands, then add the extra axis afterwards). The result is the same picture: on a saved frame the largest difference is under 0.001 of one brightness level. The model file on disk is not changed, so its SHA-256 check still holds.

### Changed
- Loading the AI model now includes a warm-up run on blank patches. It compiles the graphics card's shaders up front, so the first frames aren't slow, and it proves the batch size fits. If the batch from the hardware plan fails, the app retries with half as many patches per call (8, 4, 2, 1). If the card can't run the model at all, the app uses the processor, and the Log says why.
- The Log shows each loading step under "AI": the model rewrite, each warm-up attempt with its batch size, and the device in use.
- If the AI model fails partway through, the message starts with "AI fill failed" and names the device instead of showing the raw ONNX Runtime error. The full error is still in the Log.

## [1.0.0.19] - 2026-09-28

### Added
- Log tab (between Settings and Help): everything the app did, newest at the bottom, colour-coded by level. Every status message; each Detect, Extract, Preview and Create video with its settings, frames per second and time taken; which encoder passed its test; which graphics card the AI model loaded on (with a warning if it fell back to the processor); AI patches repainted; free space and drive type checks; unexpected errors. "Show details" adds the exact ffmpeg command lines and anything ffmpeg printed. "Copy all" puts it on the clipboard to paste into a message; Ctrl+C copies selected lines. Everything is also written to subt_activity.log next to the app (it starts over after 2 MB, keeping subt_activity.old.log).
- Hardware detection (Settings > Engines & tools > This PC, and the first lines of the Log): processor and threads, total and free memory, graphics card and its memory, and the drive the app is on (NVMe SSD / SSD / hard drive / USB / network, model and free space). Plain Windows APIs, no admin rights.
- Removal is sized to this PC: all processor threads work on frames in parallel; the number of frames held in memory at once is an eighth of the free memory (256 MB to 2 GB) instead of a fixed 512 MB; AI fill sends 1 to 8 patches per call depending on graphics memory (8 on a 12 GB card, was 4).
- Before Create video: if the output drive is short of space for the new video (about the original's size plus 30%), the app asks before starting instead of failing near the end; a note is logged when the original or the output is on a network, USB or optical drive.

## [1.0.0.18] - 2026-09-28

### Added
- Burned-in tab, step 4: "AI fill on the graphics card (best, slowest)". An AI inpainting model (LaMa) repaints the picture behind the text where it has detail (buildings, grass, faces, the edge of a car door) instead of averaging it into a smear. Smooth, out-of-focus areas keep the ordinary Fill in, which looks the same there and costs nothing. On frames saved from an affected episode the text is gone with nothing outside it changed, and lines through the text (a lanyard, a door edge) continue instead of blurring.
  - Runs inside Subtitle Studio with ONNX Runtime (built into the EXE) on the graphics card through DirectML: AMD, NVIDIA and Intel. It uses the card shown in "Hardware and text recognition" (on laptops, not the integrated one), and falls back to the processor if the card can't be used. No other program is needed.
  - The model file (LaMa from the OpenCV model zoo, Apache-2.0, 90 MB) is downloaded by the app once, on request ("Download model"), checked with SHA-256 and kept in a models folder next to the app. "Use a model file..." takes a copy you already have.
  - The text area is covered with square patches; patches go to the model in batches. The status line reports how many patches the AI repainted.

### Changed
- AI acceleration uses DirectML on every graphics card, including NVIDIA (CUDA would need NVIDIA's toolkit installed separately).
- The EXE is larger (ONNX Runtime with DirectML).

## [1.0.0.17] - 2026-09-27

### Fixed
- Burned-in tab: the before / after players were always shown on top of the frame view, as empty black or grey boxes over the frame and Check a frame, even before any preview existed. Their show / hide setting was bound in the wrong place and never applied. They now appear only after a preview or Create video, and the Frame button switches back. Every view is now checked for this mistake before a build ships.

## [1.0.0.16] - 2026-09-27

### Added
- Burned-in tab, step 4 "Remove from the picture": writes a new video without the burned-in subtitles. The original is never changed; audio, subtitle tracks, chapters and metadata are copied unchanged, and the result is added to Source / Files.
  - Every frame's subtitle area is checked with the same text finder extraction uses; only frames with text are touched, and only the text (letters, outline, shadow) is replaced. Frames are processed on all processor cores at once.
  - Two methods: "Fill in (sharpest)" rebuilds the text pixels from the picture around them; "Fill in + soften (smoothest)" adds a light blur over the area to hide remnants on busy pictures.
  - Preview 10 s: renders 10 seconds from the frame shown in Check a frame, in a few seconds.
  - Before / after: the original (with sound) and the result (muted) play side by side inside the tab, kept in step (play, pause, seek, 2 s steps). Switch back to the still frame with the Frame button.
  - Encoding uses the graphics card's own encoder when it works (AMD AMF, NVIDIA NVENC, Intel Quick Sync; checked with a short test encode), otherwise the processor (x264). High quality H.264; the same container as the original (MP4 / MOV / MKV), else MKV. Progress with frames per second and time left; Cancel removes the unfinished file.
- Extraction speed "Frame-exact: every frame": cue times exact to the frame (1/24 s at 24 fps), about three times slower than Precise timing.

### Coming next
- AI fill on the graphics card (ONNX Runtime with DirectML / CUDA) for the best result over detailed backgrounds.

## [1.0.0.15] - 2026-09-27

### Fixed
- Burned-in reading lost letters where thin white subtitles with only a faint shadow cross a light background (a face, a white collar), and dropped dimmer-looking speaker labels such as "[driver]". The clean-up now also recognises letters by their exact colour: strokes of precisely the subtitle's white, bounded by anything of another colour, are kept even without a dark edge. Colour fringes from video compression (slivers much thinner than a letter stroke) are ignored. On frames saved from an affected episode, "[driver] Excuse me, which way is the hospital?" and "Look at you. You haven't changed at all." are now read exactly (before: "Excur- me, vvmch way is the hospital?" and "Look at You ha Ilt anged"). Those frames are now part of the automatic checks.

## [1.0.0.14] - 2026-09-26

### Fixed
- Burned-in extraction broke one screen line into several stacked lines when words in the middle couldn't be read ("Look at you. You haven't changed at all." came out as "-ook at / You ha / inged a:"). Pieces at the same height are now joined left to right on one line.
- The detected subtitle area could cut off the top line of two-line subtitles with wide line spacing; it now leaves room for normal spacing.

### Added
- Burned-in tab, Check a frame: "Save images" writes the frame, the subtitle area and what the text reader sees as PNG images, plus the readings and settings in a text file, to a subt_frames folder next to the app, and opens the folder. Useful to share when reading goes wrong.

### Known limits
- White subtitles with only a faint shadow lose letters where they cross something equally light (a white shirt, a bright face). Being worked on with real frames from affected videos (improved in 1.0.0.15).

## [1.0.0.13] - 2026-09-26

### Added
- Subtitles tab: "Overlay" switch next to Follow (and Ctrl+H) to hide the cue text drawn over the video, so the bare picture shows, for example to compare burned-in subtitles with what was extracted. Remembered between sessions.

## [1.0.0.12] - 2026-09-26

### Fixed
- The subtitle preview opened in a separate "VLC (Direct3D11 output)" window, and the player in the Subtitles tab stayed black. This happened when a video was opened while the Subtitles tab wasn't on screen (for example, straight after Extract in the Burned-in tab): VLC had nowhere to draw and made its own window. The video now waits until the Subtitles tab's player is on screen, and stops when you switch tabs (it picks up at the same spot when you come back).
- Burned-in extraction kept misreads of the picture such as "hcnksj": words of four or more letters with no vowel are treated as misreads.

### Added
- Burned-in tab, "Check a frame": step (1 s) or drag to any moment and see the subtitle area exactly as the text reader gets it, what it reads there as subtitle text, and all the text it found before filtering. Updates as you move the area or switch the clean-up or language. Clicking a line under "Text found in the samples" jumps to that frame.

## [1.0.0.11] - 2026-09-26

### Changed
- Burned-in subtitle reading is much more accurate. On a test film with moving, colourful picture behind the text, correctly read subtitles went from 0 of 8 to 7 of 8 (the eighth, white text with only a shadow over a near-white light, is left out rather than garbled):
  - Clean-up before reading: only light text with a dark outline or shadow is kept, of the subtitle's own colour, and the picture behind it is removed. The text reader gets clean black text on white. On by default when Detect finds white or yellow subtitles; can be switched off for coloured ones.
  - The subtitle area is read at full resolution, and small text is enlarged (up to 3x) instead of being shrunk to 1280 px wide.
  - Each subtitle is read several times while it's on screen, including once from several frames combined (the text stays put while the picture moves), and the reading that agrees best wins.
- Detect finds a much tighter subtitle area: it looks for where text sits at the same line in many frames and says something different each time, and sizes the area for two lines. Signs, screens and captions elsewhere in the picture no longer stretch it (before, it could cover half the frame).
- New "What the text reader sees" strip under the frame: the subtitle area exactly as it will be read, updated live as you move the sliders or switch the clean-up.

### Fixed
- End credits and other text were extracted as subtitles. Now dropped: text that moves (rolling credits), blocks of more than three lines, text off-centre or much larger or smaller than the subtitles, and readings that look like garbage (for example "rcJrjye me, hlCh").
- The same subtitle was split into several short cues when some frames were misread. Readings with stray symbols around the text now count as the same subtitle, one misread frame is bridged, and neighbouring cues with the same text are merged. Cues shorter than 0.7 s are dropped.
- Start and End times in the subtitle list were cut off ("00:00:01,00"): the columns are wider.

## [1.0.0.10] - 2026-09-26

### Fixed
- Publishing failed since 1.0.0.7 (NETSDK1083 / NETSDK1082 for win10-arm and other UWP runtime identifiers). Root cause: targeting the Windows 10 SDK (for Windows OCR) made NuGet pick LibVLCSharp 3.8.5's WinUI build, which pulls in Microsoft.WindowsAppSDK and SharpDX; their build scripts add legacy UWP runtime identifiers. Updated to LibVLCSharp 3.10.1, which has no WinUI build and targets .NET 10 directly. The UseRidGraph workaround from 1.0.0.8 is removed.
- Removed the System.Security.Cryptography.ProtectedData package (warning NU1510): it is part of the .NET 10 Windows Desktop runtime.

## [1.0.0.9] - 2026-09-26

### Changed
- Moved to .NET 10 (the current long-term-support release): the app now targets net10.0-windows10.0.19041.0 and builds with the stable .NET 10 SDK pinned in global.json. Nothing changes for running it: the EXE is still self-contained and needs nothing installed.

## [1.0.0.8] - 2026-09-26

### Fixed
- 1.0.0.7 failed to publish on newer .NET SDKs with NETSDK1083 ("RuntimeIdentifier 'win10-x64' is not recognized"): the Windows SDK reference (needed for Windows OCR) still names legacy runtime identifiers. The project now loads the full runtime-identifier graph (UseRidGraph).
- The repo is pinned to the stable .NET 10 SDK with global.json (10.0.3xx, no previews), so local builds and CI use the same SDK. The app still targets .NET 8.

## [1.0.0.7] - 2026-09-26

### Added
- New "Burned-in subs" tab (between Subtitles and AI Transcribe) for subtitles that are part of the picture.
- Detect: reads 36 frames spread over the video with Windows' built-in OCR and reports whether burned-in subtitles are present, where (top or bottom) and what they say. Text that never changes (channel logos, watermarks) is ignored. A sample frame is shown with the subtitle area highlighted; the area can be adjusted with two sliders.
- Extract to subtitle: reads the subtitle area through the whole video (2, 4 or 8 checks per second, decoded on the graphics card when possible), skips frames where nothing changed, merges OCR jitter and one-frame misreads, and opens the result as a new unsaved subtitle in the Subtitles tab, paired with the video and named for it. Progress with time left, and Cancel.
- OCR language picker (any OCR language installed in Windows).
- Graphics card detection (NVIDIA, AMD, Intel) with the AI backend it will use: DirectML on AMD / Intel / NVIDIA, CUDA also offered on NVIDIA, processor fallback. Shown in the Burned-in tab and in Settings > Engines & tools.

### Changed
- Targets the Windows 10 SDK (net8.0-windows10.0.19041.0) to use the built-in Windows OCR. Requires Windows 10 1809 or later.

## [1.0.0.6] - 2026-09-26

### Fixed
- If Subtitle Studio fails while starting, it now shows the reason, logs it and exits. Before, the error was swallowed and the process kept running with no window, which locked the EXE (the next build then failed with "Access is denied").
- build_and_launch.bat detects a running Subtitle Studio (any version) and offers to close it before publishing, and stops with a clear message if the publish folder is still locked.

## [1.0.0.5] - 2026-09-26

### Fixed
- 1.0.0.4 crashed on startup ("A TwoWay or OneWayToSource binding cannot work on the read-only property 'AudioProgress'"): the audio progress bar bound two-way to a read-only value. Bound one-way; every view is now checked for this mistake before a build ships.

### Changed
- The published EXE carries its version in the name, like Video Metadata Editor: "SubtitleStudio v1.0.0.5.exe".
- The version is shown as "v1.0.0 build 5" under the app name in the toolbar, in the title bar, the status bar and Help.

## [1.0.0.4] - 2026-09-26

### Added
- Video preview in the Subtitles tab (LibVLC: plays MKV, WebM, MP4, AVI and more). Shows the paired video with the cue under the playhead drawn on top, updated live as you edit; VLC's own subtitle rendering is switched off so nothing doubles up. Play/pause, 2 s steps and a seek bar; picking a cue jumps the video to it; Follow selects the cue under the playhead while playing; drag the splitter to resize the video.
- Start / End buttons set the selected cue's times from the playhead.
- Sync tools: shift all or selected cues by a signed offset; re-time for frame-rate drift (25 / 24 / 23.976 / 29.97 presets); two-point sync from two cues lined up with the video (fixes offset and drift together).
- Audio-based sync (needs ffmpeg): Detect offset compares the cues with where speech occurs and offers the shift; Snap to speech moves cue edges to the nearest start/end of speech within 0.25 to 1 s. The audio is analysed once per video, with progress and Cancel.
- Undo / redo (Ctrl+Z / Ctrl+Y, up to 50 steps) for cue add/delete/split/merge/sort, playhead timing and all sync operations.
- Shortcuts: Ctrl+Space play/pause, Ctrl+Left / Ctrl+Right seek.

### Changed
- The EXE now carries LibVLC and is noticeably larger. It stays a single portable file: on first launch it unpacks once to a cache under %TEMP%\.net\SubtitleStudio (later launches reuse it); settings still live next to the EXE.

## [1.0.0.3] - 2026-09-25

### Added
- Subtitles editor: open SRT, VTT, ASS, SSA and MicroDVD SUB (encoding detected: UTF-8/16/32 with or without BOM, else Windows-1252); cue list with number, start, end, length and text; edit times and text in the list or in the side panel; add, split, merge and delete cues; sort by time.
- Checks: negative or zero durations and cues before 0:00 (errors), empty text, out-of-order cues and overlaps (warnings; overlaps are not flagged for ASS/SSA, where they are normal). Errors ask for confirmation before saving; F8 jumps to the next problem.
- Saving: pair with a video from Source / Files to get VideoName.lang[.forced][.hi|.sdh].ext next to it; format, language and flags set per file. Converting formats keeps italic/bold/underline and {\an} positions; ASS/SSA styles, layers and header, and VTT cue ids/settings/STYLE blocks round-trip unchanged within the same format. Saves are atomic, and a changed format never overwrites the original.
- Source / Files: double-click a subtitle, or use Open in editor / Edit (for subtitles found next to a video) / New subtitle (for a video). Sidecar list refreshes after saving.
- Image-based VobSub .sub/.idx and .sup are recognised and explained instead of opened as garbage.
- Shortcuts: Ctrl+S, Ctrl+Shift+S, Ctrl+O, Ctrl+N, Insert, Delete, F8. Closing the window with unsaved subtitle changes asks first.

### Changed
- Durations of files listed while ffmpeg was missing are read automatically once ffmpeg is found.
- Scanning a large folder shows a live count of files checked in the status bar.

## [1.0.0.2] - 2026-09-25

### Fixed
- Build failed with CS0246 ('Stream' not found): the WPF SDK removes System.IO from implicit usings. Added a project-wide global using for System.IO.

## [1.0.0.1] - 2026-09-25

### Added
- Project skeleton: WPF on .NET 8, single-file self-contained portable x64 EXE; version driven by one FullVersion property and shown in the title bar and status bar.
- MVVM shell with seven tabs (Source / Files, Subtitles, AI Transcribe, Translate, Dubbing, Settings, Help); each tab is its own view with its own MainViewModel partial, and work lives in Services.
- Source / Files: add files, add folder (optional subfolders), drag and drop anywhere on the window, Delete key to remove rows; list shows name, type, format, duration (via ffprobe) and sidecar subtitles detected by Plex/Jellyfin naming (lang, forced, hi, sdh).
- Hand-off from Video Metadata Editor: video path on the command line is imported at startup; if Subtitle Studio is already running the path is forwarded to the open window over a named pipe instead of starting a second copy.
- Settings (auto-saved to subt_settings.json next to the EXE): OpenAI API key encrypted with Windows DPAPI and masked once saved (Edit / Remove), ffmpeg and local Whisper paths with auto-detection, default output format and language with a sidecar-name preview, behaviour toggles, dark / light theme.
- Design tokens for dark (default) and light themes with a live switch and a matching native title bar; restyled buttons, inputs, lists, grid, tabs, tooltips and slim scrollbars.
- Help: features, supported formats, workflow, hand-off instructions, colour/icon legend and this revision history. Contextual (i) hints with a global on/off switch; hook for a later first-run guided tour.
- Engine interfaces for later builds: ITranscriptionService, ITranslationService, ITtsService, ISubtitleFormat, ServiceQuotaException.
- Repo tooling: build_and_launch.bat, publish-release.ps1, GitHub Actions build workflow, application icon.
