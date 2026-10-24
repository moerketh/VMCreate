#!/bin/bash
set -o pipefail

# Switches an Arch Linux guest to a desktop environment post-boot. Generic
# across desktops: the step (DesktopSwitchStep) injects the target desktop's
# parameters directly below the shebang:
#
#   DESKTOP_PACKAGES            space-separated, shell-quoted package/group names
#   DESKTOP_PACKAGES_TO_REMOVE  space-separated, shell-quoted packages to remove
#   DESKTOP_SESSION_STARTER     session starter to make default ("" = DM decides)
#   DESKTOP_DISPLAY_MANAGER     display manager to pin as default ("" = keep)
#
# pacman-family counterpart to desktop_switch_apt.sh. Runs BEFORE the Auto
# RDP backend detection (AutoRdpBackendResolveStep, order 232) so the
# detector sees the new default session. In BlackArch deployments the repo
# strap (BlackArchInstallStep, order 230) has already landed, so the BlackArch
# repos are part of the package set pacman resolves against.
#
# Arch differs from Debian in ways that shape this script: there is no
# update-alternatives x-session-manager mechanism (the display manager alone
# picks the session from .desktop files) and no debconf preseed (pacman never
# prompts), so the DM handover below is the only session/DM wiring.
#
# Sequence (mirrors the apt variant, adapted to pacman's model):
#   1. pacman -Syu (full sync + upgrade — a bare -Sy is a partial upgrade)
#   2. resolve the removal set FIRST (pacman's transaction resolver refuses
#      to remove a package an explicitly-installed package depends on, so
#      dependents of a to-remove package must be co-marked for removal —
#      compute this before installing/enabling anything)
#   3. probe every package/group in the live databases (pacman -Sp)
#   4. pacman -S --needed --noconfirm <packages>
#   5. enable the display manager + verify the systemd display-manager
#      alias and graphical.target default (degraded, never silently
#      swallowed — the fabricated-DM-enable bug class)
#   6. pacman -Rns <removal set> (degraded, not fatal)
#   7. final consistency check over everything first boot reads
#
# Output contract: the machine-readable terminal line
#     DESKTOP_SWITCH_RESULT=ok|degraded
# "degraded" = the desktop switched but a side task failed (display-manager
# handover, graphical-target default, old-package removal). Hard failures
# (package missing from the live databases, pacman failing) exit non-zero
# and fail the deployment — shipping a half-installed desktop silently is
# the fabricated-success bug class.

DEGRADED=0

echo "Syncing + upgrading pacman databases and packages..."
# OMARCHY_ALLOW_DIRECT_PACMAN=1: Omarchy's 00-omarchy-update-guard pacman
# hook (AbortOnFail) rejects every -S+-u transaction not run through
# `omarchy update` (live: openvpn and the lamco -Syu both refused). The
# variable is the guard's documented bypass; other Arch guests ignore it.
if ! OMARCHY_ALLOW_DIRECT_PACMAN=1 pacman -Syu --noconfirm 2>&1; then
    echo "ERROR: pacman -Syu failed."
    exit 1
fi

# pacman's transaction resolver refuses to remove a package that an
# explicitly-installed package depends on — a constraint dpkg's --autoremove
# handles by co-removing the dependent. Resolve the removal set BEFORE
# installing anything (and BEFORE the target DM is enabled) so that any
# dependent of a to-remove package is itself marked for removal. This keeps
# both the install probe and the later remove from tripping over breakage
# that only surfaces mid-transaction.
PACKAGES_TO_REMOVE=""
if [ -n "$DESKTOP_PACKAGES_TO_REMOVE" ]; then
    for remove_pkg in $DESKTOP_PACKAGES_TO_REMOVE; do
        PACKAGES_TO_REMOVE="$PACKAGES_TO_REMOVE $remove_pkg"
        for dependent in $(pacman -Qi 2>/dev/null | awk -v p="$remove_pkg" '
            /^Name /       { name = $3 }
            /^Depends On / {
                line = substr($0, 14)
                n = split(line, deps, /  +/)
                for (i = 1; i <= n; i++) if (deps[i] == p) { print name; break }
            }'); do
            PACKAGES_TO_REMOVE="$PACKAGES_TO_REMOVE $dependent"
        done
    done
fi

# The catalog is static offline data; the live pacman databases are the
# authority. Probe each package/group before installing so a renamed/removed
# entry fails the deployment BEFORE anything is touched. pacman -Sp prints
# the resolved .pkg.tar URL for a package name and exits non-zero for a
# name that is neither a package nor a group.
for pkg in $DESKTOP_PACKAGES; do
    if ! pacman -Sp "$pkg" >/dev/null 2>&1; then
        echo "ERROR: package or group '$pkg' does not exist in the live pacman databases."
        echo "The desktop catalog is stale — the distro no longer ships this desktop metapackage."
        exit 1
    fi
done

echo "Installing desktop package(s)/group(s): $DESKTOP_PACKAGES"
if ! pacman -S --needed --noconfirm $DESKTOP_PACKAGES 2>&1; then
    echo "ERROR: pacman -S of $DESKTOP_PACKAGES failed."
    exit 1
fi

# -- Enable the display manager and verify systemd wiring ------------------
# Arch has no debconf / /etc/X11/default-display-manager: the only lever is
# the systemd display-manager.service alias plus graphical.target. Enable
# the DM and VERIFY the alias exists — a bare `enable || true` silently
# swallowed the failure mode where enable never creates the alias and the
# machine boots to a text console with a hung session-readiness gate.
if [ -n "$DESKTOP_DISPLAY_MANAGER" ]; then
    if command -v systemctl >/dev/null 2>&1; then
        echo "Enabling $DESKTOP_DISPLAY_MANAGER.service..."
        if ! systemctl enable "$DESKTOP_DISPLAY_MANAGER.service" 2>&1; then
            DEGRADED=1
            echo "WARNING: systemctl enable $DESKTOP_DISPLAY_MANAGER.service failed."
        fi
        if [ ! -e /etc/systemd/system/display-manager.service ]; then
            DEGRADED=1
            echo "WARNING: display-manager.service alias missing after enable — the desktop will not start at boot."
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
        echo "WARNING: systemctl not available — display manager not enabled."
    fi
fi

# -- Remove the old desktop's package(s) -----------------------------------
# Degraded, not fatal: a failed remove leaves both installed, the new DM
# already owning display-manager.service. Uses the pre-resolved removal set
# (including dependents) so the transaction resolver does not refuse
# mid-removal over an explicitly-installed dependent.
if [ -n "$PACKAGES_TO_REMOVE" ]; then
    echo "Removing old desktop package(s):$PACKAGES_TO_REMOVE"
    if ! pacman -Rns --noconfirm $PACKAGES_TO_REMOVE 2>&1; then
        DEGRADED=1
        echo "WARNING: pacman removal of$PACKAGES_TO_REMOVE failed — old packages may remain."
    fi
fi

# -- Final consistency check -----------------------------------------------
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
