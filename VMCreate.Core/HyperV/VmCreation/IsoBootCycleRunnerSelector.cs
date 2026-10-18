using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Selects the ISO boot-cycle implementation per deployment from the
    /// <see cref="AppSettings.IsoVsockEnabled"/> flag: the v2 vsock runner
    /// (SSH over the Hyper-V socket channel, logic-free vmcreate-iso 2.x)
    /// or the legacy KVP-progress runner (hyperv-convert-iso 1.x). The flag
    /// defaults off until the v2 cycle validates on both deploy paths; it
    /// dies with the old path's removal.
    /// </summary>
    public sealed class IsoBootCycleRunnerSelector : IIsoBootCycleRunner
    {
        private readonly IIsoBootCycleRunner _legacy;
        private readonly IIsoBootCycleRunner _vsock;
        private readonly bool _vsockEnabled;
        private readonly ILogger<IsoBootCycleRunnerSelector> _logger;

        public IsoBootCycleRunnerSelector(
            IsoBootCycleRunner legacy,
            VsockIsoBootCycleRunner vsock,
            IOptions<AppSettings> settings,
            ILogger<IsoBootCycleRunnerSelector> logger)
        {
            _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
            _vsock = vsock ?? throw new ArgumentNullException(nameof(vsock));
            _vsockEnabled = settings?.Value?.IsoVsockEnabled ?? false;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<IsoBootCycleResult> RunAsync(
            VmCreationContext context,
            int detectedGeneration,
            string mediaPath,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            IIsoBootCycleRunner picked = _vsockEnabled ? _vsock : _legacy;
            _logger.LogInformation("ISO boot-cycle: using {Implementation} (IsoVsockEnabled={Flag})",
                picked.GetType().Name, _vsockEnabled);
            return picked.RunAsync(context, detectedGeneration, mediaPath, customizations, progress, cancellationToken);
        }
    }
}
