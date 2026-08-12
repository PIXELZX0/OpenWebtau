#!/usr/bin/env bash
# Downloads and patches worldline's third-party sources into native/third_party.
# Versions and patches are taken verbatim from vendor/OpenUtau/cpp/WORKSPACE.bazel
# so the wasm build matches what upstream builds with Bazel.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TP="$ROOT/native/third_party"
PATCHES="$ROOT/vendor/OpenUtau/cpp/third_party"

mkdir -p "$TP"
cd "$TP"

fetch() {
    local name="$1" url="$2" prefix="$3" patch="${4:-}"
    if [[ -d "$name" ]]; then
        echo "$name: already present"
        return
    fi
    echo "$name: downloading"
    curl -sL "$url" -o "$name.zip"
    unzip -q "$name.zip"
    mv "$prefix" "$name"
    rm "$name.zip"
    if [[ -n "$patch" ]]; then
        (cd "$name" && patch -p1 --forward < "$PATCHES/$patch")
    fi
}

fetch world \
    https://github.com/mmorise/World/archive/f8dd5fb289db6a7f7f704497752bf32b258f9151.zip \
    World-f8dd5fb289db6a7f7f704497752bf32b258f9151 world.patch
fetch libpyin \
    https://github.com/Sleepwalking/libpyin/archive/b38135390b335c3e8cea6ef35cf5093789b36dac.zip \
    libpyin-b38135390b335c3e8cea6ef35cf5093789b36dac libpyin.patch
fetch libgvps \
    https://github.com/Sleepwalking/libgvps/archive/2f1b4106d72f8f8138dc447bf0123820c0772cbd.zip \
    libgvps-2f1b4106d72f8f8138dc447bf0123820c0772cbd
fetch spline \
    https://github.com/ttk592/spline/archive/5894beaf91e9adbfdbe5c6c9a1c60770e380e8e8.zip \
    spline-5894beaf91e9adbfdbe5c6c9a1c60770e380e8e8 spline.patch
fetch libnpy \
    https://github.com/llohse/libnpy/archive/refs/tags/v1.0.1.zip \
    libnpy-1.0.1

echo "Done."
