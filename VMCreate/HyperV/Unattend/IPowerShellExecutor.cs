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
    }

    public sealed class PowerShellExecutor : IPowerShellExecutor
    {
        private readonly InitialSessionState _initialSessionState;

        public PowerShellExecutor()
        {
            _initialSessionState = InitialSessionState.CreateDefault();
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
    }
}
