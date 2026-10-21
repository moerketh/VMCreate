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
    }
}