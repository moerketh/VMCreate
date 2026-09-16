using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace VMCreate.HyperV.Unattend
{
    /// <summary>
    /// Result of executing a PowerShell command or pipeline.
    /// </summary>
    public sealed class PowerShellResult
    {
        public Collection<PSObject> Output { get; set; } = new();
        public bool HadErrors { get; set; }
        public string ErrorSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Abstraction over PowerShell command execution. The implementation creates a fresh
    /// PowerShell instance per call to avoid cross-call state corruption.
    /// </summary>
    public interface IPowerShellExecutor
    {
        PowerShellResult RunCommand(string command, params (string Name, object Value)[] parameters);
        PowerShellResult RunScript(string script);

        /// <summary>
        /// Runs a native executable by name with positional arguments. Unlike
        /// <see cref="RunCommand"/>, the arguments are appended with AddArgument
        /// (not AddParameter), so they are passed as-is on the application's command
        /// line. Required for console applications such as reg.exe, which is NOT a
        /// cmdlet and rejects literal parameter names like -ArgumentList
        /// ("ERROR: Invalid Argument/Option - 'ArgumentList'").
        /// </summary>
        PowerShellResult RunApplication(string application, params string[] arguments);
    }

    public sealed class PowerShellExecutor : IPowerShellExecutor
    {
        private readonly InitialSessionState _initialSessionState;

        public PowerShellExecutor()
        {
            // CreateDefault2() loads only Microsoft.PowerShell.Core — see the
            // main VMCreate.HyperV.PowerShellExecutor for the rationale and
            // measured costs. Mount/Dismount-VHD come from the explicit
            // Hyper-V import below. Get-Partition and the PartitionAccessPath
            // cmdlets are Storage-module cmdlets that resolve through module
            // auto-loading, which still works under CreateDefault2 (verified:
            // auto-loaded Get-Partition from a fresh CreateDefault2 runspace).
            _initialSessionState = InitialSessionState.CreateDefault2();
            _initialSessionState.ImportPSModule(new[] { "Hyper-V" });
        }

        public PowerShellResult RunCommand(string command, params (string Name, object Value)[] parameters)
        {
            var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddCommand(command);
                foreach (var (name, value) in parameters)
                    ps.AddParameter(name, value);

                var output = ps.Invoke();
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
            finally
            {
                runspace.Dispose();
            }
        }

        public PowerShellResult RunScript(string script)
        {
            var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript(script);

                var output = ps.Invoke();
                return new PowerShellResult
                {
                    Output = output,
                    HadErrors = ps.HadErrors,
                    ErrorSummary = ps.HadErrors
                        ? string.Join("; ", ps.Streams.Error.Select(e => e.ToString()))
                        : string.Empty
                };
            }
            finally
            {
                runspace.Dispose();
            }
        }

        public PowerShellResult RunApplication(string application, params string[] arguments)
        {
            var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddCommand(application);
                if (arguments != null)
                {
                    foreach (string argument in arguments)
                        ps.AddArgument(argument);
                }

                var output = ps.Invoke();
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
            finally
            {
                runspace.Dispose();
            }
        }
    }
}
