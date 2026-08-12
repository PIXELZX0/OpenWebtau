# OpenWebtau

[OpenUtau](https://github.com/openutau/OpenUtau) running in the browser.

The desktop app splits cleanly into a UI-agnostic `OpenUtau.Core` and an Avalonia
front end. OpenWebtau keeps `OpenUtau.Core` and the 60 built-in phonemizers as-is,
compiles them to WebAssembly, and replaces only the UI and the platform edges
(audio device, filesystem, native resampler).

## Layout

```
vendor/OpenUtau/     upstream, as a git subtree (see "Upstream" below)
src/OpenWebtau/      Blazor WebAssembly front end
  Pages/Home.razor     editor: toolbar + piano roll wiring
  Services/            WebAudioOutput (IAudioOutput), ProjectService (file I/O)
  wwwroot/js/          pianoroll.js (canvas view + input), webaudio.js, app.js
```

Model edits go through OpenUtau's own `UCommand` stack, so undo/redo, validation
and phonemizer re-runs work exactly as on desktop.

## Run

```sh
dotnet run --project src/OpenWebtau
```

## Platform edges

| Desktop | Browser |
|---|---|
| `MiniAudioOutput` (native miniaudio) | `WebAudioOutput` → Web Audio |
| Local filesystem | emscripten VFS; upload/download at the edges |
| `Worldline` native resampler | not wired up yet — see below |
| ONNX (DiffSinger, Vogen, Crepe) | compiles, throws at runtime; needs onnxruntime-web |
| `ExeResampler` / `ExeWavtool` (spawns .exe) | impossible in a browser |
| ENUNU (`NetMQ` raw TCP) | impossible in a browser |

## Upstream

`vendor/OpenUtau` is a git subtree. Pull upstream with:

```sh
git subtree pull --prefix vendor/OpenUtau https://github.com/openutau/OpenUtau.git master --squash
```

Two files there carry browser patches, both guarded by `OperatingSystem.IsBrowser()`:

- `OpenUtau.Core/Util/PathManager.cs` — no process or user profile in a browser.
- `OpenUtau.Core/Api/PhonemizerRunner.cs` — no threads; requests run inline.

## Status

Working: open/save `.ustx` (and every format Core reads), piano roll editing
(create, move, resize, delete, lyric), undo/redo, transport scrubbing.

Not working yet: audio rendering. That needs the Worldline resampler
(`vendor/OpenUtau/cpp/worldline`) built with Emscripten and linked into the wasm
runtime via `NativeFileReference`, which requires the `wasm-tools` workload:

```sh
sudo dotnet workload install wasm-tools
```
