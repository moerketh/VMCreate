using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Pins <see cref="ChrootGuestShell"/>'s wrapping contract: command
    /// wrapping (chroot + env -i + sudo() shim + heartbeat), target-path
    /// prefixing, and the stdout contract handed back to steps (protocol
    /// noise filtered, step output verbatim).
    /// </summary>
    [TestClass]
    public class ChrootGuestShellTests
    {
        private static ChrootGuestShell Create(out FakeIsoShell iso, string? label = "Sync Timezone")
        {
            iso = new FakeIsoShell();
            return new ChrootGuestShell(iso, Mock.Of<ILogger>()) { CurrentStepLabel = label };
        }

        [TestMethod]
        public void Context_IsChroot()
        {
            var shell = Create(out _);
            Assert.AreEqual(GuestExecutionContext.Chroot, shell.Context);
        }

        [TestMethod]
        public void TargetPath_PrefixesAbsoluteAndRelativePaths()
        {
            var shell = Create(out _);
            Assert.AreEqual("/mnt/new/etc/localtime", shell.TargetPath("/etc/localtime"));
            Assert.AreEqual("/mnt/new/tmp/tool.sh", shell.TargetPath("tmp/tool.sh"));
            Assert.AreEqual("/mnt/new/tmp/dir", shell.TargetPath("/tmp/dir/"));
        }

        [TestMethod]
        public void TargetPath_PassesThroughAlreadyPrefixedPaths()
        {
            var shell = Create(out _);
            Assert.AreEqual("/mnt/new", shell.TargetPath("/mnt/new"));
            Assert.AreEqual("/mnt/new/etc/x", shell.TargetPath("/mnt/new/etc/x"));
        }

        [TestMethod]
        public void TargetPath_NormalizesBackslashes()
        {
            var shell = Create(out _);
            Assert.AreEqual("/mnt/new/etc/x", shell.TargetPath("\\etc\\x"));
        }

        [TestMethod]
        public void BuildChrootCommand_WrapsInChrootWithCleanEnvironment()
        {
            var shell = Create(out _);
            string cmd = shell.BuildChrootCommand("true");

            StringAssert.StartsWith(cmd, "sudo chroot /mnt/new /usr/bin/env -i PATH=");
            StringAssert.Contains(cmd, "HOME=/root bash -c '");
            // PATH union covers merged-/usr and split-sbin layouts.
            StringAssert.Contains(cmd, "/usr/sbin:/usr/bin:/sbin:/bin");
        }

        [TestMethod]
        public void InnerScript_DefinesSudoShim_BeforeTheCommand()
        {
            var shell = Create(out _);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("sudo timedatectl status"));

            int shim = inner.IndexOf("sudo()", StringComparison.Ordinal);
            int command = inner.IndexOf("sudo timedatectl", StringComparison.Ordinal);
            Assert.IsTrue(shim >= 0, "sudo() shim must be defined");
            Assert.IsTrue(shim < command, "shim must be defined before the step command");
        }

        [TestMethod]
        public async Task ShowOnSplashAsync_RoutesThroughTheLiveIsoShell_NotTheChroot()
        {
            var shell = Create(out var iso);

            await shell.ShowOnSplashAsync("Sync Timezone", 40, CancellationToken.None);
            await shell.ShowOnSplashAsync(null, 100, CancellationToken.None);

            Assert.AreEqual(2, iso.SplashCommands.Count, "one splash command per call, on the ISO shell");
            // Instrumentation: --ping precedes the updates so a dead daemon
            // is visible in the host log, not just on the guest screen.
            StringAssert.Contains(iso.SplashCommands[0], "plymouth --ping");
            StringAssert.Contains(iso.SplashCommands[0], "VMCREATE:SPLASH_ALIVE");
            StringAssert.Contains(iso.SplashCommands[0], "plymouth display-message --text='Sync Timezone'");
            StringAssert.Contains(iso.SplashCommands[0], "plymouth system-update --progress=40");
            // Message-less call advances the bar only.
            Assert.IsFalse(iso.SplashCommands[1].Contains("--text", StringComparison.Ordinal));
            StringAssert.Contains(iso.SplashCommands[1], "--progress=100");
            // Never through the chroot wrapper.
            foreach (string cmd in iso.SplashCommands)
                Assert.IsFalse(cmd.Contains("/mnt/new", StringComparison.Ordinal), "plymouth runs on the live ISO");
        }

        [TestMethod]
        public async Task ShowOnSplashAsync_SwallowsFailures_ExceptCancellation()
        {
            var shell = Create(out var iso);
            iso.SplashThrows = new InvalidOperationException("plymouth absent");

            // Must not throw — the splash is cosmetic.
            await shell.ShowOnSplashAsync("Step", 10, CancellationToken.None);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => shell.ShowOnSplashAsync("Step", 10, cts.Token));
        }

        [TestMethod]
        public async Task ShowOnSplashAsync_WarnsOnce_WhenPlymouthDaemonDies()
        {
            // The guest screen falls back to the text console when plymouthd
            // dies mid-run; without the --ping probe that death was only a
            // visual symptom. The warning is one-shot (the daemon stays dead
            // — logging per update would spam) and names the step it died
            // during.
            var logger = new TestLogger();
            var shell = new ChrootGuestShell(new DeadSplashIsoShell(), logger)
            {
                CurrentStepLabel = "Force X11 Display Server",
            };

            await shell.ShowOnSplashAsync("Force X11 Display Server", 60, CancellationToken.None);
            await shell.ShowOnSplashAsync(null, 70, CancellationToken.None);

            Assert.AreEqual(1, logger.Warnings.Count(w => w.Contains("Plymouth daemon stopped answering")));
            StringAssert.Contains(logger.Warnings[0], "Force X11 Display Server");
        }

        [TestMethod]
        public void InnerScript_HeartbeatSleep_DoesNotHoldTheSessionPipe()
        {
            // THE production regression: the heartbeat subshell's sleep
            // inherited the SSH session's stdout; the EXIT trap killed the
            // subshell but not its sleep child, and the orphan held the pipe
            // open — every chroot step cost a full 60s heartbeat interval of
            // dead time while the inetd sshd waited for EOF. Shape pin: the
            // sleep's output must be redirected so the orphan holds nothing.
            // (The behavioral twin of this test, Wrapper_OrphanedSleep_
            // CannotHoldPipeOpen, proves the EOF semantics by executing the
            // real wrapper.)
            var shell = Create(out _);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("true"));

            StringAssert.Contains(inner, "sleep 60 >/dev/null 2>&1", "sleep must not inherit the session pipe");
        }

        [TestMethod]
        public void Wrapper_OrphanedSleep_CannotHoldPipeOpen()
        {
            // Behavioral twin, executed in WSL: the wrapper's stdout is a
            // FIFO. When the MAIN bash exits, the reader must see EOF at
            // once — unless the killed heartbeat subshell's sleep child
            // still holds the write end (the exact fd the inetd sshd waits
            // on in production). A held pipe keeps `cat` alive until the
            // 5s timeout (READER_RC=124, no STEP_RAN); a clean wrapper EOFs
            // in milliseconds. The reader's captured output must contain
            // STEP_RAN, or the probe is vacuous (quoting failure etc.).
            string? wsl = FindWsl();
            if (wsl is null)
            {
                Assert.Inconclusive("WSL not available — heartbeat pipe test skipped");
                return;
            }

            var shell = Create(out _, label: "Timing Probe");
            string inner = ExtractInnerScript(shell.BuildChrootCommand("echo STEP_RAN"));

            string result = RunHeartbeatPipeProbe(wsl, inner);
            StringAssert.Contains(result, "READER_RC=0",
                $"the reader must see EOF, not a timeout kill (output: {result})");
            StringAssert.Contains(result, "WRAPPER_RAN=STEP_RAN",
                $"the wrapper must actually run through the FIFO (output: {result}) — otherwise this probe is vacuous");
            string elapsed = result.Split(' ').First(t => t.StartsWith("EOF_AFTER_MS=", StringComparison.Ordinal));
            int ms = int.Parse(elapsed["EOF_AFTER_MS=".Length..]);
            Assert.IsTrue(ms < 3000,
                $"EOF came {ms}ms after the wrapper exited — a process (the orphaned sleep) is still holding the session pipe");
        }

        /// <summary>
        /// Runs the real generated wrapper with stdout on a FIFO and reports
        /// how long after the wrapper's exit the reader waited for EOF, plus
        /// the reader's captured output. The probe script rides stdin; the
        /// wrapper file rides argv (a /mnt path) — the wrapper bytes cannot
        /// share stdin with the probe, because the probe's own stdin reads
        /// would race bash's parser for them.
        /// </summary>
        private static string RunHeartbeatPipeProbe(string wslExe, string innerScript)
        {
            string wrapperPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vmcreate-hb-wrapper-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
            System.IO.File.WriteAllText(wrapperPath, innerScript.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));

            string wslWrapper = "/mnt/" + wrapperPath[..1].ToLowerInvariant() + wrapperPath[2..].Replace('\\', '/');
            string probe = $@"
