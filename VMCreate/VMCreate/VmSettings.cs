namespace VMCreate
{
    public class VmSettings
    {
        public string VMName { get; set; }
        public int MemoryInMB { get; set; } = 4096;
        public int CPUCount { get; set; } = 2;
        public bool VirtualizationEnabled { get; set; } = true;
        public int NewDriveSizeInGB { get; set; } = 150;
        public string EnhancedSessionTransportType { get; set; }
        public bool SecureBoot { get; internal set; }
    }
}