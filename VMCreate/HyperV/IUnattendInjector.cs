using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Injects the bundled <c>unattend.xml</c> into a Windows VHDX so that
    /// OOBE provisions the <c>flare</c> account, auto-logon, and RDP.
    /// The implementation spawns an elevated child process (UAC prompt)
    /// because <c>Mount-VHD</c> requires a full-Administrator token.
    /// </summary>
    public interface IUnattendInjector
    {
        /// <summary>
        /// Injects the unattend.xml into the VHDX at the given path.
        /// </summary>
        /// <param name="vhdxPath">Path to the VHDX file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// <c>true</c> if injection succeeded (exit code 0);
        /// <c>false</c> if the user declined the UAC prompt or the
        /// elevated child returned a non-zero exit code.
        /// </returns>
        Task<bool> InjectAsync(string vhdxPath, CancellationToken cancellationToken);
    }
}