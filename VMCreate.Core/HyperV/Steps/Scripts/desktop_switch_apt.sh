#!/bin/bash
set -o pipefail

# Switches a Linux guest's desktop environment. Generic across desktops:
# the step (DesktopSwitchStep) injects the target desktop's parameters
# directly below the shebang:
#
#   DESKTOP_PACKAGES          space-separated, shell-quoted metapackage names
#   DESKTOP_PACKAGES_TO_REMOVE  space-separated, shell-quoted metapackages to purge
#   DESKTOP_SESSION_STARTER   session starter to make default ("" = DM decides)
#   DESKTOP_DISPLAY_MANAGER   display manager to pin as default ("" = keep)
#   DESKTOP_DISPLAY_SERVER    "wayland" or "x11": what the new desktop runs on
#   DESKTOP_SWITCH_CONTEXT    "chroot" (ISO stage, the normal path: no
#                             systemd, no running desktop — files, packages
#                             and enable-state only) or "live" (booted guest,
#                             the cloud-init flow: running services and
#                             sessions are stopped too)
#
# Runs BEFORE the Auto RDP backend detection (AutoRdpBackendResolveStep,
# order 232) so the detector sees the new default session and selects the
# RDP backend the user will actually experience.
#
# Sequence (mirrors the documented manual post-configure steps):
#   1. apt-get update (retried on network/DNS failure; the output decides)
#   2. verify every metapackage exists in the live index (the catalog is
#      static; the distro's package index is the authority — probe before
#      installing so a renamed metapackage fails loudly, pre-install)
#   3. preseed the target display manager via debconf (BEFORE install, so
#      the package's config script reads the choice at install time)
#   4. apt-get install -y <packages>
#   5. default x-session-manager -> <starter> (non-interactive)
#   6. hand the display manager over to <dm> and verify systemd wiring
#      (enable without || true, display-manager.service alias exists,
#      graphical.target default) — degraded, never silently swallowed
#   7. apt-get purge <packages-to-remove> (old desktop meta + core), dry-run
#      first so it never takes the new desktop along, with dpkg recovery when
#      the purge wedges on the outgoing DM's prerm
#   8. Wayland desktop: undo X11 pins left by the xrdp path, and disable the
#      image's stock xrdp (live: stop it and end its sessions too)
#   9. drop AccountsService session choices that name removed sessions
#  10. final consistency check over everything first boot reads, including
#      the session list (new desktop present, old desktop gone)
#
# Output contract: the machine-readable terminal line
#     DESKTOP_SWITCH_RESULT=ok|degraded
# "degraded" = the desktop switched but a side task failed (session-manager
# alternative, display-manager handover, old-desktop purge). Hard failures
# (metapackage missing, apt install failing) exit non-zero and fail the
# deployment — shipping a half-installed desktop silently is the
# fabricated-success bug class.

DEGRADED=0
DESKTOP_SWITCH_CONTEXT="${DESKTOP_SWITCH_CONTEXT:-live}"
echo "Desktop switch context: $DESKTOP_SWITCH_CONTEXT"

# apt-get update exits 0 when a source fails to download (it only warns
# "W: Failed to fetch ..."), so the output decides. A transient DNS or mirror
# failure is retried with backoff; only a clean update lets a missing
# metapackage below count as a stale catalog (Kali, 2026-11-21: one DNS blip
# in the chroot was reported as "the desktop catalog is stale").
APT_FETCH_FAILED_RE='Failed to fetch|Temporary failure resolving|Could not resolve|Some index files failed to download|Network is unreachable|Connection timed out'
apt_update_ok=0
for attempt in 1 2 3 4; do
    echo "Updating package indexes (attempt $attempt/4)..."
    update_out="$(apt-get update 2>&1)"
    update_rc=$?
    printf '%s\n' "$update_out"
    if [ "$update_rc" -eq 0 ] && ! printf '%s\n' "$update_out" | grep -qE "$APT_FETCH_FAILED_RE"; then
        apt_update_ok=1
        break
    fi
    if [ "$attempt" -lt 4 ]; then
        wait_s=$((attempt * 15))
        echo "Package index download failed (network/DNS?); retrying in ${wait_s}s..."
        sleep "$wait_s"
    fi
