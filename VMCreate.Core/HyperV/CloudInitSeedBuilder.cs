using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;

namespace VMCreate
{
    /// <summary>
    /// Builds a cloud-init NoCloud seed ISO (volume label <c>cidata</c>) for
    /// first-boot customization of cloud images such as the Rocky 10 image
    /// behind the Elastic SIEM gallery item. Replaces the cloning-ISO SSH-key
    /// injection cycle: cloud-init creates the <c>vmcreate</c> automation user
    /// with the deployment's public key, enables passwordless sudo, and grows
    /// the root filesystem to the full disk — so the standard post-boot SSH
    /// machinery works unchanged. Distro-agnostic: no package installs, no
    /// distro-specific groups (see BuildUserData).
    /// </summary>
    public static class CloudInitSeedBuilder
    {
        /// <summary>
        /// Writes a seed ISO containing <c>user-data</c> + <c>meta-data</c>.
        /// </summary>
        /// <param name="outputPath">Destination .iso path.</param>
        /// <param name="vmName">VM name — sanitized into the hostname/instance-id.</param>
        /// <param name="publicKey">OpenSSH public key (single line, no newline).</param>
        /// <param name="logger">Diagnostics logger.</param>
        public static string BuildSeedIso(string outputPath, string vmName, string publicKey, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                throw new ArgumentException("A public SSH key is required for the cloud-init seed.", nameof(publicKey));

            string stagingDir = Path.Combine(Path.GetTempPath(), "vmcreate-cidata-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stagingDir);

            try
            {
                var hostname = SanitizeHostname(vmName);
                // Unique instance-id per deploy: guarantees cloud-init treats
                // this as a first boot even when a disk was re-deployed.
                string instanceId = $"vmcreate-{hostname}-{Guid.NewGuid().ToString("N")[..8]}";

                File.WriteAllText(
                    Path.Combine(stagingDir, "meta-data"),
                    $"instance-id: {instanceId}\nlocal-hostname: {hostname}\n",
                    new UTF8Encoding(false));

                File.WriteAllText(
                    Path.Combine(stagingDir, "user-data"),
                    BuildUserData(publicKey),
                    new UTF8Encoding(false));

                // NoCloud requires the filesystem label to be exactly "cidata".
                // ISO9660 + Joliet ONLY — a UDF descriptor makes blkid report
                // TYPE=udf and ds-identify skips the seed (see Imapi2Interop).
                Imapi2Interop.CreateIsoFromDirectory(
                    stagingDir, outputPath,
                    volumeName: "cidata",
                    FsiFileSystems.ISO9660 | FsiFileSystems.Joliet);

                logger.LogInformation("Created cloud-init NoCloud seed ISO at {Path} for VM {VMName}", outputPath, vmName);
                return outputPath;
            }
            finally
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
                catch { /* best effort */ }
            }
        }

        private static string BuildUserData(string publicKey)
        {
            // Distro-agnostic seed (Rocky 10 today): EL cloud images ship
            // openssh-server preinstalled+enabled and cloud-init configured,
            // so no package installs are needed on first boot — the seed only
            // provisions the automation user and grows the filesystem.
            // NB: the group is "wheel" on EL, "sudo" on Debian-family; putting
            // the user in BOTH would fail group creation on distros missing
            // one of them — cloud-init tolerates a nonexistent group only in
            // its lenient mode, so we rely on the sudo: directive alone
            // (passwordless sudo does not require group membership).
            var sb = new StringBuilder();
            sb.AppendLine("#cloud-config");
            sb.AppendLine("users:");
            sb.AppendLine("  - name: vmcreate");
            sb.AppendLine("    sudo: ALL=(ALL) NOPASSWD:ALL");
            sb.AppendLine("    shell: /bin/bash");
            sb.AppendLine("    lock_passwd: true");
            sb.AppendLine("    ssh_authorized_keys:");
            sb.AppendLine($"      - {publicKey.Trim()}");
            sb.AppendLine("  - default");
            sb.AppendLine("ssh_pwauth: false");
            sb.AppendLine("growpart:");
            sb.AppendLine("  mode: auto");
            sb.AppendLine("  devices: ['/']");
            sb.AppendLine("resize_rootfs: true");
            sb.AppendLine("package_update: false");
            sb.AppendLine("final_message: \"VMCreate cloud-init seed complete after $UPTIME seconds\"");
            return sb.ToString();
        }

        /// <summary>RFC 1123-ish hostname: lowercase letters, digits, dashes.</summary>
        private static string SanitizeHostname(string vmName)
        {
            var chars = vmName.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '-')
                    chars[i] = '-';
            }

            var hostname = new string(chars).Trim('-');
            if (hostname.Length == 0)
                hostname = "vm";
            return hostname.Length <= 63 ? hostname : hostname[..63].Trim('-');
        }
    }
}
