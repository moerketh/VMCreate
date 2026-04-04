using System;
using System.IO;

namespace VMCreate
{
    public class VmSettings
    {
        private static readonly string DefaultCloningIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "hyperv-convert.iso");

        public string VMName { get; set; }
        public int MemoryInMB { get; set; } = 4096;
        public int CPUCount { get; set; } = 2;
        public bool VirtualizationEnabled { get; set; } = true;
        public int NewDriveSizeInGB { get; set; } = 150;
        public bool AutoDetectDiskSize { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public bool SecureBoot { get; internal set; }
        public string SecureBootTemplate { get; set; } = "MicrosoftUEFICertificateAuthority";
        public bool ReplacePreviousVm { get; set; }
        public string CloningIsoPath { get; set; } = DefaultCloningIsoPath;
    }
}