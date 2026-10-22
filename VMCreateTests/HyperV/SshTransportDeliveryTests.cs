using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// End-to-end delivery guard for <see cref="SshTransport"/>: a real
    /// ssh.exe → sshd → guest bash round-trip, pinning that every byte of
    /// the linuxCommand — double quotes included — arrives intact.
    /// </summary>
    /// <remarks>
    /// The transport once delivered commands through a raw hand-quoted
    /// <c>Arguments</c> string; Windows argv parsing consumed every double
    /// quote in it, and ssh.exe joins its argv without re-quoting — so any
    /// <c>"</c> in a payload silently vanished before the guest. The chroot
    /// sudo shim degraded to <c>[ -n $user ]</c> (true when empty) and
    /// deployments died with "runuser: user -- does not exist" on the very
    /// first sudo call. Shape tests on the argv list cannot catch a revert
    /// at the spawn site — only a live round-trip can.
    /// Requires a local sshd (e.g. WSL with ssh.socket and the vmcreate key
    /// authorized for root) and skips as inconclusive when absent.
    /// </remarks>
    [TestClass]
    public class SshTransportDeliveryTests
    {
        private const string Host = "127.0.0.1";
        private const string User = "root";

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

        private static string KeyPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "vmcreate_ed25519");

        [TestMethod]
        public async Task ExecuteAsync_DeliversDoubleQuotesToGuestBashIntact()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(KeyPath) || !ProbeSshd())
                Assert.Inconclusive("local sshd + vmcreate key not available — delivery round-trip skipped");

            // [ -n "$u" ] with u empty is FALSE — but only while the double
            // quotes arrive: stripped (the old raw-Arguments delivery) it
            // becomes the always-true [ -n $u ] and exits 41.
            string command = "u=; if [ -n \"$u\" ]; then exit 41; fi; echo DELIVERY_OK";

            string stdout = await SshTransport.ExecuteAsync(
                NullLogger.Instance, "delivery-test", KeyPath, Host, User,
                knownHostsFile: null, command, Budget, CancellationToken.None);

            StringAssert.Contains(stdout, "DELIVERY_OK",
                "the guest ran the command with its double quotes intact");
        }

        [TestMethod]
        public async Task ExecuteAsync_ExitCodeReflectsGuestCommandFailure()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(KeyPath) || !ProbeSshd())
                Assert.Inconclusive("local sshd + vmcreate key not available — delivery round-trip skipped");

            // Sanity in the other direction: a failing guest command must
            // surface as a transport exception, not a silent success —
            // proving the round-trip measures the guest's real outcome.
            Exception? ex = null;
            try
            {
                await SshTransport.ExecuteAsync(
                    NullLogger.Instance, "delivery-test", KeyPath, Host, User,
                    knownHostsFile: null, "echo DYING; exit 7", Budget, CancellationToken.None);
            }
            catch (Exception e)
            {
                ex = e;
            }

            Assert.IsNotNull(ex, "guest exit 7 must throw");
            StringAssert.Contains(ex.Message, "exit code 7");
        }

        /// <summary>
        /// True when the local sshd answers for root with the vmcreate key.
        /// Any failure (no relay, no sshd, key rejected) means the lab
        /// endpoint is absent — inconclusive, not broken.
        /// </summary>
        private static bool ProbeSshd()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SshTransport.SshExePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string arg in new[]
                {
                    "-i", KeyPath,
                    "-o", "BatchMode=yes",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=NUL",
                    "-o", "ConnectTimeout=5",
                    $"{User}@{Host}",
                    "echo ok",
                })
                    psi.ArgumentList.Add(arg);

                using var p = Process.Start(psi)!;
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode == 0 && stdout.Contains("ok");
            }
            catch
            {
                return false;
            }
        }
    }
}
