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
        /// (hyperv-convert-iso 1.x). Default off until the v2 cycle
        /// validates on both deploy paths (BIOS clone + customize); flips to
        /// default-on with the old path's removal.
        /// </summary>
        public bool IsoVsockEnabled { get; set; } = false;
    }
}
