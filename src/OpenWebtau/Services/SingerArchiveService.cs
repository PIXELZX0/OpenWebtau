using OpenUtau.Core;

namespace OpenWebtau.Services;

/// <summary>
/// Puts the stored voicebanks back into the wasm VFS, once per session. Editors
/// await this before opening a stored project so singer references resolve;
/// without it a reload races project load against restore.
///
/// With a voicebank server (mcp/server.mjs), the banks are already extracted
/// there and only downloaded. Without one, the archives kept in IndexedDB are
/// extracted here in the browser.
/// </summary>
public class SingerArchiveService {
    readonly object gate = new();
    Task? restoreTask;

    /// <summary>Runs at most once per page load; later calls observe the same task.</summary>
    public Task EnsureRestoredAsync(ProjectStore store, ProjectService projects, HttpClient http) {
        lock (gate) {
            restoreTask ??= RestoreAsync(store, projects, http);
            return restoreTask;
        }
    }

    async Task RestoreAsync(ProjectStore store, ProjectService projects, HttpClient http) {
        try {
            if (await store.HasVoicebankServerAsync()) {
                await store.MoveLocalSingersToServerAsync();
                foreach (var bank in await store.ListBanksAsync()) {
                    try {
                        await projects.LoadBankAsync(http, await store.BankUrlAsync(bank.Dir), bank.Dir);
                    } catch (Exception ex) {
                        Serilog.Log.Error(ex, "Failed to load voicebank {Dir}", bank.Dir);
                    }
                }
                projects.RescanSingers();
                return;
            }
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
