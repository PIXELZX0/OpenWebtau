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
  Pages/Projects.razor  project launcher at /
  Pages/Editor.razor    editor at /edit/{id}: tracks, piano roll, install dialog
  Services/             WebAudioOutput, ProjectService, ProjectStore, Dialogs
  wwwroot/js/           pianoroll.js (canvas view + input), webaudio.js, store.js, app.js
```

## Projects and tracks

`/` lists the projects stored in this browser and creates or opens them; the editor
lives at `/edit/{id}`. Projects are kept as `.ustx` text in IndexedDB, or in the Docker
image's `/data` volume (see "Docker"), because the wasm filesystem is in-memory
and dies with the tab. Edits autosave 1.2s after the
last change, and `Export .ustx` downloads a real file.

The editor's left sidebar is the track list: rename, mute, solo, remove, and a
singer per track. The piano roll edits the selected track; the other tracks' notes
are drawn behind it in grey so the arrangement stays visible.

**Import tracks** takes several `.ust`, `.mid`, `.vsqx` or `.ustx` files at once and
appends each as its own track through `Formats.ImportTracks`, keeping the current
project's tempo.

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

For UTAU users: the properties panel carries UTAU's note properties (velocity,
intensity, modulation, and the `g`/`B`/`H` flags) plus per-phoneme STP,
pre-utterance and overlap. Each track picks a phonemizer (CV, VCV, CVVC, Korean,
English, ...); choosing a singer switches to the bank's own default when
`character.yaml` names one. `Export .ust` writes the current track back out for
classic UTAU.

The voicebank installer asks for the bank's code page (Shift-JIS, CP949, GBK,
UTF-8) and stores it with the archive, so Korean and Chinese banks keep their
aliases after a reload. The first bank installed into a project is assigned to
the current track, and a banner stays up until the track has a singer.

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

`pianoroll.js` also exports `debugState()` (note boxes in the current transform)
and stashes the last pushed state on `window.__lastPush`; automated browser tests
use these to aim clicks without guessing pixel layouts.

## Voicebank persistence

Singers install into the wasm in-memory VFS, which dies with the tab, so uploads
are archived in IndexedDB (`openwebtau.singers`, raw bytes) and reinstalled once per session
by `SingerArchiveService`. The editor awaits that restore before opening a stored
project, so singer references resolve on a fresh page load.

## Agents (MCP)

`mcp/server.mjs` is an MCP server (Node, no dependencies) that lets an AI agent
edit the project open in the editor: read it, add tracks and notes, set lyrics,
tempo, singers, phonemizers and UTAU note properties, play, undo, and export.
Edits go through the same `UCommand` stack as the mouse, so they show up live
and undo normally.

```sh
claude mcp add openwebtau -- node /path/to/OpenWebtau/mcp/server.mjs
```

This repo's `.mcp.json` registers it for Claude Code automatically. Then open a
project and switch **Agent** on in the toolbar; it stays on across reloads.

The page long-polls `http://127.0.0.1:5178` (`OPENWEBTAU_MCP_PORT`) with plain
`fetch`, so the server needs no WebSocket library. The server only answers pages
served from localhost. Tool handlers live in `Pages/Editor.Agent.cs`; a
`track` argument switches the visible track, so the agent always edits in view.

## Docker

Publishing a GitHub release builds the image and pushes it to GHCR
(`.github/workflows/docker.yml`), tagged with the release version and `latest`:

```sh
docker run -p 8080:80 -v openwebtau-data:/data ghcr.io/pixelzx0/openwebtau:latest
```

With Docker Compose, save this as `compose.yaml`:

```yaml
services:
  openwebtau:
    image: ghcr.io/pixelzx0/openwebtau:latest
    ports:
      - "8080:80"
    volumes:
      - openwebtau-data:/data
    restart: unless-stopped

volumes:
  openwebtau-data:
```

```sh
docker compose up -d        # open http://localhost:8080
docker compose pull && docker compose up -d   # update to the latest release
```

To keep the data in a folder you can see, use a bind mount such as
`./data:/data` instead of the named volume.

Projects and voicebanks are stored in `/data` (`projects/<id>.ustx`,
`singers/<archive>`, each with a `.json` sidecar), so mount a volume there to
keep them. nginx serves that directory with WebDAV `PUT`/`DELETE` and a JSON
directory listing; `store.js` uses it when `/data/` answers and falls back to
IndexedDB otherwise (e.g. under `dotnet run`). There is no authentication:
anyone who can reach the port can read and change the data, so do not expose it
publicly as is.

The image is nginx serving the published static files. The build stage compiles
`worldline.a` with the workload's Emscripten, the same way as locally. Running the
workflow by hand only builds, as a check on the Dockerfile.

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

Working end to end: create or open a project, import UTAU and MIDI files as tracks,
install a voicebank, assign singers per track, draw notes, tune them, press Play,
hear it sing. Open/save `.ustx` (and every format
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
