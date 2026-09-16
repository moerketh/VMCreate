#!/bin/bash
set -o pipefail

# -- Install hyperv-daemons (best-effort) ----------------------------------
# Provides VSS backup integration, KVP data exchange, and fcopy daemon.
if command -v apt-get >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y hyperv-daemons 2>&1 || true
elif command -v dnf >/dev/null 2>&1; then
    dnf install -y hyperv-daemons 2>&1 || true
elif command -v pacman >/dev/null 2>&1; then
    pacman -S --noconfirm hyperv 2>&1 || true
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
