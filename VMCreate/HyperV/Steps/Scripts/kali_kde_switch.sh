#!/bin/bash
set -o pipefail

# Switches a Kali Linux deployment from XFCE to the KDE Plasma desktop.
#
# Triggered by the "Kali ... (KDE)" gallery twins (tag: kali-kde) and runs
# post-boot over SSH BEFORE the Auto RDP backend detection
# (AutoRdpBackendResolveStep, order 232) so the detector sees a
# Wayland-default Plasma session and selects the Lamco RDP Server.
#
# Sequence (mirrors the documented manual post-configure steps):
#   1. apt-get update
#   2. apt-get install -y kali-desktop-kde         (KDE Plasma desktop meta)
#   3. default x-session-manager -> startplasma-wayland (non-interactive)
#   4. apt-get purge kali-desktop-xfce              (remove the old desktop)
#
# Output contract: the machine-readable terminal line
#     KALI_KDE_RESULT=ok|degraded
# "degraded" = the desktop switched but a side task failed (e.g. the
# session-manager alternative was not settable). Hard failures (apt install
# failing) exit non-zero and fail the deployment — shipping a half-installed
# desktop silently is the fabricated-success bug class.

DEGRADED=0

echo "Updating package indexes..."
if ! apt-get update 2>&1; then
    echo "ERROR: apt-get update failed."
    exit 1
fi

echo "Installing KDE Plasma desktop (kali-desktop-kde)..."
if ! DEBIAN_FRONTEND=noninteractive apt-get install -y kali-desktop-kde 2>&1; then
    echo "ERROR: apt-get install kali-desktop-kde failed."
    exit 1
fi

# Make KDE Plasma the default session — non-interactively. update-alternatives
# --config prompts interactively, which cannot run over an automation shell;
# prefer the Wayland starter explicitly (Plasma 6 defaults to Wayland and the
# Lamco backend needs a Wayland session to share), and fall back to --auto
# (highest priority) when the plasma-wayland entry is not registered.
if command -v update-alternatives >/dev/null 2>&1; then
    plasma_sm=$(update-alternatives --list x-session-manager 2>/dev/null \
        | grep 'startplasma-wayland' | head -n1 | awk '{ print $1 }')
    if [ -n "$plasma_sm" ] && [ -x "$plasma_sm" ]; then
        update-alternatives --set x-session-manager "$plasma_sm" 2>&1
        echo "Default session manager set to $plasma_sm"
    else
        update-alternatives --auto x-session-manager 2>&1
        DEGRADED=1
        echo "WARNING: startplasma-wayland not found in x-session-manager alternatives — used --auto (highest priority)."
    fi
else
    DEGRADED=1
    echo "WARNING: update-alternatives not available — session manager default not set."
fi

echo "Removing the XFCE desktop meta-package (kali-desktop-xfce)..."
if ! DEBIAN_FRONTEND=noninteractive apt-get purge -y --autoremove --allow-remove-essential kali-desktop-xfce 2>&1; then
    DEGRADED=1
    echo "WARNING: purge of kali-desktop-xfce failed — the XFCE desktop remains installed alongside KDE."
fi

if [ "$DEGRADED" = "1" ]; then
    echo "KALI_KDE_RESULT=degraded"
else
    echo "KALI_KDE_RESULT=ok"
fi
exit 0