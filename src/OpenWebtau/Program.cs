using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OpenUtau.Audio;
using OpenUtau.Core;
using OpenWebtau;
using OpenWebtau.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
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
PlaybackManager.Inst.AudioOutput = host.Services.GetRequiredService<IAudioOutput>();

Log.Information("OpenWebtau initialized. Data path = {DataPath}", PathManager.Inst.DataPath);

await host.RunAsync();
