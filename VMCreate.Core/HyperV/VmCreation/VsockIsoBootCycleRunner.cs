using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using CreateVM.HyperV.vmbus;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// ISO boot-cycle v2: drives the vmcreate-iso guest over the Hyper-V
    /// socket channel instead of the KVP progress protocol. The ISO is
    /// logic-free — this runner uploads the guest scripts and executes them
    /// over SSH (streaming stdout for progress), so deployment behavior
    /// changes ship with VMCreate, not with ISO releases.
    /// </summary>
    /// <remarks>
    /// KVP carries exactly one item: the SSH public key the guest injects
    /// for the <c>vmcreate</c> user. It is removed in a finally on every
    /// exit path (host-sent KVP items persist in the VM configuration
    /// otherwise, and a stale item blocks re-Add on the next deploy).
    ///
    /// Command transport notes: vsock commands run WITHOUT transport retry
    /// (the GUID cannot move; a dead session means a dead guest), and long
    /// guest work runs with per-line stdout streaming plus an idle timeout —
    /// see <see cref="SshGuestShell.RunStreamingCommandAsync"/>.
    /// </remarks>
    public sealed class VsockIsoBootCycleRunner : IIsoBootCycleRunner
    {
        private const string KvpKeyName = "VMCREATE_SSH_PUBKEY";

        // Guest readiness: the ISO reaches a listening sshd-vsock ~7 s after
        // power-on; the shell's own probe owns the cadence (2 s / 120 s).
        private static readonly TimeSpan GuestCommandTotalTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan GuestCommandIdleTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(10);

        private readonly IKvpSender _kvpSender;
        private readonly IKvpMaintenance _kvpMaintenance;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVsockGuestScriptRunner _scriptRunner;
        private readonly ILogger<VsockIsoBootCycleRunner> _logger;

        public VsockIsoBootCycleRunner(
            IKvpSender kvpSender,
            IKvpMaintenance kvpMaintenance,
            IGuestShellFactory guestShellFactory,
            ISshKeyManager sshKeyManager,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            IVsockGuestScriptRunner scriptRunner,
            ILogger<VsockIsoBootCycleRunner> logger)
        {
            _kvpSender = kvpSender ?? throw new ArgumentNullException(nameof(kvpSender));
            _kvpMaintenance = kvpMaintenance ?? throw new ArgumentNullException(nameof(kvpMaintenance));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _shutdownWatcher = shutdownWatcher ?? throw new ArgumentNullException(nameof(shutdownWatcher));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _scriptRunner = scriptRunner ?? throw new ArgumentNullException(nameof(scriptRunner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <param name="detectedGeneration">Same meaning as v1: 1 =
        /// BIOS/MBR source to clone to UEFI (both disks attached), 2 =
        /// customize-only (single target disk).</param>
        public async Task<IsoBootCycleResult> RunAsync(
            VmCreationContext context,
            int detectedGeneration,
            string mediaPath,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (customizations == null) throw new ArgumentNullException(nameof(customizations));

            string vmName = context.Plan.VmName;
            string? sshPublicKey = null;

            try
            {
                sshPublicKey = !string.IsNullOrEmpty(customizations.CustomSshPublicKeyPath)
                    ? await _sshKeyManager.ReadPublicKeyAsync(customizations.CustomSshPublicKeyPath, cancellationToken)
                    : await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                // The single KVP item: auth bootstrap for the guest's vmcreate
                // user. Sent before anything else so the guest's injection
                // service (running ~5 s after boot) has it by first connect.
                await _kvpSender.SendKVPToGuestAsync(vmName, KvpKeyName, sshPublicKey, cancellationToken);

                // GUID resolution must succeed — it is the vsock host string.
                string? vmGuid = await _kvpMaintenance.ResolveVmGuidAsync(vmName, cancellationToken);
                if (string.IsNullOrEmpty(vmGuid))
                    return IsoBootCycleResult.Failed($"Could not resolve the VM GUID for '{vmName}' — required as the vsock host string.");

                IGuestShell shell = _guestShellFactory.CreateVsock(vmName, _sshKeyManager.GetPrivateKeyPath(customizations.CustomSshPublicKeyPath), vmGuid);

                progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Customize));
                await shell.WaitForReadyAsync(cancellationToken);
                context.Logger.Log("ISO boot-cycle v2: vsock SSH connection established");

                // Failure forensics: collect guest logs over the live shell
                // BEFORE any force-stop. Best-effort with its own short
                // timeout — a hung guest has a dead shell too.
                try
                {
                    IsoBootCycleResult result = detectedGeneration == 1
                        ? await RunCloneAsync(shell, context, mediaPath, customizations, progress, cancellationToken)
                        : await RunCustomizeAsync(shell, context, customizations, progress, cancellationToken);

                    if (!result.Success)
                        await CollectGuestLogsAsync(shell, context, "guest steps failed");

                    if (!result.Success)
                        return result;

                    await shell.RunCommandAsync("sudo systemctl poweroff", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await CollectGuestLogsAsync(shell, context, $"exception: {ex.Message}");
                    throw;
                }

                bool shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(
                    vmName, cancellationToken, timeoutSeconds: (int)ShutdownTimeout.TotalSeconds);
                if (!shutDown)
                {
                    context.Logger.LogWarning("ISO boot-cycle v2: guest did not shut down in time; force-stopping");
                    await _lifecycleManager.StopVMAsync(vmName, cancellationToken);
                }

                if (detectedGeneration == 1)
                {
                    // The MBR source disk was attached only for the clone;
                    // delete it exactly like v1's CleanupMbrDisk state.
                    await _diskManager.RemoveHardDrive(context.Plan, IsoBootCycleRunner.MbrSourceDiskScsiControllerLocation, cancellationToken);
                    if (System.IO.File.Exists(mediaPath))
                    {
                        System.IO.File.Delete(mediaPath);
                        context.Logger.Log($"ISO boot-cycle v2: deleted original MBR source disk {mediaPath}");
                    }
                }

                await _bootManager.RemoveBootDvd(context.Plan, context.Plan.CloningIsoPath, cancellationToken);
                await _bootManager.SetFirstBootToHardDrive(context.Plan, cancellationToken);
                context.Logger.Log("ISO boot-cycle v2: finalized boot order and removed clone DVD");
                return IsoBootCycleResult.Succeeded();
            }
            finally
            {
                // KVP hygiene on EVERY exit path: success, timeout, exception,
                // cancellation. The item persists in the VM configuration
                // otherwise and blocks the next deploy's Add. Diagnostics-only
                // — cleanup must never mask the original result/exception.
                try
                {
                    await _kvpMaintenance.RemoveKvpItemAsync(vmName, KvpKeyName, sshPublicKey, CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to remove {Key} KVP item from VM {VMName}", KvpKeyName, vmName);
                }
            }
        }

        private async Task<IsoBootCycleResult> RunCloneAsync(
            IGuestShell shell, VmCreationContext context, string mediaPath,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct)
        {
            context.Logger.Log("ISO boot-cycle v2: cloning source disk to UEFI layout");
            return await _scriptRunner.RunAsync(shell, "clone", context, customizations, progress, ct);
        }

        private async Task<IsoBootCycleResult> RunCustomizeAsync(
            IGuestShell shell, VmCreationContext context,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct)
        {
            context.Logger.Log("ISO boot-cycle v2: customizing target disk in place");
            return await _scriptRunner.RunAsync(shell, "customize", context, customizations, progress, ct);
        }

        /// <summary>
        /// Best-effort guest log capture over the live vsock shell: journal
        /// tail plus the guest-side deploy log. Short-bounded so a dying
        /// guest cannot hang the failure path; every line lands in the
        /// context log (and the result diagnostics when steps failed).
        /// </summary>
        private async Task CollectGuestLogsAsync(IGuestShell shell, VmCreationContext context, string reason)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                string logs = await shell.RunCommandAsync(
                    "sudo journalctl -b -n 100 --no-pager 2>/dev/null; echo '--- guest log ---'; sudo tail -n 100 /var/log/vmcreate-iso.log 2>/dev/null || true",
                    TimeSpan.FromSeconds(25), cts.Token);
                context.Logger.LogError($"ISO boot-cycle v2 guest diagnostics ({reason}):\n{logs}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Guest log collection failed ({Reason}) — guest shell likely dead", reason);
            }
        }

        /// <summary>
        /// Parses a guest protocol line (one direction: guest → host).
        /// Splits at most 4 ways so messages containing colons survive:
        /// <c>VMCREATE:PROGRESS:&lt;pct&gt;:&lt;msg&gt;</c> → true with
        /// percent+message; anything else → false.
        /// </summary>
        public static bool TryParseProgressLine(string? line, out int percent, out string message)
        {
            percent = -1;
            message = string.Empty;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("VMCREATE:PROGRESS:", StringComparison.Ordinal))
                return false;

            // "VMCREATE:PROGRESS:42:msg text" → ["VMCREATE","PROGRESS","42","msg text"]
            var parts = line.Split(new[] { ':' }, 4);
            if (parts.Length < 3)
                return false;
            if (!int.TryParse(parts[2], out percent) || percent < 0 || percent > 100)
            {
                percent = -1;
                return false;
            }
            message = parts.Length == 4 ? parts[3] : string.Empty;
            return true;
        }

        /// <summary>
        /// Parses <c>VMCREATE:MSG:&lt;text&gt;</c> lines (free-form status
        /// text; colons in the text survive by the same 3-way split).
        /// </summary>
        public static bool TryParseMessageLine(string? line, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("VMCREATE:MSG:", StringComparison.Ordinal))
                return false;
            var parts = line.Split(new[] { ':' }, 3);
            if (parts.Length < 3)
                return false;
            message = parts[2];
            return true;
        }
    }

    /// <summary>Guest-side script execution for the v2 ISO cycle (W2 implements
    /// the embedded-script runner; W3's tests use fakes).</summary>
    public interface IVsockGuestScriptRunner
    {
        /// <param name="scenario">"clone" (MBR→UEFI, source+target attached)
        /// or "customize" (single target disk).</param>
        Task<IsoBootCycleResult> RunAsync(
            IGuestShell shell, string scenario, VmCreationContext context,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct);
    }
}