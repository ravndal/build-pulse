using System.Collections.Concurrent;

namespace BuildPulse;

/// <summary>
/// A single-threaded loop for the terminal app. Keys, ticks and every await continuation run here one at a time,
/// so <see cref="Watcher"/> and the screen never see two threads, as on a UI thread.
/// </summary>
sealed class MainLoop : SynchronizationContext
{
    readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* stopped */ }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public void Post(Action action) => Post(_ => action(), null);

    public void Stop() => _queue.CompleteAdding();

    /// <summary>Runs queued work on the calling thread until <see cref="Stop"/>.</summary>
    public void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try { callback(state); }
            catch (Exception ex) { Log.Write($"TUI: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
