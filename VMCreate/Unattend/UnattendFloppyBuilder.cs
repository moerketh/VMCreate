using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace VMCreate
{
    /// <summary>
    /// Creates a virtual floppy disk (VFD) image containing an <c>autounattend.xml</c>
    /// file for unattended Windows installation on Hyper-V Generation 2 VMs.
    ///
    /// For Gen 2 VMs, the floppy drive is not available, so this builder creates
    /// a small ISO image instead. The ISO is attached as a second DVD drive alongside
    /// the Windows installation ISO.
    /// </summary>
    public static class UnattendFloppyBuilder
    {
        /// <summary>
        /// Path to the embedded autounattend.xml template.
        /// </summary>
        private static readonly string UnattendTemplatePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Unattend", "autounattend.xml");

        /// <summary>
        /// Creates a small ISO image containing <c>autounattend.xml</c> for
        /// unattended Windows installation.
        /// </summary>
        /// <param name="outputPath">Path where the ISO file will be written.</param>
        /// <param name="logger">Logger for diagnostic messages.</param>
        /// <returns>The path to the created ISO file.</returns>
        public static string BuildUnattendIso(string outputPath, ILogger logger)
        {
            string unattendContent = File.ReadAllText(UnattendTemplatePath);
            return BuildUnattendIsoFromContent(outputPath, unattendContent, logger);
        }

        /// <summary>
        /// Creates a small ISO image containing the provided <c>autounattend.xml</c>
        /// content for unattended Windows installation.
        /// </summary>
        /// <param name="outputPath">Path where the ISO file will be written.</param>
        /// <param name="unattendXmlContent">The autounattend.xml content to embed.</param>
        /// <param name="logger">Logger for diagnostic messages.</param>
        /// <returns>The path to the created ISO file.</returns>
        public static string BuildUnattendIsoFromContent(string outputPath, string unattendXmlContent, ILogger logger)
        {
            // Create a staging directory with the autounattend.xml file
            string stagingDir = Path.Combine(Path.GetTempPath(), "vmcreate-unattend-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(stagingDir);

            try
            {
                string unattendPath = Path.Combine(stagingDir, "autounattend.xml");
                File.WriteAllText(unattendPath, unattendXmlContent);

                logger.LogInformation("Created autounattend.xml at {Path}", unattendPath);

                // Use oscdimg.exe (Windows ADK) or a fallback approach to create the ISO.
                // For simplicity, we use PowerShell's Mount-DiskImage + copy approach,
                // or we can use the built-in IMAPI COM objects.
                //
                // However, the most reliable cross-machine approach is to use the
                // System.IO.Packaging or a third-party ISO library. Since we want
                // minimal dependencies, we'll use PowerShell to create the ISO.
                string isoPath = CreateIsoWithPowerShell(stagingDir, outputPath, logger);

                logger.LogInformation("Created unattend ISO at {Path}", isoPath);
                return isoPath;
            }
            finally
            {
                // Clean up staging directory
                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, true);
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Creates an ISO image from a directory using PowerShell and Windows IMAPI2.
        /// IMAPI2 is built into all modern Windows versions.
        /// </summary>
        private static string CreateIsoWithPowerShell(string sourceDir, string isoPath, ILogger logger)
        {
            // Ensure the output directory exists
            string isoDir = Path.GetDirectoryName(isoPath);
            if (!string.IsNullOrEmpty(isoDir))
                Directory.CreateDirectory(isoDir);

            // Use IMAPI2 COM objects to create the ISO.
            // Key points about the IMAPI2 API:
            //   - IFsiDirectoryItem.AddTree(sourceDir, includeBaseDir) takes exactly 2 params
            //   - IFileSystemImageResult.ImageStream is an IStream COM object
            //   - PowerShell cannot directly call IStream.Read() on COM objects
            //   - The reliable approach is to use a .NET helper type that wraps IStream
            //     and exposes Read/Write as regular methods
            //
            // We define a small C# type inline via Add-Type that reads an IStream
            // and writes it to a file. This avoids all the PowerShell COM interop pitfalls.
            // Note: The C# type definition uses single-line strings to avoid conflicts
            // between PowerShell here-strings (@"..."@) and C# verbatim strings (@"...").

            string csharpHelper = "using System; using System.IO; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes; " +
                "public static class IStreamHelper { " +
                "public static void WriteIStreamToFile(object comStream, string filePath) { " +
                "var stream = (IStream)comStream; " +
                "using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write); " +
                "var buffer = new byte[32768]; " +
                "var bytesReadPtr = Marshal.AllocHGlobal(4); " +
                "try { " +
                "while (true) { " +
                "stream.Read(buffer, buffer.Length, bytesReadPtr); " +
                "int bytesRead = Marshal.ReadInt32(bytesReadPtr); " +
                "if (bytesRead == 0) break; " +
                "fs.Write(buffer, 0, bytesRead); " +
                "} " +
                "} finally { " +
                "Marshal.FreeHGlobal(bytesReadPtr); " +
                "} " +
                "} " +
                "}";

            string script = $@"
                $sourceDir = '{sourceDir.Replace("'", "''")}'
                $isoPath = '{isoPath.Replace("'", "''")}'

                # Define a C# helper type that can read from IStream and write to a file
                # Use -ErrorAction Ignore to skip if the type is already loaded
                if (-not ([System.Management.Automation.PSTypeName]'IStreamHelper').Type) {{
                    Add-Type -TypeDefinition '{csharpHelper}' -ErrorAction Stop
                }}

                # Create the file system image
                $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
                $fsi.FileSystemsToCreate = 7  # ISO9660 + Joliet + UDF
                $fsi.VolumeName = 'UNATTEND'

                # Add the entire directory tree (2 params: source path, includeBaseDirectory)
                $fsi.Root.AddTree($sourceDir, $false)

                # Create the result image
                $result = $fsi.CreateResultImage()
                $imageStream = $result.ImageStream

                # Use the C# helper to write the IStream to a file
                [IStreamHelper]::WriteIStreamToFile($imageStream, $isoPath)

                Write-Output $isoPath
            ";

            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddScript(script);

            var results = ps.Invoke();

            if (ps.HadErrors)
            {
                string errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Failed to create unattend ISO: {errors}");
            }

            if (!File.Exists(isoPath))
            {
                throw new Exception($"Unattend ISO was not created at expected path: {isoPath}");
            }

            return isoPath;
        }
    }
}