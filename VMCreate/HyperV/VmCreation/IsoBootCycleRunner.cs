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
        public IsoBootCycleResult(bool success, string errorMessage = null, string diagnosticsLog = null)
        {
            Success = success;
            ErrorMessage = errorMessage;
            DiagnosticsLog = diagnosticsLog;
        }

        public bool Success { get; }
        public string ErrorMessage { get; }
        public string DiagnosticsLog { get; }

        public static IsoBootCycleResult Succeeded() => new(true);
        public static IsoBootCycleResult Failed(string message, string diagnostics = null)
            => new(false, message, diagnostics);
    }

    /// <summary>
    /// States of the ISO boot-cycle state machine used by <see cref="IsoBootCycleRunner"/>.
    /// </summary>
    public enum IsoBootCycleState
    {
        Start,
        SendPadding,
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

    /// <summary>
    /// Default implementation of <see cref="IIsoBootCycleRunner"/>.
    /// </summary>
    public sealed class IsoBootCycleRunner : IIsoBootCycleRunner
    {
        private readonly IKvpSender _kvpSender;
        private readonly IKvpPoller _kvpPoller;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IHostNetworkService _hostNetworkService;
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly ILogger<IsoBootCycleRunner> _logger;
        private const int OriginalDiskScsiControllerLocation = 1;
        private const int ShutdownTimeoutSeconds = 600;

        public IsoBootCycleRunner(
            IKvpSender kvpSender,
            IKvpPoller kvpPoller,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            ISshKeyManager sshKeyManager,
            IHostNetworkService hostNetworkService,
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            ILogger<IsoBootCycleRunner> logger)
        {
            _kvpSender = kvpSender ?? throw new ArgumentNullException(nameof(kvpSender));
            _kvpPoller = kvpPoller ?? throw new ArgumentNullException(nameof(kvpPoller));
            _shutdownWatcher = shutdownWatcher ?? throw new ArgumentNullException(nameof(shutdownWatcher));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _hostNetworkService = hostNetworkService ?? throw new ArgumentNullException(nameof(hostNetworkService));
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

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
            IsoBootCycleState state = IsoBootCycleState.Start;
            IsoBootCycleResult result = null;

            context.Logger.Log($"Starting ISO boot-cycle for {vmName} (Gen {detectedGeneration})");

            while (state != IsoBootCycleState.Done && state != IsoBootCycleState.Failed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (state, result) = await StepAsync(state, context, detectedGeneration, mediaPath, customizations, progress, cancellationToken);
            }

            return result ?? IsoBootCycleResult.Succeeded();
        }

        private async Task<(IsoBootCycleState NextState, IsoBootCycleResult Result)> StepAsync(
            IsoBootCycleState state,
            VmCreationContext context,
            int detectedGeneration,
            string mediaPath,
            VmCustomizations customizations,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            string vmName = context.Plan.VmName;

            switch (state)
            {
                case IsoBootCycleState.Start:
                    if (detectedGeneration == 2)
                    {
                        progress?.Report(new CreateVMProgressInfo
                        {
                            Phase = VmDeploymentPhase.Customize,
                            DetectedGeneration = 2
                        });
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    context.Logger.Log("ISO boot-cycle: warming up guest");
                    return (IsoBootCycleState.SendPadding, null);

                case IsoBootCycleState.SendPadding:
                    await _kvpSender.SendKVPToGuestAsync(vmName, "PADDING_1", "true", cancellationToken);
                    await _kvpSender.SendKVPToGuestAsync(vmName, "PADDING_2", "true", cancellationToken);
                    await _kvpSender.SendKVPToGuestAsync(vmName, "PADDING_3", "true", cancellationToken);
                    context.Logger.Log("ISO boot-cycle: padding KVP sent");
                    return (IsoBootCycleState.SendSshKey, null);

                case IsoBootCycleState.SendSshKey:
                    string sshPublicKey;
                    if (!string.IsNullOrEmpty(customizations.CustomSshPublicKeyPath))
                        sshPublicKey = await _sshKeyManager.ReadPublicKeyAsync(customizations.CustomSshPublicKeyPath, cancellationToken);
                    else
                        sshPublicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                    _logger.LogInformation("Sending SSH public key ({Length} chars) via KVP to VM {VMName}",
                        sshPublicKey?.Length ?? 0, vmName);
                    context.Logger.Log($"ISO boot-cycle: sending SSH public key ({sshPublicKey?.Length ?? 0} chars)");
                    await _kvpSender.SendKVPToGuestAsync(vmName, "VMCREATE_SSH_PUBKEY", sshPublicKey, cancellationToken);
                    return (IsoBootCycleState.SendCustomizationFlags, null);

                case IsoBootCycleState.SendCustomizationFlags:
                    if (detectedGeneration == 2)
                        await _kvpSender.SendKVPToGuestAsync(vmName, "VMCREATE_MODE", "customize", cancellationToken);

                    if (customizations.ConfigureXrdp)
                        await _kvpSender.SendKVPToGuestAsync(vmName, "VMCREATE_XRDP", "true", cancellationToken);

                    string nameservers = customizations.DnsMode switch
                    {
                        DnsMode.Custom => customizations.CustomNameservers,
                        _ => _hostNetworkService.ResolveHostDnsServers(),
                    };
                    if (!string.IsNullOrWhiteSpace(nameservers))
                    {
                        _logger.LogInformation("Sending DNS nameservers via KVP to VM {VMName}: {Nameservers}",
                            vmName, nameservers);
                        context.Logger.Log($"ISO boot-cycle: sending nameservers ({nameservers})");
                        await _kvpSender.SendKVPToGuestAsync(vmName, "VMCREATE_NAMESERVERS", nameservers, cancellationToken);
                    }

                    return (IsoBootCycleState.WaitForGuest, null);

                case IsoBootCycleState.WaitForGuest:
                    bool shutDown;
                    if (detectedGeneration == 1)
                    {
                        bool cloneMarkerSeen = await _kvpPoller.PollKVPForProgressAsync(
                            vmName, progress, cancellationToken, ShutdownTimeoutSeconds);

                        progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Customize));

                        if (cloneMarkerSeen)
                            shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                                vmName, progress, cancellationToken, ShutdownTimeoutSeconds);
                        else
                            shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(vmName, cancellationToken, timeoutSeconds: 1);
                    }
                    else
                    {
                        shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                            vmName, progress, cancellationToken, ShutdownTimeoutSeconds);
                    }

                    if (!shutDown)
                        return (IsoBootCycleState.ShutdownOrTimeout, null);

                    context.Logger.Log("ISO boot-cycle: guest shutdown confirmed");
                    if (detectedGeneration == 1)
                        return (IsoBootCycleState.CleanupMbrDisk, null);
                    return (IsoBootCycleState.FinalizeBoot, null);

                case IsoBootCycleState.ShutdownOrTimeout:
                    _logger.LogWarning("VM {VMName} did not shut down within {Timeout}s — collecting diagnostics.",
                        vmName, ShutdownTimeoutSeconds);

                    context.Logger.LogWarning("ISO boot-cycle: guest shutdown timed out; collecting diagnostics");
                    var diagnostics = await _diagnosticsCollector
                        .CollectAsync(vmName, cancellationToken,
                            _sshKeyManager.GetPrivateKeyPath(customizations.CustomSshPublicKeyPath));

                    _logger.LogError("Guest diagnostics for {VMName}: {Summary}\n{RawOutput}",
                        vmName, diagnostics.Summary, diagnostics.RawOutput);
                    context.Logger.LogError($"ISO boot-cycle: diagnostics summary: {diagnostics.Summary}");

                    await _lifecycleManager.StopVMAsync(vmName, cancellationToken);
                    _logger.LogInformation("Force-stopped VM {VMName} after timeout.", vmName);
                    context.Logger.Log("ISO boot-cycle: force-stopped VM after timeout");

                    progress?.Report(new CreateVMProgressInfo
                    {
                        Phase = VmDeploymentPhase.Customize,
                        ErrorMessage = diagnostics.Summary,
                        DiagnosticsLog = diagnostics.RawOutput
                    });

                    return (IsoBootCycleState.Failed, IsoBootCycleResult.Failed(
                        $"ISO customization timed out. {diagnostics.Summary}",
                        diagnostics.RawOutput));

                case IsoBootCycleState.CleanupMbrDisk:
                    progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Customize, VmDeploymentSubStep.CleanupIsoBoot));
                    await _diskManager.RemoveHardDrive(context.Plan, OriginalDiskScsiControllerLocation, cancellationToken);

                    if (File.Exists(mediaPath))
                    {
                        File.Delete(mediaPath);
                        _logger.LogInformation("Deleted original MBR source disk: {MediaPath}", mediaPath);
                        context.Logger.Log($"ISO boot-cycle: deleted original MBR source disk {mediaPath}");
                    }
                    return (IsoBootCycleState.FinalizeBoot, null);

                case IsoBootCycleState.FinalizeBoot:
                    progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Customize, VmDeploymentSubStep.CleanupIsoBoot));
                    await _bootManager.RemoveBootDvd(context.Plan, context.Plan.CloningIsoPath, cancellationToken);
                    await _bootManager.SetFirstBootToHardDrive(context.Plan, cancellationToken);
                    context.Logger.Log("ISO boot-cycle: finalized boot order and removed clone DVD");
                    return (IsoBootCycleState.Done, IsoBootCycleResult.Succeeded());

                default:
                    throw new InvalidOperationException($"Unhandled ISO boot-cycle state: {state}");
            }
        }
    }
}
