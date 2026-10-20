#!/bin/bash
set -o pipefail

# Post-boot xrdp install.
#
# Normally xrdp is pre-installed by the cloning-ISO chroot (VMCREATE_XRDP
# flag), but that step is skipped under RdpBackend.Auto — the backend is
# unknown pre-boot, and installing xrdp unconditionally would collide with
# Lamco on port 3389. When Auto resolves to xrdp, this script backfills the
# install over SSH. It also runs for explicit-xrdp deployments as a cheap
# chroot backfill (a missing install is recovered here).
#
# TRANSPORT: whatever installed xrdp, the HvSocket listener for Hyper-V
# Enhanced Session Mode is APPENDED here on every path (installed or
# already-present) — without it (stock xrdp = TCP 3389 only) vmconnect
# silently falls back to the basic console and no Enhanced Session popup
# ever appears. The image's own TCP listener is kept: xrdp >= 0.9 accepts
# multiple port= lines in [Globals], so network RDP keeps working. Distro
# packages are not all built with vsock support (the ISO builds from
# source with --enable-vsock); if xrdp refuses to start with the vsock
# listener, the config is rolled back so the image keeps its TCP RDP, and
# the result line reports the fallback instead of a silent loss.
#
# Output contract: the machine-readable terminal line
#     XRDP_POSTBOOT_RESULT=installed|already
# plus the transport outcome the host step logs:
#     XRDP_POSTBOOT_TRANSPORT=vsock|tcp|unverified
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
# Appends port=vsock://-1:3389 to [Globals] (keeping any TCP port= lines)
# and pins the security settings the vsock path needs. A previous run's
# vsock line is removed first so re-runs never duplicate it.
INI=""
if [ -f /etc/xrdp/xrdp.ini ]; then
    INI=/etc/xrdp/xrdp.ini
elif [ -f /usr/local/etc/xrdp/xrdp.ini ]; then
    INI=/usr/local/etc/xrdp/xrdp.ini
fi

TRANSPORT=unverified

if [ -z "$INI" ]; then
    echo "ERROR: no xrdp.ini found — cannot configure the Enhanced Session transport."
    exit 1
fi

cp "$INI" "$INI.vmcreate-bak"

sed -i '/^\[Globals\]/,/^\[/{/^port=vsock:/d}' "$INI"
sed -i '/^\[Globals\]/a port=vsock://-1:3389' "$INI"
sed -i '/^\[Globals\]/,/^\[/{s/^security_layer=.*/security_layer=rdp/}' "$INI"
sed -i '/^\[Globals\]/,/^\[/{s/^crypt_level=.*/crypt_level=none/}' "$INI"
echo "xrdp.ini configured for HvSocket Enhanced Session: $INI"

# The service must pick up the new listener config; if the package install
# failed to start it (policy-rc.d leftovers etc.) this also recovers it.
# A restart failure after our edit usually means this distro's xrdp was
# built without vsock support — roll the config back, restart again, and
# report the TCP fallback instead of leaving the image without RDP.
if ! command -v systemctl >/dev/null 2>&1; then
    echo "WARNING: no systemctl — cannot restart xrdp; transport unverified."
else
    systemctl enable xrdp 2>/dev/null || true
    if systemctl restart xrdp 2>&1 && systemctl is-active xrdp >/dev/null 2>&1; then
        TRANSPORT=vsock
        echo "xrdp restarted and active with the vsock listener."
    else
        echo "WARNING: xrdp failed to start with the vsock listener — rolling back to the original config."
        cp "$INI.vmcreate-bak" "$INI"
        rm -f "$INI.vmcreate-bak"
        if systemctl restart xrdp 2>&1 || systemctl restart xrdp-sesman 2>&1; then
            sleep 1
            if systemctl is-active xrdp >/dev/null 2>&1; then
                TRANSPORT=tcp
                echo "xrdp restarted on the original (TCP) config — Enhanced Session is unavailable, network RDP kept."
            else
                echo "ERROR: xrdp failed to start even after rolling back the config."
                exit 1
            fi
        else
            echo "ERROR: xrdp failed to start even after rolling back the config."
            exit 1
        fi
    fi
fi

rm -f "$INI.vmcreate-bak"
echo "XRDP_POSTBOOT_RESULT=$RESULT"
echo "XRDP_POSTBOOT_TRANSPORT=$TRANSPORT"
exit 0
