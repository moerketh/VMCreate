using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCreate.Metadata
{
    /// <summary>One line of the audit log: who did what to which VM, and whether it worked.</summary>
    public sealed class AuditEntry
    {
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

        /// <summary>"Manager" or "VMCreate".</summary>
        public string Tool { get; set; } = "";

        /// <summary>DOMAIN\user that ran the tool.</summary>
        public string User { get; set; } = "";

        /// <summary>One of <see cref="AuditActions"/>.</summary>
        public string Action { get; set; } = "";

        public Guid? VmId { get; set; }

        /// <summary>The VM name at the time; names change, <see cref="VmId"/> does not.</summary>
        public string? VmName { get; set; }

        /// <summary>Human-readable specifics, e.g. "Engagements/ACME → Archive" or the switch moved from.</summary>
        public string? Detail { get; set; }

        /// <summary>Null when the action succeeded.</summary>
        public string? Error { get; set; }

        [JsonIgnore]
        public bool Succeeded => Error == null;
    }

    /// <summary>Audit action names. Dotted lower case so the log stays greppable.</summary>
    public static class AuditActions
    {
        public const string VmCreate = "vm.create";
        public const string VmStart = "vm.start";
        public const string VmShutdown = "vm.shutdown";
        public const string VmTurnOff = "vm.turnoff";
        public const string VmSave = "vm.save";
        public const string VmPause = "vm.pause";
        public const string VmResume = "vm.resume";
        public const string VmRename = "vm.rename";
        public const string VmDelete = "vm.delete";
        public const string VmMove = "vm.move";
        public const string VmProtect = "vm.protect";
        public const string VmUnprotect = "vm.unprotect";
        public const string VmExpiry = "vm.expiry";
        public const string NetworkIsolate = "network.isolate";
        public const string NetworkRestore = "network.restore";
        public const string NetworkConnect = "network.connect";
        public const string FolderCreate = "folder.create";
        public const string FolderRename = "folder.rename";
        public const string FolderMove = "folder.move";
        public const string FolderDelete = "folder.delete";
        public const string FolderExpiry = "folder.expiry";
        public const string CheckpointRevert = "checkpoint.revert";
        public const string CloneCreate = "clone.create";
        public const string TemplateMark = "template.mark";
        public const string TemplateUnmark = "template.unmark";
        public const string Archive = "archive";

        /// <summary>An unused file or empty folder removed from the Storage window.</summary>
        public const string FileDelete = "file.delete";

        /// <summary>The [vmcreate] block in Notes was edited outside VMCreate tools and taken into the store.</summary>
        public const string MetadataExternalEdit = "metadata.external";

        /// <summary>Notes and the store both changed; the store's copy was kept.</summary>
        public const string MetadataConflict = "metadata.conflict";

        /// <summary>Metadata of a VM that no longer exists was removed.</summary>
        public const string MetadataRemove = "metadata.remove";
    }

    /// <summary>
    /// Append-only record of create / delete / revert / network changes. Writes
    /// never rewrite or truncate the file; both VMCreate and the Manager append.
    /// </summary>
    public interface IAuditLog
    {
        string FilePath { get; }

        Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

        /// <summary>Newest first; optionally only one VM's entries and at most <paramref name="max"/>.</summary>
        Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid? vmId = null, int? max = null, CancellationToken cancellationToken = default);
    }

    public sealed class JsonlAuditLog : IAuditLog
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly FileLock _lock;

        public string FilePath { get; }

        /// <summary>%LOCALAPPDATA%\VMCreate\Manager\audit.jsonl</summary>
        public static string DefaultFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "Manager", "audit.jsonl");

        public JsonlAuditLog() : this(DefaultFilePath) { }

        public JsonlAuditLog(string filePath)
        {
            FilePath = Path.GetFullPath(filePath);
            _lock = new FileLock("Audit", FilePath);
        }

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(entry.User))
                entry.User = $@"{Environment.UserDomainName}\{Environment.UserName}";
            // One line per entry; serialized JSON never contains a raw newline.
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            return Task.Run(() => _lock.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                // A writer that died mid-line leaves no trailing newline; start a fresh
                // line so this entry is not glued onto (and lost with) the torn one.
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        stream.WriteByte((byte)'\n');
                }
                stream.Seek(0, SeekOrigin.End);
                stream.Write(line);
                return true;
            }, cancellationToken), cancellationToken);
        }

        public Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid? vmId = null, int? max = null, CancellationToken cancellationToken = default)
            => Task.Run<IReadOnlyList<AuditEntry>>(() =>
            {
                if (!File.Exists(FilePath))
                    return Array.Empty<AuditEntry>();

                var entries = new List<AuditEntry>();
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (reader.ReadLine() is string line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = Parse(line);
                        if (entry != null && (vmId == null || entry.VmId == vmId))
                            entries.Add(entry);
                    }
                }
                entries.Reverse();
                return max is int n && entries.Count > n ? entries.GetRange(0, n) : entries;
            }, cancellationToken);

        /// <summary>A torn or hand-edited line is skipped, never fatal: the rest of the log stays readable.</summary>
        internal static AuditEntry? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;
            try
            {
                return JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
