#!/bin/bash
set -o pipefail

# Result contract: 0 = ok, 1 = degraded (completed with warnings — the host
# step logs these). Hard failures exit non-zero before the terminal
# AUTOLOGIN_RESULT line is reached.
# STREAM RULE: the SSH transport returns STDOUT ONLY on zero-exit runs —
# stderr is dropped to a debug log, so DEGRADED/WARNING reason lines on
# stderr never reached the host's deployment log (TEST_20260910165003).
# Reason lines therefore print to stdout; hard-failure ERROR lines may use
# stderr (captured by the thrown SSH exception on non-zero exits).
DEGRADED=0

USER="__AUTOLOGIN_USER__"

# -- Detect the installed display managers (REAL detection, no mkdir) -----
# The old unconditional `mkdir -p` of all four DM config dirs made the
# later [ -d ] checks meaningless (SDDM and LightDM autologin files were
# written on every guest regardless of what is installed). Only create the
# config dir for a display manager that is actually present.
has_sddm=0; has_lightdm=0; has_gdm=0; has_greetd=0
{ [ -d /etc/sddm.conf.d ] || [ -f /etc/sddm.conf ] || command -v sddm >/dev/null 2>&1; } && has_sddm=1
{ [ -d /etc/lightdm/lightdm.conf.d ] || [ -f /etc/lightdm/lightdm.conf ] || command -v lightdm >/dev/null 2>&1; } && has_lightdm=1
{ [ -d /etc/gdm3 ] || [ -f /etc/gdm3/custom.conf ] || command -v gdm3 >/dev/null 2>&1; } && has_gdm=1
{ [ -f /etc/gdm/custom.conf ] || command -v gdm >/dev/null 2>&1; } && has_gdm=1
# greetd (Omarchy 4.x: greetd + tuigreet + UWSM autologin target; plain Arch
# greetd setups): a shipped /etc/greetd/config.toml is the authoritative
# signal; fall back to the daemon binary.
{ [ -f /etc/greetd/config.toml ] || command -v greetd >/dev/null 2>&1; } && has_greetd=1
if [ "$has_sddm$has_lightdm$has_gdm$has_greetd" = "0000" ]; then
    echo "WARNING: no display manager detected (sddm/lightdm/gdm/greetd) — autologin cannot be configured."
    DEGRADED=1
fi

