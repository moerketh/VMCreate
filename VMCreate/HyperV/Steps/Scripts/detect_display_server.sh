#!/bin/bash
set -o pipefail

# Auto RDP backend detection — runs INSIDE the guest, post-boot, before any
# RDP backend is installed. Answers one question: would a fresh graphical
# login on this guest land on a Wayland session?
#
#   yes → the Wayland-native Lamco RDP Server is a candidate
#         (AutoRdpBackendResolveStep additionally requires a Debian-family
#          distro before committing to it)
#   no  → xrdp (X11) is the only workable backend
#
# This is deliberately a STATIC capability check of the installed desktop
# (session files + defaults), not a probe of a live session: at post-boot
# time nobody has graphically logged in yet, and the decision must be
# reproducible across reboots.
#
# Output contract: the machine-readable terminal line
#     RDP_DETECT=wayland|x11
# AutoRdpBackendResolveStep parses ONLY this line; everything above it is
# diagnostic output for the deployment log.

verdict="x11"

# -- 1. Is a Wayland session even installed? --------------------------------
have_wayland=0
for f in /usr/share/wayland-sessions/*.desktop; do
    [ -f "$f" ] || continue
    have_wayland=1
    break
done

if [ "$have_wayland" = "0" ]; then
    echo "No /usr/share/wayland-sessions/*.desktop found — X11-only desktop."
    echo "RDP_DETECT=x11"
    exit 0
fi

echo "Wayland sessions found:"
ls /usr/share/wayland-sessions/ 2>/dev/null || true

# -- 2. Would the default session manager start Wayland? ---------------------
# The Debian update-alternatives slot 'x-session-manager' is the canonical
# default session manager; kali_kde_switch.sh deliberately points it at
# startplasma-wayland, so this check also carries user intent.
default_sm=""
if command -v update-alternatives >/dev/null 2>&1; then
    default_sm=$(update-alternatives --query x-session-manager 2>/dev/null \
        | awk -F': ' '/^Value:/ { print $2 }' | head -n1)
    echo "x-session-manager alternative: ${default_sm:-<none>}"
fi

case "$default_sm" in
    *wayland*)
        # startplasma-wayland, labwc, weston … an explicit Wayland starter.
        verdict="wayland"
        ;;
    *x11*|*startkde*|*xfce4-session*|*mate-session*|*lxsession*)
        # An explicit X11 starter (startplasma-x11, startkde, xfce4-session…)
        verdict="x11"
        ;;
esac

# -- 3. GNOME special case ----------------------------------------------------
# gnome-session is desktop-server-agnostic; GDM decides the display server,
# and modern GNOME defaults to Wayland unless WaylandEnable=false opts out.
if [ "$verdict" = "x11" ] && command -v gdm3 >/dev/null 2>&1; then
    if ! grep -qE '^[[:space:]]*WaylandEnable[[:space:]]*=[[:space:]]*false' \
            /etc/gdm3/custom.conf /etc/gdm/custom.conf 2>/dev/null; then
        verdict="wayland"
        echo "GDM with a Wayland session installed and no WaylandEnable=false opt-out — Wayland default."
    fi
fi

echo "RDP_DETECT=$verdict"
exit 0