#!/usr/bin/env bash
# Builds OpenUtau's worldline resampler as a wasm static library for Blazor to
# link against. Upstream builds it with Bazel; that whole toolchain is skipped
# here because the render path is a fixed, small set of translation units.
#
# Uses the Emscripten that ships with the .NET wasm-tools workload — object files
# must come from the same LLVM that relinks the app, so do not swap in Homebrew's.
#
# Run: native/build-worldline.sh   (output: native/worldline.a)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CPP="$ROOT/vendor/OpenUtau/cpp"
TP="$ROOT/native/third_party"
OUT="$ROOT/native"
OBJ="$OUT/obj"

EMSDK_ROOT="${EMSDK_ROOT:-$(ls -d /usr/local/share/dotnet/packs/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/*/tools 2>/dev/null | head -1)}"
if [[ -z "$EMSDK_ROOT" || ! -x "$EMSDK_ROOT/emscripten/emcc" ]]; then
    echo "emcc not found. Install the workload: sudo dotnet workload install wasm-tools" >&2
    exit 1
fi
EMCC="$EMSDK_ROOT/emscripten/emcc"
EMAR="$EMSDK_ROOT/emscripten/emar"

# emcc shells out to node; point it at the pack's copy so no system install is needed.
NODE_BIN="$(ls -d /usr/local/share/dotnet/packs/Microsoft.NET.Runtime.Emscripten.*.Node.*/*/tools/bin/node 2>/dev/null | head -1)"
export EM_CONFIG="$OBJ/.emscripten"
export EM_CACHE="$OBJ/emcache"

if [[ ! -d "$TP/world" ]]; then
    echo "Third-party sources missing. Run native/fetch-deps.sh first." >&2
    exit 1
fi

mkdir -p "$OBJ"
cat > "$EM_CONFIG" <<EOF
NODE_JS = '${NODE_BIN}'
LLVM_ROOT = '${EMSDK_ROOT}/bin'
BINARYEN_ROOT = '${EMSDK_ROOT}'
EMSCRIPTEN_ROOT = '${EMSDK_ROOT}/emscripten'
CACHE = '${EM_CACHE}'
EOF

INCLUDES=(
    -I "$CPP"                     # "worldline/..." style includes
    -I "$CPP/worldline"
    -I "$CPP/worldline/classic"
    -I "$CPP/worldline/common"
    -I "$CPP/worldline/f0"
    -I "$CPP/worldline/model"
    -I "$CPP/worldline/platinum"
    -I "$TP/world/src"            # "world/*.h"
    -I "$TP/world/tools"          # "audioio.h"
    -I "$TP"                      # "libgvps/gvps.h"
    -I "$TP/libpyin"
    -I "$TP/spline/src"
    -I "$TP/libnpy/include"
)

# -fwasm-exceptions and -msimd128 must match the runtime pack's own settings
# (WasmEnableExceptionHandling and WasmEnableSIMD both default to true). Without
# them these objects use Emscripten-style SjLj/EH while the rest of the module
# uses the wasm ones; the link succeeds and the runtime then aborts at the first
# unrelated native call.
#
# -O2 rather than -O3: the resampler is dominated by FFT work where -O3 buys
# little and costs noticeable code size in the wasm bundle.
CFLAGS=(-O2 -fwasm-exceptions -msimd128 -DFP_TYPE=double -Wno-everything)
CXXFLAGS=("${CFLAGS[@]}" -std=c++17)

# Excluded on purpose: worldline_main.cpp (CLI entry point), audio_output.cc and
# audio_debug.cc (miniaudio device output, replaced by Web Audio), *_test.*.
SOURCES=(
    "$CPP/worldline/worldline.cpp"
    "$CPP/worldline/phrase_synth.cpp"
    "$CPP/worldline/classic/classic_args.cpp"
    "$CPP/worldline/classic/frq.cpp"
    "$CPP/worldline/classic/resampler.cpp"
    "$CPP/worldline/classic/timing.cpp"
    "$CPP/worldline/common/timer.cpp"
    "$CPP/worldline/common/vec_utils.cpp"
    "$CPP/worldline/f0/dio_estimator.cpp"
    "$CPP/worldline/f0/dio_ss_estimator.cpp"
    "$CPP/worldline/f0/frq_estimator.cpp"
    "$CPP/worldline/f0/harvest_estimator.cpp"
    "$CPP/worldline/f0/pyin_estimator.cpp"
    "$CPP/worldline/model/effects.cpp"
    "$CPP/worldline/model/model.cpp"
    "$CPP/worldline/platinum/platinum.cpp"
    "$CPP/worldline/platinum/synthesisplatinum.cpp"
    "$TP/world/tools/audioio.cpp"
)
SOURCES+=("$TP"/world/src/*.cpp)
C_SOURCES=("$TP"/libpyin/*.c "$TP"/libgvps/*.c)

echo "Compiling $((${#SOURCES[@]} + ${#C_SOURCES[@]})) translation units..."
objects=()
compile() {
    local src="$1"; shift
    local obj="$OBJ/$(echo "${src#$ROOT/}" | tr '/' '_').o"
    objects+=("$obj")
    if [[ -f "$obj" && "$obj" -nt "$src" ]]; then
        return
    fi
    "$EMCC" "$@" "${INCLUDES[@]}" -c "$src" -o "$obj"
}

for src in "${SOURCES[@]}"; do compile "$src" "${CXXFLAGS[@]}"; done
for src in "${C_SOURCES[@]}"; do compile "$src" "${CFLAGS[@]}"; done

rm -f "$OUT/worldline.a"
"$EMAR" rcs "$OUT/worldline.a" "${objects[@]}"
echo "Built $OUT/worldline.a"
