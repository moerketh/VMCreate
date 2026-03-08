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
        public string LogoUri { get; set; }
        public string DiskUri { get; set; }
        public string SymbolUri { get; set; }
        public string ArchiveRelativePath { get; set; }
        public string SecureBoot { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public string Version { get; set; }
        public string LastUpdated { get; set; }

        public string FileType
        {
            get
            {
                string path = !string.IsNullOrEmpty(ArchiveRelativePath) ? ArchiveRelativePath : DiskUri;
                if (string.IsNullOrEmpty(path))
                    return "Unknown";

                string extension = Path.GetExtension(path).ToLower().TrimStart('.');
                string baseName = Path.GetFileNameWithoutExtension(path);

                // Handle common compression extensions
                switch (extension.ToLower())
                {
                    case "7z":
                    case "zip":
                    case "gz":
                        extension = ArchiveRelativePath != null
                        ?  Path.GetExtension(ArchiveRelativePath).ToLower().TrimStart('.')
                        :  Path.GetExtension(baseName).ToLower().TrimStart('.');
                        break;
                    case "xz":                    
                        extension = Path.GetExtension(baseName).ToLower().TrimStart('.');
                        break;
                    case "tar.gz":
                        extension = Path.GetExtension(Path.GetFileNameWithoutExtension(baseName)).ToLower().TrimStart('.');
                        break;
                    default:
                        // No compression extension, use the original extension
                        break;
                }

                // Determine file type based on the final extension
                switch (extension)
                {
                    case "ova":
                        return "OVA";
                    case "iso":
                        return "ISO";
                    case "vmdk":
                        return "VMDK";
                    case "vhd":
                        return "VHD";
                    case "vhdx":
                        return "VHDX";
                    case "qcow2":
                        return "QCOW2";
                    default:
                        return "Other";
                }
            }
        }

        public string InitialUsername { get; set; }
        public string InitialPassword { get; set; }

        /// <summary>Category label, e.g. "Security" or "General". Defaults to null (treated as General).</summary>
        public string Category { get; set; }

        /// <summary>When true, this item is surfaced at the very top of the list as officially recommended.</summary>
        public bool IsRecommended { get; set; }

        /// <summary>
        /// X (Twitter) handle for the project, e.g. "kalilinux".
        /// When set, the gallery will resolve the profile photo at runtime
        /// and use it as the list icon (SymbolUri) if SymbolUri is not already populated.
        /// </summary>
        public string XHandle { get; set; }

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