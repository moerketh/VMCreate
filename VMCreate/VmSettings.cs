namespace VMCreate
{
    public class VmSettings
    {
        public string VMName { get; set; }
        public int MemoryInMB { get; set; }
        public int CPUCount { get; set; }
        public bool VirtualizationEnabled { get; set; }
        public int NewDriveSizeInGB { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public bool SecureBoot { get; internal set; }
    }
}