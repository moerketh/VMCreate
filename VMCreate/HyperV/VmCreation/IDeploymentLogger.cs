using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Per-deployment structured log. Collects chronological step entries for the current
    /// VM deployment so users can diagnose failures without relying solely on transient
    /// console or progress output.
    /// </summary>
    public interface IDeploymentLogger
    {
        /// <summary>
        /// Name of the VM this logger is tracking.
        /// </summary>
        string VmName { get; }

        /// <summary>
        /// Adds an informational entry to the deployment log.
        /// </summary>
        void Log(string message);

        /// <summary>
        /// Adds a warning entry to the deployment log.
        /// </summary>
        void LogWarning(string message);

        /// <summary>
        /// Adds an error entry to the deployment log.
        /// </summary>
        void LogError(string message);

        /// <summary>
        /// Adds a step entry with a success/failure outcome.
        /// </summary>
        void LogStep(string stepName, bool success, string details = null);

        /// <summary>
        /// All collected log entries, oldest first.
        /// </summary>
        IReadOnlyList<DeploymentLogEntry> Entries { get; }

        /// <summary>
        /// Returns the full deployment log as a single string.
        /// </summary>
        string GetLog();

        /// <summary>
        /// Writes the log to disk. Safe to call multiple times; overwrites the file.
        /// </summary>
        void SaveToFile(string path);
    }

    /// <summary>
    /// Severity of a deployment log entry.
    /// </summary>
    public enum DeploymentLogSeverity
    {
        Info,
        Warning,
        Error,
        Step
    }

    /// <summary>
    /// A single line in the per-deployment log.
    /// </summary>
    public sealed class DeploymentLogEntry
    {
        public DeploymentLogEntry(DeploymentLogSeverity severity, string message, string details = null)
        {
            Severity = severity;
            Message = message ?? string.Empty;
            Details = details;
        }

        public DeploymentLogSeverity Severity { get; }
        public string Message { get; }
        public string Details { get; }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Details))
                return $"[{Severity}] {Message} — {Details}";
            return $"[{Severity}] {Message}";
        }
    }

    /// <summary>
    /// Default in-memory <see cref="IDeploymentLogger"/>.
    /// </summary>
    public sealed class DeploymentLogger : IDeploymentLogger
    {
        private readonly List<DeploymentLogEntry> _entries = new();
        private readonly object _lock = new();

        public DeploymentLogger(string vmName)
        {
            VmName = vmName ?? "Unnamed";
        }

        public string VmName { get; }

        public IReadOnlyList<DeploymentLogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToList();
                }
            }
        }

        public void Log(string message)
            => Add(DeploymentLogSeverity.Info, message);

        public void LogWarning(string message)
            => Add(DeploymentLogSeverity.Warning, message);

        public void LogError(string message)
            => Add(DeploymentLogSeverity.Error, message);

        public void LogStep(string stepName, bool success, string details = null)
            => Add(DeploymentLogSeverity.Step, $"{stepName}: {(success ? "OK" : "FAIL")}", details);

        public string GetLog()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Deployment log for {VmName}");
                sb.AppendLine(new string('-', 40));
                foreach (var entry in _entries)
                    sb.AppendLine(entry.ToString());
                return sb.ToString();
            }
        }

        public void SaveToFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, GetLog());
        }

        private void Add(DeploymentLogSeverity severity, string message, string details = null)
        {
            lock (_lock)
            {
                _entries.Add(new DeploymentLogEntry(severity, message, details));
            }
        }
    }
}
