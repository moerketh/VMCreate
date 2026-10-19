namespace VMCreate
{
    /// <summary>
    /// Application-wide configuration settings.
    /// Bound via IOptions&lt;AppSettings&gt; from the DI container.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Path to the qemu-img.exe executable for disk conversion.
        /// Default: C:\Program Files\qemu\qemu-img.exe
        /// </summary>
        public string QemuImgPath { get; set; } = @"C:\Program Files\qemu\qemu-img.exe";

        /// <summary>
        /// Temporary directory for extracted VM files.
        /// Default: %TEMP%\VMExtracted
        /// </summary>
        public string ExtractPath { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VMExtracted");

        /// <summary>
        /// Base directory for SSH key storage.
        /// Default: %LocalAppData%\VMCreate\ssh
        /// </summary>
        public string SshDirectory { get; set; } = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "ssh");

        /// <summary>
        /// Cache directory for gallery items.
        /// Default: %LocalAppData%\VMCreate\
        /// </summary>
        public string GalleryCachePath { get; set; } = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "gallery-cache.json");

        /// <summary>
        /// Path of the persisted <see cref="UserPreferences"/> file.
        /// Default: %LocalAppData%\VMCreate\settings.json
        /// </summary>
        public string UserPreferencesPath { get; set; } = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "settings.json");

        /// <summary>
        /// Drive the ISO boot-cycle over the Hyper-V socket channel
        /// (vmcreate-iso 2.x) instead of the legacy KVP progress protocol
        /// (hyperv-convert-iso 1.x). Default ON since 2026-10-19: both
        /// deploy shapes (GPT customize + MBR BIOS-clone) passed the
        /// flip-gate through the real CLI pipeline. The legacy path dies
        /// with its removal commit; until then VMCREATE_ISO_VSOCK=0 forces
        /// it for A/B testing.
        /// </summary>
        public bool IsoVsockEnabled { get; set; } = true;
    }
}
