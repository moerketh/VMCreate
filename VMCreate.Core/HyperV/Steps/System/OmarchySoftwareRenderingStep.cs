using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Tunes Omarchy's Hyprland for a VM without a GPU. Hyper-V exposes only
    /// <c>hyperv_drm</c> (no render node), so Hyprland and every app render in
    /// software (llvmpipe): transparency, blur, shadows and animations all cost
    /// CPU on every frame, and an RDP session pays for them again in encode.
    /// <para>
    /// Installs a permanent Omarchy toggle flag
    /// (<c>~/.local/state/omarchy/toggles/hypr/vmcreate-hyperv.lua</c>): Omarchy
    /// loads that directory after the user's config and reloads it live, so the
    /// flag beats both Omarchy's defaults and themes, and
    /// <c>omarchy-hyprland-toggle vmcreate-hyperv off</c> removes it. The flag
    /// turns animations, blur and shadows off and makes windows opaque (Omarchy
    /// tags every window <c>0.985 0.96</c>). Measured on an Omarchy 4 VM: a tiled
    /// terminal streaming output cost Hyprland 28% less CPU.
    /// </para>
    /// <para>
    /// Gated on the <c>omarchy</c> tag, independent of the RDP backend (the
    /// console renders in software too). Needs Omarchy's Lua config
    /// (<c>~/.config/hypr/hyprland.lua</c>); older hyprlang-based releases are
    /// left alone. Order 237, as the deploy user inside the post-boot SSH
    /// window, before the graphical session starts (239). Best-effort.
    /// </para>
    /// </summary>
    public class OmarchySoftwareRenderingStep : ICustomizationStep, IStepDisplayInfo
    {
        internal const string FlagFile = "$HOME/.local/state/omarchy/toggles/hypr/vmcreate-hyperv.lua";

        internal const string FlagContent =
            "-- VMCreate: Hyper-V has no GPU, so Hyprland and every app render in software\n" +
            "-- (llvmpipe). Skip work that only looks nice: no animations, no blur or\n" +
            "-- shadows, opaque windows. Remove with: omarchy-hyprland-toggle vmcreate-hyperv off\n" +
            "hl.config({\n" +
            "  animations = { enabled = false },\n" +
            "  decoration = {\n" +
            "    blur = { enabled = false },\n" +
            "    shadow = { enabled = false },\n" +
            "  },\n" +
            "})\n" +
            "o.window({ tag = \"default-opacity\" }, { opacity = \"1 1\" })\n";

        public string Name => "Tune Omarchy for software rendering";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 237;
        public string? ProgressPhaseId => null;

        public string DeployTitle => "Tune Desktop for Hyper-V";
        public string DeployDescription => "Turning off Omarchy animations and transparency (no GPU in the VM)";
        public string DeployIconName => "Desktop24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => item != null && item.HasTag(EnablePasswordlessSudoStep.OmarchyTag);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Base64 keeps the Lua's quotes and newlines out of shell quoting.
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(FlagContent));
            try
            {
                string result = await shell.RunCommandAsync(
                    "if [ ! -f \"$HOME/.config/hypr/hyprland.lua\" ]; then echo OMARCHY_SWRENDER_RESULT=skipped; " +
                    $"else mkdir -p \"$(dirname \"{FlagFile}\")\" && printf '%s' '{b64}' | base64 -d > \"{FlagFile}\" " +
                    "&& echo OMARCHY_SWRENDER_RESULT=ok || echo OMARCHY_SWRENDER_RESULT=failed; fi", ct);

                if (result.Contains("OMARCHY_SWRENDER_RESULT=ok"))
                    logger.LogInformation("Omarchy tuned for software rendering (no animations, opaque windows) on VM {VMName}.", shell.VmName);
                else if (result.Contains("OMARCHY_SWRENDER_RESULT=skipped"))
                    logger.LogInformation("NOTICE: Omarchy on VM {VMName} has no Lua Hyprland config; software-rendering tuning skipped.", shell.VmName);
                else
                    logger.LogWarning("Could not tune Omarchy for software rendering on VM {VMName} — the desktop keeps its animations and transparency: {Result}", shell.VmName, result.Trim());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Omarchy software-rendering tuning failed on VM {VMName} — the deployment continues with the stock look: {Message}",
                    shell.VmName, ex.Message);
            }
        }
    }
}
