#!/bin/bash
set -o pipefail
# Removes guest tooling left over from other hypervisors (VirtualBox Guest
# Additions, VMware open-vm-tools) so their drivers cannot conflict with
# Hyper-V's synthetic devices. One script = one package-manager transaction
# and one initramfs/GRUB regen.
#
# Non-interactive guard: over headless SSH (no TTY, stdin at EOF-forever on a
# pipe), any interactive prompt would block until the host-side timeout kills
# the session. Omarchy ships /usr/local/bin/mkinitcpio as a wrapper that ASKS
# "run limine-mkinitcpio now? [Y/n]" after -P — its `read` sat on
# anon_pipe_read for the full command timeout and killed the deploy. Redirect
# our own stdin to /dev/null so every child inherits instant-EOF stdin: the
# wrapper's `read` returns immediately (answer empty → default branch).
exec 0</dev/null
removed=0

# ── Stage 1: uninstall packages (single purge per package manager) ─────────
# Collect BOTH the VirtualBox and VMware package sets first, then purge them
# in one transaction: the dpkg/pacman lock is exclusive, and apt fails fast
# on a busy lock instead of queuing — two parallel or back-to-back purges
# would race the lock.
if command -v apt-get >/dev/null 2>&1; then
    pkgs=$( { dpkg -l 2>/dev/null | grep -oE 'virtualbox-guest-[a-z0-9-]+' ; dpkg -l 2>/dev/null | grep -oE 'open-vm-tools[a-z0-9-]*' ; } | sort -u || true)
    if [ -n "$pkgs" ]; then
        echo "Removing guest-tool packages: $pkgs"
        DEBIAN_FRONTEND=noninteractive apt-get purge -y $pkgs 2>&1 || true
        apt-get autoremove -y 2>&1 || true
        removed=1
    fi
elif command -v dnf >/dev/null 2>&1; then
    pkgs=$(rpm -qa 2>/dev/null | grep -E 'virtualbox-guest|VirtualBox-guest|open-vm-tools' || true)
    if [ -n "$pkgs" ]; then
        echo "Removing guest-tool packages: $pkgs"
        dnf remove -y $pkgs 2>&1 || true
        removed=1
    fi
elif command -v yum >/dev/null 2>&1; then
    pkgs=$(rpm -qa 2>/dev/null | grep -E 'virtualbox-guest|VirtualBox-guest|open-vm-tools' || true)
    if [ -n "$pkgs" ]; then
        echo "Removing guest-tool packages: $pkgs"
        yum remove -y $pkgs 2>&1 || true
        removed=1
    fi
elif command -v pacman >/dev/null 2>&1; then
    pkgs=$( { pacman -Qq 2>/dev/null | grep virtualbox-guest ; pacman -Qq 2>/dev/null | grep open-vm-tools ; } | sort -u || true)
    if [ -n "$pkgs" ]; then
        echo "Removing guest-tool packages: $pkgs"
        pacman -Rns --noconfirm $pkgs 2>&1 || true
        removed=1
    fi
elif command -v zypper >/dev/null 2>&1; then
    pkgs=$(rpm -qa 2>/dev/null | grep -E 'virtualbox-guest|VirtualBox-guest|open-vm-tools' || true)
    if [ -n "$pkgs" ]; then
        echo "Removing guest-tool packages: $pkgs"
        zypper remove -y $pkgs 2>&1 || true
        removed=1
    fi
fi

# ── Stage 2: VirtualBox leftovers outside the package manager ──────────────
# ── Method 1: ISO-based install (/opt/VBoxGuestAdditions-*/uninstall.sh) ──
for d in /opt/VBoxGuestAdditions-*; do
    if [ -x "$d/uninstall.sh" ]; then
        echo "Uninstalling via $d/uninstall.sh"
        "$d/uninstall.sh" 2>&1 || echo "uninstall.sh exited $? (non-fatal)"
        rm -rf "$d"
        removed=1
    fi
done

# ── Method 2: unload guest kernel modules ──
for mod in vboxguest vboxsf vboxvideo; do
    if lsmod 2>/dev/null | grep -q "^$mod "; then
        echo "Unloading kernel module: $mod"
        rmmod "$mod" 2>/dev/null || true
        removed=1
    fi
done

