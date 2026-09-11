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
# Output contract: the machine-readable terminal line
#     XRDP_POSTBOOT_RESULT=installed|already
# Hard failures exit non-zero before the terminal line (RunCommandAsync
# throws on the host and the deployment fails loudly).

if command -v apt-get >/dev/null 2>&1; then
    if dpkg -s xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (dpkg) — skipping install."
        echo "XRDP_POSTBOOT_RESULT=already"
        exit 0
    fi
    echo "Installing xrdp via apt..."
    if ! apt-get update -qq 2>&1; then
        echo "WARNING: apt-get update failed — continuing with existing package indexes."
    fi
    if ! DEBIAN_FRONTEND=noninteractive apt-get install -y xrdp xorgxrdp 2>&1; then
        echo "ERROR: apt-get install xrdp failed."
        exit 1
    fi
elif command -v dnf >/dev/null 2>&1; then
    if rpm -q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (rpm) — skipping install."
        echo "XRDP_POSTBOOT_RESULT=already"
        exit 0
    fi
    echo "Installing xrdp via dnf..."
    if ! dnf install -y xrdp xorgxrdp 2>&1; then
        echo "ERROR: dnf install xrdp failed."
        exit 1
    fi
elif command -v zypper >/dev/null 2>&1; then
    # openSUSE: the gallery ships Tumbleweed, whose KDE/Plasma default is
    # Wayland but is not Lamco-capable (rpm distro) — Auto resolves it to
    # xrdp, and zypper is the only way in.
    if rpm -q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (rpm) — skipping install."
        echo "XRDP_POSTBOOT_RESULT=already"
        exit 0
    fi
    echo "Installing xrdp via zypper..."
    if ! zypper --non-interactive install xrdp xorgxrdp 2>&1; then
        echo "ERROR: zypper install xrdp failed."
        exit 1
    fi
elif command -v pacman >/dev/null 2>&1; then
    if pacman -Q xrdp >/dev/null 2>&1; then
        echo "xrdp already installed (pacman) — skipping install."
        echo "XRDP_POSTBOOT_RESULT=already"
        exit 0
    fi
    echo "Installing xrdp via pacman..."
    if ! pacman -S --noconfirm xrdp 2>&1; then
        echo "ERROR: pacman install xrdp failed."
        exit 1
    fi
else
    echo "ERROR: no supported package manager (apt/dnf/zypper/pacman) found — cannot install xrdp."
    exit 1
fi

echo "XRDP_POSTBOOT_RESULT=installed"
exit 0