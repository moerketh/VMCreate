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

        /// <summary>
        /// Error message from the ISO guest (collected via PowerShell Direct).
        /// When set, the current phase transitions to Failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Full diagnostic log from the ISO guest (journal, service status, dmesg).
        /// Only populated when an error is detected and diagnostics are collected.
        /// </summary>
        public string DiagnosticsLog { get; set; }
    }
}
