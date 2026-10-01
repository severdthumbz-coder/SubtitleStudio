using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Services;

/// <summary>
/// One Subtitle Studio window per Windows session. A second launch (e.g. another VME hand-off)
/// forwards its paths over a named pipe to the running window and exits.
/// Pipe protocol: UTF-8 text, one absolute path per line; an empty message just activates the window.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstanceService()
    {
        var scope = $"{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = $"SubtitleStudio.Handoff.{scope}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\SubtitleStudio.SingleInstance.{scope}", out bool createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    /// <summary>Second instance only: send paths to the primary. False if it could not be reached.</summary>
    public bool TrySendToPrimary(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);

            // We were just launched by the user/VME, so we may hold foreground rights; pass them on
            // so the primary window is allowed to come to the front.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var path in paths) writer.WriteLine(path);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Primary only: listen for hand-offs. The callback runs on a background thread.</summary>
    public void StartListening(Action<IReadOnlyList<string>> onPathsReceived)
    {
        if (!IsPrimary) return;
        _ = Task.Run(() => ListenLoopAsync(onPathsReceived, _cts.Token));
    }

    private async Task ListenLoopAsync(Action<IReadOnlyList<string>> onPathsReceived, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var paths = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                onPathsReceived(paths);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // A broken client must not kill the listener. Back off briefly and keep serving.
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned by this thread; the OS releases it on exit */ }
        }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
