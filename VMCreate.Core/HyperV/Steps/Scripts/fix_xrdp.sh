#!/bin/bash
set -o pipefail

# -- Fix xrdp xorg.conf (comment out DRM lines) --------------------------
# hyperv_drm does not expose a DRI render node (/dev/dri/renderD128), so
# xrdp sessions that load the xorgxrdp module will fail to start if the
# DRMDevice line is present.  DRI3 and DRMAllowList also reference DRM
# devices that don't exist on Hyper-V.  Comment out all three lines so xrdp
# falls back to software rendering, which works reliably on Hyper-V.
# Note: Load "glamoregl" and Load "xorgxrdp" are kept -- xorgxrdp.so links
# against glamoregl and removing it would break the module load chain.
if [ -f /etc/X11/xrdp/xorg.conf ]; then
    cp /etc/X11/xrdp/xorg.conf /etc/X11/xrdp/xorg.conf.bak
    sed -i 's|^\([\t ]*Option[\t ]*"DRMDevice".*\)|# \1  # hyperv_drm has no render node|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    sed -i 's|^\([\t ]*Option[\t ]*"DRI3".*\)|# \1  # no DRI support on hyperv_drm|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    sed -i 's|^\([\t ]*Option[\t ]*"DRMAllowList".*\)|# \1  # no DRM devices to allow|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    echo "xrdp xorg.conf: commented out DRMDevice, DRI3, DRMAllowList (backup at .bak)"
else
    echo "No xrdp xorg.conf found -- skipping"
fi

# -- Write xrdp startwm.sh with X11 environment ----------------------------
# xrdp sessions have a minimal PATH and no D-Bus session, which causes
# "command not found" errors for dbus-launch and other tools.  The default
# startwm.sh may also not set XDG_SESSION_TYPE=x11, causing toolkits to
# attempt Wayland rendering.  We write a startwm.sh that:
#   - Sources /etc/profile and ~/.profile for a sane environment
#   - Sets LANG/LC_ALL (xrdp sessions often have no locale)
#   - Forces XDG_SESSION_TYPE=x11 so toolkits use X11
#   - Disables KWin compositing via KWIN_COMPOSE=N
#   - Forces Qt software rendering (QT_QUICK_BACKEND=software,
#     QSG_RENDER_LOOP=basic) which is required for xrdp's software renderer
#   - Writes ~/.config/kwinrc per-session (handles new users)
#   - Removes cached KWin output config that may reference invalid modes
#   - Uses /usr/bin/dbus-launch (xrdp's PATH may not include /usr/bin)
#   - Detects and exec's the appropriate X11 desktop session
if [ -d /etc/xrdp ]; then
    if [ -f /etc/xrdp/startwm.sh ]; then
        cp /etc/xrdp/startwm.sh /etc/xrdp/startwm.sh.bak
    fi
    cat > /etc/xrdp/startwm.sh << 'STARTWM_EOF'
#!/bin/sh
# xrdp session startup -- forced X11 with software rendering for Hyper-V
if test -r /etc/profile; then . /etc/profile; fi
if test -r ~/.profile; then . ~/.profile; fi

export LANG=en_US.UTF-8
export LC_ALL=en_US.UTF-8
export XDG_SESSION_TYPE=x11
export KWIN_COMPOSE=N
export QT_QUICK_BACKEND=software
export QSG_RENDER_LOOP=basic

# Write per-session kwinrc to disable compositing (handles new users too)
mkdir -p ~/.config
cat > ~/.config/kwinrc << 'KRC'
[Compositing]
Backend=XRender
Enabled=false
OpenGLIsUnsafe=true
KRC
rm -f ~/.config/kwinoutputconfig.json

# Detect and start the appropriate X11 desktop session
if command -v startplasma-x11 >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=KDE
    export XDG_CURRENT_DESKTOP=KDE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec /usr/bin/startplasma-x11
elif command -v startxfce4 >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=XFCE
    export XDG_CURRENT_DESKTOP=XFCE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec startxfce4
elif command -v gnome-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=GNOME
    export XDG_CURRENT_DESKTOP=GNOME
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec gnome-session
elif command -v cinnamon-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=CINNAMON
    export XDG_CURRENT_DESKTOP=Cinnamon
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec cinnamon-session
elif command -v mate-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=MATE
    export XDG_CURRENT_DESKTOP=MATE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec mate-session
else
    # Fallback: try whatever X11 session was detected, or just start X
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec xterm
fi
STARTWM_EOF
    chmod +x /etc/xrdp/startwm.sh
    echo "xrdp startwm.sh written with X11 environment (backup at .bak)"
else
    echo "xrdp not installed -- skipping startwm.sh"
fi

# -- Allow colord device/profile creation without a polkit prompt -----------
# An xrdp session is not an "active local" seat session, so colord's
# create-device/create-profile actions fall back to auth_admin: every xrdp
# login opens "Authentication is required to create a color managed device"
# (live: PwnCloudOS, on first connect and after every restart). The JS rule
# covers polkit >= 0.106 (rules.d); the .pkla covers the legacy 0.105
# localauthority backend. Each backend ignores the other's file. No
# session-state condition: the xrdp session is exactly the one polkit does
# not rate local+active. Colord devices/profiles are display colour
# metadata — low-risk to allow for any logged-in user.
if [ -d /usr/share/polkit-1 ] || [ -d /etc/polkit-1 ]; then
    mkdir -p /etc/polkit-1/rules.d /etc/polkit-1/localauthority/50-local.d
    cat > /etc/polkit-1/rules.d/45-vmcreate-xrdp-colord.rules << 'COLORD_RULES_EOF'
// VMCreate: no colord auth prompt in xrdp sessions.
polkit.addRule(function(action, subject) {
    if ((action.id == "org.freedesktop.color-manager.create-device" ||
         action.id == "org.freedesktop.color-manager.create-profile" ||
         action.id == "org.freedesktop.color-manager.delete-device" ||
         action.id == "org.freedesktop.color-manager.delete-profile" ||
         action.id == "org.freedesktop.color-manager.modify-device" ||
         action.id == "org.freedesktop.color-manager.modify-profile") &&
        subject.user != "root") {
        return polkit.Result.YES;
    }
});
COLORD_RULES_EOF
    cat > /etc/polkit-1/localauthority/50-local.d/45-vmcreate-xrdp-colord.pkla << 'COLORD_PKLA_EOF'
[VMCreate: no colord auth prompt in xrdp sessions]
Identity=unix-user:*
Action=org.freedesktop.color-manager.create-device;org.freedesktop.color-manager.create-profile;org.freedesktop.color-manager.delete-device;org.freedesktop.color-manager.delete-profile;org.freedesktop.color-manager.modify-device;org.freedesktop.color-manager.modify-profile
ResultAny=yes
ResultInactive=yes
ResultActive=yes
COLORD_PKLA_EOF
    chmod 644 /etc/polkit-1/rules.d/45-vmcreate-xrdp-colord.rules \
              /etc/polkit-1/localauthority/50-local.d/45-vmcreate-xrdp-colord.pkla
    echo "polkit: colord device/profile actions allowed without a prompt (xrdp sessions)"
fi

echo "=== xrdp fix complete ==="
exit 0
