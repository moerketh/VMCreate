using System;
using System.IO;

namespace VMCreate
{
    public class VmSettings
    {
        private static readonly string DefaultCloningIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "vmcreate-iso.iso");

        public string VMName { get; set; } = "";
        public int MemoryInMB { get; set; } = 4096;
        public int CPUCount { get; set; } = 4;
        public bool VirtualizationEnabled { get; set; } = true;
        public int NewDriveSizeInGB { get; set; } = 150;
        public bool AutoDetectDiskSize { get; set; }
        public string? EnhancedSessionTransportType { get; set; }
        public bool SecureBoot { get; internal set; }
        public string SecureBootTemplate { get; set; } = "MicrosoftUEFICertificateAuthority";
        public bool ReplacePreviousVm { get; set; }
        public string CloningIsoPath { get; set; } = DefaultCloningIsoPath;

        /// <summary>
        /// Hyper-V virtual switch to connect the VM's NIC to. Null (default)
        /// keeps the deployment's stock target — the Default Switch.
        /// </summary>
        public string? SwitchName { get; set; }
    }
}