using System;
using System.IO;

namespace VMCreate
{
    /// <summary>
    /// Immutable plan for a single VM deployment. Created once from <see cref="VmSettings"/>
    /// just before deployment begins and never mutated during the deployment pipeline,
    /// so the wizard's original settings remain untouched.
    /// </summary>
    public sealed record VmDeploymentPlan
    {
        private static readonly string DefaultCloningIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "hyperv-convert.iso");

        public VmDeploymentPlan(
            string vmName,
            int memoryInMB = 4096,
            int cpuCount = 2,
            bool virtualizationEnabled = true,
            int newDriveSizeInGB = 150,
            bool autoDetectDiskSize = false,
            string? enhancedSessionTransportType = null,
            bool secureBoot = false,
            string? secureBootTemplate = "MicrosoftUEFICertificateAuthority",
            bool replacePreviousVm = false,
            string? cloningIsoPath = null)
        {
            if (string.IsNullOrWhiteSpace(vmName))
                throw new ArgumentException("VM name cannot be empty.", nameof(vmName));

            VmName = vmName;
            MemoryInMB = memoryInMB;
            CpuCount = cpuCount;
            VirtualizationEnabled = virtualizationEnabled;
            NewDriveSizeInGB = newDriveSizeInGB;
            AutoDetectDiskSize = autoDetectDiskSize;
            EnhancedSessionTransportType = enhancedSessionTransportType;
            SecureBoot = secureBoot;
            SecureBootTemplate = secureBootTemplate ?? "MicrosoftUEFICertificateAuthority";
            ReplacePreviousVm = replacePreviousVm;
            CloningIsoPath = cloningIsoPath ?? DefaultCloningIsoPath;
            // Sibling cache file in the same directory: the v2 ISO never
            // collides with the legacy one, and each owns its sidecar.
            CloningIsoV2Path = Path.Combine(
                Path.GetDirectoryName(CloningIsoPath) ?? ".",
                "vmcreate-iso-v2.iso");
            // Resolve the VMCREATE_ISO_PATH dev override ONCE, at plan
            // creation: every consumer (downloader, attach/detach, the
            // selector's interlock) then sees the same path for the whole
            // deployment instead of re-reading the environment.
            IsoPathOverride = ResolveIsoPathOverride();
        }

        private static string? ResolveIsoPathOverride()
        {
            string? overridePath = Environment.GetEnvironmentVariable("VMCREATE_ISO_PATH");
            return !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
                ? overridePath
                : null;
        }

        /// <summary>
        /// Creates a new plan from the supplied mutable settings.
        /// </summary>
        public static VmDeploymentPlan FromSettings(VmSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            return new VmDeploymentPlan(
                settings.VMName,
                settings.MemoryInMB,
                settings.CPUCount,
                settings.VirtualizationEnabled,
                settings.NewDriveSizeInGB,
                settings.AutoDetectDiskSize,
                settings.EnhancedSessionTransportType,
                settings.SecureBoot,
                settings.SecureBootTemplate,
                settings.ReplacePreviousVm,
                settings.CloningIsoPath);
        }

        /// <summary>
        /// Returns a copy of this plan with the VM name replaced by the given value.
        /// Used by the orchestrator to append the deployment timestamp without mutating
        /// the original plan or the wizard's settings.
        /// </summary>
        public VmDeploymentPlan WithVmName(string vmName) => this with { VmName = vmName };

        public string VmName { get; init; }
        public int MemoryInMB { get; init; }
        public int CpuCount { get; init; }
        public bool VirtualizationEnabled { get; init; }
        public int NewDriveSizeInGB { get; init; }
        public bool AutoDetectDiskSize { get; init; }
        public string? EnhancedSessionTransportType { get; init; }
        public bool SecureBoot { get; init; }
        public string SecureBootTemplate { get; init; }
        public bool ReplacePreviousVm { get; init; }
        public string CloningIsoPath { get; init; }

        /// <summary>
        /// ISO vsock mode (vmcreate-iso 2.x) drives the boot-cycle over the
        /// Hyper-V socket channel. Stamped from <see cref="AppSettings"/> by
        /// <see cref="CreateVM.BuildPlan"/> when the plan is built — the
        /// one source of truth per deployment; VmSettings deliberately has
        /// no copy so it can never claim to work and be ignored.
        /// </summary>
        public bool IsoVsockEnabled { get; init; }

        /// <summary>
        /// Separate cache file for the v2 ISO: the sidecar version file
        /// pairs with its ISO, so a flag toggle never re-downloads and the
        /// two generations never overwrite each other.
        /// </summary>
        public string CloningIsoV2Path { get; init; }

        /// <summary>
        /// The ISO path this deployment actually attaches/detaches/downloads:
        /// the v2 cache when the flag is on, the legacy cache otherwise.
        /// The <c>VMCREATE_ISO_PATH</c> environment variable overrides both
        /// (dev lever: point a deploy at a locally built ISO — must exist).
        /// Resolved once at plan creation; see <see cref="IsoPathOverride"/>.
        /// </summary>
        public string EffectiveCloningIsoPath
            => IsoPathOverride ?? (IsoVsockEnabled ? CloningIsoV2Path : CloningIsoPath);

        /// <summary>
        /// The resolved <c>VMCREATE_ISO_PATH</c> override, or null when the
        /// variable is unset or stale (file missing). Frozen at plan creation
        /// so one deployment never sees two different ISO paths.
        /// </summary>
        public string? IsoPathOverride { get; init; }

        /// <summary>True when the dev override is active for this plan.</summary>
        public bool IsIsoPathOverridden => IsoPathOverride != null;
    }
}
