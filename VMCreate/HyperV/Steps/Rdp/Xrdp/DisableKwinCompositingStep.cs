using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Disables KWin compositing for all users on the guest VM by writing
    /// <c>~/.config/kwinrc</c> with <c>Enabled=false</c>, <c>Backend=XRender</c>,
    /// and <c>OpenGLIsUnsafe=true</c>.
    /// <para>
    /// KWin's OpenGL compositing does not work through xrdp's software renderer
    /// or on Hyper-V's limited framebuffer. This step also removes cached
    /// KWin output configs (<c>kwinoutputconfig.json</c>) that may reference
    /// invalid display modes.
    /// </para>
    /// <para>
    /// This is belt-and-suspenders with the kwinrc written by
    /// <see cref="FixXrdpStep"/> — that handles per-session kwinrc for xrdp
    /// logins, while this step handles existing user home directories for
    /// LightDM logins.
    /// </para>
    /// <para>
    /// Safe no-op when no user home directories exist.
    /// </para>
    /// <para>
    /// Runs at Order 250, after <see cref="FixXrdpStep"/> (245) and before
    /// <see cref="FixAccountsServiceStep"/> (255).
    /// </para>
    /// </summary>
    public class DisableKwinCompositingStep : ICustomizationStep
    {
        public string Name => "Disable KWin Compositing";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 250;
        public string? ProgressPhaseId => "Sub_DisableKwinCompositing";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Disabling KWin compositing on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("disable_kwin_compositing.sh");
            await shell.CopyContentAsync(script, "/tmp/disable_kwin_compositing.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/disable_kwin_compositing.sh && sudo rm -f /tmp/disable_kwin_compositing.sh", ct);

            logger.LogInformation("KWin compositing disable result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
