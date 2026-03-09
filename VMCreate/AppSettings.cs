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
    }
}
