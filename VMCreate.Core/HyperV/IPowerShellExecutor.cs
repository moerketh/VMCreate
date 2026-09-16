using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV
{
    /// <summary>
    /// Abstraction over PowerShell command/script execution with a shared Hyper-V module session.
    /// The implementation creates a fresh PowerShell instance per call to avoid cross-call state corruption.
    /// </summary>
    public interface IPowerShellExecutor : IDisposable
    {
        /// <summary>
        /// Runs a PowerShell command asynchronously.
        /// </summary>
        Task<PowerShellResult> RunCommandAsync(string command, IEnumerable<KeyValuePair<string, object?>>? parameters, CancellationToken cancellationToken);

        /// <summary>
        /// Runs a PowerShell script asynchronously.
        /// </summary>
        Task<PowerShellResult> RunScriptAsync(string script, CancellationToken cancellationToken);

        /// <summary>
        /// Opens and discards a throwaway runspace against the shared session state,
        /// paying the one-off Hyper-V module import + JIT cost (~2.3 s measured on
        /// first open) away from the first real command. Safe to call on machines
        /// without Hyper-V: failures are logged and swallowed, and because every
        /// Run* call opens its own runspace from the untouched InitialSessionState,
        /// a failed warmup cannot affect later calls — they keep their normal
        /// lazy failure semantics.
        /// </summary>
        void Warmup();
    }

    /// <summary>
    /// Result of executing a PowerShell command or pipeline.
    /// </summary>
    public sealed class PowerShellResult
    {
        public System.Collections.ObjectModel.Collection<PSObject> Output { get; init; } = new();
        public bool HadErrors { get; init; }
        public string ErrorSummary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Default implementation of <see cref="IPowerShellExecutor"/> that creates a fresh
    /// PowerShell instance per call, backed by a shared InitialSessionState with the
    /// Hyper-V module pre-imported.
    /// </summary>
    public sealed class PowerShellExecutor : IPowerShellExecutor
    {
        private readonly InitialSessionState _initialSessionState;
        private readonly object _runspaceGate = new();
        private bool _disposed;

        public PowerShellExecutor()
        {
            // Timed at Information: InitialSessionState construction is the
            // bulk of PowerShell-hosting startup cost and lands on the first
            // Hyper-V call of the session (this executor is a singleton in
            // App.xaml.cs, constructed lazily on first resolve).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // CreateDefault2() populates only Microsoft.PowerShell.Core;
            // CreateDefault() adds every built-in cmdlet/provider (~600
            // types to materialize on first runspace open — measured ~600 ms
            // constructor + ~2 s first open before this change). Everything
            // this executor needs beyond Core is pulled in by the explicit
            // Hyper-V module import below, and the managers call only
            // Hyper-V cmdlets plus reg.exe (Unattend executor below), all
            // of which resolve fine without the full default set.
            _initialSessionState = InitialSessionState.CreateDefault2();
            _initialSessionState.ImportPSModule(new[] { "Hyper-V" });
            LogStartup("powershell-session-state-built", sw);
        }

        /// <summary>
        /// Logs a one-shot PowerShell-hosting startup cost at Information.
        /// The executor is process-singleton, so this fires once and marks
        /// where "first thing you click is slow" actually goes.
        /// </summary>
        private static void LogStartup(string milestone, System.Diagnostics.Stopwatch sw)
        {
            Serilog.Log.Information("Startup: {ElapsedMs} ms — {Milestone} (PowerShell hosting)",
                sw.ElapsedMilliseconds, milestone);
        }

        public Task<PowerShellResult> RunCommandAsync(
            string command,
            IEnumerable<KeyValuePair<string, object?>>? parameters,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() =>
            {
                using var runspace = CreateRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddCommand(command);
                foreach (var parameter in parameters ?? Enumerable.Empty<KeyValuePair<string, object?>>())
                    ps.AddParameter(parameter.Key, parameter.Value);

                var output = ps.Invoke();
                return CreateResult(output, ps);
            }, cancellationToken);
        }

        public Task<PowerShellResult> RunScriptAsync(string script, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() =>
            {
                using var runspace = CreateRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript(script);

                var output = ps.Invoke();
                return CreateResult(output, ps);
            }, cancellationToken);
        }

        private Runspace CreateRunspace()
        {
            // Gated: the background Warmup() can overlap a real caller's
            // first open on the same InitialSessionState; serializing
            // creation+open removes that race. Worst case a real call waits
            // for the in-flight warmup (never longer than the cold-open cost
            // it replaces); all existing callers are sequential anyway.
            lock (_runspaceGate)
            {
                var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
                runspace.Open();
                return runspace;
            }
        }

        public void Warmup()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // One throwaway open pays the module import + JIT cost of the
                // first real CreateRunspace() so deploys don't. The runspace is
                // discarded immediately; _initialSessionState is untouched, so
                // real commands later behave exactly as without warmup.
                using var runspace = CreateRunspace();
                LogStartup("powershell-runspace-warmed", sw);
            }
            catch (Exception ex)
            {
                // Expected on machines without Hyper-V (module import fails at
                // open) — a failed warmup must not break a later deploy.
                Serilog.Log.Information("Startup: {ElapsedMs} ms — powershell-warmup-skipped (PowerShell hosting): {Reason}",
                    sw.ElapsedMilliseconds, ex.GetType().Name);
            }
        }

        private static PowerShellResult CreateResult(System.Collections.ObjectModel.Collection<PSObject> output, PowerShell ps)
        {
            var result = new PowerShellResult
            {
                Output = output,
                HadErrors = ps.HadErrors,
                ErrorSummary = ps.HadErrors
                    ? string.Join("; ", ps.Streams.Error.Select(e => e.ToString()))
                    : string.Empty
            };
            ps.Streams.Error.Clear();
            return result;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PowerShellExecutor));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // InitialSessionState does not implement IDisposable; nothing to release here.
            }
        }
    }
}
