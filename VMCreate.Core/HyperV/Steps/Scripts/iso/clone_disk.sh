#!/bin/bash
set -eo pipefail

# VMCreate ISO v2 — clone workflow (MBR source -> UEFI target).
# The HOST decides the scenario and uploads this script; no KVP signaling,
# no guest-side decision. Ported 1:1 from hyperv-convert-iso autorun.sh.
#
# Split into sub-commands so the host can run chroot-capable customization
# steps BETWEEN the phases (mounts persist in the live ISO across SSH
# sessions):
#
#   clone_disk.sh prepare [xrdp_flag] [nameservers] [swap_mb]
#       Full clone: partition, partclone, GRUB, initramfs, then the built-in
#       chroot work (Hyper-V packages, xrdp, network fixes…). Leaves the
#       mounts in place — the host continues with chroot step execution.
#   clone_disk.sh finalize [ssh_pubkey]
#       Automation user, ESP redirect grub.cfg (must stay AFTER every chroot
#       apt call — Ubuntu's signed-grub dpkg trigger overwrites it), sync +
#       unmount.
#   clone_disk.sh cleanup
#       sync + unmount. Failure-path only: the host calls this before
#       collecting logs and powering off, so a failed prepare or chroot
#       step never leaves the target filesystem dirty.
#
# Legacy single-invocation behavior is gone: the host always drives the
# phases in order (prepare → chroot steps → finalize).
#   xrdp_flag   "true" installs xrdp in the target (Enhanced Session)
#   nameservers comma-separated DNS for the chroot (host-resolved)
#   swap_mb     swap file size in MB (default 8192)
#   ssh_pubkey  public key for the target's automation user
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

# Failure-path sub-command: see header. Idempotent — safe to call even if
# prepare never reached the mounts.
if [ "$SUBCOMMAND" = "cleanup" ]; then
    echo "clone_disk.sh cleanup: syncing and unmounting"
    sync
    cleanup_mounts
    echo "clone_disk.sh cleanup completed"
    exit 0
fi

echo "=== clone_disk.sh ($SUBCOMMAND) started at $(date -Iseconds) ==="
echo "vmcreate-iso version: $(cat /etc/vmcreate-iso-version 2>/dev/null || echo 'unknown')"
echo "--- Environment ---"
echo "Kernel: $(uname -r)"
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT 2>&1 || true


# Detect disks: new (empty, no partitions) and old (has partitions)
detect_disks

report_progress "PREFLIGHT" "Disk detection complete"

# ── prepare: clone + mount + built-in chroot work ─────────────────────
# Everything from here through the xrdp block runs ONLY in prepare; the
# mounts stay up so the host can execute chroot-capable customization
# steps between prepare and finalize.
if [ "$SUBCOMMAND" != "prepare" ]; then
    # finalize: /mnt/new was mounted by prepare; skip to its own section.
    true
else

# Run pre-flight checks before proceeding with disk operations
preflight_checks

# Partition new disk (GPT with ESP and root)
report_progress "PARTITION" "Starting disk partitioning"
sgdisk --zap-all $new_disk
sgdisk --new=1:2048:+512M --typecode=1:ef00 --change-name=1:ESP $new_disk  # ESP
sgdisk --new=2::0 --typecode=2:8300 --change-name=2:root $new_disk         # Root (rest, combined boot/root)
retry 3 1 partprobe "$new_disk"
udevadm settle

# Detect partitions on old_disk
echo "Detecting partitions on $old_disk" | tee -a /tmp/clone.log

detect_partitions "$old_disk"

# Detect root filesystem type for correct partclone tool selection
root_fs_type=$(blkid -o value -s TYPE "$root_part" 2>/dev/null || echo "ext4")
echo "Root filesystem type: $root_fs_type"
clone_tool=$(select_partclone_tool "$root_fs_type")
if [ -z "$clone_tool" ]; then
    echo "ERROR: Unsupported root filesystem type: $root_fs_type" | tee -a /tmp/error.log
    exit 1
fi
echo "Selected clone tool: $clone_tool"

# Format new ESP partition (only needed if no source ESP to clone, but partclone
# will overwrite it if there is one — cheap to do unconditionally for vfat)
mkfs.vfat -F32 "${new_disk}1"

# Only format root if partclone will NOT overwrite it (shouldn't happen, but guard)
# partclone --dev-to-dev overwrites the partition, so skip mkfs

# Clone root
report_progress "CLONE_ROOT" "Starting root partition cloning"
echo "Cloning root from $root_part to ${new_disk}2 using $clone_tool"



