using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Runs post-boot customization steps against a live guest shell.
    /// </summary>
    public interface IPostBootCustomizationService
    {
        /// <summary>
        /// Returns true if any Linux post-boot step applies to the selected configuration.
        /// </summary>
        bool HasLinuxPostBootSteps(GalleryItem item, VmCustomizations customizations);

        /// <summary>
        /// Returns true if any Windows post-boot step applies to the selected configuration.
        /// </summary>
        bool HasWindowsPostBootSteps(GalleryItem item, VmCustomizations customizations);

        /// <summary>
        /// Runs Linux post-boot steps over SSH in order and reports progress.
        /// </summary>
        /// <param name="chrootCompleted">
        /// Names of steps that already ran inside the ISO stage's chroot
        /// (see <see cref="VmCreationContext.ChrootCompletedSteps"/>); those
        /// steps are skipped here. Null/empty when no ISO stage ran (the
        /// cloud-init flow runs every step post-boot).
        /// </param>
        Task RunLinuxPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken,
            int upToOrderExclusive = int.MaxValue,
            int fromOrderInclusive = 0,
            ISet<string>? chrootCompleted = null);

        /// <summary>
        /// Runs Windows post-boot steps over PowerShell Direct in order and reports progress.
        /// </summary>
        Task RunWindowsPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// Runs the deployment's chroot-capable Linux steps inside the ISO
        /// stage's chroot (between the scenario's prepare and finalize
        /// phases), in the same Order the post-boot pipeline would use.
        /// Reports each step with <c>Phase=Customize</c> so the splash and
        /// the deploy page light up the pre-boot cards. Every completed
        /// step's name is recorded in <paramref name="chrootCompleted"/>
        /// so the later post-boot run skips it.
        /// </summary>
        Task RunLinuxChrootAsync(
            ChrootGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            ISet<string> chrootCompleted,
            CancellationToken cancellationToken);
    }

    public class PostBootCustomizationService : IPostBootCustomizationService
    {
        private readonly IPostBootStepPlanner _planner;
        private readonly ILogger<PostBootCustomizationService> _logger;
        public PostBootCustomizationService(
            IPostBootStepPlanner planner,
            ILogger<PostBootCustomizationService> logger)
        {
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Convenience overload for direct construction (tests, hosts that build
        /// the service by hand): wraps the step set in the shared planner so
        /// both always order identically.
        /// </summary>
        public PostBootCustomizationService(
            IEnumerable<ICustomizationStep> customizationSteps,
            ILogger<PostBootCustomizationService> logger)
            : this(new PostBootStepPlanner(customizationSteps ?? throw new ArgumentNullException(nameof(customizationSteps))), logger)
        {
        }

        public bool HasLinuxPostBootSteps(GalleryItem item, VmCustomizations customizations)
            => _planner.GetPlannedSteps(StepPlatform.Linux, item, customizations).Any();

        public bool HasWindowsPostBootSteps(GalleryItem item, VmCustomizations customizations)
            => _planner.GetPlannedSteps(StepPlatform.Windows, item, customizations).Any();

        public async Task RunLinuxPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken,
            int upToOrderExclusive = int.MaxValue,
            int fromOrderInclusive = 0,
            ISet<string>? chrootCompleted = null)
        {
            // Phase/platform filtering is a static property of each step, but
            // IsApplicable is deliberately evaluated LIVE inside the loop:
            // RdpBackend.Auto is rewritten to a concrete backend mid-run by
            // AutoRdpBackendResolveStep (Order 232), and the
            // backend-dependent steps (Lamco install 235, autologin 238,
            // xrdp block 240-270) must react to the resolved backend — both
            // the newly-applicable Lamco side and the suddenly-inapplicable
            // xrdp side. A pre-loop snapshot of IsApplicable would freeze the
            // Auto state into every later step's decision and defeat the
            // whole point of runtime resolution. HasLinuxPostBootSteps
            // (called before SSH exists) still uses the same IsApplicable,
            // where Auto counts as "has steps" because the resolver itself
            // is always applicable under Auto.
            var steps = _planner.GetOrderedCandidates(StepPlatform.Linux)
                .Where(s => s.Order < upToOrderExclusive && s.Order >= fromOrderInclusive)
                .ToList();

            if (!steps.Any(s => s.IsApplicable(item, customizations)))
                return;

            int completed = 0;
            int total = steps.Count;
            foreach (var step in steps)
            {
                // Live (just-in-time) applicability: skip steps whose gate
                // changed as a result of an earlier step mutating
                // customizations (the Auto resolver), and steps that already
                // ran inside the ISO chroot. A skipped candidate leaves the
                // denominator — it counts neither as done nor as pending —
                // so the bar tracks progress across the steps that will
                // actually run and walks the full 0→100% range. total is
                // always >= 1 at the report below because a report only
                // happens for a step that is about to run.
                if (!step.IsApplicable(item, customizations)
                    || (chrootCompleted?.Contains(step.Name) ?? false))
                {
                    total--;
                    continue;
                }

                _logger.LogInformation("Running Linux post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                progress.Report(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.PostBoot,
                    ProgressPercentage = (int)((double)completed / total * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, customizations, _logger, cancellationToken);

                completed++;
                _logger.LogInformation("Completed Linux post-boot step: {StepName}", step.Name);
            }

            progress.Report(CreateVMProgressInfo.ForProgress(VmDeploymentPhase.PostBoot, 100));
        }

        public async Task RunLinuxChrootAsync(
            ChrootGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            ISet<string> chrootCompleted,
            CancellationToken cancellationToken)
        {
            // Same live-IsApplicable semantics as RunLinuxPostBootAsync: the
            // Auto RDP resolver runs chroot-side too (its detection is a
            // static file check) and mutates RdpBackend mid-run, so every
            // later step's applicability must be evaluated just-in-time.
            var steps = _planner.GetOrderedCandidates(StepPlatform.Linux)
                .Where(s => s.SupportsChroot)
                .ToList();

            if (!steps.Any(s => s.IsApplicable(item, customizations)))
                return;

            int completed = 0;
            int total = steps.Count;
            foreach (var step in steps)
            {
                if (!step.IsApplicable(item, customizations))
                {
                    total--;
                    continue;
                }

                _logger.LogInformation("Running Linux chroot step: {StepName} (order {Order})", step.Name, step.Order);
                shell.CurrentStepLabel = step.Name;
                progress.Report(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.Customize,
                    ProgressPercentage = (int)((double)completed / total * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, customizations, _logger, cancellationToken);

                chrootCompleted.Add(step.Name);
                completed++;
                _logger.LogInformation("Completed Linux chroot step: {StepName}", step.Name);
            }

            shell.CurrentStepLabel = null;
            progress.Report(CreateVMProgressInfo.ForProgress(VmDeploymentPhase.Customize, 100));
        }

        public async Task RunWindowsPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            // Same live-IsApplicable semantics as RunLinuxPostBootAsync: no
            // Windows step mutates customizations today, but the service must
            // not carry two different applicability models.
            var steps = _planner.GetOrderedCandidates(StepPlatform.Windows).ToList();

            if (!steps.Any(s => s.IsApplicable(item, customizations)))
                return;

            int completed = 0;
            int total = steps.Count;
            foreach (var step in steps)
            {
                // Same live applicability + skip-shrink semantics as the
                // Linux loop: skipped candidates leave the denominator so
                // the bar covers the steps that actually run. The service
                // must not carry two different progress models.
                if (!step.IsApplicable(item, customizations))
                {
                    total--;
                    continue;
                }

                _logger.LogInformation("Running Windows post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                progress.Report(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.PostBoot,
                    ProgressPercentage = (int)((double)completed / total * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, customizations, _logger, cancellationToken);

                try
                {
                    await shell.WaitForReadyAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VM {VMName} may not be ready after step {StepName}, attempting to continue", plan.VmName, step.Name);
                }

                completed++;
                _logger.LogInformation("Completed Windows post-boot step: {StepName}", step.Name);
            }

            progress.Report(CreateVMProgressInfo.ForProgress(VmDeploymentPhase.PostBoot, 100));
        }
    }
}
