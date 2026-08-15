using Microsoft.JSInterop;

namespace OpenWebtau.Services;

/// <summary>
/// Native browser prompt/confirm. Blocking dialogs are crude, but they are the one
/// thing that works while the single wasm thread is busy.
/// </summary>
public class Dialogs {
    readonly IJSRuntime js;
    IJSObjectReference? module;

    public Dialogs(IJSRuntime js) {
        this.js = js;
    }

    async Task<IJSObjectReference> Module() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/app.js");

    public async Task<string?> PromptAsync(string message, string value) =>
        await (await Module()).InvokeAsync<string?>("prompt", message, value);

    public async Task<bool> ConfirmAsync(string message) =>
        await (await Module()).InvokeAsync<bool>("confirmDialog", message);

    public async Task DownloadAsync(string fileName, byte[] bytes, string mime) =>
        await (await Module()).InvokeVoidAsync("downloadBytes", fileName, Convert.ToBase64String(bytes), mime);
}
