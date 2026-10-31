using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCreate.Metadata
{
    /// <summary>
    /// Persistent VM metadata shared by VMCreate (writes provenance at deploy
    /// time) and VMCreate.Manager (everything else). Both processes can write,
    /// so every change is a locked read-modify-write against the file on disk.
    /// </summary>
    public interface IVmMetadataStore
    {
        string FilePath { get; }

        /// <summary>Reads the current document. The result is a private copy; edits to it are not persisted.</summary>
        Task<MetadataDocument> LoadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Re-reads the file, applies <paramref name="mutate"/> and writes the
        /// result atomically, all under a cross-process lock.
        /// </summary>
        Task<T> UpdateAsync<T>(Func<MetadataDocument, T> mutate, CancellationToken cancellationToken = default);

        /// <summary>Raised (on a thread-pool thread) when the file changes, including changes by another process.</summary>
        event EventHandler? Changed;
    }

    public sealed class MetadataStoreException : Exception
    {
        public MetadataStoreException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public sealed class JsonVmMetadataStore : IVmMetadataStore, IDisposable
    {
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        private readonly string _mutexName;
        private FileSystemWatcher? _watcher;

        public string FilePath { get; }

        public event EventHandler? Changed;

        /// <summary>%LOCALAPPDATA%\VMCreate\Manager\metadata.json</summary>
        public static string DefaultFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "Manager", "metadata.json");

        public JsonVmMetadataStore() : this(DefaultFilePath) { }

        public JsonVmMetadataStore(string filePath)
        {
            FilePath = Path.GetFullPath(filePath);
            // One lock per file, shared by every process in the session.
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant()));
            _mutexName = @"Local\VMCreate.Metadata." + Convert.ToHexString(hash, 0, 8);
        }

        public Task<MetadataDocument> LoadAsync(CancellationToken cancellationToken = default)
            => Task.Run(() => WithLock(ReadDocument, cancellationToken), cancellationToken);

        public Task<T> UpdateAsync<T>(Func<MetadataDocument, T> mutate, CancellationToken cancellationToken = default)
            => Task.Run(() => WithLock(() =>
            {
                var doc = ReadDocument();
                T result = mutate(doc);
                WriteDocument(doc);
                return result;
            }, cancellationToken), cancellationToken);

        /// <summary>Starts raising <see cref="Changed"/> for writes to the file.</summary>
        public void StartWatching()
        {
            if (_watcher != null)
                return;
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
            {
                // The atomic write lands as a rename of the temp file onto the target.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _watcher.Created += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _watcher.Renamed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose() => _watcher?.Dispose();

        private T WithLock<T>(Func<T> action, CancellationToken cancellationToken)
        {
            using var mutex = new Mutex(false, _mutexName);
            bool acquired = false;
            try
            {
                while (!acquired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                    }
                    catch (AbandonedMutexException)
                    {
                        // A writer died mid-update. The write itself is atomic
                        // (temp file + rename), so the file is still consistent.
                        acquired = true;
                    }
                }
                return action();
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
        }

        private MetadataDocument ReadDocument()
        {
            if (!File.Exists(FilePath))
                return new MetadataDocument();

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new MetadataDocument();

            MetadataDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<MetadataDocument>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Never fall back to an empty document here: the next update
                // would overwrite the user's metadata with nothing.
                throw new MetadataStoreException($"Metadata file '{FilePath}' is not valid JSON; fix or move it aside. A copy of the previous version is in '{FilePath}.bak'.", ex);
            }

            if (doc == null)
                return new MetadataDocument();
            if (doc.SchemaVersion > MetadataDocument.CurrentSchemaVersion)
                throw new MetadataStoreException($"Metadata file '{FilePath}' has schema version {doc.SchemaVersion}, newer than this build supports ({MetadataDocument.CurrentSchemaVersion}). Update VMCreate.");
            return doc;
        }

        private void WriteDocument(MetadataDocument doc)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            doc.SchemaVersion = MetadataDocument.CurrentSchemaVersion;
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