done

if [ "$apt_update_ok" != "1" ]; then
    echo "ERROR: could not download the package indexes after 4 attempts."
    # Name the cause: a resolver problem in this environment is not a
    # catalog problem, and the fix is different.
    # The hosts apt failed on, from its "Err:N http://host/..." lines.
    failed_hosts="$(printf '%s\n' "$update_out" \
        | sed -nE 's#^Err:[0-9]+[[:space:]]+[a-z]+://([^/ ]+).*#\1#p' | sort -u)"
    for host in $failed_hosts; do
        if getent hosts "$host" >/dev/null 2>&1; then
            echo "  mirror $host resolves now (the failure is past DNS: mirror or route)."
        else
            echo "  mirror $host does NOT resolve: no working DNS in the $DESKTOP_SWITCH_CONTEXT environment."
        fi
    done
    nameservers="$(grep -E '^nameserver' /etc/resolv.conf 2>/dev/null | awk '{print $2}' | tr '\n' ' ')"
    echo "  /etc/resolv.conf nameservers: ${nameservers:-<none>}"
    echo "This is a network failure, not a stale desktop catalog; retrying the deployment may succeed."
    exit 1
fi

# The catalog is static offline data; the live package index is the
# authority. Probe each metapackage before installing so a renamed/removed
# metapackage fails the deployment BEFORE anything is touched. The index
# downloaded cleanly above, so a miss here really is a catalog problem.
for pkg in $DESKTOP_PACKAGES; do
    if ! apt-cache show "$pkg" >/dev/null 2>&1; then
        echo "ERROR: metapackage '$pkg' does not exist in the live package index."
        echo "The desktop catalog is stale — the distro no longer ships this desktop metapackage."
        exit 1
    fi
done

# -- Preseed the target display manager (debconf) --------------------------
# Stock images ship lightdm; installing a desktop pulls its own DM and
# Debian's debconf prompt would offer the choice interactively. A
# non-interactive apt run skips the prompt and keeps the old DM — exactly
# the state that wedged TEST_20260910165003. Preseed BEFORE the install so
# the package's config script reads the choice at install time.
if [ -n "$DESKTOP_DISPLAY_MANAGER" ]; then
    echo "Preseeding $DESKTOP_DISPLAY_MANAGER as the display manager..."
    echo "shared/default-x-display-manager select $DESKTOP_DISPLAY_MANAGER" \
        | debconf-set-selections 2>/dev/null || true
fi

echo "Installing desktop metapackage(s): $DESKTOP_PACKAGES"
if ! DEBIAN_FRONTEND=noninteractive apt-get install -y $DESKTOP_PACKAGES 2>&1; then
    echo "ERROR: apt-get install of $DESKTOP_PACKAGES failed."
    exit 1
fi

# -- Make the target desktop's session the default -------------------------
# update-alternatives --config prompts interactively, which cannot run over
# an automation shell. Some metapackages leave their session starter
# unregistered in the alternatives group (kali-desktop-kde never listed
# startplasma-wayland on TEST_20260910135942), so register from PATH when
# the group does not know the starter yet, then --set explicitly.
if [ -n "$DESKTOP_SESSION_STARTER" ]; then
    if command -v update-alternatives >/dev/null 2>&1; then
        starter_path=$(update-alternatives --list x-session-manager 2>/dev/null \
            | grep -F "$DESKTOP_SESSION_STARTER" | head -n1 | awk '{ print $1 }')
        if [ -z "$starter_path" ] && command -v "$DESKTOP_SESSION_STARTER" >/dev/null 2>&1; then
            # Register the binary the metapackage shipped (GNOME's
            # gnome-session also only registers via /usr/share/xsessions).
            update-alternatives --install /usr/bin/x-session-manager \
                x-session-manager "$(command -v "$DESKTOP_SESSION_STARTER")" 60 2>&1
            starter_path="$(command -v "$DESKTOP_SESSION_STARTER")"
        fi
        if [ -n "$starter_path" ] && [ -x "$starter_path" ]; then
            if update-alternatives --set x-session-manager "$starter_path" 2>&1; then
                echo "Default session manager set to $starter_path"
            else
                DEGRADED=1
                echo "WARNING: update-alternatives --set failed for $starter_path."
            fi
        else
            update-alternatives --auto x-session-manager 2>&1
            DEGRADED=1
            echo "WARNING: $DESKTOP_SESSION_STARTER not found in x-session-manager alternatives — used --auto (highest priority)."
        fi
    else
        DEGRADED=1
        echo "WARNING: update-alternatives not available — session manager default not set."
    fi
