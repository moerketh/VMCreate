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

                // Strip compression and archive wrappers to reach the inner extension.
                // e.g. "image.vmdk.xz" → ".vmdk", "image.vhdx.zip" → ".vhdx"
                // Only unwraps when a recognized disk extension is inside;
                // a plain "archive.zip" with no inner disk extension stays as Archive.
                if (ext is "xz" or "gz" or "bz2" or "lz" or "zst" or "zip")
                {
                    string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name))
                                       .ToLowerInvariant().TrimStart('.');
                    if (inner is "vmdk" or "qcow2" or "vhdx" or "vhd" or "iso" or "ova")
                    {
                        name = Path.GetFileNameWithoutExtension(name);
                        ext = inner;
                    }
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

        /// <summary>
        /// True when the image is a native Hyper-V disk that needs no conversion,
        /// no customization ISO, and no post-boot Linux steps (e.g. Windows dev environments).
        /// Auto-detected from the DiskUri (".HyperV." in the filename, or a .zip
        /// hosted on download.microsoft.com) or set explicitly.
        /// </summary>
        public bool IsNativeHyperV
        {
            get => _isNativeHyperV
                   || (!string.IsNullOrEmpty(DiskUri) && IsNativeHyperVUri(DiskUri));
            set => _isNativeHyperV = value;
        }
        private bool _isNativeHyperV;

        private static bool IsNativeHyperVUri(string uri)
        {
            if (uri.IndexOf(".HyperV.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Microsoft's own gallery only ships native Hyper-V zips
            if (uri.IndexOf("download.microsoft.com/download/", StringComparison.OrdinalIgnoreCase) >= 0
                && uri.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return true;

            // Canonical ships native Hyper-V VHDX images for Ubuntu
            if (uri.IndexOf("partner-images.canonical.com/hyper-v/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// True when the downloaded file is wrapped in a compression or archive
        /// format that must be extracted before the disk can be used.
        /// Unlike <see cref="FileType"/> (which reports the inner disk type),
        /// this checks the outermost extension of the URI.
        /// </summary>
        public bool NeedsExtraction
        {
            get
            {
                if (string.IsNullOrEmpty(DiskUri))
                    return false;

                string name = GetFileNameFromUri(DiskUri);
                string ext = Path.GetExtension(name).ToLowerInvariant().TrimStart('.');
                return ext is "xz" or "gz" or "bz2" or "lz" or "zst"
                           or "zip" or "7z" or "rar" or "tar" or "ova";
            }
        }

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