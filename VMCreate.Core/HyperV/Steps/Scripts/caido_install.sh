#!/bin/bash
# Installs Caido — a modern, Rust-based web security proxy (Burp alternative) —
# as the desktop app for Omarchy. Prefers the AUR `caido-desktop` package when an
# AUR helper is available; otherwise downloads the official Linux AppImage to
# ~/.local/share/caido and wires a launcher (the app registration step picks it up).
#
# Contract: the terminal line is CAIDO_RESULT=ok|skipped|degraded.
#   ok       — caido binary/app installed and invocable.
#   skipped  — no network / no usable install source; not attempted.
#   degraded — partial install (downloaded but launcher wiring failed).

set -o pipefail

APP_DIR="$HOME/.local/share/caido"
BIN_LINK="$HOME/.local/bin/caido"
mkdir -p "$HOME/.local/bin"

# Already present?
if command -v caido >/dev/null 2>&1 || [ -e "$BIN_LINK" ]; then
    echo "caido already installed."
    echo "CAIDO_RESULT=ok"; exit 0
fi

# 1) AUR helper path (Omarchy ships paru).
if command -v paru >/dev/null 2>&1; then
    if paru -S --noconfirm --needed caido-desktop >/dev/null 2>&1; then
        echo "installed caido-desktop via paru."
        echo "CAIDO_RESULT=ok"; exit 0
    fi
    echo "WARN: paru install of caido-desktop failed; falling back to AppImage." >&2
fi

# 2) Official AppImage fallback.
if ! command -v curl >/dev/null 2>&1; then
    echo "SKIP: curl not found and no AUR helper — cannot fetch Caido."
    echo "CAIDO_RESULT=skipped"; exit 0
fi
mkdir -p "$APP_DIR"
URL="$(curl -fsSL "https://api.github.com/repos/caido/caido/releases/latest" \
    | grep -oE 'https://[^"]*caido-desktop[^"]*-x86_64\.AppImage' | head -n1)"
if [ -z "$URL" ]; then
    URL="https://caido.download/releases/latest/caido-desktop-x86_64.AppImage"
fi
if curl -fL "$URL" -o "$APP_DIR/caido.AppImage" 2>/dev/null; then
    chmod +x "$APP_DIR/caido.AppImage"
    ln -sf "$APP_DIR/caido.AppImage" "$BIN_LINK"
    echo "installed Caido AppImage to $APP_DIR."
    echo "CAIDO_RESULT=ok"
else
    echo "WARN: could not download Caido AppImage." >&2
    echo "CAIDO_RESULT=degraded"
fi
exit 0
