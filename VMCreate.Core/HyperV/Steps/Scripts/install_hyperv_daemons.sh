#!/bin/bash
set -o pipefail
# Non-interactive guard: headless SSH gives scripts a no-TTY, pipe stdin that
# never yields EOF on its own — an interactive `read` blocks until the host
# kills the session. Omarchy's /usr/local/bin/mkinitcpio wrapper asks
# "run limine-mkinitcpio now? [Y/n]" after -P; with /dev/null stdin its read
# returns instantly (empty answer → default branch). See remove_vmware.sh.
exec 0</dev/null

# -- Install hyperv-daemons (best-effort) ----------------------------------
# Provides VSS backup integration, KVP data exchange, and fcopy daemon.
if command -v apt-get >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y hyperv-daemons 2>&1 || true
elif command -v dnf >/dev/null 2>&1; then
    dnf install -y hyperv-daemons 2>&1 || true
elif command -v pacman >/dev/null 2>&1; then
    pacman -S --noconfirm hyperv 2>&1 || true
    # Arch's hyperv package ships the three integration daemons disabled.
    # Without hv_kvp_daemon, Hyper-V never learns the guest IP and VMCreate's
    # SSH discovery stays dark (this is what stranded Omarchy deployments in
    # "Waiting for SSH"); VSS and fcopy round out the integration services.
    for daemon in hv_kvp_daemon hv_vss_daemon hv_fcopy_daemon; do
        systemctl enable --now "$daemon" 2>&1 || true
    done
fi

# -- Regenerate initramfs (applies blacklist + module changes) ------------
if command -v update-initramfs >/dev/null 2>&1; then
    update-initramfs -u -k all 2>&1 || true
elif command -v dracut >/dev/null 2>&1; then
    dracut --regenerate-all --force 2>&1 || true
elif command -v mkinitcpio >/dev/null 2>&1; then
    mkinitcpio -P 2>&1 || true
elif command -v mkinitrd >/dev/null 2>&1; then
    mkinitrd 2>&1 || true
fi

echo "=== hyperv-daemons install complete ==="
exit 0
