#!/bin/bash
set -o pipefail

# -- Detect root filesystem type -----------------------------------------
# btrfs is sensitive to sudden I/O resumption after a Hyper-V freeze/thaw
# cycle. When the VM is suspended and resumed, btrfs can stall while
# recovering, causing systemd-journald to block in fsync() and get killed by
# its watchdog. This breaks the RDP transport and leaves sessions stale.
# ext4, xfs, and other filesystems handle post-resume I/O stalls gracefully,
# so disabling suspend is only needed on btrfs.
root_fs=$(findmnt -n -o FSTYPE / 2>/dev/null)

if [ "$root_fs" != "btrfs" ]; then
    echo "Root filesystem is ${root_fs:-unknown} (not btrfs) -- suspend is safe, skipping"
    exit 0
fi

echo "Root filesystem is btrfs -- disabling suspend to prevent journald crash on resume"

# -- Write systemd sleep.conf drop-in ------------------------------------
# Uses a drop-in so we don't modify the distro's main sleep.conf and the
# setting survives package updates. Idempotent -- safe to re-run.
mkdir -p /etc/systemd/sleep.conf.d

cat > /etc/systemd/sleep.conf.d/99-disable-suspend.conf << 'EOF'
[Sleep]
AllowSuspend=no
AllowHibernation=no
AllowHybridSleep=no
AllowSuspendThenHibernate=no
EOF

echo "=== btrfs suspend disable complete ==="
exit 0
