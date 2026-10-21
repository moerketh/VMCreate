#!/bin/bash
set -eo pipefail

# VMCreate ISO v2 — customize workflow: mount the existing GPT disk, chroot
# into it and apply customizations without cloning. The HOST decides the
# scenario; ported 1:1 from hyperv-convert-iso customize_only.sh.
#
# Split into sub-commands so the host can run chroot-capable customization
# steps BETWEEN the phases (mounts persist in the live ISO across SSH
# sessions):
#
#   customize_target.sh prepare [xrdp_flag] [nameservers] [swap_mb]
#       Mount the target, bind-mount /dev /proc /sys, run the built-in
#       chroot work (Hyper-V packages, xrdp, network fixes…). Leaves the
#       mounts in place — the host continues with chroot step execution.
#       MUST NOT unmount on exit: failure or success, the next phase (or
#       the host's cleanup call) owns unmounting.
#   customize_target.sh finalize [ssh_pubkey]
#       Automation user, HVSOCK report, sync + unmount.
#   customize_target.sh cleanup
#       sync + unmount. Failure-path only: the host calls this before
#       collecting logs and powering off, so a failed prepare or chroot
#       step never leaves the target filesystem dirty.
#
# Legacy single-invocation behavior is gone: the host always drives the
# phases in order (prepare → chroot steps → finalize).
source /opt/vmcreate/common.sh

SUBCOMMAND="${1:-}"
case "$SUBCOMMAND" in
    prepare)
        XRDP_FLAG="${2:-false}"
        VMCREATE_NAMESERVERS="${3:-}"
        VMCREATE_SWAP_MB="${4:-8192}"
        ;;
    finalize)
        SSH_PUBKEY="${2:-}"
        ;;
    cleanup)
        ;;
    *)
        echo "Usage: $0 prepare [xrdp_flag] [nameservers] [swap_mb] | finalize [ssh_pubkey] | cleanup" >&2
        exit 2
        ;;
esac

# Failure-path sub-command: the host calls this after a failed prepare or
# chroot step, BEFORE collecting logs and powering off, so the target
# filesystem is synced and unmounted (dirty unmount = btrfs recovery on
# next boot at best, corruption at worst). Idempotent — safe to call even
# if prepare never reached the mounts.
if [ "$SUBCOMMAND" = "cleanup" ]; then
    echo "customize_target.sh cleanup: syncing and unmounting"
    sync
    cleanup_mounts
    echo "customize_target.sh cleanup completed"
    exit 0
fi

report_progress "CUSTOMIZE_START" "Starting customize-only mode"

# ── prepare: mount target + built-in chroot work ──────────────────────
# Everything from here through the xrdp block runs ONLY in prepare; the
# mounts stay up so the host can execute chroot-capable customization
# steps between prepare and finalize.
if [ "$SUBCOMMAND" = "prepare" ]; then

# In customize-only mode there is exactly one partitioned disk attached.
# The ISO boots from DVD; the target disk is the only /dev/sd* device.
target_disk=""
for disk in /dev/sd[a-z]; do
    [ -b "$disk" ] || continue
    part_count=$(lsblk -l -o NAME -n "$disk" | wc -l)
    if [ "$part_count" -gt 1 ]; then
        target_disk="$disk"
        break
    fi
done

if [ -z "$target_disk" ]; then
    report_progress "CUSTOMIZE_ERROR" "No partitioned disk found"
    echo "ERROR: Could not find a partitioned disk to customize." | tee -a /tmp/error.log
    exit 1
fi

echo "Target disk: $target_disk"

# Detect root and ESP partitions on the target disk
report_progress "DETECT" "Detecting partitions on target disk"

# Find root partition (ext4 with /etc/fstab)
root_part=""
esp_part=""
root_mount_opts=""
temp_check="/tmp/check_root"
mkdir -p "$temp_check"

for part in $(lsblk -lpno NAME,TYPE "$target_disk" | grep ' part$' | awk '{print $1}'); do
    fs_type=$(blkid -o value -s TYPE "$part" 2>/dev/null || true)

    # Check for ESP (vfat)
    if [ "$fs_type" = "vfat" ]; then
        esp_part="$part"
        echo "Detected ESP: $esp_part"
        continue
    fi

    # Check for root (ext2/3/4, btrfs, xfs — anything with fstab + bin)
    if [[ "$fs_type" =~ ^(ext[234]|btrfs|xfs)$ ]]; then
        if mount -o ro "$part" "$temp_check" 2>/dev/null; then
            if [ -f "$temp_check/etc/fstab" ] && [ -d "$temp_check/bin" ]; then
                root_part="$part"
                echo "Detected root: $root_part (fs: $fs_type)"
                umount "$temp_check"
                break
            fi
            umount "$temp_check"
        fi
        # btrfs subvolume handling: many distros (Parrot, openSUSE, Fedora)
        # put root in a subvolume like @ or @rootfs. A plain mount shows
        # the top-level tree which lacks /etc/fstab and /bin.
        if [ "$fs_type" = "btrfs" ] && [ -z "$root_part" ]; then
            for subvol in @ @rootfs; do
                if mount -o ro,subvol="$subvol" "$part" "$temp_check" 2>/dev/null; then
                    if [ -f "$temp_check/etc/fstab" ] && [ -d "$temp_check/bin" ]; then
                        root_part="$part"
                        root_mount_opts="-o subvol=$subvol"
                        echo "Detected root: $root_part (fs: $fs_type, subvol: $subvol)"
                        umount "$temp_check"
                        break
                    fi
                    umount "$temp_check"
                fi
            done
            [ -n "$root_part" ] && break
        fi
    fi