# ── Method 3: DKMS entries ──
if command -v dkms >/dev/null 2>&1; then
    dkms_vbox=$(dkms status 2>/dev/null | grep -i virtualbox || true)
    if [ -n "$dkms_vbox" ]; then
        echo "Removing DKMS entries: $dkms_vbox"
        dkms status 2>/dev/null | grep -i virtualbox | while IFS=, read -r name ver rest; do
            mod=$(echo "$name" | xargs)
            v=$(echo "$ver" | xargs)
            dkms remove "$mod/$v" --all 2>/dev/null || true
        done
        removed=1
    fi
fi

# ── Method 4: guest services and files ──
for svc in vboxadd vboxadd-service vboxadd-x11; do
    if [ -f "/etc/systemd/system/${svc}.service" ] || \
       [ -f "/lib/systemd/system/${svc}.service" ] || \
       [ -f "/usr/lib/systemd/system/${svc}.service" ]; then
        echo "Disabling service: $svc"
        systemctl disable "${svc}.service" 2>/dev/null || true
        systemctl stop "${svc}.service" 2>/dev/null || true
        rm -f "/etc/systemd/system/${svc}.service" 2>/dev/null || true
        removed=1
    fi
done

[ -d /mnt/vbox_iso ] && rmdir /mnt/vbox_iso 2>/dev/null && echo "Removed /mnt/vbox_iso" || true
rm -rf /usr/lib/virtualbox 2>/dev/null || true
rm -f /usr/bin/VBox* /usr/sbin/VBox* 2>/dev/null || true
rm -rf /var/lib/VBoxGuestAdditions 2>/dev/null || true

# ── Stage 3: VMware leftovers outside the package manager ──────────────────
# Blacklist all VMware kernel modules: prevents them from loading even if
# leftover files exist. Always written — the blacklist is free and harmless on
# a clean guest, unlike the initramfs regen below which is gated on removal.
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

# Remove leftover VMware config directories and X11 driver config
rm -rf /etc/vmware-tools /var/run/vmware* /var/lib/vmware* 2>/dev/null || true
rm -f /etc/X11/xorg.conf.d/*vmware* 2>/dev/null || true
rm -f /etc/X11/xorg.conf 2>/dev/null || true

# Unload any currently loaded VMware modules
for mod in vmw_balloon vmw_pvscsi vmw_vmxnet3 vmwgfx vmw_vmci; do
    if lsmod 2>/dev/null | grep -q "^$mod "; then
        echo "Unloading kernel module: $mod"
        rmmod "$mod" 2>/dev/null || true
        removed=1
    fi
done

# ── Stage 4: regenerate boot artifacts (only when something was removed) ───
# initramfs regeneration is by far the most expensive part of this step —
# mkinitcpio -P measured >6 min on a 2-vCPU guest — and the boot artifacts
# only change when a kernel module was blacklisted/removed or a package was
# purged. Skipping it on a clean guest saves the whole cost. GRUB update goes
# with it for the same reason. The exit code is captured and reported: a
# silent failure here leaves the blacklist unapplied until the next kernel
# update.
if [ "$removed" -eq 1 ]; then
    _initramfs_rc=0
    if command -v update-initramfs >/dev/null 2>&1; then
        update-initramfs -u -k all 2>&1 || _initramfs_rc=$?
    elif command -v dracut >/dev/null 2>&1; then
        dracut --regenerate-all --force 2>&1 || _initramfs_rc=$?
    elif command -v mkinitcpio >/dev/null 2>&1; then
        mkinitcpio -P 2>&1 || _initramfs_rc=$?
    elif command -v mkinitrd >/dev/null 2>&1; then
        mkinitrd 2>&1 || _initramfs_rc=$?
    fi
    if [ "$_initramfs_rc" -ne 0 ]; then
        echo "WARNING: initramfs regeneration failed (exit $_initramfs_rc) — the VMware blacklist may not be active until the next kernel update"
    fi

    if command -v update-grub >/dev/null 2>&1; then
        update-grub 2>&1 || true
    elif command -v grub2-mkconfig >/dev/null 2>&1; then
        grub2-mkconfig -o /boot/grub2/grub.cfg 2>&1 || true
    elif command -v grub-mkconfig >/dev/null 2>&1; then
        grub-mkconfig -o /boot/grub/grub.cfg 2>&1 || true
    fi
fi

[ $removed -eq 0 ] && echo "No guest tools from other hypervisors found" || echo "Guest tools removed and drivers blacklisted"
exit 0
