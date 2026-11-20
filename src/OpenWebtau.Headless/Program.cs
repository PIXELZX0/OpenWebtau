using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenUtau.Core;
using OpenWebtau.Headless;
using Serilog;

// OpenWebtau without a browser: mcp/server.mjs starts this when an MCP tool is
// called and no editor tab is connected. It runs the same tools
// (Agent/AgentCommands.cs) on the projects and voicebanks in the data directory
// that the web app keeps on the server.
//
//   OpenWebtau.Headless --data <dir>
//
// stdin/stdout carry one JSON object per line:
//   -> {"id":1,"method":"get_project","params":{...}}
//   <- {"id":1,"ok":true,"result":...}   or   {"id":1,"ok":false,"error":"..."}
// The first line out is {"ready":true}. Logs go to stderr. EOF on stdin exits.

string dataDir = Path.GetFullPath(ArgValue("--data") ?? Path.Combine(Environment.CurrentDirectory, "data"));

// Core writes nothing to stdout on purpose, but keep the protocol stream to
// ourselves anyway.
var protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
Console.SetOut(Console.Error);

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Sink(new StderrSink())
    .CreateLogger();

NativeLibrary.SetDllImportResolver(typeof(DocManager).Assembly, ResolveWorldline);

// Core keeps singers, prefs and caches under XDG_DATA_HOME / XDG_CACHE_HOME on
// Linux. Point them into the data directory so the user's own OpenUtau install
// (~/.local/share/OpenUtau) is left alone.
var stateDir = Path.Combine(dataDir, ".headless");
Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(stateDir, "share"));
Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(stateDir, "cache"));
Directory.CreateDirectory(PathManager.Inst.DataPath);
Directory.CreateDirectory(PathManager.Inst.CachePath);
Directory.CreateDirectory(PathManager.Inst.SingersPath);

// One thread owns the document, as in the desktop app. Two queues feed it:
// work Core posts (phonemizer results, PostOnUIThread, await continuations),
// and tool calls from stdin. A call that waits pumps only the first, so calls
// never nest and replies stay in order.
var queue = new BlockingCollection<Action>();
var calls = new BlockingCollection<Action>();
var context = new QueueContext(queue);
SynchronizationContext.SetSynchronizationContext(context);
DocManager.Inst.Initialize(Thread.CurrentThread, TaskScheduler.FromCurrentSynchronizationContext());
DocManager.Inst.PostOnUIThread = action => queue.Add(action);

// Same registration as the web app, so the phonemizer list is identical.
foreach (var type in typeof(OpenUtau.Plugin.Builtin.ArpasingPhonemizer).Assembly.GetExportedTypes()) {
    if (!type.IsAbstract && type.IsSubclassOf(typeof(OpenUtau.Api.Phonemizer))) {
        OpenUtau.Api.PhonemizerFactory.Get(type);
    }
}
OpenUtau.Api.PhonemizerFactory.Get(typeof(OpenWebtau.Phonemizers.AutoPhonemizer));
OpenUtau.Api.PhonemizerFactory.BuildList();
OpenUtau.Classic.ToolsManager.Inst.Initialize();
DocManager.Inst.ExecuteCmd(new LoadProjectNotification(OpenUtau.Core.Format.Ustx.Create()));

var host = new HeadlessHost(dataDir, queue);
var reader = new Thread(() => {
    try {
        string? line;
        while ((line = Console.In.ReadLine()) != null) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var request = line;
            calls.Add(() => Reply(request));
        }
    } finally {
        calls.CompleteAdding();
    }
}) { IsBackground = true };

protocol.WriteLine(JsonSerializer.Serialize(new { ready = true }));
Log.Information("OpenWebtau headless ready. Data = {DataDir}", dataDir);
reader.Start();
// Core's posted work first; the short wait on calls keeps it from idling long.
while (true) {
    if (queue.TryTake(out var work)) { Run(work); continue; }
    if (calls.TryTake(out var call, 20)) { Run(call); continue; }
    if (calls.IsCompleted) break;   // stdin closed
}
return 0;

void Reply(string line) {
    JsonElement id = default;
    string reply;
    try {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        id = root.GetProperty("id").Clone();
        var method = root.GetProperty("method").GetString() ?? "";
        var args = root.TryGetProperty("params", out var p) ? p.GetRawText() : "{}";
        // Everything inside completes on this thread: the host does no real async
        // work, and anything posted back lands in this queue.
        reply = host.Call(method, args);
    } catch (Exception e) {
        reply = JsonSerializer.Serialize(new { ok = false, error = e.Message });
    }
    // Splice the id into the AgentCommands reply object.
    var idJson = id.ValueKind == JsonValueKind.Undefined ? "null" : id.GetRawText();
    protocol.WriteLine("{\"id\":" + idJson + "," + reply.TrimStart('{'));
}

static void Run(Action action) {
    try {
        action();
    } catch (Exception e) {
        Log.Error(e, "Unhandled error on the main queue");
    }
}

string? ArgValue(string name) {
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static IntPtr ResolveWorldline(string name, Assembly assembly, DllImportSearchPath? path) {
    if (name != "worldline") return IntPtr.Zero;
    string rid = RuntimeInformation.ProcessArchitecture switch {
        Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
        Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
        _ when OperatingSystem.IsMacOS() => "osx",
        Architecture.Arm64 => "win-arm64",
        Architecture.X86 => "win-x86",
        _ => "win-x64",
    };
    string file = OperatingSystem.IsLinux() ? "libworldline.so"
        : OperatingSystem.IsMacOS() ? "libworldline.dylib" : "worldline.dll";
    string full = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
    return File.Exists(full) && NativeLibrary.TryLoad(full, out var handle) ? handle : IntPtr.Zero;
}
