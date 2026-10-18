using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Selects the ISO boot-cycle implementation per deployment from the
    /// plan's <see cref="VmDeploymentPlan.IsoVsockEnabled"/> flag (stamped
    /// from <see cref="AppSettings"/> when the plan is built — one source
    /// of truth per deployment): the v2 vsock runner
    /// (SSH over the Hyper-V socket channel, logic-free vmcreate-iso 2.x)
    /// or the legacy KVP-progress runner (hyperv-convert-iso 1.x). The flag
    /// defaults off until the v2 cycle validates on both deploy paths; it
    /// dies with the old path's removal.
    /// </summary>
    public sealed class IsoBootCycleRunnerSelector : IIsoBootCycleRunner
    {
        private readonly IIsoBootCycleRunner _legacy;
        private readonly IIsoBootCycleRunner _vsock;
        private readonly ILogger<IsoBootCycleRunnerSelector> _logger;

        public IsoBootCycleRunnerSelector(
            ILegacyIsoBootCycleRunner legacy,
            IVsockIsoBootCycleRunner vsock,
            ILogger<IsoBootCycleRunnerSelector> logger)
        {
            _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
            _vsock = vsock ?? throw new ArgumentNullException(nameof(vsock));
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
            // The flag comes from the PLAN — the same single source the
            // downloader and strategy used — never re-read from AppSettings.
            bool vsockEnabled = context.Plan.IsoVsockEnabled;

            // Safety interlock: v2 needs a vmcreate-iso 2.x guest (vsock
            // sshd). If the flag is on but the cached ISO predates the v2
            // downloader, booting the old hyperv-convert-iso would wait
            // 120 s for a vsock listener that never appears. The version
            // sidecar tells us which world the cache is in. SKIPPED when
            // the VMCREATE_ISO_PATH override is active on this plan: a dev
            // pointing at a locally built ISO knows what they built — no
            // sidecar exists to read.
            if (vsockEnabled && !context.Plan.IsIsoPathOverridden)
            {
                string? isoVersion = TryReadCachedIsoVersion(context.Plan.EffectiveCloningIsoPath);
                if (isoVersion is null || !isoVersion.StartsWith("2.", StringComparison.Ordinal))
                {
                    string message =
                        $"IsoVsockEnabled is set but the cloning ISO is version {isoVersion ?? "(no version sidecar)"} (need 2.x); " +
                        "using the legacy KVP runner. The vsock runner becomes usable once the downloader fetches vmcreate-iso 2.x.";
                    _logger.LogWarning("{Message}", message);
                    // Also the deployment log: during W6 validation someone
                    // can enable the flag, get a clean deploy and never
                    // notice the legacy runner did the work.
                    context.Logger?.LogWarning($"ISO boot-cycle: {message}");
                    return _legacy.RunAsync(context, detectedGeneration, mediaPath, customizations, progress, cancellationToken);
                }
            }

            IIsoBootCycleRunner picked = vsockEnabled ? _vsock : _legacy;
            _logger.LogInformation("ISO boot-cycle: using {Implementation} (IsoVsockEnabled={Flag})",
                picked.GetType().Name, vsockEnabled);
            return picked.RunAsync(context, detectedGeneration, mediaPath, customizations, progress, cancellationToken);
        }

        /// <summary>Reads the &lt;iso&gt;.version sidecar written by the
        /// downloader. Null when absent/unreadable (= treat as not-2.x).</summary>
        private static string? TryReadCachedIsoVersion(string? isoPath)
        {
            if (string.IsNullOrEmpty(isoPath)) return null;
            try
            {
                string versionPath = isoPath + ".version";
                return File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
