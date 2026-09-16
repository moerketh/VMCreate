#!/bin/bash
set -o pipefail

# -- Restore previously disabled Wayland sessions --------------------------
# If a prior run moved .desktop files to the disabled/ directory, move them
# back so that display managers can resolve session names during config read.
# This is essential for LightDM, which may have a stale cached session name.
mkdir -p /usr/share/wayland-sessions/disabled 2>/dev/null || true
restored=0
for f in /usr/share/wayland-sessions/disabled/*.desktop; do
    [ -f "$f" ] || continue
    base=$(basename "$f")
    mv "$f" "/usr/share/wayland-sessions/$base" 2>/dev/null || true
    restored=1
done
[ $restored -eq 1 ] && echo "Restored previously disabled Wayland sessions" || echo "No previously disabled Wayland sessions to restore"

# Also restore any .desktop.disabled files left by older scripts that
# renamed plasma.desktop -> plasma.desktop.disabled instead of moving to a
# subdirectory.
for f in /usr/share/wayland-sessions/*.desktop.disabled; do
    [ -f "$f" ] || continue
    original="${f%.disabled}"
    mv "$f" "$original" 2>/dev/null || true
    restored=1
done

# -- NOW disable Wayland sessions -----------------------------------------
# Only after display manager configs and AccountsService overrides are written
# do we move Wayland session files to the disabled/ directory.  This ordering
# is critical because LightDM and AccountsService read session names from
# .desktop files.  With user-session and XSession both pinned to X11 sessions,
# the cached Wayland name is irrelevant and disabling the file is safe.
mkdir -p /usr/share/wayland-sessions/disabled
moved=0
for f in /usr/share/wayland-sessions/*.desktop; do
    [ -f "$f" ] || continue
    base=$(basename "$f")
    [ -f "/usr/share/wayland-sessions/disabled/$base" ] || mv "$f" "/usr/share/wayland-sessions/disabled/$base" 2>/dev/null || true
    moved=1
done
[ $moved -eq 1 ] && echo "Wayland sessions disabled" || echo "No Wayland sessions found"

# -- Unblock Hyper-V kernel modules -----------------------------------------
# Some converted VMs may have Hyper-V modules blacklisted from a previous
# hypervisor's tools. Remove the blacklist so Hyper-V integration works.
rm -f /etc/modprobe.d/blacklist-hyperv.conf 2>/dev/null || true

echo "=== Wayland session disable complete ==="
exit 0