fi

# -- Hand the display manager over and verify systemd wiring ---------------
# Belt-and-braces for the debconf preseed: if the handover did not happen
# at install time, force it now so the target DM owns the system before
# the purge below can wedge the outgoing DM's prerm.
if [ -n "$DESKTOP_DISPLAY_MANAGER" ]; then
    dm_bin="$(command -v "$DESKTOP_DISPLAY_MANAGER" 2>/dev/null || true)"
    if command -v systemctl >/dev/null 2>&1 && [ -n "$dm_bin" ]; then
        current_dm="$(cat /etc/X11/default-display-manager 2>/dev/null || true)"
        if [ "$current_dm" != "$dm_bin" ]; then
            # Sync debconf BEFORE writing the file directly —
            # /etc/X11/default-display-manager is dpkg-owned: a later
            # dpkg-reconfigure/reinstall of the DM package rewrites it from
            # debconf state and would silently flip the DM back.
            echo "shared/default-x-display-manager select $DESKTOP_DISPLAY_MANAGER" \
                | debconf-set-selections 2>/dev/null || true
            echo "$dm_bin" > /etc/X11/default-display-manager
            echo "Display manager set to $DESKTOP_DISPLAY_MANAGER (was: ${current_dm:-unset})."
            for other_dm in lightdm gdm3 sddm; do
                if [ "$other_dm" != "$DESKTOP_DISPLAY_MANAGER" ]; then
                    systemctl disable "$other_dm.service" 2>/dev/null || true
                fi
            done
        else
            echo "Display manager already $DESKTOP_DISPLAY_MANAGER."
        fi
        # Enable the DM and VERIFY. A bare `enable || true` silently
        # swallowed the failure mode where enable never creates the
        # display-manager.service alias (observed after a wedged handover):
        # the machine then booted to a text console and the deployment's
        # session-readiness gate hung. The alias symlink is the ground
        # truth systemd uses to pick the DM at graphical.target — check it
        # exists, and fail loudly (degraded) if not.
        echo "Enabling $DESKTOP_DISPLAY_MANAGER.service..."
        if ! systemctl enable "$DESKTOP_DISPLAY_MANAGER.service" 2>&1; then
            DEGRADED=1
            echo "WARNING: systemctl enable $DESKTOP_DISPLAY_MANAGER.service failed."
        fi
        if [ ! -e /etc/systemd/system/display-manager.service ]; then
            DEGRADED=1
            echo "WARNING: display-manager.service alias missing after enable — first boot will not start a graphical display manager."
            # Last-resort repair: create the alias systemd would have made.
            ln -sf "/usr/lib/systemd/system/$DESKTOP_DISPLAY_MANAGER.service" /etc/systemd/system/display-manager.service 2>&1 \
                || ln -sf "/lib/systemd/system/$DESKTOP_DISPLAY_MANAGER.service" /etc/systemd/system/display-manager.service 2>&1 \
                || true
            if [ -e /etc/systemd/system/display-manager.service ]; then
                systemctl daemon-reload 2>&1 || true
                echo "Repaired display-manager.service alias manually."
            fi
        fi
        # The default target must be graphical — the DM only starts there.
        # A multi-user default lands on a text console even with the DM
        # enabled.
        default_target="$(systemctl get-default 2>/dev/null || true)"
        if [ "$default_target" != "graphical.target" ]; then
            echo "Default target is '$default_target' — setting graphical.target..."
            if systemctl set-default graphical.target 2>&1; then
                echo "Default target set to graphical.target."
            else
                DEGRADED=1
                echo "WARNING: could not set graphical.target as default."
            fi
        fi
    else
        DEGRADED=1
        echo "WARNING: $DESKTOP_DISPLAY_MANAGER not resolvable or systemctl missing — display manager not handed over."
    fi
