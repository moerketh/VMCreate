#!/bin/bash
set -o pipefail
removed=0

# -- Step 1: Uninstall open-vm-tools packages ------------------------------
if command -v apt-get >/dev/null 2>&1; then
    vmware_pkgs=$(dpkg -l 2>/dev/null | grep -oE 'open-vm-tools[a-z0-9-]*' | sort -u || true)
    if [ -n "$vmware_pkgs" ]; then
        echo "Removing VMware packages: $vmware_pkgs"
        DEBIAN_FRONTEND=noninteractive apt-get purge -y $vmware_pkgs 2>&1 || true
        apt-get autoremove -y 2>&1 || true
        removed=1
    fi
elif command -v dnf >/dev/null 2>&1; then
    vmware_pkgs=$(rpm -qa 2>/dev/null | grep -E 'open-vm-tools' || true)
    if [ -n "$vmware_pkgs" ]; then
        echo "Removing VMware packages: $vmware_pkgs"
        dnf remove -y $vmware_pkgs 2>&1 || true
        removed=1
    fi
elif command -v pacman >/dev/null 2>&1; then
    vmware_pkgs=$(pacman -Qq 2>/dev/null | grep open-vm-tools || true)
    if [ -n "$vmware_pkgs" ]; then
        echo "Removing VMware packages: $vmware_pkgs"
        pacman -Rns --noconfirm $vmware_pkgs 2>&1 || true
        removed=1
    fi
elif command -v zypper >/dev/null 2>&1; then
    vmware_pkgs=$(rpm -qa 2>/dev/null | grep -E 'open-vm-tools' || true)
    if [ -n "$vmware_pkgs" ]; then
        echo "Removing VMware packages: $vmware_pkgs"
        zypper remove -y $vmware_pkgs 2>&1 || true
        removed=1
    fi
fi

# -- Step 2: Blacklist all VMware kernel modules ---------------------------
# Prevents them from loading even if leftover files exist.
cat > /etc/modprobe.d/blacklist-vmware.conf << 'BLACKLIST_EOF'
# Block all VMware drivers (they conflict with Hyper-V)
blacklist vmw_balloon
blacklist vmw_pvscsi
blacklist vmw_vmxnet3
blacklist vmwgfx
blacklist vmw_vmci
blacklist vmware_balloon
blacklist vmware_vmxnet3
blacklist vmware_pvscsi
blacklist vmw_vmci
blacklist vmware_vmci
BLACKLIST_EOF
echo "Blacklisted VMware kernel modules"
removed=1

# -- Step 3: Remove leftover VMware config directories ---------------------
rm -rf /etc/vmware-tools /var/run/vmware* /var/lib/vmware* 2>/dev/null || true

# -- Step 4: Remove VMware X11 driver config -------------------------------
rm -f /etc/X11/xorg.conf.d/*vmware* 2>/dev/null || true
rm -f /etc/X11/xorg.conf 2>/dev/null || true

# -- Step 5: Unload any currently loaded VMware modules ---------------------
for mod in vmw_balloon vmw_pvscsi vmw_vmxnet3 vmwgfx vmw_vmci; do
    if lsmod 2>/dev/null | grep -q "^$mod "; then
        echo "Unloading kernel module: $mod"
        rmmod "$mod" 2>/dev/null || true
        removed=1
    fi
done

# -- Step 6: Regenerate initramfs (critical -- applies blacklist) ----------
if command -v update-initramfs >/dev/null 2>&1; then
    update-initramfs -u -k all 2>&1 || true
elif command -v dracut >/dev/null 2>&1; then
    dracut --regenerate-all --force 2>&1 || true
elif command -v mkinitcpio >/dev/null 2>&1; then
    mkinitcpio -P 2>&1 || true
elif command -v mkinitrd >/dev/null 2>&1; then
    mkinitrd 2>&1 || true
fi

# -- Step 7: Update GRUB (removes any VMware references) --------------------
if command -v update-grub >/dev/null 2>&1; then
    update-grub 2>&1 || true
elif command -v grub2-mkconfig >/dev/null 2>&1; then
    grub2-mkconfig -o /boot/grub2/grub.cfg 2>&1 || true
elif command -v grub-mkconfig >/dev/null 2>&1; then
    grub-mkconfig -o /boot/grub/grub.cfg 2>&1 || true
fi

[ $removed -eq 0 ] && echo "No VMware tools found" || echo "VMware tools removed and drivers blacklisted"
exit 0
