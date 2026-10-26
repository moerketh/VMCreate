using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VMCreate.Cidata;

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
        /// Writes a seed ISO containing <c>user-data</c> + <c>meta-data</c>,
        /// plus a NoCloud <c>network-config</c> (v2) when
        /// <paramref name="staticNetwork"/> requests static addressing.
        /// </summary>
        /// <param name="outputPath">Destination .iso path.</param>
        /// <param name="vmName">VM name — sanitized into the hostname/instance-id.</param>
        /// <param name="publicKey">OpenSSH public key (single line, no newline).</param>
        /// <param name="logger">Diagnostics logger.</param>
        /// <param name="customUserData">
        /// Optional extra user content (a shell script, <c>#cloud-config</c>
        /// document, <c>#include</c> list, …) merged into the seed ahead of the
        /// built-in block. Null/empty keeps the seed a plain single-document
        /// cloud-config, byte-identical to the pre-<c>--cloud-init</c> seed.
        /// </param>
        /// <param name="staticNetwork">Optional static addressing; null keeps DHCP.</param>
        public static string BuildSeedIso(
            string outputPath, string vmName, string publicKey, ILogger logger,
            string? customUserData = null,
            StaticNetworkConfig? staticNetwork = null)
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                throw new ArgumentException("A public SSH key is required for the cloud-init seed.", nameof(publicKey));

            string? custom = NormalizeCustomUserData(customUserData);
            if (custom != null)
                ValidateCustomUserData(custom);

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
                    custom == null
                        ? BuildUserData(publicKey)
                        : BuildMultipartUserData(publicKey, custom),
                    new UTF8Encoding(false));

                if (NoCloudNetworkConfig.IsRequested(staticNetwork))
                {
                    // Static addressing rides the seed, not a post-boot patch:
                    // cloud-init applies it on first boot, before user-data.
                    File.WriteAllText(
                        Path.Combine(stagingDir, "network-config"),
                        NoCloudNetworkConfig.Render(staticNetwork!),
                        new UTF8Encoding(false));
                    logger.LogInformation("Static network config added to the NoCloud seed for VM {VMName}: {Ip}",
                        vmName, staticNetwork!.StaticIp);
                }

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

        /// <summary>
        /// Reads and validates a user-supplied cloud-init user-data file for
        /// <c>--cloud-init</c>. Shared by the CLI (fail-loud validation before
        /// any download starts) and <see cref="BuildSeedIso"/>. Throws
        /// <see cref="ArgumentException"/>-family exceptions with actionable
        /// messages when the file is missing, unreadable, empty, or not a
        /// cloud-init document (no recognized leading marker).
        /// </summary>
        public static string LoadUserDataFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A cloud-init user-data file path is required.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException($"Cloud-init user-data file not found: {path}", path);

            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                throw new IOException($"Cloud-init user-data file could not be read: {path} ({ex.Message})", ex);
            }

            string? normalized = NormalizeCustomUserData(content);
            if (normalized == null)
                throw new ArgumentException($"Cloud-init user-data file is empty: {path}");

            ValidateCustomUserData(normalized);
            return normalized;
        }

        /// <summary>
        /// Trims a BOM and leading blank lines and normalizes CRLF/CR to LF.
        /// Returns null for blank content. cloud-init re-sniffs each part's
        /// document type from its first non-whitespace line, so the normalized
        /// text must start with the document marker exactly as cloud-init
        /// expects it.
        /// </summary>
        private static string? NormalizeCustomUserData(string? customUserData)
        {
            if (string.IsNullOrWhiteSpace(customUserData))
                return null;

            string content = customUserData.TrimStart('\uFEFF');
            content = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

            // Leading blank lines would defeat the first-line prefix sniff
            // (type_from_starts_with lstrips whitespace itself, but keeping
            // the marker on the first line makes the file byte-honest).
            content = content.TrimStart('\n', ' ', '\t');
            return content.Length == 0 ? null : content;
        }

        /// <summary>
        /// The first-line markers cloud-init's <c>INCLUSION_TYPES_MAP</c>
        /// recognizes when a part carries no explicit content type — longest
        /// first so <c>#include-once</c> outranks the <c>#include</c> prefix,
        /// matching cloud-init's own sort. A file matching none of these is
        /// treated by cloud-init as an unknown type and silently skipped, so
        /// this rejects loudly instead. <c>## template: jinja</c> is accepted
        /// in the list for parity; a templated user-data document whose first
        /// line is the marker is still processed via the jinja2 handler.
        /// </summary>
        private static readonly string[] CloudInitDocumentMarkers =
        {
            "## template: jinja",
            "#include-once",
            "#include",
            "#cloud-config",
            "#cloud-config-jsonp",
            "#cloud-config-archive",
            "#cloud-boothook",
            "#part-handler",
            "#!"
        };

        /// <summary>
        /// Rejects content cloud-init would silently ignore or mishandle.
        /// A MIME multipart document is detected by its leading header line —
        /// email headers must be the first thing in the document — so a
        /// script that merely <i>embeds</i> MIME-looking text mid-file is
        /// fine. cloud-init does not recursively re-parse MIME parts, so a
        /// multipart file embedded as a part would never run; rejecting it
        /// here with the actionable message beats shipping a dead part.
        /// </summary>
        private static void ValidateCustomUserData(string normalizedContent)
        {
            string firstLine = FirstLine(normalizedContent);

            if (firstLine.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase)
                || firstLine.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "The cloud-init user-data file is already a MIME multipart document. " +
                    "The NoCloud seed is itself the multipart envelope; provide a single cloud-init document " +
                    "(e.g. a #! shell script or a #cloud-config document) instead.");

            bool recognized = false;
            foreach (string marker in CloudInitDocumentMarkers)
            {
                if (firstLine.StartsWith(marker, StringComparison.Ordinal))
                {
                    recognized = true;
                    break;
                }
            }

            if (!recognized)
                throw new ArgumentException(
                    $"The cloud-init user-data file does not start with a recognized cloud-init document marker. " +
                    $"The first line must start with one of: {string.Join(", ", CloudInitDocumentMarkers)}.");
        }

        /// <summary>The document's first line — cloud-init sniffs the type from it.</summary>
        private static string FirstLine(string content)
        {
            int nl = content.IndexOf('\n');
            return (nl < 0 ? content : content[..nl]).TrimEnd('\r');
        }

        /// <summary>
        /// Builds the multipart <c>user-data</c> for a deployment with custom
        /// user content: the custom part first, the built-in cloud-config block
        /// second. cloud-init executes parts in order and merges cloud-config
        /// parts with its default <c>dict(replace)</c> merger, so the built-in
        /// block wins any conflicting top-level key while everything the user
        /// added that does not collide applies verbatim — the additive
        /// contract behind <c>--cloud-init</c>.
        /// </summary>
        internal static string BuildMultipartUserData(string publicKey, string customUserData)
        {
            // A random boundary that is provably absent from both parts: a
            // payload that happens to contain the delimiter string would split
            // the document early and hand cloud-init truncated parts.
            string boundary = NewBoundaryAbsentFrom(customUserData, BuildUserData(publicKey));

            var sb = new StringBuilder();
            sb.Append("MIME-Version: 1.0\n");
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\n\n");

            sb.Append("--").Append(boundary).Append('\n');
            // text/plain + the content's own leading marker: cloud-init re-sniffs
            // the type from the first line when a part declares text/plain (see
            // TYPE_NEEDED in user_data.py), so shell scripts, cloud-config
            // documents, includes and part-handlers all dispatch natively.
            sb.Append("Content-Type: text/plain; charset=\"utf-8\"\n");
            sb.Append("Content-Transfer-Encoding: 7bit\n");
            sb.Append("Content-Disposition: attachment; filename=\"user_cloud_init.txt\"\n\n");
            sb.Append(customUserData);
            if (!customUserData.EndsWith('\n'))
                sb.Append('\n');

            sb.Append('\n').Append("--").Append(boundary).Append('\n');
            sb.Append("Content-Type: text/cloud-config; charset=\"utf-8\"\n");
            sb.Append("MIME-Version: 1.0\n");
            sb.Append("Content-Transfer-Encoding: 7bit\n");
            sb.Append("Content-Disposition: attachment; filename=\"vmcreate_builtin.yaml\"\n\n");
            sb.Append(BuildUserData(publicKey));

            sb.Append('\n').Append("--").Append(boundary).Append("--\n");
            return sb.ToString();
        }

        private static string NewBoundaryAbsentFrom(string customUserData, string builtinUserData)
        {
            // Length chosen so the delimiter line (2 dashes + boundary) stays
            // within the 70-column RFC 2045 soft guidance; re-randomizes only
            // on the astronomically unlikely collision.
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            const int length = 28;
            Span<char> chars = stackalloc char[length];
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            string boundary;
            do
            {
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = alphabet[bytes[i] % alphabet.Length];
                boundary = "==vmcreate" + new string(chars);
                RandomNumberGenerator.Fill(bytes);
            }
            while (customUserData.Contains(boundary, StringComparison.Ordinal)
                   || builtinUserData.Contains(boundary, StringComparison.Ordinal));
            return boundary;
        }

        /// <summary>
        /// Renders the built-in <c>#cloud-config</c> block: the automation
        /// user, SSH key, passwordless sudo and growpart machinery every
        /// post-boot step depends on.
        /// </summary>
        internal static string BuildUserData(string publicKey)
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