fi

# -- Remove the old desktop ------------------------------------------------
# The old desktop's meta AND its core packages by name: purging only the
# meta left Kali's Xfce installed (desktop-base's "... | xfce4" dependency
# keeps xfce4 out of autoremove), and xrdp sessions kept landing in it.
# Only installed names are passed (apt-get purge errors on a name the index
# no longer knows). A dry run comes first: a purge that would also remove
# the new desktop or its display manager (a shared dependency) does not run.
# Degraded, not fatal: a failed purge leaves both desktops installed, the
# new one still being the default session — but recover dpkg when the purge
# wedges on the outgoing DM's prerm (TEST_20260910165003: lightdm's prerm
# failed under --autoremove, exit 10, "too many errors, stopping" — lightdm
# SURVIVED as the active DM. Recover: hand the DM over inside dpkg's
# expected flow, then configure pending packages so the purge completes.)
if [ -n "$DESKTOP_PACKAGES_TO_REMOVE" ]; then
    to_remove=""
    for pkg in $DESKTOP_PACKAGES_TO_REMOVE; do
        if dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null | grep -q 'install ok installed'; then
            to_remove="$to_remove $pkg"
        fi
    done
    to_remove="${to_remove# }"
    if [ -z "$to_remove" ]; then
        echo "Old desktop already absent ($DESKTOP_PACKAGES_TO_REMOVE)."
    else
        sim_removed=$(apt-get -s purge --autoremove $to_remove 2>/dev/null | awk '/^(Purg|Remv) / { print $2 }')
        clash=""
        for pkg in $DESKTOP_PACKAGES $DESKTOP_DISPLAY_MANAGER; do
            if printf '%s\n' "$sim_removed" | grep -qx "$pkg"; then
                clash="$clash $pkg"
            fi
        done
        if [ -n "$clash" ]; then
            DEGRADED=1
            echo "WARNING: purging the old desktop ($to_remove) would also remove:$clash — old desktop left installed."
        else
            echo "Removing old desktop package(s): $to_remove"
            if ! DEBIAN_FRONTEND=noninteractive apt-get purge -y --autoremove --allow-remove-essential $to_remove 2>&1; then
                echo "WARNING: purge of $to_remove failed — attempting $DESKTOP_DISPLAY_MANAGER handover + dpkg recovery."
                echo "shared/default-x-display-manager select $DESKTOP_DISPLAY_MANAGER" \
                    | debconf-set-selections 2>/dev/null || true
                if command -v dpkg-reconfigure >/dev/null 2>&1 && [ -n "$DESKTOP_DISPLAY_MANAGER" ]; then
                    DEBIAN_FRONTEND=noninteractive dpkg-reconfigure "$DESKTOP_DISPLAY_MANAGER" 2>&1 || true
                fi
                if [ -n "$DESKTOP_DISPLAY_MANAGER" ] && command -v "$DESKTOP_DISPLAY_MANAGER" >/dev/null 2>&1; then
                    echo "$(command -v "$DESKTOP_DISPLAY_MANAGER")" > /etc/X11/default-display-manager
                fi
                if DEBIAN_FRONTEND=noninteractive dpkg --configure -a 2>&1; then
                    echo "dpkg recovered after purge failure."
                else
                    DEGRADED=1
                    echo "WARNING: dpkg --configure -a failed after the purge failure — dpkg may be in a broken state."
                fi
                DEGRADED=1
            fi
        fi
    fi
fi

