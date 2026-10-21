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
        public void InnerScript_SudoShim_MapsMinusUToRunuserEnv_AndDropsFlags()
        {
            var shell = Create(out _);
            string inner = ExtractInnerScript(shell.BuildChrootCommand("sudo -u alice VAR=1 kwriteconfig6 --file x"));

            StringAssert.Contains(inner, "runuser -u \"$user\" -- env \"$@\"");
            StringAssert.Contains(inner, "env \"$@\"");
            // -u consumes its argument; unknown/password flags shift away.
            StringAssert.Contains(inner, "-u) user=\"$2\"; shift 2 ;;");
            StringAssert.Contains(inner, "-*) shift ;;");
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

            StringAssert.Contains(inner, "( sudo bash /tmp/tool.sh )");
            Assert.IsTrue(inner.TrimEnd().EndsWith(")", StringComparison.Ordinal),
                "the subshell must be the last construct — nothing may run after it");
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
                    return Task.FromResult("");
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
