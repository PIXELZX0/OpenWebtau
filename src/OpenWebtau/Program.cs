using System.Text;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OpenUtau.Audio;
using OpenUtau.Core;
using OpenWebtau;
using OpenWebtau.Services;
using Serilog;

// UTAU voicebanks ship shift-jis oto.ini and character.txt; wasm only carries the
// built-in encodings until this provider is registered.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Sink(new BrowserConsoleSink())
    .CreateLogger();

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<IAudioOutput, WebAudioOutput>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddScoped<ProjectStore>();
builder.Services.AddScoped<Dialogs>();

var host = builder.Build();

// OpenUtau.Core expects these dirs to exist before anything touches Preferences.
Directory.CreateDirectory(PathManager.Inst.DataPath);
Directory.CreateDirectory(PathManager.Inst.CachePath);
Directory.CreateDirectory(PathManager.Inst.SingersPath);

// Single-threaded in the browser: the "UI thread" is the only thread. Core's
// main scheduler must run inline, because the thread pool never runs here.
DocManager.Inst.Initialize(Thread.CurrentThread, new InlineTaskScheduler());
DocManager.Inst.PostOnUIThread = action => action();

// Core discovers phonemizers by loading OpenUtau.Plugin.Builtin.dll off disk, which
// the browser cannot do. The assembly is already referenced, so register its
// phonemizers directly instead.
foreach (var type in typeof(OpenUtau.Plugin.Builtin.ArpasingPhonemizer).Assembly.GetExportedTypes()) {
    if (!type.IsAbstract && type.IsSubclassOf(typeof(OpenUtau.Api.Phonemizer))) {
        OpenUtau.Api.PhonemizerFactory.Get(type);
    }
}
OpenUtau.Api.PhonemizerFactory.BuildList();
Log.Information("Registered {Count} phonemizers.", OpenUtau.Api.PhonemizerFactory.GetAll().Length);

PlaybackManager.Inst.AudioOutput = host.Services.GetRequiredService<IAudioOutput>();

// Registers WorldlineResampler and SharpWavtool. Without it the renderer cannot
// resolve the "worldline" resampler name and every phrase fails.
OpenUtau.Classic.ToolsManager.Inst.Initialize();

// DocManager starts on a bare `new UProject()`, which has none of the expression
// descriptors (VEL, VOL, ...) that phoneme validation dereferences. The desktop app
// never hits that because it loads a project on startup; do the same here.
DocManager.Inst.ExecuteCmd(new LoadProjectNotification(OpenUtau.Core.Format.Ustx.Create()));

Log.Information("OpenWebtau initialized. Data path = {DataPath}", PathManager.Inst.DataPath);

await host.RunAsync();