# -- Detect an available Wayland session name -------------------------------
# Pick the first available wayland-sessions/*.desktop. Prefer plasma/mutter
# (GNOME/KDE) names, fall back to whatever exists. Do NOT disable or move any
# session files — Lamco needs Wayland enabled.
wayland_session=""
for f in /usr/share/wayland-sessions/*.desktop; do
    [ -f "$f" ] || continue
    base=$(basename "$f" .desktop)
    wayland_session="$base"
    # Preference order: plasma, plasmax11 is X11 so skip; prefer 'plasma' then 'gnome' then 'mutter'
    case "$base" in
        plasma|gnome|mutter) wayland_session="$base"; break ;;
    esac
done

if [ -z "$wayland_session" ]; then
    echo "No /usr/share/wayland-sessions/*.desktop found — Wayland may not be installed."
    echo "Autologin configured but no Wayland session to select."
    # Fall back to the display manager default rather than failing.
    wayland_session=""
fi

echo "Selected Wayland session: ${wayland_session:-(dm default)}"

# -- Add the user to render/video groups for PipeWire + GPU access ---------
# Best-effort: groups may not exist on minimal installs.
getent group render >/dev/null 2>&1 && usermod -aG render "$USER" 2>/dev/null || true
getent group video  >/dev/null 2>&1 && usermod -aG video  "$USER" 2>/dev/null || true
getent group audio  >/dev/null 2>&1 && usermod -aG audio  "$USER" 2>/dev/null || true

# -- Enable linger so the user's systemd services run at boot --------------
loginctl enable-linger "$USER" 2>/dev/null || true

# -- Report which display managers were detected ---------------------------
# (The old $dm detection ran a sed that never matched hyphen-free unit paths
# like sddm.service and was used only in an echo; it is gone. Branching is
# done with the has_* flags computed at the top of the script.)
echo "Display managers detected: sddm=$has_sddm lightdm=$has_lightdm gdm=$has_gdm greetd=$has_greetd"

# -- GDM (GNOME: Ubuntu, Debian-GNOME) -------------------------------------
# Enable AutomaticLogin in /etc/gdm3/custom.conf or /etc/gdm/custom.conf.
# Do NOT set WaylandEnable=false — Lamco requires Wayland.
if [ "$has_gdm" = "1" ]; then
    for conf in /etc/gdm3/custom.conf /etc/gdm/custom.conf; do
        if [ -f "$conf" ]; then
            if ! grep -q '^\[daemon\]' "$conf"; then
                printf '\n[daemon]\n' >> "$conf"
            fi
            # Remove any existing AutomaticLogin/AutomaticLoginEnable lines then append fresh.
            sed -i '/^#\?AutomaticLogin=/d; /^#\?AutomaticLoginEnable=/d' "$conf"
            sed -i '/^\[daemon\]/a AutomaticLoginEnable=true' "$conf"
            sed -i '/^\[daemon\]/a AutomaticLogin='"$USER" "$conf"
            echo "Configured GDM autologin in $conf"
        fi
    done
fi

# -- SDDM (KDE: Parrot-KDE, Debian-KDE) ------------------------------------
# Build the [Autologin] block conditionally — avoid $(...) inside heredocs
# so bash parses cleanly.
if [ "$has_sddm" = "1" ]; then
    mkdir -p /etc/sddm.conf.d
    {
        printf '[Autologin]\nUser=%s\n' "$USER"
        if [ -n "$wayland_session" ]; then
            printf 'Session=%s\n' "$wayland_session"
        fi
    } > /etc/sddm.conf.d/99-lamco-autologin.conf
    echo "Configured SDDM autologin in /etc/sddm.conf.d/99-lamco-autologin.conf"
fi

# -- LightDM (Parrot, some Debian spins) -----------------------------------
# Parrot historically uses LightDM. Set autologin-user + autologin-session.
if [ "$has_lightdm" = "1" ]; then
    mkdir -p /etc/lightdm/lightdm.conf.d
    {
        printf '[Seat:*]\nautologin-user=%s\nautologin-user-timeout=0\n' "$USER"
        if [ -n "$wayland_session" ]; then
            printf 'autologin-session=%s\n' "$wayland_session"
        fi
    } > /etc/lightdm/lightdm.conf.d/99-lamco-autologin.conf
    # LightDM requires the user to be in the autologin group on some distros
    getent group autologin >/dev/null 2>&1 && usermod -aG autologin "$USER" 2>/dev/null || true
    echo "Configured LightDM autologin in /etc/lightdm/lightdm.conf.d/99-lamco-autologin.conf"
fi

# -- greetd (Omarchy 4.x on Arch: greetd + tuigreet + UWSM) -----------------
# greetd autologin = an [initial_session] block in /etc/greetd/config.toml
# that starts the session once at boot (greeter-less) as $USER; the
# [default_session] greeter below it only runs when that session exits.
# The session command is parsed out of the existing config when it is a
# tuigreet-style `--cmd` argument, so a future Omarchy layout change to the
# greeter line still yields the compositor the distro wanted. Fallbacks
# cover Arch/UWSM conventions, then bare Hyprland.
if [ "$has_greetd" = "1" ]; then
    greetd_conf="/etc/greetd/config.toml"
    session_cmd=""
    if [ -f "$greetd_conf" ]; then
        # tuigreet greeter form: command = "tuigreet --time --remember --cmd <session>"
        session_cmd="$(grep -oE -- '--cmd[[:space:]]+"?[^"]+"?' "$greetd_conf" | head -1 | sed -E 's/^--cmd[[:space:]]+"?//; s/"$//')"
    fi
    if [ -z "$session_cmd" ]; then
        if command -v uwsm >/dev/null 2>&1 && [ -f /usr/share/wayland-sessions/hyprland.desktop ]; then
            session_cmd="uwsm start -- hyprland.desktop"
        elif command -v Hyprland >/dev/null 2>&1; then
            session_cmd="Hyprland"
        elif [ -n "$wayland_session" ]; then
            session_cmd="$wayland_session"
        fi
    fi
    if [ -z "$session_cmd" ]; then
        echo "WARNING: greetd detected but no session command derivable (no tuigreet --cmd, no uwsm/Hyprland, no wayland-sessions) — greetd autologin NOT configured."
        DEGRADED=1
    else
        mkdir -p /etc/greetd
        # Replace a stale [initial_session] (idempotent re-provisioning), then
        # append a fresh block. sed range-delete, then TOML append, keeps the
        # [default_session] greeter untouched.
        if [ -f "$greetd_conf" ]; then
            sed -i '/^\[initial_session\]/,/^\[/ { /^\[initial_session\]/d; /^\[/!d }' "$greetd_conf"
        fi
        {
            printf '\n[initial_session]\n'
            printf 'command = "%s"\n' "$session_cmd"
            printf 'user = "%s"\n' "$USER"
        } >> "$greetd_conf"
        systemctl enable greetd.service >/dev/null 2>&1 || true
        echo "Configured greetd autologin ([initial_session] command='$session_cmd' user='$USER')."
    fi
fi

# -- Create monitors.xml to force 1920x1080@60 resolution -----------------
# The hyperv_drm driver reports 1024x768 as the preferred mode. mutter/GNOME
# reads monitors.xml to override the DRM preferred mode. KWin uses KScreen
# config instead but falls through to DRM default without a config file.
USER_HOME=$(getent passwd "$USER" | cut -d: -f6)
if [ -n "$USER_HOME" ] && [ -d "$USER_HOME" ]; then
    if [ "$has_greetd" = "1" ]; then
        # Hyprland under greetd: monitors.xml (mutter) and a KDE autostart
        # entry do not apply. Omarchy's Hyprland runs the LUA config —
        # hyprland.lua requires hypr.monitors from ~/.config/hypr/ — so a
        # classic hyprland.conf fragment is silently ignored (see
        # install_lamco.sh for the full findings). Prepend a wildcard
        # 1920x1080@60 rule to monitors.lua; first match wins over the
        # distro's preferred-mode wildcard. Idempotent via marker grep.
        mkdir -p "$USER_HOME/.config/hypr"
        MON_LUA="$USER_HOME/.config/hypr/monitors.lua"
        if [ -f "$MON_LUA" ]; then
            if ! grep -q 'VMCreate: forced RDP resolution' "$MON_LUA"; then
                sed -i '1i -- VMCreate: forced RDP resolution on Hyper-V (first match wins over the preferred-wildcard below)\nhl.monitor({ output = "", mode = "1920x1080@60", position = "auto", scale = 1 })\n' "$MON_LUA"
                echo "Prepended 1920x1080@60 monitor rule to monitors.lua."
            fi
        else
            cat > "$MON_LUA" << 'MON_LUA_EOF'
-- VMCreate: forced RDP resolution on Hyper-V (first match wins).
hl.monitor({ output = "", mode = "1920x1080@60", position = "auto", scale = 1 })
MON_LUA_EOF
            chown "$USER" "$MON_LUA" 2>/dev/null || true
            echo "Created monitors.lua with the 1920x1080@60 rule."
        fi
        echo "Configured Hyprland (Lua config) for 1920x1080@60 resolution."
    else
    mkdir -p "$USER_HOME/.config"
    cat > "$USER_HOME/.config/monitors.xml" << 'MONITORS_EOF'
<monitors>
  <configuration>
    <layoutmode>logical</layoutmode>
    <logicalmonitor>
      <x>0</x>
      <y>0</y>
      <scale>1</scale>
      <primary>yes</primary>
      <monitor>
        <monitorspec>
          <connector>Virtual-1</connector>
          <vendor>unknown</vendor>
          <product>unknown</product>
          <serial>unknown</serial>
        </monitorspec>
        <mode>
          <width>1920</width>
          <height>1080</height>
          <rate>60.000</rate>
        </mode>
      </monitor>
    </logicalmonitor>
  </configuration>
</monitors>
MONITORS_EOF
    chown "$USER" "$USER_HOME/.config/monitors.xml" 2>/dev/null || true
    echo "Created monitors.xml for 1920x1080@60 resolution."

    # -- Set display resolution via kscreen-doctor for KDE/KWin -----------
    # KWin ignores monitors.xml and uses KScreen config instead. Without a
    # KScreen config, KWin falls through to the hyperv_drm default
    # (1024x768). kscreen-doctor sets the mode at runtime; this KDE
    # autostart entry re-applies it after the Wayland session starts, before
    # lamco-rdp-server connects.
    # Inside the USER_HOME guard on purpose: an unresolvable home previously
    # wrote to /.config/autostart as root.
    if command -v kscreen-doctor >/dev/null 2>&1; then
        mkdir -p "$USER_HOME/.config/autostart"
        cat > "$USER_HOME/.config/autostart/kscreen-set-resolution.desktop" << 'KSCREEN_AUTOSTART_EOF'
[Desktop Entry]
Type=Application
Name=Set Display Resolution
Exec=kscreen-doctor output.1.mode.1920x1080@60
X-KDE-autostart-phase=2
NoDisplay=true
KSCREEN_AUTOSTART_EOF
        chown "$USER" "$USER_HOME/.config/autostart/kscreen-set-resolution.desktop" 2>/dev/null || true
        echo "Created KDE autostart script for 1920x1080@60 via kscreen-doctor."
    else
        echo "kscreen-doctor not found — KWin will use DRM default (1024x768)."
    fi
    fi
else
    echo "WARNING: no home directory for $USER (getent) — monitors.xml/Hyprland monitor config and the kscreen autostart are NOT installed."
    DEGRADED=1
fi

# -- Retired: vgem dummy render node ---------------------------------------
# The RDP capture path is all-software (MemFd buffers, llvmpipe on card0);
# no render node or DMA-BUF is needed. Remove any vgem artifacts left by
# older deployments - vgem racing the real DRM node for the renderD128
# name is worse than having no render node at all. The software-render
# env vars live in InstallLamcoRdpStep (kwin-software-render.sh).
rm -f /etc/modules-load.d/vgem.conf /etc/udev/rules.d/99-vgem-render.rules 2>/dev/null || true

echo "=== graphical Wayland autologin configured for $USER ==="
# Machine-readable terminal line: the host-side step parses this to decide
# ok / degraded (warnings logged) — a zero-exit run without the line is
# treated as failed by the C# step.
if [ "${DEGRADED:-0}" = "1" ]; then
    echo "AUTOLOGIN_RESULT=degraded"
    exit 0
fi
echo "AUTOLOGIN_RESULT=ok"
exit 0
