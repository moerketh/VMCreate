using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

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

    }
}