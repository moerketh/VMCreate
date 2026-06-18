namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Result of resolving a disk image's Hyper-V generation and size requirements.
    /// </summary>
    public sealed class VmGenerationResolution
    {
        public VmGenerationResolution(
            int vmGeneration,
            string partitionScheme,
            long detectedVirtualSizeBytes,
            int newDriveSizeInGB)
        {
            VmGeneration = vmGeneration;
            PartitionScheme = partitionScheme;
            DetectedVirtualSizeBytes = detectedVirtualSizeBytes;
            NewDriveSizeInGB = newDriveSizeInGB;
        }

        /// <summary>
        /// The detected VM generation for this media (1 for MBR/BIOS, 2 for UEFI/GPT).
        /// </summary>
        public int VmGeneration { get; }

        /// <summary>
        /// The detected partition scheme name (e.g. "GPT" or "MBR").
        /// </summary>
        public string PartitionScheme { get; }

        /// <summary>
        /// The virtual size of the source disk in bytes, if relevant; otherwise 0.
        /// </summary>
        public long DetectedVirtualSizeBytes { get; }

        /// <summary>
        /// The effective new drive size in GB. When auto-detection is enabled this is the
        /// computed value; otherwise it is the supplied value (after validation).
        /// </summary>
        public int NewDriveSizeInGB { get; }
    }
}
