namespace VMCreate
{
    public class CreateVMProgressInfo
    {
        public string Phase { get; set; }
        public string URI { get; set; }
        public int ProgressPercentage { get; set; }
        public double DownloadSpeed { get; set; }

        /// <summary>
        /// Set to "1" (MBR) or "2" (GPT) when partition detection completes.
        /// Allows the Deploy page to insert MBR-specific phase cards dynamically.
        /// </summary>
        public string DetectedGeneration { get; set; }
    }
}
