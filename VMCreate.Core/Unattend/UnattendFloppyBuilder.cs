using Microsoft.Extensions.Logging;
using System;
using System.IO;

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

                // Build the ISO with the in-box IMAPI2FS COM component via
                // direct interop (see Imapi2Interop). IMAPI2 is built into
                // all modern Windows versions, so this needs no external
                // tools or bundled dependencies.
                Imapi2Interop.CreateIsoFromDirectory(stagingDir, outputPath, volumeName: "UNATTEND");

                logger.LogInformation("Created unattend ISO at {Path}", outputPath);
                return outputPath;
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
    }
}