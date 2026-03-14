using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes VirtualBox Guest Additions from the guest VM post-boot.
    /// Handles three installation methods:
    ///   1. ISO-based: /opt/VBoxGuestAdditions-*/uninstall.sh
    ///   2. Package-based: virtualbox-guest-* deb/rpm packages
    ///   3. Leftover cleanup: kernel modules, mount points, services
    /// Safe no-op when VBox was never installed.
    /// </summary>
    public class RemoveVBoxGuestAdditionsStep : ICustomizationStep
    {
        public string Name => "Remove VirtualBox Guest Additions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 50; // Run early, before timezone/package steps

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Removing VirtualBox Guest Additions from VM {VMName}", shell.VmName);

            // Deploy the removal script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            // Normalize to LF — CopyContentAsync base64-encodes the string as-is,
            // and the C# verbatim literal contains Windows CRLF line endings.
            string script = RemovalScript.Replace("\r\n", "\n");
            await shell.CopyContentAsync(script, "/tmp/remove_vbox.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/remove_vbox.sh; sudo rm -f /tmp/remove_vbox.sh", ct);

            logger.LogInformation("VBox removal result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string RemovalScript = @"#!/bin/bash
set -o pipefail
removed=0

# ── Method 1: ISO-based install (/opt/VBoxGuestAdditions-*/uninstall.sh) ──
for d in /opt/VBoxGuestAdditions-*; do
    if [ -x ""$d/uninstall.sh"" ]; then
        echo ""Uninstalling via $d/uninstall.sh""
        ""$d/uninstall.sh"" 2>&1 || echo ""uninstall.sh exited $? (non-fatal)""
        rm -rf ""$d""
        removed=1
    fi
done

# ── Method 2: Package-based install (apt/dnf/pacman) ──
if command -v dpkg >/dev/null 2>&1; then
    vbox_pkgs=$(dpkg -l 2>/dev/null | grep -oE 'virtualbox-guest-[a-z0-9-]+' | sort -u)
    if [ -n ""$vbox_pkgs"" ]; then
        echo ""Removing VBox packages: $vbox_pkgs""
        DEBIAN_FRONTEND=noninteractive apt-get purge -y $vbox_pkgs 2>&1 || true
        apt-get autoremove -y 2>&1 || true
        removed=1
    fi
elif command -v rpm >/dev/null 2>&1; then
    vbox_pkgs=$(rpm -qa 2>/dev/null | grep -E 'virtualbox-guest|VirtualBox-guest' || true)
    if [ -n ""$vbox_pkgs"" ]; then
        echo ""Removing VBox packages: $vbox_pkgs""
        if command -v dnf >/dev/null 2>&1; then
            dnf remove -y $vbox_pkgs 2>&1 || true
        elif command -v yum >/dev/null 2>&1; then
            yum remove -y $vbox_pkgs 2>&1 || true
        fi
        removed=1
    fi
elif command -v pacman >/dev/null 2>&1; then
    vbox_pkgs=$(pacman -Qq 2>/dev/null | grep virtualbox-guest || true)
    if [ -n ""$vbox_pkgs"" ]; then
        echo ""Removing VBox packages: $vbox_pkgs""
        pacman -Rns --noconfirm $vbox_pkgs 2>&1 || true
        removed=1
    fi
fi

# ── Method 3: Cleanup leftovers ──
for mod in vboxguest vboxsf vboxvideo; do
    if lsmod 2>/dev/null | grep -q ""^$mod ""; then
        echo ""Unloading kernel module: $mod""
        rmmod ""$mod"" 2>/dev/null || true
        removed=1
    fi
done

if command -v dkms >/dev/null 2>&1; then
    dkms_vbox=$(dkms status 2>/dev/null | grep -i virtualbox || true)
    if [ -n ""$dkms_vbox"" ]; then
        echo ""Removing DKMS entries: $dkms_vbox""
        dkms status 2>/dev/null | grep -i virtualbox | while IFS=, read -r name ver rest; do
            mod=$(echo ""$name"" | xargs)
            v=$(echo ""$ver"" | xargs)
            dkms remove ""$mod/$v"" --all 2>/dev/null || true
        done
        removed=1
    fi
fi

for svc in vboxadd vboxadd-service vboxadd-x11; do
    if [ -f ""/etc/systemd/system/${svc}.service"" ] || \
       [ -f ""/lib/systemd/system/${svc}.service"" ] || \
       [ -f ""/usr/lib/systemd/system/${svc}.service"" ]; then
        echo ""Disabling service: $svc""
        systemctl disable ""${svc}.service"" 2>/dev/null || true
        systemctl stop ""${svc}.service"" 2>/dev/null || true
        rm -f ""/etc/systemd/system/${svc}.service"" 2>/dev/null || true
        removed=1
    fi
done

[ -d /mnt/vbox_iso ] && rmdir /mnt/vbox_iso 2>/dev/null && echo ""Removed /mnt/vbox_iso"" || true
rm -rf /usr/lib/virtualbox 2>/dev/null || true
rm -f /usr/bin/VBox* /usr/sbin/VBox* 2>/dev/null || true
rm -rf /var/lib/VBoxGuestAdditions 2>/dev/null || true

[ $removed -eq 0 ] && echo ""No VirtualBox Guest Additions found""
exit 0
";
    }
}
