using OpenUtau.Core;

namespace OpenWebtau.Services;

/// <summary>
/// Reinstalls voicebank archives from IndexedDB into the wasm VFS, once per
/// session. Editors await this before opening a stored project so singer
/// references resolve; without it a reload races project load against restore.
/// </summary>
public class SingerArchiveService {
    readonly object gate = new();
    Task? restoreTask;

    /// <summary>Runs at most once per page load; later calls observe the same task.</summary>
    public Task EnsureRestoredAsync(ProjectStore store, ProjectService projects) {
        lock (gate) {
            restoreTask ??= RestoreAsync(store, projects);
            return restoreTask;
        }
    }

    async Task RestoreAsync(ProjectStore store, ProjectService projects) {
        try {
            await foreach (var (name, content, encoding) in store.ReadSingerArchivesAsync()) {
                try {
                    projects.InstallSinger(name, content, encoding);
                } catch (Exception ex) {
                    Serilog.Log.Error(ex, "Failed to restore singer {Name}", name);
                }
            }
        } catch (Exception ex) {
            Serilog.Log.Error(ex, "Failed to read voicebank archives");
        }
    }
}
