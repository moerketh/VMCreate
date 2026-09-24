#!/bin/bash
set -o pipefail

# Switches a Linux guest's desktop environment post-boot. Generic across
# desktops: the step (DesktopSwitchStep) injects the target desktop's
# parameters directly below the shebang:
#
#   DESKTOP_PACKAGES          space-separated, shell-quoted metapackage names
#   DESKTOP_PACKAGES_TO_REMOVE  space-separated, shell-quoted metapackages to purge
#   DESKTOP_SESSION_STARTER   session starter to make default ("" = DM decides)
#   DESKTOP_DISPLAY_MANAGER   display manager to pin as default ("" = keep)
#
# Runs BEFORE the Auto RDP backend detection (AutoRdpBackendResolveStep,
# order 232) so the detector sees the new default session and selects the
# RDP backend the user will actually experience.
#
# Sequence (mirrors the documented manual post-configure steps):
#   1. apt-get update
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
#   7. apt-get purge <packages-to-remove> (old desktop meta), with dpkg
#      recovery when the purge wedges on the outgoing DM's prerm
#   8. final consistency check over everything first boot reads
#
# Output contract: the machine-readable terminal line
#     DESKTOP_SWITCH_RESULT=ok|degraded
# "degraded" = the desktop switched but a side task failed (session-manager
# alternative, display-manager handover, old-desktop purge). Hard failures
# (metapackage missing, apt install failing) exit non-zero and fail the
# deployment — shipping a half-installed desktop silently is the
# fabricated-success bug class.

DEGRADED=0

echo "Updating package indexes..."
if ! apt-get update 2>&1; then
    echo "ERROR: apt-get update failed."
    exit 1
fi

# The catalog is static offline data; the live package index is the
# authority. Probe each metapackage before installing so a renamed/removed
# metapackage fails the deployment BEFORE anything is touched.
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

# -- Remove the old desktop's metapackage(s) -------------------------------
# (e.g. the stock desktop meta). Degraded, not fatal: a failed purge
# leaves both desktops installed, the new one still being the default
# session — but recover dpkg when the purge wedges on the outgoing DM's
# prerm (TEST_20260910165003: lightdm's prerm failed under --autoremove,
# exit 10, "too many errors, stopping" — lightdm SURVIVED as the active
# DM. Recover: hand the DM over inside dpkg's expected flow, then
# configure pending packages so the purge completes.)
if [ -n "$DESKTOP_PACKAGES_TO_REMOVE" ]; then
    echo "Removing old desktop metapackage(s): $DESKTOP_PACKAGES_TO_REMOVE"
    if ! DEBIAN_FRONTEND=noninteractive apt-get purge -y --autoremove --allow-remove-essential $DESKTOP_PACKAGES_TO_REMOVE 2>&1; then
        echo "WARNING: purge of $DESKTOP_PACKAGES_TO_REMOVE failed — attempting $DESKTOP_DISPLAY_MANAGER handover + dpkg recovery."
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

if [ "$DEGRADED" = "1" ]; then
    echo "DESKTOP_SWITCH_RESULT=degraded"
else
    echo "DESKTOP_SWITCH_RESULT=ok"
fi
exit 0
