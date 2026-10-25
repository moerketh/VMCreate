using System;
using System.IO;
using System.Linq;

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
            ValidateVmName(vmName);

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
        public VmDeploymentPlan WithVmName(string vmName)
        {
            ValidateVmName(vmName);
            return this with { VmName = vmName };
        }

        /// <summary>
        /// Validates a VM name before it is used anywhere: it becomes a host
        /// filesystem path component (VmPathService.GetVirtualHardDiskPath,
        /// per-VM directories, disk and ISO filenames), a PowerShell/WQL value,
        /// and an argument to native tools (vmconnect.exe). The VM name is
        /// seeded from gallery-item names that originate in remote feeds, so it
        /// must never carry path separators, '..' segments, rooted/absolute
        /// forms, reserved device names, or characters invalid in filenames.
        /// </summary>
        /// <exception cref="ArgumentException">The name is unusable.</exception>
        public static void ValidateVmName(string vmName)
        {
            if (string.IsNullOrWhiteSpace(vmName))
                throw new ArgumentException("VM name cannot be empty.", nameof(vmName));

            if (Path.IsPathRooted(vmName) || vmName.Contains('/') || vmName.Contains('\\'))
                throw new ArgumentException(
                    $"VM name '{vmName}' must not contain path separators or be an absolute path.",
                    nameof(vmName));

            // Collapse-then-check: reject any segment that becomes ".." once
            // trimmed of the dots/spaces Windows ignores in path segments.
            foreach (string segment in vmName.Split('/', '\\'))
            {
                if (segment == ".." || segment.TrimEnd('.', ' ') == "..")
                    throw new ArgumentException(
                        $"VM name '{vmName}' must not contain '..' path segments.",
                        nameof(vmName));
            }

            if (vmName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException(
                    $"VM name '{vmName}' contains characters that are invalid in file names.",
                    nameof(vmName));

            // Reserved device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9) —
            // with or without an extension, with or without trailing spaces.
            string stem = vmName.Split('.')[0].TrimEnd(' ');
            if (stem.Length == 3 && (stem.StartsWith("CON", StringComparison.OrdinalIgnoreCase)
                                     || stem.StartsWith("PRN", StringComparison.OrdinalIgnoreCase)
                                     || stem.StartsWith("AUX", StringComparison.OrdinalIgnoreCase)
                                     || stem.StartsWith("NUL", StringComparison.OrdinalIgnoreCase))
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                                          || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                                      && char.IsDigit(stem[3])))
                throw new ArgumentException(
                    $"VM name '{vmName}' collides with a reserved Windows device name.",
                    nameof(vmName));
        }

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
