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
        Task RunLinuxPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);

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
    }

    public class PostBootCustomizationService : IPostBootCustomizationService
    {
        private readonly IEnumerable<ICustomizationStep> _customizationSteps;
        private readonly ILogger<PostBootCustomizationService> _logger;

        public PostBootCustomizationService(
            IEnumerable<ICustomizationStep> customizationSteps,
            ILogger<PostBootCustomizationService> logger)
        {
            _customizationSteps = customizationSteps ?? Array.Empty<ICustomizationStep>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool HasLinuxPostBootSteps(GalleryItem item, VmCustomizations customizations)
            => _customizationSteps.Any(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux && s.IsApplicable(item, customizations));

        public bool HasWindowsPostBootSteps(GalleryItem item, VmCustomizations customizations)
            => _customizationSteps.Any(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Windows && s.IsApplicable(item, customizations));

        public async Task RunLinuxPostBootAsync(
            IGuestShell shell,
            VmDeploymentPlan plan,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
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
            var steps = _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux)
                .OrderBy(s => s.Order)
                .ToList();

            if (!steps.Any(s => s.IsApplicable(item, customizations)))
                return;

            int completed = 0;
            int total = steps.Count;
            foreach (var step in steps)
            {
                // Live (just-in-time) applicability: skip steps whose gate
                // changed as a result of an earlier step mutating
                // customizations (the Auto resolver). Skipped steps do not
                // count toward progress — the denominator stays fixed at the
                // initial candidate count so the bar never stalls at <100%
                // when trailing steps are expected to be skipped.
                if (!step.IsApplicable(item, customizations))
                    continue;

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
            var steps = _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Windows)
                .OrderBy(s => s.Order)
                .ToList();

            if (!steps.Any(s => s.IsApplicable(item, customizations)))
                return;

            int completed = 0;
            int total = steps.Count;
            foreach (var step in steps)
            {
                if (!step.IsApplicable(item, customizations))
                    continue;

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
