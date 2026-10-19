#!/bin/bash
set -o pipefail

# Post-boot xrdp install.
#
# Normally xrdp is pre-installed by the cloning-ISO chroot (VMCREATE_XRDP
# flag), but that step is skipped under RdpBackend.Auto — the backend is
# unknown pre-boot, and installing xrdp unconditionally would collide with
# Lamco on port 3389. When Auto resolves to xrdp, this script backfills the
# install over SSH. It also runs for explicit-xrdp deployments as a cheap
# chroot backfill (a missing install is recovered here; an existing one is
# detected and reported without touching anything).
#
# TRANSPORT: whatever installed xrdp, the HvSocket listener for Hyper-V
# Enhanced Session Mode is configured HERE on every path — without it
# (stock xrdp = TCP 3389 only) vmconnect silently falls back to the basic
# console and no Enhanced Session popup ever appears. Mirrors the seds in
# the ISO chroot's install_xrdp.sh.
#
# Output contract: the machine-readable terminal line
#     XRDP_POSTBOOT_RESULT=installed|already
# Hard failures exit non-zero before the terminal line (RunCommandAsync
# throws on the host and the deployment fails loudly).

RESULT=unknown

if command -v apt-get >/dev/null 2>&1; then
    if dpkg -s xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (dpkg) — skipping install."
        RESULT=already
    else
        echo "Installing xrdp via apt..."
        if ! apt-get update -qq 2>&1; then
            echo "WARNING: apt-get update failed — continuing with existing package indexes."
        fi
        if ! DEBIAN_FRONTEND=noninteractive apt-get install -y xrdp xorgxrdp 2>&1; then
            echo "ERROR: apt-get install xrdp failed."
            exit 1
        fi
        RESULT=installed
    fi
elif command -v dnf >/dev/null 2>&1; then
    if rpm -q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (rpm) — skipping install."
        RESULT=already
    else
        echo "Installing xrdp via dnf..."
        if ! dnf install -y xrdp xorgxrdp 2>&1; then
            echo "ERROR: dnf install xrdp failed."
            exit 1
        fi
        RESULT=installed
    fi
elif command -v zypper >/dev/null 2>&1; then
    # openSUSE: the gallery ships Tumbleweed, whose KDE/Plasma default is
    # Wayland but is not Lamco-capable (rpm distro) — Auto resolves it to
    # xrdp, and zypper is the only way in.
    if rpm -q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (rpm) — skipping install."
        RESULT=already
    else
        echo "Installing xrdp via zypper..."
        if ! zypper --non-interactive install -y xrdp xorgxrdp 2>&1; then
            echo "ERROR: zypper install xrdp failed."
            exit 1
        fi
        RESULT=installed
    fi
elif command -v pacman >/dev/null 2>&1; then
    if pacman -Q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (pacman) — skipping install."
        RESULT=already
    else
        echo "Installing xrdp via pacman..."
        if ! pacman -S --noconfirm xrdp xorgxrdp 2>&1; then
            echo "ERROR: pacman install xrdp failed."
            exit 1
        fi
        RESULT=installed
    fi
else
    echo "ERROR: no supported package manager (apt/dnf/zypper/pacman) found — cannot install xrdp."
    exit 1
fi

# ── Enhanced Session transport (every path, idempotent) ──────────────
INI=""
if [ -f /etc/xrdp/xrdp.ini ]; then
    INI=/etc/xrdp/xrdp.ini
elif [ -f /usr/local/etc/xrdp/xrdp.ini ]; then
    INI=/usr/local/etc/xrdp/xrdp.ini
fi

if [ -n "$INI" ]; then
    sed -i '/^\[Globals\]/,/^\[/{s/^port=.*/port=vsock:\/\/-1:3389/}' "$INI"
    sed -i '/^\[Globals\]/,/^\[/{s/^security_layer=.*/security_layer=rdp/}' "$INI"
    sed -i '/^\[Globals\]/,/^\[/{s/^crypt_level=.*/crypt_level=none/}' "$INI"
    echo "xrdp.ini configured for HvSocket Enhanced Session: $INI"
else
    echo "WARNING: no xrdp.ini found — Enhanced Session transport NOT configured."
fi

# The service must pick up the new listener config; if the package install
# failed to start it (policy-rc.d leftovers etc.) this also recovers it.
if command -v systemctl >/dev/null 2>&1; then
    systemctl enable xrdp 2>/dev/null || true
    if ! systemctl restart xrdp 2>&1; then
        echo "WARNING: systemctl restart xrdp failed — trying xrdp-sesman..."
        systemctl restart xrdp-sesman 2>/dev/null || true
    elif systemctl is-active xrdp >/dev/null 2>&1; then
        echo "xrdp restarted and active."
    fi
fi

echo "XRDP_POSTBOOT_RESULT=$RESULT"
exit 0
