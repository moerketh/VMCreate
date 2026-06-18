namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// The result of preparing media for VM creation. Carries the canonical path
    /// to the prepared media plus any metadata discovered during preparation.
    /// </summary>
    public sealed class MediaPreparationResult
    {
        public MediaPreparationResult(
            string finalMediaPath,
            int vmGeneration,
            long detectedVirtualSizeBytes = 0)
        {
            FinalMediaPath = finalMediaPath;
            VmGeneration = vmGeneration;
            DetectedVirtualSizeBytes = detectedVirtualSizeBytes;
        }

        /// <summary>
        /// The canonical path to the media after preparation (e.g. after move,
        /// rename, or conversion). This is the path callers should use for
        /// attaching the disk to the VM.
        /// </summary>
        public string FinalMediaPath { get; }

        /// <summary>
        /// The detected VM generation for this media (1 for MBR/BIOS,
        /// 2 for UEFI/GPT).
        /// </summary>
        public int VmGeneration { get; }

        /// <summary>
        /// The virtual size of the source disk in bytes, if relevant to the
        /// media type; otherwise 0.
        /// </summary>
        public long DetectedVirtualSizeBytes { get; }
    }
}
