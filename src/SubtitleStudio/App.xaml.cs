using System.Text;
using System.Windows;
using System.Windows.Threading;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Onboarding;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Playback;
using SubtitleStudio.Services.Video;
using SubtitleStudio.ViewModels;

namespace SubtitleStudio;

/// <summary>
/// Startup only: single-instance check, composition of services, theme, window, hand-off.
/// No feature logic lives here.
/// </summary>
public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var handoffPaths = HandoffService.ParseArgs(e.Args);

        // Already running? Forward the paths (VME hand-off) to that window and exit.
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary && _singleInstance.TrySendToPrimary(handoffPaths))
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);

        try
        {
            StartApp(handoffPaths);
        }
        catch (Exception ex)
        {
            // A failed start must never leave a window-less process behind (it would lock the EXE
            // and the single-instance slot). Explain, log, exit.
            FailStartup(ex);
        }
    }

    private void StartApp(IReadOnlyList<string> handoffPaths)
    {
        var settingsService = new SettingsService(AppPaths.SettingsFile);
        var config = settingsService.Load();

        var log = new ActivityLog(Path.Combine(AppPaths.ExeDirectory, "subt_activity.log"));
        _services = new AppServices
        {
            Config = config,
            Settings = settingsService,
            ApiKeys = new ApiKeyStore(config, settingsService, new SecureKeyService()),
            Theme = new ThemeService(),
            Import = new FileImportService(),
            Ffmpeg = new FfmpegLocator(),
            Probe = new MediaProbeService(),
            Dialogs = new WpfDialogService(),
            Tour = new NullGuidedTourService(),
            Playback = new VlcPlaybackService(),
            CreatePlayer = () => new VlcPlaybackService(),
            LoadInpaintModel = (path, gpu, batch) => OnnxInpaintModel.Load(path, gpu, batch, m => log.Info("AI", m)),
            Log = log,
            Session = new SessionStore(Path.Combine(AppPaths.ExeDirectory, SessionStore.FileName)),
            BurnedIn = new BurnedInService(new FrameSampler(), new WindowsOcrService()),
        };

        // AI Transcribe: whisper.cpp in the app (Vulkan graphics card or processor). Its messages go to the Log.
        var ffmpegLocator = _services.Ffmpeg;
        _services.TranscriptionEngines.Add(new Services.Transcription.LocalWhisperEngine(
            new Services.Transcription.WhisperNetRunner((message, warning) =>
                {
                    if (warning) log.Warning("whisper.cpp", message);
                    else log.Detail("whisper.cpp", message);
                },
                // The Silero speech detector is built into the EXE; written to models\vad on first use.
                () => Services.Transcription.SileroVad.EnsureModel(Path.Combine(AppPaths.ExeDirectory, "models", "vad"), m => log.Warning("Transcribe", m))),
            () => ffmpegLocator.Resolve(config.FfmpegPath), log));

        // Translate: a language model with llama.cpp in the app (same graphics card choice). Its messages go to the Log.
        _services.TranslationEngines.Add(new Services.Translation.LocalLlmTranslator(
            new Services.Translation.LlamaSharpRunner((message, warning) =>
            {
                if (warning) log.Warning("llama.cpp", message);
                else log.Detail("llama.cpp", message);
            }), log));

        _services.Theme.Apply(config.Theme);
        _services.Log.Info("App", $"Subtitle Studio {AppInfo.DisplayVersion} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}.");

        // Libraries unpacked by older builds of the single-file EXE (never removed by .NET).
        var appLog = _services.Log;
        _ = Task.Run(() =>
        {
            try
            {
                if (ExtractionCleanup.Run(AppContext.BaseDirectory) is { } r && r.Deleted + r.Skipped > 0)
                    appLog.Info("App", $"Removed {r.Deleted} unpacked folder(s) left by older builds in %TEMP%\\.net ({r.FreedBytes / (1024.0 * 1024):0} MB)" + (r.Skipped > 0 ? $"; {r.Skipped} in use, tried again next start." : "."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                appLog.Detail("App", "Clean-up of older unpacked folders skipped: " + ex.Message);
            }
        });

        _viewModel = new MainViewModel(_services);
        AsyncRelayCommand.UnhandledError = ex =>
        {
            LogError(ex);
            _services.Log.Error("App", "Unexpected error: " + ex);
            _viewModel.SetStatus($"Something went wrong: {ex.Message}", StatusKind.Error);
        };

        _window = new MainWindow(_viewModel, _services.Theme, config.Window);
        MainWindow = _window;
        _window.Show();

        if (settingsService.LoadWarning is { } warning)
            _viewModel.SetStatus(warning, StatusKind.Warning);
        else if (!_services.Ffmpeg.Resolve(config.FfmpegPath).IsComplete)
            _viewModel.SetStatus("Ready. ffmpeg was not found: durations will show '-' until it is set in Settings > Engines & tools.", StatusKind.Warning);

        // Set in OnStartup before StartApp runs (the compiler can't see that across methods).
        if (_singleInstance is { IsPrimary: true } instance)
            instance.StartListening(paths => Dispatcher.InvokeAsync(() => OnHandoffReceived(paths)));

        _ = StartSessionAsync(handoffPaths);

        _ = MaybeStartGuidedTourAsync();
    }

    /// <summary>
    /// Crash recovery: a session left by a run that didn't close normally is offered back (if enabled in
    /// Settings), with its interrupted Create video or Extract restarted if wanted; then hand-off files
    /// are imported, and only then does autosaving start (so the old session is never overwritten first).
    /// </summary>
    private async Task StartSessionAsync(IReadOnlyList<string> handoffPaths)
    {
        if (_viewModel is null || _services is null) return;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // the main window is on screen first
            var store = _services.Session;
            if (store is not null && _services.Config.RestoreSessionAfterCrash && store.LoadLeftOver() is { HasContent: true } left)
            {
                _services.Log.Warning("Session", $"The previous session didn't close normally (last saved {left.SavedUtc.ToLocalTime():g}).");
                var choice = Views.Dialogs.RestoreSessionWindow.Ask(_window, left);
                _services.Log.Info("Session", "Restore prompt: " + choice switch
                {
                    Views.Dialogs.RestoreChoice.RestoreAndRestart => "restore and restart the interrupted job.",
                    Views.Dialogs.RestoreChoice.Restore => "restore.",
                    _ => "start fresh.",
                });
                if (choice == Views.Dialogs.RestoreChoice.StartFresh) store.Clear();
                else
                {
                    // Autosave starts now, so the restored session is kept while a restarted job runs.
                    _viewModel.StartSessionAutosave();
                    await _viewModel.RestoreSessionAsync(left, choice == Views.Dialogs.RestoreChoice.RestoreAndRestart);
                }
            }
            else store?.Clear();

            if (handoffPaths.Count > 0)
                await _viewModel.ImportAsync(handoffPaths, "hand-off");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogError(ex);
            _services.Log.Error("Session", "Restoring the previous session failed: " + ex.Message);
        }
        finally
        {
            _viewModel.StartSessionAutosave();
        }
    }

    private void FailStartup(Exception ex)
    {
        LogError(ex);
        MessageBox.Show(
            $"Subtitle Studio could not start:\n\n{ex.Message}\n\nDetails were written to {AppPaths.ErrorLogFileName} next to the EXE.",
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        _window?.Close();
        Shutdown(1);
    }

    private void OnHandoffReceived(IReadOnlyList<string> paths)
    {
        if (_viewModel is null || _window is null || _services is null) return;

        if (_services.Config.BringToFrontOnHandoff || paths.Count == 0)
            _window.BringToFront();

        var resolved = HandoffService.ParseArgs(paths);
        if (resolved.Count > 0)
            _ = _viewModel.ImportAsync(resolved, "hand-off");
    }

    private async Task MaybeStartGuidedTourAsync()
    {
        if (_services is null || _window is null) return;
        var config = _services.Config;
        if (!config.ShowGuidedTourOnFirstRun || config.FirstRunCompleted || !_services.Tour.IsAvailable) return;

        await _services.Tour.StartAsync(_window);
        config.FirstRunCompleted = true;
        _services.Settings.ScheduleSave(config);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_window is null || !_window.IsLoaded)
        {
            // Still starting up (or the window never appeared): don't linger invisibly.
            FailStartup(e.Exception);
            return;
        }

        LogError(e.Exception);
        _services?.Log.Error("App", "Unexpected error: " + e.Exception);
        _viewModel?.SetStatus($"Unexpected error: {e.Exception.Message} (details in {AppPaths.ErrorLogFileName})", StatusKind.Error);
        e.Handled = true;
    }

    private static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppInfo.Version}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            // Capped like the activity log: past 2 MB it becomes subt_errors.old.log and a new file starts.
            var file = new FileInfo(AppPaths.ErrorLogFile);
            if (file.Exists && file.Length > 2L << 20)
                File.Move(file.FullName, Path.ChangeExtension(file.FullName, ".old.log"), overwrite: true);
            File.AppendAllText(AppPaths.ErrorLogFile, entry, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            _viewModel?.EndSession(); // normal close: nothing to restore next time
            foreach (var engine in _services.TranscriptionEngines.OfType<IDisposable>()) engine.Dispose();
            foreach (var engine in _services.TranslationEngines.OfType<IDisposable>()) engine.Dispose();
            _services.Playback.Dispose();
            _viewModel?.ReleaseResources();
            _services.Settings.SaveNow(_services.Config);
            _services.Settings.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
