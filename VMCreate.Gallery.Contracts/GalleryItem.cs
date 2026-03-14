using System;
using System.IO;

namespace VMCreate
{
    public class GalleryItem
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string ThumbnailUri { get; set; }
        public string DiskUri { get; set; }
        public string SymbolUri { get; set; }
        [Obsolete("Set is no longer needed. Disk files are auto-detected after extraction. " +
                 "Existing values are ignored by the extraction pipeline.")]
        public string ArchiveRelativePath { get; set; }
        public string SecureBoot { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public string Version { get; set; }
        public string LastUpdated { get; set; }

        /// <summary>
        /// Infers the download type from <see cref="DiskUri"/>.
        /// For archives (OVA, ZIP, 7Z) the actual disk type is unknown until extraction;
        /// use <see cref="DiskFileDetector.DetectFileType"/> on the extracted file instead.
        /// </summary>
        public string FileType
        {
            get
            {
                if (string.IsNullOrEmpty(DiskUri))
                    return "Unknown";

                // Peel off compression wrappers to find the real extension.
                // e.g. "image.vmdk.xz" → "image.vmdk" → ".vmdk"
                //      "image.vmdk.gz" → "image.vmdk" → ".vmdk"
                string name = GetFileNameFromUri(DiskUri);
                string ext = Path.GetExtension(name).ToLowerInvariant().TrimStart('.');

                // Strip single-file compression to reach the inner extension
                if (ext is "xz" or "gz" or "bz2" or "lz" or "zst")
                {
                    name = Path.GetFileNameWithoutExtension(name);
                    ext = Path.GetExtension(name).ToLowerInvariant().TrimStart('.');
                }

                return ext switch
                {
                    "vmdk"  => "VMDK",
                    "qcow2" => "QCOW2",
                    "vhdx"  => "VHDX",
                    "vhd"   => "VHD",
                    "iso"   => "ISO",
                    "ova"   => "OVA",
                    "zip" or "7z" or "rar" or "tar" => "Archive",
                    _ => "Other"
                };
            }
        }

        private static string GetFileNameFromUri(string uri)
        {
            // Handle both local file paths and HTTP URIs
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !parsed.IsFile)
                return Path.GetFileName(parsed.LocalPath);
            return Path.GetFileName(uri);
        }

        public string InitialUsername { get; set; }
        public string InitialPassword { get; set; }

        /// <summary>
        /// URL to a checksum file for verifying download integrity.
        /// Supports GNU coreutils format (<c>hash  filename</c>), BSD-style
        /// (<c>SHA256 (filename) = hash</c>), and bare hash (single-line) files.
        /// When set, the downloaded file is verified before extraction.
        /// </summary>
        public string ChecksumUri { get; set; }

        /// <summary>
        /// Inline expected hash of the downloaded file.
        /// Use this instead of <see cref="ChecksumUri"/> when the hash is known at compile time.
        /// </summary>
        public string Checksum { get; set; }

        /// <summary>
        /// Hash algorithm used for verification: "sha256" (default) or "sha512".
        /// Applies to both <see cref="ChecksumUri"/> and <see cref="Checksum"/>.
        /// </summary>
        public string ChecksumAlgorithm { get; set; }

        /// <summary>Category label, e.g. "Security" or "General". Defaults to null (treated as General).</summary>
        public string Category { get; set; }

        /// <summary>When true, this item is surfaced at the very top of the list as officially recommended.</summary>
        public bool IsRecommended { get; set; }



        /// <summary>Returns true when Category is "Security" (case-insensitive).</summary>
        public bool IsSecurity =>
            string.Equals(Category, "Security", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true for pre-installed disk images (VHDX, VHD, VMDK, QCOW2, OVA)
        /// as opposed to ISO installers that require manual installation.
        /// </summary>
        public bool IsPreInstalled
        {
            get
            {
                var ft = FileType;
                return ft != "ISO" && ft != "Unknown" && ft != "Other";
            }
        }

    }
}