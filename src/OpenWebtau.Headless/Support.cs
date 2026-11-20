using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace OpenWebtau.Headless;

/// <summary>Posts continuations onto the main queue, so awaits come back to the document thread.</summary>
sealed class QueueContext : SynchronizationContext {
    readonly BlockingCollection<Action> queue;

    public QueueContext(BlockingCollection<Action> queue) {
        this.queue = queue;
    }

    public override void Post(SendOrPostCallback d, object? state) {
        try {
            queue.Add(() => d(state));
        } catch (InvalidOperationException) {
            // Shutting down: stdin closed and the queue no longer takes work.
        }
    }

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>Serilog has no console sink in the core package; stdout is the protocol anyway.</summary>
sealed class StderrSink : ILogEventSink {
    public void Emit(LogEvent e) {
        Console.Error.WriteLine($"[{e.Level.ToString()[..3].ToUpperInvariant()}] {e.RenderMessage()}");
        if (e.Exception != null) Console.Error.WriteLine(e.Exception);
    }
}