# Run partclone in background with logfile
# Use a named pipe so we can capture partclone's exit code directly
$clone_tool --force --dev-to-dev --source "$root_part" --output "${new_disk}2" --logfile /tmp/partclone.log 2>&1 | tee -a /tmp/partclone_process.log >/dev/null &
clone_pipeline_pid=$!
last_progress=""

while kill -0 $clone_pipeline_pid 2>/dev/null; do
    if [ -f /tmp/partclone_process.log ]; then
        # Clean last 20 lines — convert \r to \n (partclone uses \r for in-place
        # updates), then strip ANSI escape codes.
        cleaned=$(tail -n 20 /tmp/partclone_process.log | tr '\r' '\n' | sed 's/\x1b\[[0-9;]*[a-zA-Z]//g')
        # Parse with awk: use regex to extract percentage and rate from Elapsed lines.
        # Partclone rate may be in GB/min, MB/min, or KB/min — forward the actual unit.
        progress=$(echo "$cleaned" | awk '
            /Elapsed:/ {
                perc = ""
                rate = ""
                for (i = 1; i <= NF; i++) {
                    if (perc == "" && $i ~ /^[0-9.]+%/) {
                        perc = $i
                        gsub(/[%,]/, "", perc)
                    }
                    if (rate == "" && $i ~ /[0-9][GgMmKk][Bb]\/min/) {
                        rate = $i
                        gsub(/,$/, "", rate)
                    }
                }
                if (perc != "") {
                    if (rate != "")
                        print "Progress: " perc "% | Rate: " rate
                    else
                        print "Progress: " perc "%"
                }
            }
        ' | tail -n 1)

        if [ -n "$progress" ]; then
            # Emit only when the parsed progress line CHANGED: a hung partclone
        # re-prints the same percentage forever, and re-emitting it would
        # keep the host's idle clock fed until the total timeout. The
        # decimals (45.12) change continuously on a healthy clone.
        if [ "$progress" != "$last_progress" ]; then
            last_progress="$progress"
            case "$progress" in
            *"%"*)
                pct=$(echo "$progress" | grep -oE '[0-9]+' | head -1)
                [ -n "$pct" ] && progress "$((10 + pct / 2))" "cloning root: $progress"
                ;;
        esac
        fi

        fi
    else
        echo "Waiting for partclone.log to be created..."  # Optional debug message
    fi
    sleep 1  # Poll interval
done

# Wait for the pipeline to finish and check exit code (pipefail ensures partclone failures propagate)
wait $clone_pipeline_pid
clone_exit=$?
if [ $clone_exit -ne 0 ]; then
    report_progress "CLONE_ERROR" "Root partition cloning failed with exit code $clone_exit"
    echo "ERROR: $clone_tool failed with exit code $clone_exit" | tee -a /tmp/error.log
    exit 1
fi

# Send final completion KVP
progress 60 "cloning root: completed"
echo "Partclone conversion completed."

# Clone ESP if detected
if [ -n "$esp_part" ]; then
  report_progress "CLONE_ESP" "Starting ESP partition cloning"
  echo "Cloning ESP from $esp_part to ${new_disk}1"
  partclone.vfat --force --dev-to-dev --source "$esp_part" --output "${new_disk}1" 2>&1
  esp_clone_exit=$?
  if [ $esp_clone_exit -ne 0 ]; then
      report_progress "CLONE_ERROR" "ESP cloning failed with exit code $esp_clone_exit"
      echo "ERROR: partclone.vfat failed with exit code $esp_clone_exit" | tee -a /tmp/error.log
      exit 1
  fi
else
  echo "No ESP detected on old disk (new ESP will be populated by grub-install)."
fi

# Verify cloned partitions are clean
report_progress "VERIFY" "Verifying cloned filesystems"
if verify_clone "${new_disk}2" "${new_disk}1"; then
    echo "Filesystem verification PASSED"
else
    echo "ERROR: Filesystem verification FAILED" | tee -a /tmp/error.log
    exit 1
fi

# Resize root filesystem to fill the new (potentially larger) partition
report_progress "RESIZE" "Resizing root filesystem to fill partition"
resize_cloned_filesystem "${new_disk}2" "$root_fs_type"

# Mount new root and ESP
mkdir -p /mnt/new
retry 3 1 mount ${new_disk}2 /mnt/new
mkdir -p /mnt/new/boot/efi
retry 3 1 mount ${new_disk}1 /mnt/new/boot/efi