done
rmdir "$temp_check"

if [ -z "$root_part" ]; then
    report_progress "CUSTOMIZE_ERROR" "No root partition found on $target_disk"
    echo "ERROR: Could not find root partition on $target_disk." | tee -a /tmp/error.log
    exit 1
fi

# Mount root (with subvolume option if detected)
mkdir -p /mnt/new
if [ -n "$root_mount_opts" ]; then
    retry 3 1 mount $root_mount_opts "$root_part" /mnt/new
else
    retry 3 1 mount "$root_part" /mnt/new
fi

# Mount ESP if found
if [ -n "$esp_part" ]; then
    mkdir -p /mnt/new/boot/efi
    retry 3 1 mount "$esp_part" /mnt/new/boot/efi
fi

# ── Mount additional fstab entries (e.g. /home on separate partition/subvol) ──
# Some distros (Ubuntu 24.04+, openSUSE) use a separate btrfs subvolume or
# partition for /home.  If we don't mount it before chroot, any user/home dirs
# we create will land on the root subvolume and be hidden when the real /home
# is mounted at boot.
mount_fstab_extras() {
    local fstab="/mnt/new/etc/fstab"
    [ -f "$fstab" ] || return 0

    # Read fstab, skip comments/empty/swap, skip / and /boot/efi (already mounted)
    while read -r fs_spec fs_file fs_vfstype fs_mntops _; do
        [[ "$fs_spec" =~ ^#  ]] && continue
        [ -z "$fs_spec" ] && continue
        [ "$fs_vfstype" = "swap" ] && continue
        [ "$fs_file" = "/" ] && continue
        [ "$fs_file" = "/boot/efi" ] && continue
        [ "$fs_file" = "/boot" ] && continue
        # Only mount real filesystems (skip proc/sys/devpts/tmpfs etc)
        case "$fs_vfstype" in
            proc|sysfs|devpts|tmpfs|devtmpfs|cgroup*|securityfs|debugfs|efivarfs|fuse*) continue ;;
        esac

        local target="/mnt/new${fs_file}"
        mkdir -p "$target"

        # Handle btrfs subvolumes specified in fstab options
        if [ "$fs_vfstype" = "btrfs" ]; then
            local subvol_opt
            subvol_opt=$(echo "$fs_mntops" | tr ',' '\n' | grep '^subvol=' | head -1)
            if [ -n "$subvol_opt" ]; then
                echo "Mounting fstab entry: $fs_file ($fs_vfstype, $subvol_opt)"
                mount -o "$subvol_opt" "$root_part" "$target" 2>/dev/null && continue
            fi
        fi

        # Handle UUID= and LABEL= references
        local dev="$fs_spec"
        if [[ "$fs_spec" =~ ^UUID= ]]; then
            dev=$(blkid -U "${fs_spec#UUID=}" 2>/dev/null || true)
        elif [[ "$fs_spec" =~ ^LABEL= ]]; then
            dev=$(blkid -L "${fs_spec#LABEL=}" 2>/dev/null || true)
        fi

        if [ -n "$dev" ] && [ -b "$dev" ]; then
            echo "Mounting fstab entry: $fs_file ($fs_vfstype, $dev)"
            mount -t "$fs_vfstype" "$dev" "$target" 2>/dev/null || \
                echo "WARNING: Failed to mount fstab $fs_file — user home dirs may not persist" | tee -a /tmp/error.log
        fi
    done < "$fstab"
}
mount_fstab_extras

# ── Create swap file (most GPT gallery images ship without swap) ───────
report_progress "CREATE_SWAP" "Creating swap file"
create_swap_file /mnt/new "${VMCREATE_SWAP_MB:-8192}"

# Bind mounts for chroot
mount --bind /dev /mnt/new/dev
mount --bind /proc /mnt/new/proc
mount --bind /sys /mnt/new/sys
mount --bind /dev/pts /mnt/new/dev/pts
modprobe efivarfs
mount --bind /sys/firmware/efi/efivars /mnt/new/sys/firmware/efi/efivars || true
setup_chroot_dns /mnt/new "$VMCREATE_NAMESERVERS"

# Copy autorun files for chroot scripts
mkdir -p /mnt/new/opt/vmcreate
cp /opt/vmcreate/install_xrdp.sh /mnt/new/opt/vmcreate/ 2>/dev/null || true

# ── Capture SSH state BEFORE any chroot apt calls ────────────────────
capture_ssh_state /mnt/new

# ── Fix conflicting apt sources ──────────────────────────────────────
fix_apt_repo_conflicts /mnt/new
# ── Start Tor if the target uses tor+https APT sources ──────────────
start_tor_if_needed /mnt/new

# ── Configure temporary second NIC for post-boot SSH ─────────────────
# Writes firewall rules + DHCP config for eth1; no-op if not needed.
configure_temp_nic /mnt/new

# ── Hyper-V guest optimization ───────────────────────────────────────
report_progress "INSTALL_HYPERV_PACKAGES" "Installing Hyper-V guest integration services"
install_hyperv_packages /mnt/new

# ── Ensure critical services are enabled via direct symlinks ──────────
enable_services_via_symlinks /mnt/new

# Generate SSH host keys if missing
generate_ssh_host_keys /mnt/new

# ── Fix network configs: replace hardcoded interface names ────────────
fix_netplan_for_hyperv /mnt/new
fix_networkmanager_for_hyperv /mnt/new
fix_interfaces_for_hyperv /mnt/new

# ── Disable cloud-init network override ──────────────────────────────
disable_cloud_init_network /mnt/new

# Read KVP flags
if [ "$XRDP_FLAG" = "true" ]; then
    report_progress "INSTALL_XRDP" "Installing XRDP for Enhanced Session support"
    echo "Installing xrdp for Hyper-V Enhanced Session support"
    # Non-fatal: a failed XRDP install should not invalidate a successful customization
    if chroot /mnt/new /bin/bash /opt/vmcreate/install_xrdp.sh; then
        echo "XRDP installation completed successfully"
    else
        report_progress "XRDP_WARNING" "XRDP installation failed, VM will boot without XRDP"
        echo "WARNING: XRDP installation failed, continuing..." | tee -a /tmp/error.log
    fi
    # Safety: remove policy-rc.d in case install_xrdp.sh failed mid-way
    rm -f /mnt/new/usr/sbin/policy-rc.d
    rm -f /mnt/new/opt/vmcreate/install_xrdp.sh
fi

# NOTE: guest PowerShell (pwsh) installation removed — all Linux post-boot
# customization runs over SSH with bash scripts; pwsh/PS-remoting was never
# consumed by the host.

# ── Guard the host's chroot steps against service starts ─────────────
# Between prepare and finalize the host runs chroot-capable customization
# steps; their package installs (apt/dnf) must not try to start services
# (no systemd in the chroot — a starting postinst fails dpkg). policy-rc.d
# exit 101 denies every service action on Debian-family. Created HERE (not
# earlier): install_xrdp.sh removes the file at its end, and this step must
# come out ahead of that removal. finalize AND cleanup remove it — a
# leftover would keep denying service starts on the failed target's next
# boot too.
printf '#!/bin/sh\nexit 101\n' > /mnt/new/usr/sbin/policy-rc.d
chmod +x /mnt/new/usr/sbin/policy-rc.d

echo "customize_target.sh prepare completed (mounts held for chroot steps)"

fi # ── end prepare ────────────────────────────────────────────────────

# ── finalize: after the host's chroot steps ───────────────────────────
if [ "$SUBCOMMAND" = "finalize" ]; then

# ── Stop Tor daemon if we started it ─────────────────────────────────
# Tor serves the chroot's apt; it must survive the host's chroot steps
# (they may run apt too) and stops only now.
stop_tor_if_running

# ── Remove the chroot's policy-rc.d guard ────────────────────────────
# From here on the target must behave like itself again (services start
# at first boot). cleanup_mounts powers the target off via the host, so
# this is the LAST chroot filesystem write.
rm -f /mnt/new/usr/sbin/policy-rc.d

# ── Create automation user and inject SSH key on target VM ───────────
# The public key arrives as an argument (header).
report_progress "SSH_SETUP" "Setting up automation user and SSH key on target VM"
if [ -n "$SSH_PUBKEY" ]; then
    echo "Injecting SSH key and creating vmcreate automation user on target VM"
    create_automation_user /mnt/new "$SSH_PUBKEY"
else
    echo "No SSH public key in KVP — skipping automation user setup"
fi

# Clean up guest scripts from the target VM
rm -rf /mnt/new/opt/vmcreate

# ── Report vsock capability of the TARGET (not the ISO) ──────────────
# The host logs this; Phase 4b decides the post-boot NIC question from
# it. Read the target's own module tree for every installed kernel —
# never modinfo in the ISO (that reports the ISO's kernel).
hvsock_found=no
for kdir in /mnt/new/lib/modules/*; do
    [ -d "$kdir" ] || continue
    if compgen -G "$kdir/kernel/net/vmw_vsock/hv_sock.ko*" >/dev/null \
       || grep -q '/hv_sock\.ko' "$kdir/modules.builtin" 2>/dev/null; then
        hvsock_found=yes
        break
    fi
done
echo "VMCREATE:HVSOCK:${hvsock_found}"

# ── Final contract: everything unmounted and synced ──────────────────
progress 100 "customize workflow complete"
sync
cleanup_mounts
echo "customize_target.sh finalize completed"

fi # ── end finalize ───────────────────────────────────────────────────

exit 0
