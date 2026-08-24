#!/bin/bash
# Build and install the instrumented screencast plugin on the VM.
# Preserves the previously-patched plugin as screencast.so.shm for rollback.
set -e
SRC=/home/vmcreate/kwin-src/kwin-6.3.6
DEST=/usr/lib/x86_64-linux-gnu/qt6/plugins/kwin/plugins/screencast.so

cd "$SRC"
# Match the known-working Aug 18 build exactly: Release, no KWIN_BUILD switches.
# A Debug-config plugin loaded into the Release KWin process breaks qobject_cast
# (mixed Qt debug/release ABI) -> "OpenGL compositing is required" artifact.
if [ ! -f build/CMakeCache.txt ] || grep -q "CMAKE_BUILD_TYPE:STRING=Debug" build/CMakeCache.txt; then
    rm -rf build
    mkdir -p build && cd build
    cmake .. -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF -DQT_MAJOR_VERSION=6 2>&1 | tail -3
else
    cd build
fi
make screencast -j"$(nproc)" 2>&1 | tail -3
BUILT="$SRC/build/bin/kwin/plugins/screencast.so"
ls -la "$BUILT"
# keep the currently-installed (patched) plugin for rollback
if [ ! -f "${DEST}.shm" ]; then
    cp "$DEST" "${DEST}.shm"
fi
install -m 0755 "$BUILT" "$DEST"
echo "INSTALLED: $DEST (from instrumented build)"
md5sum "${DEST}" "${DEST}.shm" "$BUILT" | cut -c1-12