# Merge separate /boot if detected
if [ ! -z "$boot_part" ]; then
  report_progress "MERGE_BOOT" "Merging separate /boot partition"
  echo "Merging /boot from $boot_part into /mnt/new/boot/"
  temp_old_boot="/tmp/old_boot"
  mkdir -p "$temp_old_boot"
  mount $boot_part "$temp_old_boot"
  rsync -av "$temp_old_boot/" /mnt/new/boot/
  umount "$temp_old_boot"
  rmdir "$temp_old_boot"
fi

# Update fstab with new UUIDs
report_progress "UPDATE_FSTAB" "Updating fstab with new UUIDs"
update_fstab "/mnt/new/etc/fstab" "$root_part" "${new_disk}1" "${new_disk}2"

# Validate fstab for stale references to old disk
validate_fstab "/mnt/new/etc/fstab" "$old_disk"

# Create swap file to replace the removed swap partition
report_progress "CREATE_SWAP" "Creating swap file"
create_swap_file /mnt/new "${VMCREATE_SWAP_MB:-8192}"

# Bind mounts for chroot
mount --bind /dev /mnt/new/dev
mount --bind /proc /mnt/new/proc
mount --bind /sys /mnt/new/sys
#mount --bind /run /mnt/new/run
mount --bind /dev/pts /mnt/new/dev/pts
modprobe efivarfs
mount --bind /sys/firmware/efi/efivars /mnt/new/sys/firmware/efi/efivars || true
setup_chroot_dns /mnt/new "$VMCREATE_NAMESERVERS"

# copy autorun files
mkdir -p /mnt/new/opt/vmcreate
cp /opt/vmcreate/* /mnt/new/opt/vmcreate

# Install grub with UEFI support
report_progress "INSTALL_GRUB" "Installing GRUB bootloader"
chroot /mnt/new /bin/bash /opt/vmcreate/install_grub.sh "$new_disk"
rm /mnt/new/opt/vmcreate/install_grub.sh

# ── Disable stale resume (hibernate) device ──────────────────────────
# The old MBR disk may have had a swap partition configured as the resume
# device. The new GPT disk has no swap, so the stale UUID causes a ~90s
# boot delay ("Gave up waiting for suspend/resume device").
echo "Disabling stale resume device references..."
if [ -f /mnt/new/etc/initramfs-tools/conf.d/resume ]; then
    echo "RESUME=none" > /mnt/new/etc/initramfs-tools/conf.d/resume
    echo "Cleared /etc/initramfs-tools/conf.d/resume"
fi
# Also strip resume= from GRUB command line defaults
if [ -f /mnt/new/etc/default/grub ]; then
    sed -i 's/resume=UUID=[^ ]*/noresume/g; s/resume=\/dev\/[^ ]*/noresume/g' /mnt/new/etc/default/grub
fi

# ── Install kernel postinst hook for ESP redirect ────────────────────
# This hook re-generates the ESP redirect grub.cfg after every kernel
# install, ensuring the boot chain survives future package upgrades.
install_grub_postinst_hook /mnt/new

# ── Force Hyper-V modules into initramfs ─────────────────────────────
# Ensure hv_vmbus, hv_storvsc, hv_netvsc, hv_utils are included so the
# VM can find its root disk after kernel upgrades.
ensure_hyperv_initramfs_modules /mnt/new

# Regenerate initramfs so the resume hook and Hyper-V modules take effect
if chroot /mnt/new /bin/bash -c 'command -v update-initramfs >/dev/null 2>&1'; then
    echo "Regenerating initramfs..."
    chroot /mnt/new update-initramfs -u -k all 2>&1 || echo "WARNING: update-initramfs failed (non-fatal)"
elif chroot /mnt/new /bin/bash -c 'command -v dracut >/dev/null 2>&1'; then
    echo "Regenerating initramfs (dracut)..."
    chroot /mnt/new dracut --force 2>&1 || echo "WARNING: dracut failed (non-fatal)"
elif chroot /mnt/new /bin/bash -c 'command -v mkinitcpio >/dev/null 2>&1'; then
    echo "Regenerating initramfs (mkinitcpio)..."
    chroot /mnt/new mkinitcpio -P 2>&1 || echo "WARNING: mkinitcpio failed (non-fatal)"
fi

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

if [ "$XRDP_FLAG" = "true" ]; then
    report_progress "INSTALL_XRDP" "Installing XRDP for Enhanced Session support"
    echo "Installing xrdp for Hyper-V Enhanced Session support"
    # Non-fatal: a failed XRDP install should not invalidate a successful conversion
    if chroot /mnt/new /bin/bash /opt/vmcreate/install_xrdp.sh; then
        echo "XRDP installation completed successfully"
    else
        report_progress "XRDP_WARNING" "XRDP installation failed, VM will boot without XRDP"
        echo "WARNING: XRDP installation failed, continuing..." | tee -a /tmp/error.log
    fi
    rm -f /mnt/new/opt/vmcreate/install_xrdp.sh
