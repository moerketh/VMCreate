using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Disables Wayland session files on the guest VM by moving them to a
    /// <c>disabled/</c> subdirectory, and unblocks Hyper-V kernel modules.
    /// <para>
    /// <b>Ordering is critical:</b> This step must run after display manager
    /// configs and AccountsService overrides are written, because LightDM
    /// and AccountsService read session names from .desktop files.  With
    /// <c>user-session</c> and <c>XSession</c> both pinned to X11 sessions,
    /// the cached Wayland name is irrelevant and disabling the file is safe.
    /// </para>
    /// <para>
    /// Also restores any previously disabled Wayland session files at the
    /// start, so that display managers can resolve session names during
    /// config-read before the files are disabled again.
    /// </para>
    /// <para>
    /// Safe no-op when no Wayland sessions exist.
    /// </para>
    /// <para>
    /// Runs at Order 270, after <see cref="FixAccountsServiceStep"/> (255)
    /// and before <see cref="InstallHypervDaemonsStep"/> (280).
    /// </para>
    /// </summary>
    public class DisableWaylandSessionsStep : ICustomizationStep
    {
        public string Name => "Disable Wayland Sessions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 270;
        public string? ProgressPhaseId => "Sub_DisableWaylandSessions";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Disabling Wayland sessions on VM {VMName}", shell.VmName);

            string script = WaylandScript.Replace("\r\n", "\n");
            await shell.CopyContentAsync(script, "/tmp/disable_wayland.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/disable_wayland.sh && sudo rm -f /tmp/disable_wayland.sh", ct);

            logger.LogInformation("Wayland disable result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string WaylandScript = @"#!/bin/bash
set -o pipefail

# -- Restore previously disabled Wayland sessions --------------------------
# If a prior run moved .desktop files to the disabled/ directory, move them
# back so that display managers can resolve session names during config read.
# This is essential for LightDM, which may have a stale cached session name.
mkdir -p /usr/share/wayland-sessions/disabled 2>/dev/null || true
restored=0
for f in /usr/share/wayland-sessions/disabled/*.desktop; do
    [ -f ""$f"" ] || continue
    base=$(basename ""$f"")
    mv ""$f"" ""/usr/share/wayland-sessions/$base"" 2>/dev/null || true
    restored=1
done
[ $restored -eq 1 ] && echo ""Restored previously disabled Wayland sessions"" || echo ""No previously disabled Wayland sessions to restore""

# Also restore any .desktop.disabled files left by older scripts that
# renamed plasma.desktop -> plasma.desktop.disabled instead of moving to a
# subdirectory.
for f in /usr/share/wayland-sessions/*.desktop.disabled; do
    [ -f ""$f"" ] || continue
    original=""${f%.disabled}""
    mv ""$f"" ""$original"" 2>/dev/null || true
    restored=1
done

# -- NOW disable Wayland sessions -----------------------------------------
# Only after display manager configs and AccountsService overrides are written
# do we move Wayland session files to the disabled/ directory.  This ordering
# is critical because LightDM and AccountsService read session names from
# .desktop files.  With user-session and XSession both pinned to X11 sessions,
# the cached Wayland name is irrelevant and disabling the file is safe.
mkdir -p /usr/share/wayland-sessions/disabled
moved=0
for f in /usr/share/wayland-sessions/*.desktop; do
    [ -f ""$f"" ] || continue
    base=$(basename ""$f"")
    [ -f ""/usr/share/wayland-sessions/disabled/$base"" ] || mv ""$f"" ""/usr/share/wayland-sessions/disabled/$base"" 2>/dev/null || true
    moved=1
done
[ $moved -eq 1 ] && echo ""Wayland sessions disabled"" || echo ""No Wayland sessions found""

# -- Unblock Hyper-V kernel modules -----------------------------------------
# Some converted VMs may have Hyper-V modules blacklisted from a previous
# hypervisor's tools. Remove the blacklist so Hyper-V integration works.
rm -f /etc/modprobe.d/blacklist-hyperv.conf 2>/dev/null || true

echo ""=== Wayland session disable complete ===""
exit 0
";
    }
}
