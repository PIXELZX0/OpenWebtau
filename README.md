# OpenWebtau

[OpenUtau](https://github.com/openutau/OpenUtau) running in the browser.

The desktop app splits cleanly into a UI-agnostic `OpenUtau.Core` and an Avalonia
front end. OpenWebtau keeps `OpenUtau.Core` and its built-in phonemizers as-is,
compiles them to WebAssembly, and replaces only the UI and the platform edges
(audio device, filesystem, native resampler).

## Layout

```
vendor/OpenUtau/     upstream, as a git subtree (see "Upstream" below)
src/OpenWebtau/      Blazor WebAssembly front end
  Pages/Home.razor     editor: toolbar, install dialog, piano roll wiring
  Services/            WebAudioOutput (IAudioOutput), ProjectService (file I/O)
  wwwroot/js/          pianoroll.js (canvas view + input), webaudio.js, app.js
```

Model edits go through OpenUtau's own `UCommand` stack, so undo/redo, validation
and phonemizer re-runs work exactly as on desktop.

## Editing

The piano roll has three layers. Notes and Pitch are the two toolbar modes; the
expression lane along the bottom is always live and follows the `Exp` selector.

| Layer | Interaction |
|---|---|
| Notes | drag to draw · drag to move · right edge to resize · right-click to delete · double-click for the lyric |
| Pitch | drag a control point to bend · click the line to add one · right-click to delete · alt-click to cycle its shape (`io`/`l`/`i`/`o`) |
| Vibrato | drag the pink handle at the note's right edge left to set length; the second handle sets depth, shift-drag it for period |
| Expression | paint the bottom lane. Curve expressions (`dyn`, `pitd`) draw freehand; per-note ones (`vel`, `vol`, `mod`, ...) show a bar per note |

The pitch curve drawn on screen mirrors `MusicMath.InterpolateShape` and
`UVibrato.Evaluate`, so it matches what the resampler actually renders. Pitch
points are stored in milliseconds from the note start; the view works in ticks
and converts at the boundary rather than teaching JS the project time axis.

## Platform edges

| Desktop | Browser |
|---|---|
| `MiniAudioOutput` (native miniaudio) | `WebAudioOutput` → Web Audio |
| Local filesystem | emscripten VFS; upload/download at the edges |
| `Worldline` native resampler | same C++, built to wasm and statically linked |
| `SharpWavtool` | unchanged — it is pure C# |
| ONNX (DiffSinger, Vogen, Crepe) | compiles, throws at runtime; needs onnxruntime-web |
| `ExeResampler` / `ExeWavtool` (spawns .exe) | impossible in a browser |
| ENUNU (`NetMQ` raw TCP) | impossible in a browser |

Losing `ExeResampler` costs third-party resamplers (moresampler, tn_fnds). It does
not cost classic UTAU rendering: `WorldlineResampler` and `SharpWavtool` are the
defaults, and both work here.

## Building the native resampler

`Worldline` is C++. Upstream builds it with Bazel; this repo compiles the render
path directly with the Emscripten that ships in the `wasm-tools` workload:

```sh
sudo dotnet workload install wasm-tools   # once
native/fetch-deps.sh                      # third-party sources, pinned to upstream
native/build-worldline.sh                 # -> native/worldline.a
```

The archive name must stay `worldline.a`: the wasm p/invoke table keys on the file
name, and `OpenUtau.Core` declares `[DllImport("worldline")]`.

## Tests

```sh
node src/OpenWebtau/wwwroot/js/pianoroll.test.mjs
```

## Upstream

`vendor/OpenUtau` is a git subtree. Pull upstream with:

```sh
git subtree pull --prefix vendor/OpenUtau https://github.com/openutau/OpenUtau.git master --squash
```

These files carry browser patches:

| File | Why |
|---|---|
| `OpenUtau.Core/Util/PathManager.cs` | no process or user profile in a browser |
| `OpenUtau.Core/Api/PhonemizerRunner.cs` | no threads; requests run inline |
| `OpenUtau.Core/Util/Preferences.cs` | read the exe directory without `Process` |
| `OpenUtau.Core/DocManager.cs` | `AppContext.BaseDirectory` is `/`, so `GetDirectoryName` is null |
| `OpenUtau.Core/OpenUtau.Core.csproj` | drop `MiniAudioOutput` when targeting the browser |
| `OpenUtau.Core/Classic/ClassicSinger.cs` | no `FileSystemWatcher` |
| `OpenUtau.Core/Classic/ClassicRenderer.cs` | resample sequentially; no thread pool |
| `OpenUtau.Core/Render/RenderTask.cs` | new: inlines offloaded render work |
| `OpenUtau.Core/Render/RenderEngine.cs`, `PlaybackManager.cs` | route offloaded work through `RenderTask` |
| `cpp/worldline/classic/classic_args.cpp` | two abseil calls replaced with stdlib, dropping the abseil dependency |

## Status

Working end to end: install a UTAU voicebank, assign it to the track, draw notes,
tune them, press Play, hear it sing. Open/save `.ustx` (and every format
`Formats.ReadProject` handles), note editing, portamento and vibrato, expression
curves and per-note expressions, undo/redo, 73 phonemizers, and the worldline
resampler running as WebAssembly.

Installing a voicebank shows a progress dialog. The read phase reports real bytes;
extraction runs synchronously inside Core and blocks the only browser thread, so
that phase is shown as indeterminate rather than faking a percentage.

Not available in the browser: third-party `.exe` resamplers, ENUNU (raw TCP), and
DiffSinger/Vogen (ONNX compiles but throws; it needs onnxruntime-web). The default
`WORLDLINE-R` renderer is also unusable — see "Threading and the browser" below —
so tracks are pinned to `CLASSIC`, which reaches the same resampler.

## Threading and the browser

Browser wasm runs on one thread with no thread pool, and OpenUtau.Core assumes
otherwise in several places. Each of these fails silently rather than throwing:

| Assumption | What breaks | Fix here |
|---|---|---|
| `System.Threading.Timer` | Callbacks never fire; the audio pump and playhead were dead | `Task.Delay` loops |
| `TaskScheduler.Default` for `DocManager.MainScheduler` | Phonemizer results never posted, so nothing renders | `InlineTaskScheduler` |
| `Task.Run` + `task.Wait()` in the render pipeline | Deadlock | `RenderTask.Run` inlines on browser |
| `Parallel.ForEach` in `ClassicRenderer` | Interpreter abort | Sequential loop on browser |
| Reverse p/invoke (`LogCallback` in worldline's PhraseSynth API) | Aborts the runtime, no managed exception | Use the `CLASSIC` renderer |
| `new Thread` in `PhonemizerRunner` | `PlatformNotSupportedException` | Run inline |
| `FileSystemWatcher` in `OtoWatcher` | Singer reload throws | Skipped on browser |

`Span<byte>` marshalled as `JSType.MemoryView` also arrives in JS as a `MemoryView`,
not a `TypedArray`; `webaudio.js` calls `.slice()` to get real bytes. Without that
the enqueue silently dropped every chunk.

## Build configuration

`WasmBuildNative` relinks the runtime so `worldline.a` can be linked in. A Debug
relink defaults to `-O0`, which pulls the debug sysroot libraries and produces a
runtime that aborts with a bare `ExitStatus` on the first zip extraction, so the
project pins `EmccLinkOptimizationFlag` to `-O2` in every configuration.

```sh
dotnet run -c Release --project src/OpenWebtau
```
