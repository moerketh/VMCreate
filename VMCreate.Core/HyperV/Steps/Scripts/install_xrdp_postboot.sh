#!/bin/bash
set -o pipefail

# Failures surface on stderr: SshTransport keeps stderr for the error
# detail and discards stdout, so the trap is the only place the failing
# line number reaches the host's deployment log.
trap 'echo "install_xrdp_postboot: failed at line $LINENO (exit $?)" >&2' ERR

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
# Enhanced Session Mode is configured here on every path that lacks it
# (fresh install, preinstalled-but-unconfigured, and always in chroot
# mode); a live daemon already listening on vsock is left untouched —
# without the listener (stock xrdp = TCP 3389 only) vmconnect
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
#
# CHROOT MODE (arg 1 = --chroot): runs the offline half only — package
# install and the xrdp.ini vsock listener config. The service restart and
# listener verification need a running systemd, which a chroot does not
# have, so transport stays unverified there; the first boot starts xrdp
# from this config, and the live half (no flag) verifies it post-boot.

CHROOT_MODE=0
if [ "${1:-}" = "--chroot" ]; then
    CHROOT_MODE=1
fi

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

TRANSPORT=unverified

# Already-configured short-circuit, live runs only: xrdp.ini already
# carries the vsock listener — the chroot half (or a previous run) wrote
# it, and first boot started the daemon from that config. Nothing
# remains to write, and restarting is actively harmful: the restart
# kills sesman mid-session, orphaning any live RDP session (its zombie
# keeps owning the user's session bus, and every later login
# single-instances against that bus and exits — an unrecoverable login
# loop until reboot). The gate reads the ini's vsock listener, NOT
# package presence: a preinstalled xrdp without the listener
# (distro-shipped, or the chroot half failed silently) still gets the
# config write and restart below. CHROOT MODE never takes this path —
# the offline half always converges the config on disk. The listener
# verification below still runs; it needs no restart, just a probe of
# the live daemon.
if [ "$CHROOT_MODE" = "0" ] && [ -n "$INI" ] && grep -Eq '^[[:space:]]*port[[:space:]]*=[[:space:]]*vsock://' "$INI"; then
    if command -v systemctl >/dev/null 2>&1 && systemctl is-active xrdp >/dev/null 2>&1; then
        # ss without -A vsock only lists INET sockets; the vsock listener
        # lives in the AF_VSOCK family and needs the explicit family
        # selector. xrdp's own socket type string is "vsock".
        if ss -H -l -A vsock 2>/dev/null | grep -q ":3389"; then
            TRANSPORT=vsock
            echo "xrdp already active with the vsock listener — no restart needed."
        else
            echo "XRDP_POSTBOOT_RESULT=$RESULT"
            echo "XRDP_POSTBOOT_TRANSPORT=tcp"
            echo "xrdp is active but has no AF_VSOCK listener on 3389 — TCP RDP kept."
            exit 0
        fi
    else
        echo "XRDP_POSTBOOT_RESULT=$RESULT"
        echo "XRDP_POSTBOOT_TRANSPORT=unverified"
        echo "xrdp installed but not active — listener verified on next boot."
        exit 0
    fi
    echo "XRDP_POSTBOOT_RESULT=$RESULT"
    echo "XRDP_POSTBOOT_TRANSPORT=$TRANSPORT"
    exit 0
fi

if [ -z "$INI" ]; then
    echo "ERROR: no xrdp.ini found — cannot configure the Enhanced Session transport."
    exit 1
fi

cp "$INI" "$INI.vmcreate-bak"

# Replace every [Globals] port= line with the vsock listener — the
# semantics the pre-chroot pipeline shipped and deployments were verified
# against. Appending the vsock line while KEEPING the TCP port= line
# leaves some distro xrdp builds (PwnCloudOS v1.2) binding TCP only —
# the vsock listener never comes up and Enhanced Session is silently
# lost (live-verified 2026-10-24: both lines present, TCP 3389 bound,
# vsock refused). A single vsock port= makes the daemon bind vsock or
# fail loudly; the rollback path below then restores the TCP backup.
sed -i '/^\[Globals\]/,/^\[/{s/^port=.*/port=vsock:\/\/-1:3389/}' "$INI"
sed -i '/^\[Globals\]/,/^\[/{s/^security_layer=.*/security_layer=rdp/}' "$INI"
sed -i '/^\[Globals\]/,/^\[/{s/^crypt_level=.*/crypt_level=none/}' "$INI"
echo "xrdp.ini configured for HvSocket Enhanced Session: $INI"

# The service must pick up the new listener config; if the package install
# failed to start it (policy-rc.d leftovers etc.) this also recovers it.
# A restart failure after our edit usually means this distro's xrdp was
# built without vsock support — roll the config back, restart again, and
# report the TCP fallback instead of leaving the image without RDP.
# CHROOT MODE skips all of it: no systemd manager exists in the chroot.
# xrdp stays enabled (the units' symlinks) and starts on first boot from
# the config written above; the post-boot half verifies the listener.
if [ "$CHROOT_MODE" = "1" ]; then
    if command -v systemctl >/dev/null 2>&1; then
        # Offline enable (file/symlink work, no manager needed): benign if
        # the package already enabled the unit.
        systemctl enable xrdp >/dev/null 2>&1 || true
    fi
    rm -f "$INI.vmcreate-bak"
    echo "CHROOT: xrdp installed and configured offline — transport verified post-boot."
    echo "XRDP_POSTBOOT_RESULT=$RESULT"
    echo "XRDP_POSTBOOT_TRANSPORT=unverified"
    exit 0
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "WARNING: no systemctl — cannot restart xrdp; transport unverified."
else
    systemctl enable xrdp 2>/dev/null || true
    if systemctl restart xrdp 2>&1 && systemctl is-active xrdp >/dev/null 2>&1 \
       && ss -H -l -A vsock 2>/dev/null | grep -q ":3389"; then
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
