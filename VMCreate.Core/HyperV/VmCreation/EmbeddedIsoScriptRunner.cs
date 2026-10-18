using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// W2 implementation of <see cref="IVsockGuestScriptRunner"/>: uploads
    /// the embedded ISO guest scripts (Scripts/iso/*.sh) over the vsock
    /// shell and executes the scenario script with the v2 progress protocol
    /// streamed line-by-line (parsed into <see cref="CreateVMProgressInfo"/>
    /// and forwarded to the splash by the scripts themselves — the host
    /// never round-trips for splash updates).
    /// </summary>
    /// <remarks>
    /// Script upload is small text over CopyContentAsync (ssh stdin pipe).
    /// Execution runs on <see cref="SshGuestShell.RunStreamingCommandAsync"/>
    /// with a generous total timeout and an idle budget: a healthy clone
    /// streams partclone percentages continuously; silence means hung.
    /// </remarks>
    public sealed class EmbeddedIsoScriptRunner : IVsockGuestScriptRunner
    {
        // Total: partclone of a large disk is the long pole; the legacy
        // cycle's shutdown budget was 10 min but cloning ran far longer —
        // v1 had no single timeout over the whole guest workflow.
        private static readonly TimeSpan ScenarioTotalTimeout = TimeSpan.FromMinutes(90);
        // Idle: scripts must not go silent longer than this without a
        // protocol line (partclone percentages arrive at least every few
        // seconds; apt/dnf can print nothing for a while — 5 min headroom).
        private static readonly TimeSpan ScenarioIdleTimeout = TimeSpan.FromMinutes(5);

        // Scenario name -> uploaded script file (Scripts/iso/).
        private static readonly Dictionary<string, string> ScenarioScripts = new(StringComparer.Ordinal)
        {
            ["clone"] = "clone_disk.sh",
            ["customize"] = "customize_target.sh",
        };

        private const string CommonScript = "common.sh";
        private const string GrubScript = "install_grub.sh";
        private const string XrdpScript = "install_xrdp.sh";
        private const string GuestScriptDir = "/opt/vmcreate";

        private readonly ILogger<EmbeddedIsoScriptRunner> _logger;

        public EmbeddedIsoScriptRunner(ILogger<EmbeddedIsoScriptRunner> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IsoBootCycleResult> RunAsync(
            IGuestShell shell, string scenario, string? sshPublicKey, VmCreationContext context,
            VmCustomizations customizations, IProgress<CreateVMProgressInfo>? progress, CancellationToken ct)
        {
            if (!ScenarioScripts.TryGetValue(scenario, out string? scenarioScript))
                throw new ArgumentException($"Unknown ISO scenario '{scenario}'.");

            // ── Upload: common + scenario + chroot-executed helpers ──────
            // The chroot scripts are placed where the scenario scripts
            // expect them (v1 layout: /opt/autorun → v2: /opt/vmcreate).
            await shell.RunCommandAsync($"sudo mkdir -p {GuestScriptDir}", ct);
            foreach (string script in new[] { CommonScript, scenarioScript, GrubScript, XrdpScript })
            {
                string content = ScriptResourceLoader.Load(script);
                string guestPath = $"{GuestScriptDir}/{script}";
                await shell.CopyContentAsync(content, guestPath, ct);
                await shell.RunCommandAsync($"chmod +x '{guestPath}'", ct);
                _logger.LogDebug("Uploaded {Script} -> {GuestPath}", script, guestPath);
            }

            // ── Scenario arguments (v1's KVP keys, now argv) ─────────────
            string xrdp = customizations.ConfigureXrdp ? "true" : "false";
            string? nameservers = customizations.DnsMode switch
            {
                DnsMode.Custom => customizations.CustomNameservers,
                _ => null, // Host mode: the chroot keeps its own resolv.conf
                           // (v1's host-side resolution moved to post-boot).
            };
            string swapMb = "8192";

            string args = $"{xrdp} {BashSingleQuote(nameservers ?? "")} {swapMb} {BashSingleQuote(sshPublicKey ?? "")}";
            string command = $"sudo bash {GuestScriptDir}/{scenarioScript} {args}";

            // ── Streamed execution with the v2 protocol ─────────────────
            var concrete = (SshGuestShell)shell;
            var parser = new ProtocolLineParser(progress, _logger, scenario);
            string output;
            try
            {
                output = await concrete.RunStreamingCommandAsync(
                    command, ScenarioTotalTimeout, ScenarioIdleTimeout, parser.OnLine, ct);
            }
            catch (TimeoutException tex)
            {
                return IsoBootCycleResult.Failed(
                    $"ISO {scenario} scenario timed out: {tex.Message}", parser.CollectedOutput);
            }
            catch (Exception ex)
            {
                return IsoBootCycleResult.Failed(
                    $"ISO {scenario} scenario failed: {ex.Message}", parser.CollectedOutput);
            }

            // A protocol line may also record the target's vsock capability
            // for Phase 4b's post-boot NIC decision.
            if (parser.HvsockCapability is { } cap)
                _logger.LogInformation("Target vsock capability: {Capability}", cap);

            // Scripts exit non-zero on failure — the transport throws on
            // that, so reaching here means exit 0.
            return IsoBootCycleResult.Succeeded();
        }

        /// <summary>POSIX single-quote wrapping for argv safety.</summary>
        private static string BashSingleQuote(string value)
            => "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Parses the guest stdout protocol live (one direction: guest → host)
    /// and reports host-side progress. Owned by the runner so failure paths
    /// keep whatever output was seen before the error.
    /// </summary>
    public sealed class ProtocolLineParser
    {
        private readonly IProgress<CreateVMProgressInfo>? _progress;
        private readonly ILogger _logger;
        private readonly string _scenario;
        private readonly System.Text.StringBuilder _collected = new();

        public ProtocolLineParser(IProgress<CreateVMProgressInfo>? progress, ILogger logger, string scenario)
        {
            _progress = progress;
            _logger = logger;
            _scenario = scenario;
        }

        public string? HvsockCapability { get; private set; }

        public string CollectedOutput => _collected.ToString();

        /// <summary>Runs on the stdout callback thread: cheap, no throws
        /// (the transport catches and rethrows callback exceptions, but a
        /// parser bug must not kill a running clone).</summary>
        public void OnLine(string line)
        {
            lock (_collected) _collected.AppendLine(line);

            if (VsockIsoBootCycleRunner.TryParseProgressLine(line, out int pct, out string msg))
            {
                _logger.LogDebug("[{Scenario}] {Percent,3}% {Message}", _scenario, pct, msg);
                _progress?.Report(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.Customize,
                    ProgressPercentage = pct,
                    StepName = msg,
                });
            }
            else if (VsockIsoBootCycleRunner.TryParseMessageLine(line, out string text))
            {
                _logger.LogDebug("[{Scenario}] {Message}", _scenario, text);
                _progress?.Report(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.Customize,
                    StepName = text,
                });
            }
            else if (line.StartsWith("VMCREATE:HVSOCK:", StringComparison.Ordinal))
            {
                HvsockCapability = line.Substring("VMCREATE:HVSOCK:".Length);
            }
            // Non-protocol lines are step chatter; kept in CollectedOutput.
        }
    }
}