fi

# NOTE: guest PowerShell (pwsh) installation removed — all Linux post-boot
# customization runs over SSH with bash scripts; pwsh/PS-remoting was never
# consumed by the host.

echo "clone_disk.sh prepare completed (mounts held for chroot steps)"

fi # ── end prepare ────────────────────────────────────────────────────

# ── finalize: after the host's chroot steps ───────────────────────────
if [ "$SUBCOMMAND" = "finalize" ]; then

# ── Stop Tor daemon if we started it ─────────────────────────────────
# Tor serves the chroot's apt; it must survive the host's chroot steps
# (they may run apt too) and stops only now.
stop_tor_if_running

# ── Create automation user and inject SSH key on target VM ───────────
# The 'vmcreate' user is used by the host for post-boot SSH connections.
# The public key arrives as an argument (header).
if [ -n "$SSH_PUBKEY" ]; then
    echo "Injecting SSH key and creating vmcreate automation user on target VM"
    create_automation_user /mnt/new "$SSH_PUBKEY"
else
    echo "No SSH public key in KVP — skipping automation user setup"
fi

# ── Fix ESP redirect grub.cfg ────────────────────────────────────────
# The GRUB EFI binary has its prefix embedded as /boot/grub (or /boot/grub2).
# At EFI boot, GRUB's $root defaults to the ESP, so it looks for its config
# at <ESP>/boot/grub/grub.cfg — NOT at <ESP>/EFI/BOOT/grub.cfg.
# Additionally, Ubuntu's grub-efi-amd64-signed dpkg trigger overwrites
# EFI/BOOT/grub.cfg with a broken single-quoted version.
# We write the redirect to BOTH the embedded-prefix path and the EFI dirs,
# AFTER all chroot apt-get calls, from the host side.
if [ -d /mnt/new/boot/grub2 ]; then
    _grub_dir="/boot/grub2"
else
    _grub_dir="/boot/grub"
fi
# Session-independent root source: prepare ran in a different SSH session,
# so its $new_disk variable is gone here — derive the root partition from
# the mount itself.
_root_uuid=$(blkid -o value -s UUID "$(findmnt -n -o SOURCE /mnt/new)" 2>/dev/null || true)
if [ -n "$_root_uuid" ]; then
    # Discover the distro-specific EFI directory from install_grub.sh output
    # (it writes to EFI/<distro_id>/ via --bootloader-id)
    _distro_efi_dirs=""
    for _candidate in /mnt/new/boot/efi/EFI/*/; do
        _base=$(basename "$_candidate")
        # Skip known generic dirs — add the rest as distro-specific
        case "$_base" in
            BOOT|GRUB) continue ;;
            *) _distro_efi_dirs="$_distro_efi_dirs $_candidate" ;;
        esac
    done

    # The critical path: GRUB looks here based on its embedded prefix
    # Also write to EFI standard directories and distro-specific directories
    for _dir in "/mnt/new/boot/efi${_grub_dir}" /mnt/new/boot/efi/EFI/BOOT /mnt/new/boot/efi/EFI/GRUB $_distro_efi_dirs; do
        mkdir -p "$_dir"
        cat > "$_dir/grub.cfg" <<GRUBCFG
search.fs_uuid ${_root_uuid} root
set prefix=(\$root)${_grub_dir}
configfile \$prefix/grub.cfg
GRUBCFG
    done
    echo "Wrote ESP redirect grub.cfg (root UUID: $_root_uuid, grub dir: $_grub_dir)"
    echo "--- Final ESP contents ---"
    find /mnt/new/boot/efi -type f 2>&1 || true
    echo "--- Final ESP ${_grub_dir}/grub.cfg ---"
    cat "/mnt/new/boot/efi${_grub_dir}/grub.cfg"
    echo "---"
else
    echo "WARNING: Could not determine root UUID for ESP redirect grub.cfg"
fi

# Clean up autorun scripts from the target VM
rm -rf /mnt/new/opt/vmcreate

report_progress "REBOOT" "Shutting down VM to boot from converted disk"

# ── Final contract: everything unmounted and synced ──────────────────
# The host powers off after this script exits; if the shutdown budget is
# exceeded it FORCE-STOPS the VM and still reports success — safe only
# because nothing is mounted and all data is flushed here.
progress 100 "clone workflow complete"
sync
cleanup_mounts
echo "clone_disk.sh finalize completed"

fi # ── end finalize ───────────────────────────────────────────────────

exit 0