# -- Wayland desktop: undo X11 pins ------------------------------------------
# force_x11.sh and disable_wayland.sh (the xrdp path) pin the display
# managers to X11 and park Wayland sessions in wayland-sessions/disabled.
# A Wayland desktop never runs with xrdp (the front ends and the deployment
# refuse the combination), so a pin left behind by an earlier xrdp run would
# only keep the new desktop off Wayland. Stock gdm3 ships the line commented.
if [ "$DESKTOP_DISPLAY_SERVER" = "wayland" ]; then
    for f in /usr/share/wayland-sessions/disabled/*.desktop /usr/share/wayland-sessions/*.desktop.disabled; do
        [ -f "$f" ] || continue
        base=$(basename "$f" .disabled)
        # A parked session of the desktop just purged (xfce-wayland.desktop)
        # is no longer dpkg's to remove — it was moved. Drop it, don't offer it.
        exec_bin=$(sed -n 's/^Exec=//p' "$f" | head -n 1 | awk '{ print $1 }')
        if [ -n "$exec_bin" ] && ! command -v "$exec_bin" >/dev/null 2>&1; then
            rm -f "$f" && echo "Dropped parked session $base ($exec_bin is gone)."
            continue
        fi
        if mv "$f" "/usr/share/wayland-sessions/$base" 2>/dev/null; then
            echo "Restored Wayland session $base."
        fi
    done
    rmdir /usr/share/wayland-sessions/disabled 2>/dev/null || true
    for gdm_conf in /etc/gdm3/custom.conf /etc/gdm/custom.conf; do
        if [ -f "$gdm_conf" ] && grep -q '^WaylandEnable=false' "$gdm_conf"; then
            sed -i 's/^WaylandEnable=false/#WaylandEnable=false/' "$gdm_conf" \
                && echo "Re-enabled Wayland in $gdm_conf."
        fi
    done
    if [ -f /etc/sddm.conf.d/force-x11.conf ]; then
        rm -f /etc/sddm.conf.d/force-x11.conf && echo "Removed the SDDM X11 pin."
    fi

    # The image's stock xrdp (Kali ships it enabled) cannot serve a Wayland
    # desktop: a login starts the new desktop nested on xrdp's own X server.
    # Live: Enhanced Session reached it during a post-boot switch, and the
    # orphaned session (it outlives a stopped xrdp) wedged the Lamco
    # install's kscreen-doctor. A Wayland desktop's backend is Lamco or none
    # (the front ends and the deployment refuse xrdp), so the stock xrdp
    # goes dark here; the Lamco install takes vsock:3389 later.
    for unit in xrdp.service xrdp-sesman.service; do
        [ "$(systemctl is-enabled "$unit" 2>/dev/null)" = "enabled" ] || continue
        if systemctl disable "$unit" 2>&1; then
            echo "Disabled the stock $unit (cannot serve a Wayland desktop)."
        else
            DEGRADED=1
            echo "WARNING: could not disable the stock $unit — Enhanced Session may land in an xrdp session."
        fi
    done
    if [ "$DESKTOP_SWITCH_CONTEXT" = "live" ]; then
        for unit in xrdp.service xrdp-sesman.service; do
            if systemctl is-active --quiet "$unit" 2>/dev/null; then
                systemctl stop "$unit" 2>&1 && echo "Stopped the stock $unit."
            fi
        done
        # Stopping xrdp leaves its sessions running (Xorg, the nested
        # desktop): end them, or they hold the user's session bus and
        # graphical-session.target.
        if command -v loginctl >/dev/null 2>&1; then
            for sid in $(loginctl list-sessions --no-legend 2>/dev/null | awk '{ print $1 }'); do
                if [ "$(loginctl show-session "$sid" -p Service --value 2>/dev/null)" = "xrdp-sesman" ]; then
                    loginctl terminate-session "$sid" 2>&1 && echo "Ended xrdp session $sid."
                fi
            done
        fi
    fi
fi

# -- Drop stale AccountsService session choices -----------------------------
# A remembered session (XSession=/Session=) naming a session file that is
# gone (lightdm-xsession, xfce after the purge) makes the display manager
# start a desktop that no longer exists. Drop it; the DM then starts its
# default, the new desktop.
for acct in /var/lib/AccountsService/users/*; do
    [ -f "$acct" ] || continue
    for key in XSession Session; do
        value=$(sed -n "s/^$key=//p" "$acct" | head -n 1)
        [ -n "$value" ] || continue
        if [ ! -f "/usr/share/xsessions/$value.desktop" ] && [ ! -f "/usr/share/wayland-sessions/$value.desktop" ]; then
            sed -i "/^$key=/d" "$acct" && echo "Dropped stale $key=$value for $(basename "$acct")."
        fi
    done
done

# -- Final consistency check ------------------------------------------------
# "Reboot and make sure all our changes were made properly" is the doc's
# verification step. We cannot reboot inside this step (the deploy
# continues over SSH), but we CAN verify the facts first boot reads: the
# display-manager default, the session default, and the systemd wiring
# (display-manager.service alias + graphical.target default). A wrong set
# means the first boot lands on the old desktop or a text console —
# report degraded, never a silent ok.
if [ -n "$DESKTOP_DISPLAY_MANAGER" ]; then
    final_dm="$(cat /etc/X11/default-display-manager 2>/dev/null || true)"
    if command -v "$DESKTOP_DISPLAY_MANAGER" >/dev/null 2>&1 \
        && [ "$final_dm" != "$(command -v "$DESKTOP_DISPLAY_MANAGER")" ]; then
        DEGRADED=1
        echo "WARNING: $DESKTOP_DISPLAY_MANAGER is installed but /etc/X11/default-display-manager is '$final_dm' — first boot may start the wrong DM."
    fi
fi
if [ -n "$DESKTOP_SESSION_STARTER" ]; then
    final_sm="$(update-alternatives --query x-session-manager 2>/dev/null | grep '^Value:' | awk '{print $2}')"
    expected_sm="$(command -v "$DESKTOP_SESSION_STARTER" 2>/dev/null || true)"
    if [ -n "$expected_sm" ] && [ "$final_sm" != "$expected_sm" ]; then
        DEGRADED=1
        echo "WARNING: default session manager is '$final_sm', expected $expected_sm."
    fi
fi
if [ -n "$DESKTOP_DISPLAY_MANAGER" ] && command -v systemctl >/dev/null 2>&1; then
    if [ ! -e /etc/systemd/system/display-manager.service ]; then
        DEGRADED=1
        echo "WARNING: display-manager.service alias is missing — the display manager will not start at boot."
    fi
    final_target="$(systemctl get-default 2>/dev/null || true)"
    if [ "$final_target" != "graphical.target" ]; then
        DEGRADED=1
        echo "WARNING: default target is '$final_target', expected graphical.target — boot lands on a text console."
    fi
fi

# The session list is what the display manager (and xrdp's startwm) offers:
# the new desktop must have a session, the removed one must not.
session_files=$(ls /usr/share/xsessions/*.desktop /usr/share/wayland-sessions/*.desktop 2>/dev/null)
echo "Installed sessions: $(printf '%s\n' "$session_files" | xargs -r -n1 basename | paste -sd' ')"
if [ -n "$DESKTOP_SESSION_STARTER" ]; then
    if [ -z "$session_files" ] \
        || ! grep -qsE "^Exec=.*$DESKTOP_SESSION_STARTER" $session_files; then
        DEGRADED=1
        echo "WARNING: no session file starts $DESKTOP_SESSION_STARTER — the display manager will not offer the new desktop."
    fi
fi
if [ -n "$DESKTOP_PACKAGES_TO_REMOVE" ]; then
    for session_file in $session_files; do
        owner=$(dpkg-query -S "$session_file" 2>/dev/null | grep -v '^diversion' | head -n 1 | cut -d: -f1)
        for pkg in $DESKTOP_PACKAGES_TO_REMOVE; do
            if [ "$owner" = "$pkg" ]; then
                DEGRADED=1
                echo "WARNING: session $(basename "$session_file") of the old desktop ($pkg) is still installed."
            fi
        done
    done
fi

if [ "$DEGRADED" = "1" ]; then
    echo "DESKTOP_SWITCH_RESULT=degraded"
else
    echo "DESKTOP_SWITCH_RESULT=ok"
fi
exit 0
