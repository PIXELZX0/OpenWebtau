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

var host = builder.Build();

// OpenUtau.Core expects these dirs to exist before anything touches Preferences.
Directory.CreateDirectory(PathManager.Inst.DataPath);
Directory.CreateDirectory(PathManager.Inst.CachePath);
Directory.CreateDirectory(PathManager.Inst.SingersPath);

// Single-threaded in the browser: the "UI thread" is the only thread, so the
// default scheduler already runs continuations where Core expects them.
DocManager.Inst.Initialize(Thread.CurrentThread, TaskScheduler.Default);
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

Log.Information("OpenWebtau initialized. Data path = {DataPath}", PathManager.Inst.DataPath);

await host.RunAsync();
