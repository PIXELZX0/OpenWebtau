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
| `cpp/worldline/classic/classic_args.cpp` | two abseil calls replaced with stdlib, dropping the abseil dependency |

## Status

Working: open/save `.ustx` (and every format Core reads), piano roll editing
(create, move, resize, delete, lyric), undo/redo, transport scrubbing, 73
phonemizers, worldline running as wasm.

Missing for end-to-end singing: there is no UI yet to install a UTAU voicebank
into the browser filesystem, so nothing has samples to render from.
