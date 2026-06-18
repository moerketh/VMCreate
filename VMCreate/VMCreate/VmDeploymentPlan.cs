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
    }
}
