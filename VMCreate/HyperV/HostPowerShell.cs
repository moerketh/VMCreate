using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace VMCreate.HyperV
{
    /// <summary>
    /// Shared host-side PowerShell hosting utilities.
    ///
    /// Every host-side PowerShell invocation in VMCreate must run in a
    /// CreateDefault2-based runspace with the Hyper-V module pre-imported
    /// through the <see cref="InitialSessionState"/>. The bare
    /// <c>PowerShell.Create()</c> default (a full default-ISS runspace)
    /// depends on cmdlet auto-loading that is not guaranteed to work
    /// uniformly under the trimmed hosting package
    /// (System.Management.Automation): the failed package-swap experiment
    /// measured default-ISS throwing PSSnapInException and Utility
    /// cmdlets auto-loading against an installed machine pwsh. Routing
    /// all host-side hosting through this class makes the swap a
    /// no-op for callers and keeps exactly one place that knows how a
    /// host-side runspace is constructed.
    ///
    /// The shape mirrors <see cref="PowerShellExecutor"/>'s
    /// CreateRunspace (fresh runspace per call, ISS with Core + an
    /// explicit Hyper-V import), for code paths that build a runspace
    /// per call instead of resolving the process-wide executor.
    ///
    /// Guest-side scripts (executed inside the guest by PowerShell Direct
    /// or SSH) are NOT affected: they run in the guest's own PowerShell.
    /// </summary>
    internal static class HostPowerShell
    {
        /// <summary>
        /// Builds the shared InitialSessionState for host-side PowerShell
        /// work: CreateDefault2 (only Microsoft.PowerShell.Core built-ins)
        /// plus an explicit Hyper-V module import.
        /// </summary>
        private static InitialSessionState CreateSessionState()
        {
            var iss = InitialSessionState.CreateDefault2();
            iss.ImportPSModule(new[] { "Hyper-V" });
            return iss;
        }

        /// <summary>
        /// Creates and opens a fresh CreateDefault2 + Hyper-V runspace for
        /// host-side PowerShell work. The caller owns the runspace and must
        /// dispose it (dispose an assigned <c>PowerShell</c> instance first,
        /// then the runspace — same order as the executor).
        /// </summary>
        internal static Runspace CreateRunspace()
        {
            var runspace = RunspaceFactory.CreateRunspace(CreateSessionState());
            runspace.Open();
            return runspace;
        }

        /// <summary>
        /// Best-effort Dismount-VHD for a VHDX that may or may not be
        /// mounted. Shared by media preparation (locked destination file)
        /// and failed-deployment cleanup so the two paths cannot drift
        /// apart. Never throws. Failures are logged at Debug (the VHDX
        /// was not necessarily mounted — errors here are often benign);
        /// the caller decides whether a successful dismount warrants an
        /// Information-level log.
        /// </summary>
        internal static bool TryDismountVhdx(string vhdxPath, ILogger logger)
        {
            try
            {
                using var runspace = CreateRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
                ps.Invoke();
                if (ps.HadErrors)
                {
                    logger.LogDebug("Dismount-VHD reported errors (VHDX may not have been mounted): {Error}",
                        string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Dismount-VHD threw (VHDX may not have been mounted)");
                return false;
            }
        }
    }
}