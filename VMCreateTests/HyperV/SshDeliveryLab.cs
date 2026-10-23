using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// The local ssh delivery lab: WSL Ubuntu running sshd (port 22 relayed
    /// by wslrelay to 127.0.0.1), the vmcreate key authorized for root, and
    /// a bind-mounted <c>/mnt/new</c> so chroot wrappers resolve. Tests that
    /// need a REAL ssh.exe → sshd → bash round-trip probe this; when the lab
    /// is absent they skip as inconclusive.
    /// </summary>
    internal static class SshDeliveryLab
    {
        // IPv6 loopback: wslrelay reliably relays [::1]; the IPv4 relay can
        // drop its 127.0.0.1 binding when WSL services restart mid-session.
        public const string Host = "::1";
        public const string User = "root";

        public static string KeyPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh", "vmcreate_ed25519");

        /// <summary>
        /// True when the lab endpoint answers and the chroot root exists —
        /// probes once per call, cheap enough for a test's Assert.Inconclusive
        /// gate. Any failure means the lab is absent, not the code broken.
        /// </summary>
        public static bool IsAvailable()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(KeyPath))
                return false;

            try
            {
                // The chroot root bind must exist before the ssh probe tests
                // for it (WSL recycles drop the mount).
                var mount = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\wsl.exe"),
                    Arguments = "-d Ubuntu -u root -- bash -c 'mkdir -p /run/sshd /mnt/new; mountpoint -q /mnt/new || mount --bind / /mnt/new; test -x /mnt/new/usr/bin/env && echo MOUNT_OK'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (mount is null)
                    return false;
                string mountOut = mount.StandardOutput.ReadToEnd();
                mount.WaitForExit(20000);
                if (mount.ExitCode != 0 || !mountOut.Contains("MOUNT_OK"))
                    return false;

                var probe = new ProcessStartInfo
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
                    "echo LAB_OK",
                })
                    probe.ArgumentList.Add(arg);
                using var p = Process.Start(probe)!;
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                return p.ExitCode == 0 && stdout.Contains("LAB_OK");
            }
            catch
            {
                return false;
            }
        }
    }
}
