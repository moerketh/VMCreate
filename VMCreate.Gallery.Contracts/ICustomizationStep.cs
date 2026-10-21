using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// A single, self-contained customization step that can be applied to a VM.
    /// Steps declare when they run (<see cref="Phase"/>), whether they apply to the
    /// current gallery item and user selections (<see cref="IsApplicable"/>), and
    /// their execution order (<see cref="Order"/>).
    /// 
    /// Implementations are auto-discovered via assembly scanning and executed by
    /// the orchestration pipeline in <see cref="Order"/> sequence.
    /// </summary>
    public interface ICustomizationStep
    {
        /// <summary>Human-readable name for logging and UI display (e.g. "Sync Timezone").</summary>
        string Name { get; }

        /// <summary>When this step runs relative to the VM lifecycle.</summary>
        CustomizationPhase Phase { get; }

        /// <summary>
        /// True when the step can execute against a chroot of the target
        /// filesystem during the ISO stage (its shell's
        /// <see cref="IGuestShell.Context"/> is
        /// <see cref="GuestExecutionContext.Chroot"/>): it must not rely on a
        /// running systemd, D-Bus, the target's kernel, or any live-session
        /// state — a step whose commands only touch files, packages and
        /// enable-state qualifies. Chroot-capable steps run pre-boot when the
        /// deployment has an ISO stage and post-boot otherwise (the cloud-init
        /// flow), so they must work in BOTH contexts. Defaults to false.
        /// </summary>
        bool SupportsChroot => false;

        /// <summary>
        /// True for verify-semantics steps that must run POST-BOOT even
        /// after completing inside the ISO chroot. Such a step branches on
        /// <see cref="IGuestShell.Context"/>: chroot = do the offline half
        /// (install/configure), live = verify the result (running services,
        /// listeners) — the halves need different systems. The chroot run
        /// still lands in the ledger, but the post-boot pipeline re-runs the
        /// step instead of skipping it. Implies the step also declares
        /// <see cref="SupportsChroot"/>. Defaults to false.
        /// </summary>
        bool RunsAfterChroot => false;

        /// <summary>
        /// The guest OS this step targets. The orchestrator only runs a step whose
        /// <see cref="Platform"/> matches the VM being deployed, so Linux steps never run
        /// over PowerShell Direct and Windows steps never run over SSH. Required — every
        /// step must declare its platform.
        /// </summary>
        StepPlatform Platform { get; }

        /// <summary>
        /// Execution order within the same phase. Lower values run first.
        /// Suggested ranges: 100 = early generic, 200 = package install, 300 = config deploy, 900 = cleanup.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Stable identifier used to correlate runtime progress reports with UI phase cards.
        /// When null or empty, the orchestrator falls back to <see cref="Name"/u003e.
        /// </summary>
        string? ProgressPhaseId { get; }

        /// <summary>
        /// Returns true if this step should execute for the given gallery item and customization options.
        /// Called before <see cref="ExecuteAsync"/u003e — steps that return false are skipped entirely.
        /// Both arguments are nullable: steps must tolerate null (defensive gate for
        /// gallery items that never populated optional fields).
        /// </summary>
        bool IsApplicable(GalleryItem? item, VmCustomizations? customizations);

        /// <summary>
        /// Executes the customization step against the guest VM via the provided shell.
        /// </summary>
        Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct);
    }
}
