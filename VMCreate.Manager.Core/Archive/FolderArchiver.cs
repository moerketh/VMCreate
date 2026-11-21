using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.HyperV;
using VMCreate.Metadata;

namespace VMCreate.Manager.Archive
{
    /// <summary>A VM to archive, with the Manager metadata that goes into the manifest.</summary>
    public sealed record ArchiveItem(Guid VmId, string Name, VmMetadata? Metadata);

    /// <summary>
    /// manifest.json at the root of an archive: what was exported, from where,
    /// with which metadata, and a SHA-256 per file to verify the copy later.
    /// </summary>
    public sealed class ArchiveManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Folder { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string Tool { get; set; } = "VMCreate Manager";
        public string User { get; set; } = "";
        public string Host { get; set; } = "";

        /// <summary>True when the user stopped the archive; VMs after the cancelled one are not listed.</summary>
        public bool Cancelled { get; set; }

        public List<ArchivedVm> Vms { get; set; } = new();
    }

    public sealed class ArchivedVm
    {
        public Guid VmId { get; set; }
        public string Name { get; set; } = "";

        /// <summary>Export folder, relative to the manifest. Import it with Hyper-V Manager → Import Virtual Machine.</summary>
        public string Directory { get; set; } = "";

        public string? Description { get; set; }
        public string? Purpose { get; set; }
        public List<string> Tags { get; set; } = new();
        public DateOnly? ExpiresOn { get; set; }
        public bool WasTemplate { get; set; }
        public Guid? TemplateId { get; set; }
        public VmProvenance? Provenance { get; set; }

        /// <summary>The login name only: the password is DPAPI-bound to this user and machine, so it is not exported.</summary>
        public string? Username { get; set; }

        public List<ArchivedFile> Files { get; set; } = new();

        /// <summary>Null when the export completed.</summary>
        public string? Error { get; set; }
    }

    public sealed class ArchivedFile
    {
        /// <summary>Relative to the manifest.</summary>
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed record ArchiveResult(string Directory, string ManifestPath, ArchiveManifest Manifest)
    {
        public IEnumerable<ArchivedVm> Exported => Manifest.Vms.Where(v => v.Error == null);
        public IEnumerable<ArchivedVm> Failed => Manifest.Vms.Where(v => v.Error != null);
        public long TotalBytes => Manifest.Vms.SelectMany(v => v.Files).Sum(f => f.Size);
        public bool Cancelled => Manifest.Cancelled;
    }

    /// <summary>
    /// Exports every VM of a folder (configuration, checkpoints, disks; linked
    /// clones include their parents, re-parented inside the export) into one
    /// directory with a manifest. Deleting the originals is a separate step.
    /// </summary>
    public sealed class FolderArchiver
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IHyperVHost _host;
        private readonly ILogger<FolderArchiver> _logger;

        public FolderArchiver(IHyperVHost host, ILogger<FolderArchiver>? logger = null)
        {
            _host = host;
            _logger = logger ?? NullLogger<FolderArchiver>.Instance;
        }

        /// <summary>
        /// Archives into a new "{folder}_{yyyyMMdd-HHmm}" directory under
        /// <paramref name="destinationRoot"/>. A VM that fails to export is recorded
        /// with its error and the rest continue; the manifest is rewritten after
        /// each VM so an interrupted archive still describes what it holds.
        /// Cancelling stops the running export, removes that VM's partial
        /// directory, and returns with <see cref="ArchiveResult.Cancelled"/>;
        /// VMs finished before that stay in the archive.
        /// </summary>
        public async Task<ArchiveResult> ArchiveAsync(string folderPath, IReadOnlyList<ArchiveItem> vms, string destinationRoot,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Now;
            string dir = UniqueDirectory(Path.Combine(destinationRoot, ArchiveDirectoryName(folderPath, now)));
            Directory.CreateDirectory(dir);
            string manifestPath = Path.Combine(dir, "manifest.json");
            var manifest = new ArchiveManifest
            {
                Folder = folderPath,
                CreatedAt = now,
                User = $@"{Environment.UserDomainName}\{Environment.UserName}",
                Host = Environment.MachineName,
            };

            for (int i = 0; i < vms.Count && !manifest.Cancelled; i++)
            {
                var item = vms[i];
                string sub = UniqueDirectory(Path.Combine(dir, SafeName(item.Name)));
                var entry = ToArchivedVm(item, Path.GetFileName(sub));
                manifest.Vms.Add(entry);
                try
                {
                    progress?.Report($"Exporting {i + 1}/{vms.Count}: {item.Name}…");
                    await _host.ExportVmAsync(item.VmId, sub, cancellationToken).ConfigureAwait(false);
                    progress?.Report($"Checksumming {i + 1}/{vms.Count}: {item.Name}…");
                    entry.Files = await HashFilesAsync(dir, sub, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Archive of {Folder} cancelled at {Vm}", folderPath, item.Name);
                    manifest.Cancelled = true;
                    entry.Files.Clear();
                    entry.Error = RemovePartialExport(sub)
                        ? "Cancelled; the partial export was removed."
                        : $"Cancelled; remove the partial export '{entry.Directory}' by hand.";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not archive {Vm}", item.Name);
                    entry.Error = ex.Message;
                }
                finally
                {
                    await WriteManifestAsync(manifestPath, manifest).ConfigureAwait(false);
                }
            }
            return new ArchiveResult(dir, manifestPath, manifest);
        }

        internal static ArchivedVm ToArchivedVm(ArchiveItem item, string directory)
        {
            var m = item.Metadata;
            return new ArchivedVm
            {
                VmId = item.VmId,
                Name = item.Name,
                Directory = directory,
                Description = m?.Description,
                Purpose = m?.Purpose,
                Tags = m?.Tags.ToList() ?? new(),
                ExpiresOn = m?.ExpiresOn,
                WasTemplate = m?.IsTemplate ?? false,
                TemplateId = m?.TemplateId,
                Provenance = m?.Provenance,
                Username = m?.Credential?.Username,
            };
        }

        /// <summary>"Engagements/ACME" at 2026-11-01 14:05 → "Engagements-ACME_20261101-1405".</summary>
        internal static string ArchiveDirectoryName(string folderPath, DateTimeOffset at)
            => $"{SafeName(folderPath.Replace('/', '-'))}_{at:yyyyMMdd-HHmm}";

        internal static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return safe.Length == 0 ? "vm" : safe;
        }

        private static string UniqueDirectory(string path)
        {
            string candidate = path;
            for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
                candidate = $"{path} ({i})";
            return candidate;
        }

        /// <summary>Only ever called on the fresh, uniquely named directory this archive created for the VM.</summary>
        private bool RemovePartialExport(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the partial export {Directory}", dir);
                return false;
            }
        }

        private static async Task<List<ArchivedFile>> HashFilesAsync(string root, string dir, CancellationToken ct)
        {
            var files = new List<ArchivedFile>();
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                files.Add(new ArchivedFile
                {
                    Path = Path.GetRelativePath(root, path),
                    Size = stream.Length,
                    Sha256 = Convert.ToHexStringLower(hash),
                });
            }
            return files;
        }

        private static async Task WriteManifestAsync(string path, ArchiveManifest manifest)
        {
            string temp = path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
    }
}
