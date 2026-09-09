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

# -- 3. SDDM special case -----------------------------------------------------
# SDDM (KDE's display manager; stock on Parrot KDE, Debian KDE, Kali KDE)
# does not participate in the x-session-manager alternative, so stock
# Plasma 6 guests matched nothing in section 2 and fell through to the
# x11 verdict — losing the Lamco path entirely.
# What a fresh graphical login lands on under SDDM: the greeter preselects
# the Session= pinned in /etc/sddm.conf (or /etc/sddm.conf.d/*.conf, which
# override it — SDDM reads conf.d in alphabetical order, later wins); with
# no pin anywhere it falls back to the compiled-in default session, which
# on Plasma 6 is the WAYLAND plasma. Section 1 already proved a Wayland
# session is installed, so an unpinned SDDM ⇒ fresh login lands on Wayland.
if [ "$verdict" = "x11" ] && command -v sddm >/dev/null 2>&1; then
    sddm_session=""
    # Last non-empty Session= wins (conf.d overrides the main file).
    for conf in /etc/sddm.conf /etc/sddm.conf.d/*.conf; do
        [ -f "$conf" ] || continue
        found=$(grep -hE '^[[:space:]]*Session[[:space:]]*=' "$conf" 2>/dev/null \
            | head -n1 | cut -d= -f2- | tr -d '[:space:]')
        [ -n "$found" ] && sddm_session="$found"
    done

    if [ -n "$sddm_session" ]; then
        # Normalize: strip any path prefix and .desktop suffix — SDDM
        # accepts both 'plasma' and 'plasma.desktop' spellings.
        sddm_session=${sddm_session##*/}
        sddm_session=${sddm_session%.desktop}

        if [ -f "/usr/share/xsessions/$sddm_session.desktop" ] \
            && [ ! -f "/usr/share/wayland-sessions/$sddm_session.desktop" ]; then
            # Resolves under xsessions only (plasmax11 and friends): the
            # pin explicitly targets an X11 session — keep the x11 verdict.
            echo "SDDM session pinned to X11: $sddm_session"
        else
            # Resolves under wayland-sessions (plasma → Wayland on
            # Plasma 6), or under both (wayland-sessions checked first
            # because a same-named pin is how Plasma names its Wayland
            # session).
            verdict="wayland"
            echo "SDDM session $sddm_session -> Wayland default."
        fi
    else
        # No Session pin anywhere: Plasma 6's SDDM greeter preselects its
        # compiled-in default — the Wayland session.
        verdict="wayland"
        echo "SDDM Session pin unset - Plasma 6 greeter defaults to the Wayland session."
    fi
fi

# -- 4. GNOME special case ----------------------------------------------------
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