using Microsoft.JSInterop;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenWebtau.Agent;
using OpenWebtau.Services;

namespace OpenWebtau.Pages;

/// <summary>
/// Commands from the MCP bridge (mcp/server.mjs). The tools themselves are in
/// Agent/AgentCommands.cs, shared with the headless host; this is the editor's
/// side of them, so the user sees every edit live and can undo it. A "track"
/// argument switches the visible track, so the agent edits in plain view.
/// </summary>
public partial class Editor : IAgentHost {
    IJSObjectReference? agentModule;
    string agentState = "off";
    AgentCommands? agentCommands;

    async Task ToggleAgent() {
        agentModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/agent.js");
        if (agentState == "off") {
            await agentModule.InvokeVoidAsync("start", selfRef);
        } else {
            await agentModule.InvokeVoidAsync("stop");
            agentState = "off";
        }
    }

    /// Reconnects after a reload when the user left the bridge switched on.
    async Task ResumeAgent() {
        agentModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/agent.js");
        if (await agentModule.InvokeAsync<bool>("wasEnabled")) {
            await agentModule.InvokeVoidAsync("start", selfRef);
        }
    }

    [JSInvokable]
    public Task OnAgentState(string state) {
        agentState = state;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task<string> AgentCall(string method, string argsJson) {
        agentCommands ??= new AgentCommands(this);
        var reply = await agentCommands.Call(method, argsJson);
        StateHasChanged();
        return reply;
    }

    // --- IAgentHost -------------------------------------------------------

    UProject IAgentHost.Project => Project;
    string IAgentHost.ProjectId => Id;
    string IAgentHost.ProjectName => projectName;
    int IAgentHost.TrackIndex => trackIndex;
    Task IAgentHost.SelectTrack(int index) => SelectTrack(index);
    UVoicePart IAgentHost.EnsurePart() => EnsurePart();
    Task IAgentHost.AfterEdit() => AfterEdit();
    void IAgentHost.ClearSelection() => selection.Clear();

    Task<object?> IAgentHost.Play(int? from) {
        if (from is int tick) OnSeek(tick);
        if (!playing) PlayOrPause();
        return Task.FromResult<object?>(new { playing });
    }

    Task<object?> IAgentHost.Stop() {
        Stop();
        return Task.FromResult<object?>(new { playing });
    }

    async Task<object?> IAgentHost.Export(string format) {
        switch (format) {
            case "ustx": await Download(); break;
            case "ust": await ExportUst(); break;
            case "wav": await ExportWav(); break;
        }
        return status;
    }

    async Task<object?> IAgentHost.ListProjects() => await Store.ListAsync();

    async Task<object?> IAgentHost.OpenProject(string id) {
        var summary = (await Store.ListAsync()).FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException($"no project '{id}'; see list_projects");
        if (id != Id) SwitchProjectSoon(id);
        return new { id, name = summary.Name, note = "The editor is reloading on this project; call get_project next." };
    }

    async Task<object?> IAgentHost.CreateProject(string name) {
        var id = ProjectStore.NewId();
        await Store.SaveAsync(id, name, OpenUtau.Core.Format.Ustx.Create());
        SwitchProjectSoon(id);
        return new { id, name, note = "The editor is reloading on the new project; call get_project next." };
    }

    /// The editor loads its project once, on page load, so switching reloads the
    /// page; the bridge reconnects by itself. Waits for the reply to go out first
    /// and saves this project, since the reload drops a pending autosave.
    void SwitchProjectSoon(string id) {
        _ = InvokeAsync(async () => {
            await Task.Delay(300);
            autoSaveCts?.Cancel();
            await Store.SaveAsync(Id, projectName, Project);
            Nav.NavigateTo($"/edit/{id}", forceLoad: true);
        });
    }
}
