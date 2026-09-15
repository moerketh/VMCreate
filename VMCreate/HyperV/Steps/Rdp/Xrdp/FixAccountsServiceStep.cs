using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Fixes AccountsService per-user session overrides on the guest VM.
    /// <para>
    /// AccountsService stores per-user session preferences in
    /// <c>/var/lib/AccountsService/users/&lt;username&gt;</c>.  The
    /// <c>XSession=</c> key takes priority over LightDM's
    /// <c>user-session=</c> and can reference a Wayland session
    /// (e.g. <c>XSession=plasma</c>) that no longer exists after Wayland
    /// is disabled.  This step rewrites or removes any <c>XSession=</c>
    /// that points to a Wayland session, and creates missing entries for
    /// users who haven't logged in yet.
    /// </para>
    /// <para>
    /// Note: Plasma X11 sessions have different names across distros:
    /// Parrot OS / KDE Neon use <c>plasmax11</c> (no hyphen),
    /// Kubuntu / openSUSE use <c>plasma-x11</c> (with hyphen).
    /// This step detects which variant exists at runtime.
    /// </para>
    /// <para>
    /// Must run before <see cref="DisableWaylandSessionsStep"/> so that
    /// LightDM can resolve session names from .desktop files that are
    /// still present when AccountsService is read.
    /// </para>
    /// <para>
    /// Runs at Order 255, after <see cref="DisableKwinCompositingStep"/> (250)
    /// and before <see cref="DisableWaylandSessionsStep"/> (270).
    /// </para>
    /// </summary>
    public class FixAccountsServiceStep : ICustomizationStep
    {
        public string Name => "Fix AccountsService Sessions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 255;
        public string? ProgressPhaseId => "Sub_FixAccountsService";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Fixing AccountsService session overrides on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("fix_accounts.sh");
            await shell.CopyContentAsync(script, "/tmp/fix_accounts.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/fix_accounts.sh && sudo rm -f /tmp/fix_accounts.sh", ct);

            logger.LogInformation("AccountsService fix result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
