using Microsoft.JSInterop;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenWebtau.Services;

public record ProjectSummary(string Id, string Name, long Updated, int Tracks, int Notes) {
    public DateTime UpdatedAt => DateTimeOffset.FromUnixTimeMilliseconds(Updated).LocalDateTime;
}

/// <summary>
/// Keeps projects in IndexedDB as .ustx text. The wasm filesystem is in-memory, so
/// anything not written here is gone when the tab closes.
/// </summary>
public class ProjectStore {
    const string ScratchDir = "/tmp/openwebtau";

    readonly IJSRuntime js;
    IJSObjectReference? module;

    public ProjectStore(IJSRuntime js) {
        this.js = js;
    }

    async Task<IJSObjectReference> Module() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/store.js");

    public async Task<List<ProjectSummary>> ListAsync() {
        var m = await Module();
        return await m.InvokeAsync<List<ProjectSummary>>("list");
    }

    public async Task<string> SaveAsync(string id, string name, UProject project) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, "save.ustx");
        Ustx.Save(path, project);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);

        int notes = project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count);
        var m = await Module();
        await m.InvokeVoidAsync("save", id, name, Convert.ToBase64String(bytes), project.tracks.Count, notes);
        return id;
    }

    /// <summary>Loads a stored project and makes it the current one.</summary>
    public async Task<bool> OpenAsync(string id) {
        var m = await Module();
        var base64 = await m.InvokeAsync<string?>("load", id);
        if (base64 == null) return false;

        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, "open.ustx");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        Formats.LoadProject(new[] { path });
        return true;
    }

    public async Task DeleteAsync(string id) {
        var m = await Module();
        await m.InvokeVoidAsync("remove", id);
    }

    public async Task RenameAsync(string id, string name) {
        var m = await Module();
        await m.InvokeVoidAsync("rename", id, name);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}
