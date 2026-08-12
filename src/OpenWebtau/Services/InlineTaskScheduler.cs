namespace OpenWebtau.Services;

/// <summary>
/// OpenUtau.Core posts results back to the UI through DocManager.MainScheduler
/// (phonemizer responses, singer reloads, installer callbacks). TaskScheduler.Default
/// routes those to the thread pool, which never runs in single-threaded wasm, so the
/// work silently disappears — phonemizer output never lands, and nothing renders.
///
/// The browser has exactly one thread and Core only posts from it, so running each
/// task inline is both correct and the cheapest thing that works.
/// </summary>
public sealed class InlineTaskScheduler : TaskScheduler {
    protected override void QueueTask(Task task) => TryExecuteTask(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        => TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
}
