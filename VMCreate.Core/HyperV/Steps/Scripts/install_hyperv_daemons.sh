#!/bin/bash
set -o pipefail
# Non-interactive guard: headless SSH gives scripts a no-TTY, pipe stdin that
# never yields EOF on its own — an interactive `read` blocks until the host
# kills the session. Omarchy's /usr/local/bin/mkinitcpio wrapper asks
# "run limine-mkinitcpio now? [Y/n]" after -P; with /dev/null stdin its read
# returns instantly (empty answer → default branch). See remove_guest_tools.sh.
exec 0</dev/null

# The ISO cycle installs the integration packages pre-boot
# (INSTALL_HYPERV_PACKAGES in clone_disk.sh), so the common case here is
# verify-only. changed=1 only when a package is newly installed or a daemon
# newly enabled — the expensive initramfs regen at the end runs only then.
changed=0

# -- Install hyperv-daemons (best-effort) ----------------------------------
# Provides VSS backup integration, KVP data exchange, and fcopy daemon.
if command -v apt-get >/dev/null 2>&1; then
    if dpkg -s hyperv-daemons >/dev/null 2>&1; then
        echo "hyperv-daemons already installed"
    else
        DEBIAN_FRONTEND=noninteractive apt-get install -y hyperv-daemons 2>&1 || true
        dpkg -s hyperv-daemons >/dev/null 2>&1 && changed=1
    fi
elif command -v dnf >/dev/null 2>&1; then
    if rpm -q hyperv-daemons >/dev/null 2>&1; then
        echo "hyperv-daemons already installed"
    else
        dnf install -y hyperv-daemons 2>&1 || true
        rpm -q hyperv-daemons >/dev/null 2>&1 && changed=1
    fi
elif command -v pacman >/dev/null 2>&1; then
    if pacman -Q hyperv >/dev/null 2>&1; then
        echo "hyperv already installed"
    else
        pacman -S --noconfirm hyperv 2>&1 || true
        pacman -Q hyperv >/dev/null 2>&1 && changed=1
    fi
    # Arch's hyperv package ships the three integration daemons disabled.
    # Without hv_kvp_daemon, Hyper-V never learns the guest IP and VMCreate's
    # SSH discovery stays dark (this is what stranded Omarchy deployments in
    # "Waiting for SSH"); VSS and fcopy round out the integration services.
    for daemon in hv_kvp_daemon hv_vss_daemon hv_fcopy_daemon; do
        if ! systemctl is-enabled "$daemon" >/dev/null 2>&1; then
            systemctl enable --now "$daemon" 2>&1 || true
            systemctl is-enabled "$daemon" >/dev/null 2>&1 && changed=1
        fi
    done
fi

# -- Regenerate initramfs (applies blacklist + module changes) ------------
# Only when something changed above: the regen is the expensive half (~18s
# measured) and the boot artifacts only differ when a package was installed
# or modules were blacklisted/unblacklisted. A verify-only run skips it.
if [ "$changed" -eq 1 ]; then
    if command -v update-initramfs >/dev/null 2>&1; then
        update-initramfs -u -k all 2>&1 || true
    elif command -v dracut >/dev/null 2>&1; then
        dracut --regenerate-all --force 2>&1 || true
    elif command -v mkinitcpio >/dev/null 2>&1; then
        mkinitcpio -P 2>&1 || true
    elif command -v mkinitrd >/dev/null 2>&1; then
        mkinitrd 2>&1 || true
    fi
    echo "hyperv integration changed — initramfs regenerated"
else
    echo "hyperv integration already in place — nothing to do"
fi

echo "=== hyperv-daemons install complete ==="
exit 0