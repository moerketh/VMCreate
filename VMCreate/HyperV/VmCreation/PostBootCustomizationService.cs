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
            VmSettings vmSettings,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// Runs Windows post-boot steps over PowerShell Direct in order and reports progress.
        /// </summary>
        Task RunWindowsPostBootAsync(
            IGuestShell shell,
            VmSettings vmSettings,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);
    }

    internal class PostBootCustomizationService : IPostBootCustomizationService
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
            VmSettings vmSettings,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            var steps = _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux && s.IsApplicable(item, customizations))
                .OrderBy(s => s.Order)
                .ToList();

            if (steps.Count == 0)
                return;

            int completed = 0;
            foreach (var step in steps)
            {
                _logger.LogInformation("Running Linux post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                progress.Report(new CreateVMProgressInfo
                {
                    Phase = "PostBoot",
                    ProgressPercentage = (int)((double)completed / steps.Count * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, customizations, _logger, cancellationToken);

                completed++;
                _logger.LogInformation("Completed Linux post-boot step: {StepName}", step.Name);
            }

            progress.Report(new CreateVMProgressInfo { Phase = "PostBoot", ProgressPercentage = 100 });
        }

        public async Task RunWindowsPostBootAsync(
            IGuestShell shell,
            VmSettings vmSettings,
            GalleryItem item,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            var steps = _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Windows && s.IsApplicable(item, customizations))
                .OrderBy(s => s.Order)
                .ToList();

            if (steps.Count == 0)
                return;

            int completed = 0;
            foreach (var step in steps)
            {
                _logger.LogInformation("Running Windows post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                progress.Report(new CreateVMProgressInfo
                {
                    Phase = "PostBoot",
                    ProgressPercentage = (int)((double)completed / steps.Count * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, customizations, _logger, cancellationToken);

                // After each step, the VM may have rebooted. Re-establish the shell connection.
                try
                {
                    await shell.WaitForReadyAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VM {VMName} may not be ready after step {StepName}, attempting to continue", vmSettings.VMName, step.Name);
                }

                completed++;
                _logger.LogInformation("Completed Windows post-boot step: {StepName}", step.Name);
            }

            progress.Report(new CreateVMProgressInfo { Phase = "PostBoot", ProgressPercentage = 100 });
        }
    }
}
