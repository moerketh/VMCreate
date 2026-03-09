using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Manages a per-user Ed25519 SSH keypair stored in a configurable directory.
    /// The keypair is generated on first use and reused for all VMs.
    /// The public key is injected into VMs via KVP during the ISO customization phase,
    /// enabling secure key-based PowerShell Direct connections for post-boot configuration.
    /// </summary>
    public class SshKeyManager : ISshKeyManager
    {
        private readonly string _sshDirectory;

        private static readonly string PrivateKeyFileName = "vmcreate_ed25519";
        private static readonly string PublicKeyFileName = "vmcreate_ed25519.pub";

        private readonly ILogger<SshKeyManager> _logger;

        public SshKeyManager(ILogger<SshKeyManager> logger, IOptions<AppSettings> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var settings = options?.Value ?? new AppSettings();
            _sshDirectory = settings.SshDirectory;
        }

        /// <summary>Absolute path to the private key file.</summary>
        public string PrivateKeyPath => Path.Combine(_sshDirectory, PrivateKeyFileName);

        /// <summary>Absolute path to the public key file.</summary>
        public string PublicKeyPath => Path.Combine(_sshDirectory, PublicKeyFileName);

        /// <summary>
        /// Ensures the SSH keypair exists. Generates one if missing.
        /// Returns the public key content for injection into VMs.
        /// </summary>
        public async Task<string> EnsureKeyPairAsync(CancellationToken ct = default)
        {
            if (File.Exists(PrivateKeyPath) && File.Exists(PublicKeyPath))
            {
                string existingKey = (await File.ReadAllTextAsync(PublicKeyPath, ct)).Trim();
                if (!string.IsNullOrEmpty(existingKey))
                {
                    _logger.LogDebug("Using existing SSH keypair from {Path}", _sshDirectory);
                    return existingKey;
                }
            }

            _logger.LogInformation("Generating new Ed25519 SSH keypair in {Path}", _sshDirectory);
            await GenerateKeyPairAsync(ct);

            string publicKey = (await File.ReadAllTextAsync(PublicKeyPath, ct)).Trim();
            if (string.IsNullOrEmpty(publicKey))
                throw new InvalidOperationException("ssh-keygen produced an empty public key file.");

            return publicKey;
        }

        /// <summary>
        /// Reads the public key content from a custom key file path.
        /// Used when the user provides their own SSH key instead of the auto-generated one.
        /// </summary>
        public async Task<string> ReadPublicKeyAsync(string customPublicKeyPath, CancellationToken ct = default)
        {
            if (!File.Exists(customPublicKeyPath))
                throw new FileNotFoundException($"Custom SSH public key not found: {customPublicKeyPath}");

            string content = (await File.ReadAllTextAsync(customPublicKeyPath, ct)).Trim();
            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"Custom SSH public key file is empty: {customPublicKeyPath}");

            // Basic validation: public keys start with ssh-ed25519, ssh-rsa, ecdsa-sha2-, etc.
            if (!content.StartsWith("ssh-") && !content.StartsWith("ecdsa-"))
                throw new InvalidOperationException($"File does not look like an SSH public key: {customPublicKeyPath}");

            return content;
        }

        /// <summary>
        /// Gets the private key path to use for PowerShell Direct connections.
        /// Returns the custom key path if provided and valid, otherwise the auto-generated key.
        /// </summary>
        public string GetPrivateKeyPath(string customPublicKeyPath = null)
        {
            if (!string.IsNullOrEmpty(customPublicKeyPath))
            {
                // Convention: private key is the public key path without .pub extension
                string privatePath = customPublicKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
                    ? customPublicKeyPath[..^4]
                    : customPublicKeyPath;

                if (File.Exists(privatePath))
                    return privatePath;

                _logger.LogWarning("Custom private key not found at {Path}, falling back to auto-generated key", privatePath);
            }

            return PrivateKeyPath;
        }

        private async Task GenerateKeyPairAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_sshDirectory);

            // Remove existing files to avoid ssh-keygen overwrite prompt
            if (File.Exists(PrivateKeyPath)) File.Delete(PrivateKeyPath);
            if (File.Exists(PublicKeyPath)) File.Delete(PublicKeyPath);

            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                Arguments = $"-t ed25519 -f \"{PrivateKeyPath}\" -N \"\" -C \"vmcreate@{Environment.MachineName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ssh-keygen. Ensure OpenSSH is installed.");

            string stdout = await process.StandardOutput.ReadToEndAsync(ct);
            string stderr = await process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _logger.LogError("ssh-keygen failed (exit {Code}): {StdErr}", process.ExitCode, stderr);
                throw new InvalidOperationException($"ssh-keygen failed with exit code {process.ExitCode}: {stderr}");
            }

            _logger.LogInformation("SSH keypair generated: {PublicKey}", PublicKeyPath);
        }
    }
}
