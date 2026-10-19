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

        /// <summary>The SCSI location the MBR source disk is attached at;
        /// the clone scenario's cleanup detaches (and deletes) it there.</summary>
        public const int MbrSourceDiskScsiControllerLocation = 1;

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
            IGuestShell? shell = null;

            try
            {
                sshPublicKey = !string.IsNullOrEmpty(customizations.CustomSshPublicKeyPath)
                    ? await _sshKeyManager.ReadPublicKeyAsync(customizations.CustomSshPublicKeyPath, cancellationToken)
                    : await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                // The single KVP item: auth bootstrap for the guest's vmcreate
                // user. Sent before anything else so the guest's injection
                // service (running ~5 s after boot) has it by first connect.
                // Replace semantics: Add, and on failure Modify in place (a
                // stale item from an aborted deploy makes Add fail; Modify
                // overwrites it). If both fail, the ADD error surfaces — it
                // names the real problem.
                try
                {
                    await _kvpSender.SendKVPToGuestAsync(vmName, KvpKeyName, sshPublicKey, cancellationToken);
                }
                catch (Exception addEx) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(addEx, "KVP Add for {Key} failed; trying Modify in place (stale item from an aborted deploy?)", KvpKeyName);
                    try
                    {
                        await _kvpMaintenance.ModifyKvpItemAsync(vmName, KvpKeyName, sshPublicKey, cancellationToken);
                    }
                    catch (Exception modifyEx)
                    {
                        // Both failed: log WHY Modify failed (otherwise the
                        // cause is invisible), then surface the original Add
                        // error with its stack intact.
                        _logger.LogWarning(modifyEx, "KVP Modify for {Key} also failed; surfacing the Add error", KvpKeyName);
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(addEx).Throw();
                        throw; // unreachable
                    }
                }

                // GUID resolution must succeed — it is the vsock host string.
                // Thrown (not returned) so the whole-body catch stops the VM:
                // the smoke run hits this shape when the wrong ISO booted.
                string? vmGuid = await _kvpMaintenance.ResolveVmGuidAsync(vmName, cancellationToken);
                if (string.IsNullOrEmpty(vmGuid))
                    throw new InvalidOperationException(
                        $"Could not resolve the VM GUID for '{vmName}' — required as the vsock host string.");

                shell = _guestShellFactory.CreateVsock(vmName, _sshKeyManager.GetPrivateKeyPath(customizations.CustomSshPublicKeyPath), vmGuid);

                progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Customize));
                await shell.WaitForReadyAsync(cancellationToken);
                context.Logger.Log("ISO boot-cycle v2: vsock SSH connection established");

                // Failure forensics: collect guest logs over the live shell
                // BEFORE any stop. Best-effort with its own short timeout —
                // a hung guest has a dead shell too. Logs flow to BOTH the
                // context log and the failed result (the GUI shows the
                // result's DiagnosticsLog).
                IsoBootCycleResult stepsResult;
                stepsResult = detectedGeneration == 1
                    ? await RunCloneAsync(shell, sshPublicKey, context, mediaPath, customizations, progress, cancellationToken)
                    : await RunCustomizeAsync(shell, sshPublicKey, context, customizations, progress, cancellationToken);

                if (!stepsResult.Success)
                {
                    string logs = await CollectGuestLogsAsync(shell, context, "guest steps failed");
                    await StopVmQuietlyAsync(vmName, cancellationToken);
                    return IsoBootCycleResult.Failed(stepsResult.ErrorMessage ?? "guest steps failed", logs);
                }

                // Success: the scenario's CollectedOutput is the only record
                // of what the guest did (the autorun log dies at the flip,
                // plan §1.4) — write it to the deployment log.
                if (!string.IsNullOrWhiteSpace(stepsResult.DiagnosticsLog))
                    context.Logger.Log($"ISO boot-cycle v2: guest scenario output:\n{stepsResult.DiagnosticsLog.Trim()}");

                // Power off OUTSIDE the steps' try/catch: the guest killing
                // sshd mid-command races this call, and the losing side is
                // usually us (ssh exits 255). --no-block returns immediately
                // before the shutdown begins, and a transport error from
                // this one call is EXPECTED, not a deploy failure.
                try
                {
                    await shell.RunCommandAsync("sudo systemctl --no-block poweroff", cancellationToken);
                }
                // ONLY transport errors are expected here (the guest killing
                // sshd mid-command). A real failure — sudo or systemctl
                // exiting 1 — must NOT be swallowed: the run would wait out
                // the whole shutdown budget, force-stop and report success.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && SshGuestShell.IsSshTransportError(ex))
                {
                    context.Logger.Log($"ISO boot-cycle v2: poweroff transport error (expected if sshd died first): {ex.Message}");
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
                    await _diskManager.RemoveHardDrive(context.Plan, MbrSourceDiskScsiControllerLocation, cancellationToken);
                    if (System.IO.File.Exists(mediaPath))
                    {
                        // The clone is DONE — a locked file here (e.g. a
                        // lingering checkpoint, AV scanner) must not fail the
                        // cycle: the whole-body catch would surface it as a
                        // deploy failure and the orchestrator would delete a
                        // VM whose clone succeeded. Leave the file behind and
                        // warn; the disk is already detached.
                        try
                        {
                            System.IO.File.Delete(mediaPath);
                            context.Logger.Log($"ISO boot-cycle v2: deleted original MBR source disk {mediaPath}");
                        }
                        catch (Exception deleteEx) when (!cancellationToken.IsCancellationRequested)
                        {
                            context.Logger.LogWarning(
                                $"ISO boot-cycle v2: could not delete detached MBR source disk {mediaPath} ({deleteEx.Message}); leaving the file — manual cleanup may be needed");
                        }
                    }
                }

                await _bootManager.RemoveBootDvd(context.Plan, context.Plan.EffectiveCloningIsoPath, cancellationToken);
                await _bootManager.SetFirstBootToHardDrive(context.Plan, cancellationToken);
                context.Logger.Log("ISO boot-cycle v2: finalized boot order and removed clone DVD");
                return IsoBootCycleResult.Succeeded(stepsResult.DiagnosticsLog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !cancellationToken.IsCancellationRequested)
            {
                // Whole-body catch: EVERY failure after the cycle began —
                // GUID resolution, vsock readiness (120 s timeout), the
                // steps, anything — stops the VM. The smoke run hits exactly
                // these paths (wrong ISO booted, key not injected, hvc
                // trouble), and debug-preserved runs bypass the
                // orchestrator's cleanup, so the runner must be
                // self-contained. Logs are collected only when a shell
                // exists; earlier failures never had one.
                if (shell is not null)
                    await CollectGuestLogsAsync(shell, context, $"runner exception: {ex.Message}");
                await StopVmQuietlyAsync(vmName, CancellationToken.None);
                throw;
            }
            finally
            {
                // KVP hygiene on EVERY exit path: success, timeout, exception,
                // cancellation. The item persists in the VM configuration
                // otherwise and blocks the next deploy's Add. Diagnostics-only
                // — cleanup must never mask the original result/exception, and
                // must be BOUNDED (a failed Remove with no token would hang
                // the deploy forever; 30 s then log-and-move-on).
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _kvpMaintenance.RemoveKvpItemAsync(vmName, KvpKeyName, sshPublicKey, cleanupCts.Token);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to remove {Key} KVP item from VM {VMName}", KvpKeyName, vmName);
                }
            }
        }

        private async Task<IsoBootCycleResult> RunCloneAsync(
            IGuestShell shell, string? sshPublicKey, VmCreationContext context, string mediaPath,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct)
        {
            context.Logger.Log("ISO boot-cycle v2: cloning source disk to UEFI layout");
            return await _scriptRunner.RunAsync(shell, "clone", sshPublicKey, context, customizations, progress, ct);
        }

        private async Task<IsoBootCycleResult> RunCustomizeAsync(
            IGuestShell shell, string? sshPublicKey, VmCreationContext context,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct)
        {
            context.Logger.Log("ISO boot-cycle v2: customizing target disk in place");
            return await _scriptRunner.RunAsync(shell, "customize", sshPublicKey, context, customizations, progress, ct);
        }

        /// <summary>
        /// Best-effort guest log capture over the live vsock shell: journal
        /// tail plus the guest-side deploy log. Short-bounded so a dying
        /// guest cannot hang the failure path. Lines land in the context log
        /// AND are returned (callers attach them to the failed result — the
        /// GUI renders DiagnosticsLog, not the deployment log).
        /// </summary>
        private async Task<string> CollectGuestLogsAsync(IGuestShell shell, VmCreationContext context, string reason)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                string logs = await shell.RunCommandAsync(
                    "sudo journalctl -b -n 100 --no-pager 2>/dev/null; echo '--- guest log ---'; sudo tail -n 100 /var/log/vmcreate-iso.log 2>/dev/null || true",
                    TimeSpan.FromSeconds(25), cts.Token);
                context.Logger.LogError($"ISO boot-cycle v2 guest diagnostics ({reason}):\n{logs}");
                return logs;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Guest log collection failed ({Reason}) — guest shell likely dead", reason);
                return $"(guest log collection failed: {ex.Message})";
            }
        }

        /// <summary>Stop the VM, swallowing errors: failure paths must not
        /// trade their original error for a WMI failure.</summary>
        private async Task StopVmQuietlyAsync(string vmName, CancellationToken ct)
        {
            try { await _lifecycleManager.StopVMAsync(vmName, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop VM {VMName} on the failure path", vmName); }
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
        /// <param name="sshPublicKey">The automation-user key the runner
        /// already delivered via KVP — also becomes the scenario script's
        /// pubkey argument (single source of truth).</param>
        Task<IsoBootCycleResult> RunAsync(
            IGuestShell shell, string scenario, string? sshPublicKey, VmCreationContext context,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct);
    }
}