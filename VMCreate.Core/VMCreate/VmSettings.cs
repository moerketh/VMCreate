using System;
using System.IO;

namespace VMCreate
{
    public class VmSettings
    {
        private static readonly string DefaultCloningIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "hyperv-convert.iso");

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

        /// <summary>Drives the ISO boot-cycle over vsock (vmcreate-iso 2.x).
        /// Mirrors <see cref="AppSettings.IsoVsockEnabled"/>; default off
        /// until the v2 cycle validates on both deploy paths.</summary>
        public bool IsoVsockEnabled { get; set; }
    }
}