wfile='{wslWrapper}'
fifo=/tmp/vmcreate-hb.$$
out=/tmp/vmcreate-hbo.$$
rm -f ""$fifo"" ""$out""; mkfifo ""$fifo""
( timeout 5 cat ""$fifo"" > ""$out"" ) &
reader=$!
bash ""$wfile"" >""$fifo"" 2>/dev/null
wrc=$?
rstart=$(date +%s%3N)
wait ""$reader""; rrc=$?
rend=$(date +%s%3N)
saw=$(tr -d '\r\n' < ""$out"")
rm -f ""$fifo"" ""$out""
echo ""EOF_AFTER_MS=$((rend-rstart)) READER_RC=$rrc WRAPPER_RAN=$saw""
";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = wslExe,
                    Arguments = "-d Ubuntu -u root bash -s",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardInput.Write(probe.Replace("\r\n", "\n"));
                p.StandardInput.Close();
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                if (p.ExitCode != 0)
                    Assert.Fail($"probe exited {p.ExitCode}: {stderr.Trim()}");
                return stdout.Trim();
            }
            finally
            {
                try { System.IO.File.Delete(wrapperPath); } catch (System.IO.IOException) { }
            }
        }

        [TestMethod]
        public void InnerScript_SudoShim_MapsMinusUToRunuserEnv_AndDropsFlags()
        {
            var shell = Create(out _);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("sudo -u alice VAR=1 kwriteconfig6 --file x"));

            StringAssert.Contains(inner, "runuser -u \"$user\" -- env \"$@\"");
            StringAssert.Contains(inner, "env \"$@\"");
            // Unknown/password flags shift away.
            StringAssert.Contains(inner, "-*) shift ;;");
        }

        [TestMethod]
        public void SudoShim_ParsesSudoFlagRules_BashVerified()
        {
            // Executes the REAL generated shim in bash and checks flag
            // parsing through behavior, not string shapes — the production
            // bug ("runuser: user -- does not exist", VirtualBox's
            // uninstall.sh tree calling sudo -u --) lived exactly here and
            // a string-only pin missed it. 'id -u' reports the resolved uid:
            // a parse that misreads the username exits non-zero (runuser)
            // or prints garbage.
            string runner = BuildShimOnlyScript();
            // The production killer: '--' as the -u value.
            AssertShimOk(runner, new[] { "-u", "--", "id", "-u" }, "0",
                "sudo -u -- cmd must treat -- as end-of-options, not a username");
            // Empty -u value.
            AssertShimOk(runner, new[] { "-u", "", "--", "id", "-u" }, "0",
                "an empty -u value must be dropped, not passed through");
            // Option-shaped -u value (a following flag, not a username).
            AssertShimOk(runner, new[] { "-u", "-n", "id", "-u" }, "0",
                "an option-shaped -u value must be dropped");
            // Lone '--' terminator.
            AssertShimOk(runner, new[] { "--", "id", "-u" }, "0",
                "bare -- must end option parsing and be consumed");
            // The correct forms keep working.
            AssertShimOk(runner, new[] { "-u", "root", "id", "-u" }, "0",
                "sudo -u root cmd maps to runuser");
            AssertShimOk(runner, new[] { "--user=root", "id", "-u" }, "0",
                "--user=NAME is honored like -u NAME");
            AssertShimOk(runner, new[] { "-u", "root", "--", "id", "-u" }, "0",
                "-u root -- cmd keeps runuser semantics");
        }

        [TestMethod]
        public void SudoShim_VarPrefixes_StillRouteThroughEnv()
        {
            string runner = BuildShimOnlyScript();
            AssertShimOk(runner, new[] { "FOO=bar", "printenv", "FOO" }, "bar",
                "sudo VAR=val cmd must exec via env, not a binary named VAR=…");
            AssertShimOk(runner, new[] { "-n", "FOO=bar", "printenv", "FOO" }, "bar",
                "dropped flags must not disturb the env prefix");
        }

        /// <summary>
        /// Extracts the sudo() shim from the real wrapper. The appended
        /// <c>sudo "$@"</c> makes the file a runner: piping argv through
        /// the GENERATED shim exercises the production code path verbatim,
        /// not a reimplementation that could drift from it.
        /// </summary>
        private static string BuildShimOnlyScript()
        {
            var shell = Create(out _, label: null);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("true"));
            // The shim is everything up to the subshell-wrapped command
            // (which the newline fix guarantees starts on its own line).
            int tail = inner.LastIndexOf("\n( true )", StringComparison.Ordinal);
            Assert.IsTrue(tail > 0, "wrapper must carry the shim on its own lines before the subshell");
            return inner[..tail] + "\nsudo \"$@\"\n";
        }

        /// <summary>Runs one shim invocation in WSL Ubuntu and asserts the
        /// exit code is 0 and the trimmed stdout equals
        /// <paramref name="expected"/>.</summary>
        private static void AssertShimOk(string script, string[] args, string expected, string because)
        {
            string result = RunBash(script, args);
            Assert.AreEqual(expected, result.Trim(), $"[{string.Join(" ", args)}] {because}");
        }

        private static string RunBash(string script, string[] args)
        {
            // Script rides stdin (bash -s): no Windows→WSL path translation.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d Ubuntu -u root bash -s -- {string.Join(' ', args.Select(a => $"'{a.Replace("'", "'\\''")}'"))}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardInput.Write(script.Replace("\r\n", "\n"));
            p.StandardInput.Close();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            if (p.ExitCode != 0)
                Assert.Fail($"bash exited {p.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        private static string? FindWsl()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            string wsl = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            return System.IO.File.Exists(wsl) ? wsl : null;
        }

        [TestMethod]
        public void InnerScript_SudoShim_EnvAbsorbsVarAssignments()
        {
            // Steps use "sudo DEBIAN_FRONTEND=noninteractive apt-get …";
            // "$@" would exec a binary literally named "DEBIAN_FRONTEND=…",
            // so the no-user branch must route through env too.
            var shell = Create(out _);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("sudo DEBIAN_FRONTEND=noninteractive apt-get install -y x"));

            StringAssert.Contains(inner, "env \"$@\"");
            // The bare passthrough branch must be gone.
            Assert.IsFalse(inner.Contains("else\n    \"$@\"\n  fi", StringComparison.Ordinal),
                "the no-user branch must invoke env, not a bare \"$@\"");
        }

        [TestMethod]
        public void InnerScript_IncludesHeartbeatLoop_WhenLabelSet()
        {
            var shell = Create(out _, label: "Install OpenVPN");
            string inner = ExtractInnerScript(shell.BuildChrootCommand("apt-get install -y openvpn"));

            StringAssert.Contains(inner, "echo VMCREATE:MSG:'Install OpenVPN'");
            StringAssert.Contains(inner, "trap 'kill $_hb 2>/dev/null' EXIT");
        }

        [TestMethod]
        public void InnerScript_HeartbeatIsSelfTerminating()
        {
            // The loop must watch the wrapper ($$): a step that resets the
            // EXIT trap (EnablePasswordlessSudo style) would otherwise
            // orphan an infinite loop that holds stdout open and hangs the
            // SSH session until the total timeout.
            var shell = Create(out _, label: "Any");
            string inner = ExtractInnerScript(shell.BuildChrootCommand("true"));

            StringAssert.Contains(inner, "while kill -0 $$ 2>/dev/null; do");
            Assert.IsFalse(inner.Contains("while :; do", StringComparison.Ordinal),
                "an unconditional heartbeat loop is not survivable");
        }

        [TestMethod]
        public void InnerScript_RunsCommandInSubshell()
        {
            // The subshell is what protects the wrapper's EXIT trap (and the
            // heartbeat kill) from a hostile step command.
            var shell = Create(out _, label: null);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("sudo bash /tmp/tool.sh"));

            StringAssert.Contains(inner, "\n( sudo bash /tmp/tool.sh )");
            Assert.IsTrue(inner.TrimEnd().EndsWith(")", StringComparison.Ordinal),
                "the subshell must be the last construct — nothing may run after it");
        }

        [TestMethod]
        public void InnerScript_NoLabel_WrapperIsParseableBash()
        {
            // Regression: the no-label path once emitted 'sudo(){…}( cmd )'
            // — no newline between the shim's closing brace and the
            // subshell — which is a bash syntax error, so every chroot
            // command without a step label failed to parse at all. Running
            // the extracted shim end-to-end in real bash proves the
            // generated code parses AND executes.
            string script = BuildShimOnlyScript();
            AssertShimOk(script, new[] { "true" }, string.Empty,
                "the no-label wrapper must execute (an unparseable wrapper fails here)");
        }

        [TestMethod]
        public void InnerScript_OmitsHeartbeat_WhenNoLabel()
        {
            var shell = Create(out _, label: null);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("true"));

            Assert.IsFalse(inner.Contains("VMCREATE:MSG", StringComparison.Ordinal),
                "no heartbeat without an attributable step label");
        }

        [TestMethod]
        public void InnerScript_EscapesStepCommandQuotes_AndUsesLfOnly()
        {
            var shell = Create(out _);
            string wrapped = shell.BuildChrootCommand(@"echo 'it'\''s' ""quoted"" $VAR");

            // Un-escaping the payload must yield the step command verbatim:
            // the wrapper must transport quoting, not mangle it.
            StringAssert.Contains(ExtractInnerScript(wrapped), @"echo 'it'\''s' ""quoted"" $VAR");
            // The wrapped command must never carry CRLF: bash on the guest
            // treats \r as a real character and chokes on it.
            Assert.IsFalse(wrapped.Contains("\r", StringComparison.Ordinal),
                "wrapped command must be LF-only");
        }

        [TestMethod]
        public async Task RunCommandAsync_StreamsThroughIsoShell_AndFiltersProtocolNoise()
        {
            var shell = Create(out var iso);

            string returned = await shell.RunCommandAsync(
                "sudo bash /tmp/tool.sh", TimeSpan.FromMinutes(5), CancellationToken.None);

            Assert.AreEqual(1, iso.StreamedCommands.Count, "must go through the streaming path (idle budget)");
            StringAssert.StartsWith(iso.StreamedCommands[0].Command, "sudo chroot /mnt/new /usr/bin/env -i");
            Assert.AreEqual(TimeSpan.FromMinutes(5), iso.StreamedCommands[0].Timeout);
            Assert.AreEqual(TimeSpan.FromMinutes(5), iso.StreamedCommands[0].IdleTimeout);

            // Step-visible stdout keeps real output verbatim and drops the
            // VMCREATE: protocol lines (steps parse RESULT= prefixes, not chatter).
            StringAssert.Contains(returned, "XRDP_POSTBOOT_RESULT=already");
            StringAssert.Contains(returned, "plain output line");
            Assert.IsFalse(returned.Contains("VMCREATE:MSG", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task RunCommandAsync_DefaultOverload_UsesCycleTotalBudget()
        {
            var shell = Create(out var iso);

            await shell.RunCommandAsync("true", CancellationToken.None);

            Assert.AreEqual(TimeSpan.FromMinutes(30), iso.StreamedCommands[0].Timeout);
        }

        [TestMethod]
        public async Task CopyMethods_PrefixGuestPaths()
        {
            var shell = Create(out var iso);

            await shell.CopyContentAsync("data", "/etc/vmcreate/x.conf", CancellationToken.None);
            await shell.CopySecretAsync("secret", "/etc/vmcreate/key", CancellationToken.None);
            await shell.CopyFileAsync(@"C:\host\file", "/opt/tool.sh", CancellationToken.None);

            CollectionAssert.AreEqual(new[]
            {
                "/mnt/new/etc/vmcreate/x.conf",
                "/mnt/new/etc/vmcreate/key",
                "/mnt/new/opt/tool.sh",
            }, iso.CopiedPaths);
        }

        [TestMethod]
        public async Task RunCommandWithStdinAsync_WrapsCommand_AndForwardsStdin()
        {
            var shell = Create(out var iso);

            await shell.RunCommandWithStdinAsync("sudo -S id", "password", CancellationToken.None);

            var call = iso.StdinCalls.Single();
            StringAssert.StartsWith(call.Command, "sudo chroot /mnt/new /usr/bin/env -i");
            StringAssert.Contains(ExtractInnerScript(call.Command), "sudo -S id");
            Assert.AreEqual("password", call.Stdin);
        }

        /// <summary>Recovers the inner bash -c payload from the wrapped
        /// command, un-escaping the POSIX single-quote re-opening so tests
        /// assert against the script bash will actually parse.</summary>
        private static string ExtractInnerScript(string wrapped)
        {
            int start = wrapped.IndexOf("bash -c '", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "wrapper must carry a bash -c payload");
            start += "bash -c '".Length;
            return wrapped[start..^1].Replace("'\\''", "'");
        }

        /// <summary>Captures warnings so the one-shot plymouth-death latch
        /// is assertable without a logging framework harness.</summary>
        private sealed class TestLogger : ILogger
        {
            public List<string> Warnings { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    lock (Warnings) Warnings.Add(formatter(state, exception));
            }
        }

        /// <summary>ISO shell whose plymouth is dead: commands succeed but
        /// the --ping marker never comes back.</summary>
        private sealed class DeadSplashIsoShell : SshGuestShell
        {
            public DeadSplashIsoShell() : base(Mock.Of<ILogger>(), "TestVM", "key") { }

            public override Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
                => Task.FromResult(""); // no SPLASH_ALIVE marker = daemon dead
        }

        /// <summary>
        /// ISO-side stand-in: records streaming calls so tests can assert the
        /// wrapping without a real ssh session.
        /// </summary>
        private sealed class FakeIsoShell : SshGuestShell
        {
            public FakeIsoShell() : base(Mock.Of<ILogger>(), "TestVM", "key") { }

            public sealed record StreamedCall(string Command, TimeSpan Timeout, TimeSpan IdleTimeout);
            public List<StreamedCall> StreamedCommands { get; } = new();
            public List<string> CopiedPaths { get; } = new();
            public List<(string Command, string Stdin)> StdinCalls { get; } = new();
            public List<string> SplashCommands { get; } = new();
            public Exception? SplashThrows { get; set; }

            public override Task<string> RunStreamingCommandAsync(
                string linuxCommand, TimeSpan timeout, TimeSpan idleTimeout,
                Action<string> onStdoutLine, CancellationToken ct)
            {
                StreamedCommands.Add(new StreamedCall(linuxCommand, timeout, idleTimeout));
                foreach (string line in new[]
                {
                    "VMCREATE:MSG:Sync Timezone",       // heartbeat noise
                    "plain output line",
                    "XRDP_POSTBOOT_RESULT=already",
                })
                    onStdoutLine(line);
                return Task.FromResult("");
            }

            public override Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (SplashThrows is not null) throw SplashThrows;
                if (command.Contains("plymouth", StringComparison.Ordinal))
                {
                    SplashCommands.Add(command);
                    // Simulate a live plymouthd: the --ping probe's marker
                    // comes back on stdout (empty string = daemon dead).
                    return Task.FromResult(command.Contains("--ping", StringComparison.Ordinal)
                        ? "VMCREATE:SPLASH_ALIVE"
                        : "");
                }
                return Task.FromResult("");
            }

            public override Task CopyContentAsync(string content, string guestPath, CancellationToken ct)
            {
                CopiedPaths.Add(guestPath);
                return Task.CompletedTask;
            }

            public override Task CopySecretAsync(string content, string guestPath, CancellationToken ct)
            {
                CopiedPaths.Add(guestPath);
                return Task.CompletedTask;
            }

            public override Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct)
            {
                CopiedPaths.Add(guestPath);
                return Task.CompletedTask;
            }

            public override Task<string> RunCommandWithStdinAsync(string command, string stdinContent, CancellationToken ct)
            {
                StdinCalls.Add((command, stdinContent));
                return Task.FromResult("");
            }
        }
    }
}
