namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// The graphics card and the processor speaking at the same time: one line on the card, several on the
/// processor. Each line goes to whichever has room, the card first, so a single line (Preview) is made
/// on the card. If the card fails on a line, it's left out from then on and the line is said again on the
/// processor.
/// </summary>
public sealed class SharedKokoroModel : IKokoroModel
{
    private sealed class Device(IKokoroModel model)
    {
        public IKokoroModel Model { get; } = model;
        public SemaphoreSlim Free { get; } = new(model.Concurrency, model.Concurrency);
        public volatile bool Failed;
        public int Pieces;
        public long Ticks;
    }

    private readonly Device[] _devices;
    private readonly SemaphoreSlim _any;
    private readonly Action<string>? _log;

    /// <param name="models">In order of preference (the graphics card first).</param>
    public SharedKokoroModel(IReadOnlyList<IKokoroModel> models, Action<string>? log = null)
    {
        if (models.Count == 0) throw new ArgumentException("No models.", nameof(models));
        _devices = models.Select(m => new Device(m)).ToArray();
        Concurrency = models.Sum(m => Math.Max(1, m.Concurrency));
        _any = new SemaphoreSlim(Concurrency, Concurrency);
        _log = log;
    }

    public string DeviceLabel => string.Join(" and ", _devices.Where(d => !d.Failed).Select(d => d.Model.DeviceLabel));

    public int Concurrency { get; }

    public float[] Speak(long[] tokens, float[] style, float speed)
    {
        _any.Wait();
        try
        {
            while (true)
            {
                foreach (var d in _devices)
                {
                    if (d.Failed || !d.Free.Wait(0)) continue;
                    try
                    {
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        var audio = d.Model.Speak(tokens, style, speed);
                        Interlocked.Increment(ref d.Pieces);
                        Interlocked.Add(ref d.Ticks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                        return audio;
                    }
                    catch (Exception ex) when (d != _devices[^1] && ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // The graphics card failed on this line: the processor takes over, from now on.
                        d.Failed = true;
                        _log?.Invoke($"{d.Model.DeviceLabel} failed on a line ({ex.Message.Split('\n')[0].Trim()}); the processor speaks the rest.");
                    }
                    finally
                    {
                        d.Free.Release();
                    }
                }
                Thread.Yield();
            }
        }
        finally
        {
            _any.Release();
        }
    }

    /// <summary>"312 lines on DirectML on … (0.9 s each), 268 on the processor (1.6 s each)", then counting again from zero.</summary>
    public string TakeUsage()
    {
        var parts = _devices.Select(d =>
        {
            int n = Interlocked.Exchange(ref d.Pieces, 0);
            long t = Interlocked.Exchange(ref d.Ticks, 0);
            double each = n == 0 ? 0 : t / (double)System.Diagnostics.Stopwatch.Frequency / n;
            return $"{n} on {d.Model.DeviceLabel}" + (n > 0 ? $" ({each:0.00} s each)" : string.Empty);
        });
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        foreach (var d in _devices)
        {
            d.Model.Dispose();
            d.Free.Dispose();
        }
        _any.Dispose();
    }
}
