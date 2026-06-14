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
        /// </summary>
        bool IsApplicable(GalleryItem item, VmCustomizations customizations);

        /// <summary>
        /// Executes the customization step against the guest VM via the provided shell.
        /// </summary>
        Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct);
    }
}
