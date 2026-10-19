using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Result of an ISO boot-cycle run.
    /// </summary>
    public sealed class IsoBootCycleResult
    {
        public IsoBootCycleResult(bool success, string? errorMessage = null, string? diagnosticsLog = null)
        {
            Success = success;
            ErrorMessage = errorMessage;
            DiagnosticsLog = diagnosticsLog;
        }

        public bool Success { get; }
        public string? ErrorMessage { get; }
        public string? DiagnosticsLog { get; }

        public static IsoBootCycleResult Succeeded(string? log = null) => new(true, diagnosticsLog: log);
        public static IsoBootCycleResult Failed(string message, string? diagnostics = null)
            => new(false, message, diagnostics);
    }

    /// <summary>
    /// States of the ISO boot-cycle state machine used by <see cref="IsoBootCycleRunner"/>.
    /// </summary>
    public enum IsoBootCycleState
    {
        Start,
        SendSshKey,
        SendCustomizationFlags,
        WaitForGuest,
        ShutdownOrTimeout,
        CleanupMbrDisk,
        FinalizeBoot,
        Done,
        Failed
    }

    /// <summary>
    /// Orchestrates the ISO-based boot cycle for converted disk images:
    /// sending KVP customization tokens to the guest, waiting for the guest
    /// to shut down, optionally cloning an MBR disk to GPT, and restoring
    /// the primary hard-drive boot order.
    /// </summary>
    public interface IIsoBootCycleRunner
    {
        /// <summary>
        /// Runs the ISO boot-cycle state machine.
        /// </summary>
        Task<IsoBootCycleResult> RunAsync(
            VmCreationContext context,
            int detectedGeneration,
            string mediaPath,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);
    }


}

