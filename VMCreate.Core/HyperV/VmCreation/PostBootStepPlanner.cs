using System;
using System.Collections.Generic;
using System.Linq;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Single source of truth for post-boot step ordering and card identity.
    /// Both the execution pipeline (<see cref="PostBootCustomizationService"/>) and
    /// the deployment UI build their step lists from this planner, so the card
    /// list always matches the order the backend actually runs steps in.
    /// </summary>
    public interface IPostBootStepPlanner
    {
        /// <summary>
        /// All post-boot candidates for the platform, in execution order:
        /// <see cref="ICustomizationStep.Order"/>, then <see cref="ICustomizationStep.Name"/>
        /// (ordinal) as a deterministic tie-break for steps that share an Order.
        /// </summary>
        IReadOnlyList<ICustomizationStep> GetOrderedCandidates(StepPlatform platform);

        /// <summary>
        /// Candidates whose <see cref="ICustomizationStep.IsApplicable"/> is true for the
        /// given item and customizations, in execution order. Like the executor, callers
        /// evaluating this mid-deployment must re-invoke it: step applicability can change
        /// when an earlier step (the Auto RDP resolver) mutates the shared customizations.
        /// </summary>
        IReadOnlyList<ICustomizationStep> GetPlannedSteps(StepPlatform platform, GalleryItem? item, VmCustomizations? customizations);

        /// <summary>
        /// The chroot candidates for this deployment: Linux post-boot steps that
        /// declared <see cref="ICustomizationStep.SupportsChroot"/> and are currently
        /// applicable, in execution order. These run pre-boot inside the ISO stage
        /// when one exists (and post-boot otherwise). Same live-applicability
        /// caveat as <see cref="GetPlannedSteps"/>.
        /// </summary>
        IReadOnlyList<ICustomizationStep> GetPlannedChrootSteps(GalleryItem? item, VmCustomizations? customizations);

        /// <summary>
        /// True when <see cref="GetPlannedChrootSteps"/> would return at least
        /// one step — i.e. the ISO cycle has chroot customization work to run
        /// between its prepare and finalize phases.
        /// </summary>
        bool HasChrootSteps(GalleryItem? item, VmCustomizations? customizations);

        /// <summary>
        /// The chroot candidates that will NOT run again post-boot (i.e.
        /// excluding verify-semantics <c>RunsAfterChroot</c> steps, whose
        /// cards belong in the post-boot list next to their live half).
        /// Used by the deploy UI to decide which cards move into the
        /// Customize phase.
        /// </summary>
        IReadOnlyList<ICustomizationStep> GetChrootOnlySteps(GalleryItem? item, VmCustomizations? customizations);

        /// <summary>
        /// True when the deployment must boot the cloning ISO for a Gen 2
        /// (UEFI) target: pre-boot xrdp is configured or any Linux post-boot
        /// step applies (chroot-capable steps included — they execute inside
        /// the ISO stage). Single source of truth for the strategy AND the
        /// deploy page's dynamic card insertion, so the two can't diverge.
        /// </summary>
        bool NeedsIsoBootCycle(GalleryItem? item, VmCustomizations? customizations);
    }

    /// <inheritdoc/>
    public class PostBootStepPlanner : IPostBootStepPlanner
    {
        /// <summary>
        /// The SSH-restore tail boundary. The disk-image strategy splits the Linux
        /// post-boot pipeline around the hard KVP heartbeat gate at this Order:
        /// everything below runs first, then the gate, then the restore tail.
        /// </summary>
        public const int SshRestoreOrder = 900;

        /// <summary>
        /// The phase ID of the card that represents <paramref name="step"/> on the
        /// deployment page. Uses the step's <see cref="ICustomizationStep.ProgressPhaseId"/>
        /// when set, and a stable name-derived ID otherwise, so every executed step
        /// maps to exactly one card.
        /// </summary>
        public static string CardId(ICustomizationStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            return !string.IsNullOrWhiteSpace(step.ProgressPhaseId)
                ? step.ProgressPhaseId!
                : "Sub_Step_" + step.Name;
        }

        /// <summary>
        /// Static form of <see cref="IPostBootStepPlanner.NeedsIsoBootCycle"/> for
        /// consumers that hold a step collection but not the planner (the deploy
        /// page presenter receives the step dictionary, not DI services).
        /// </summary>
        public static bool NeedsIsoBootCycle(
            GalleryItem? item, VmCustomizations? customizations, IEnumerable<ICustomizationStep> steps)
            => customizations?.ConfigureXrdp == true
               || steps.Any(s => s.Phase == CustomizationPhase.PostBoot
                                 && s.Platform == StepPlatform.Linux
                                 && s.IsApplicable(item, customizations));

        private readonly IEnumerable<ICustomizationStep> _customizationSteps;

        public PostBootStepPlanner(IEnumerable<ICustomizationStep> customizationSteps)
        {
            _customizationSteps = customizationSteps ?? throw new ArgumentNullException(nameof(customizationSteps));
        }

        /// <inheritdoc/>
        public IReadOnlyList<ICustomizationStep> GetOrderedCandidates(StepPlatform platform)
            => _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == platform)
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

        /// <inheritdoc/>
        public IReadOnlyList<ICustomizationStep> GetPlannedSteps(StepPlatform platform, GalleryItem? item, VmCustomizations? customizations)
            => GetOrderedCandidates(platform)
                .Where(s => s.IsApplicable(item, customizations))
                .ToList();

        /// <inheritdoc/>
        public IReadOnlyList<ICustomizationStep> GetPlannedChrootSteps(GalleryItem? item, VmCustomizations? customizations)
            => GetOrderedCandidates(StepPlatform.Linux)
                .Where(s => s.SupportsChroot)
                .Where(s => s.IsApplicable(item, customizations))
                .ToList();

        /// <inheritdoc/>
        public bool HasChrootSteps(GalleryItem? item, VmCustomizations? customizations)
            => GetPlannedChrootSteps(item, customizations).Count > 0;

        /// <inheritdoc/>
        public IReadOnlyList<ICustomizationStep> GetChrootOnlySteps(GalleryItem? item, VmCustomizations? customizations)
            => GetPlannedChrootSteps(item, customizations)
                .Where(s => !s.RunsAfterChroot)
                .ToList();

        /// <inheritdoc/>
        public bool NeedsIsoBootCycle(GalleryItem? item, VmCustomizations? customizations)
            => NeedsIsoBootCycle(item, customizations, _customizationSteps);
    }
}