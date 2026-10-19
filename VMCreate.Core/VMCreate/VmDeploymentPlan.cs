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
            "VMCreate", "vmcreate-iso.iso");

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
            // Cache path: the vmcreate-iso (v2) file. Kept as a parameter-
            // derived property so existing callers (and tests) that pass a
            // custom CloningIsoPath keep working.
            CloningIsoPath = cloningIsoPath ?? DefaultCloningIsoPath;
            // Resolve the VMCREATE_ISO_PATH dev override ONCE, at plan
            // creation: every consumer (downloader, attach/detach) then
            // sees the same path for the whole deployment instead of
            // re-reading the environment.
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
        /// The ISO path this deployment actually attaches/detaches/downloads
        /// (the vmcreate-iso cache). The <c>VMCREATE_ISO_PATH</c> environment
        /// variable overrides it (dev lever: point a deploy at a locally
        /// built ISO — must exist). Resolved once at plan creation; see
        /// <see cref="IsoPathOverride"/>.
        /// </summary>
        public string EffectiveCloningIsoPath
            => IsoPathOverride ?? CloningIsoPath;

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
