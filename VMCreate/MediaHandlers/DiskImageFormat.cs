namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Supported disk image / media formats used by the media-handler pipeline.
    /// </summary>
    public enum DiskImageFormat
    {
        Unknown,
        Iso,
        Vhdx,
        Vhd,
        Vmdk,
        Qcow2,
        Ova,
        Archive,
        Other
    }

    public static class DiskImageFormatExtensions
    {
        /// <summary>
        /// Maps a file extension (with or without leading dot) to a <see cref="DiskImageFormat"/>.
        /// </summary>
        public static DiskImageFormat FromExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return DiskImageFormat.Other;

            string ext = extension.TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "vmdk" => DiskImageFormat.Vmdk,
                "qcow2" => DiskImageFormat.Qcow2,
                "vhdx" => DiskImageFormat.Vhdx,
                "vhd" => DiskImageFormat.Vhd,
                "iso" => DiskImageFormat.Iso,
                "ova" => DiskImageFormat.Ova,
                "zip" or "7z" or "rar" or "tar" => DiskImageFormat.Archive,
                _ => DiskImageFormat.Other
            };
        }

        /// <summary>
        /// Returns the historical uppercase display string for this format (e.g. "ISO", "VMDK").
        /// Used where <see cref="DiskImageFormat"/> must be shown as text.
        /// </summary>
        public static string ToDisplayString(this DiskImageFormat format)
            => format switch
            {
                DiskImageFormat.Iso => "ISO",
                DiskImageFormat.Vhdx => "VHDX",
                DiskImageFormat.Vhd => "VHD",
                DiskImageFormat.Vmdk => "VMDK",
                DiskImageFormat.Qcow2 => "QCOW2",
                DiskImageFormat.Ova => "OVA",
                DiskImageFormat.Archive => "Archive",
                DiskImageFormat.Unknown => "Unknown",
                _ => "Other"
            };
    }
}
