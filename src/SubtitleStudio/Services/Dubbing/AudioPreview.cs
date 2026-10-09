using System.Runtime.InteropServices;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>Plays a short WAV (a voice preview) and stops it. Behind an interface so tests stay silent.</summary>
public interface IAudioPreview
{
    void Play(string wavPath);

    void Stop();
}

public sealed class SilentAudioPreview : IAudioPreview
{
    public string? LastPlayed { get; private set; }

    public void Play(string wavPath) => LastPlayed = wavPath;

    public void Stop()
    {
    }
}

/// <summary>Windows' own sound playback (winmm PlaySound): plays in the background, a new sound stops the last.</summary>
public sealed class WindowsAudioPreview : IAudioPreview
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndFilename = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    public void Play(string wavPath)
    {
        if (!PlaySound(wavPath, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault))
            throw new InvalidOperationException("Windows couldn't play the preview (is a sound device connected?).");
    }

    public void Stop() => PlaySound(null, IntPtr.Zero, 0);
}
