using Serilog.Core;
using Serilog.Events;

namespace OpenWebtau.Services;

/// <summary>
/// Serilog.Sinks.Console produces no output under Blazor WebAssembly, which hides
/// everything OpenUtau.Core logs. Console.WriteLine does reach the browser console,
/// so route log events straight through it.
/// </summary>
public class BrowserConsoleSink : ILogEventSink {
    public void Emit(LogEvent logEvent) {
        Console.WriteLine($"[{logEvent.Level}] {logEvent.RenderMessage()}");
        if (logEvent.Exception != null) {
            Console.WriteLine(logEvent.Exception.ToString());
        }
    }
}
