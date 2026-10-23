using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Executes the embedded ISO scripts (clone_disk.sh with common.sh) in
    /// WSL bash — behavior tests for phase logic that bash -n syntax checks
    /// cannot see.
    /// </summary>
    /// <remarks>
    /// The sub-command split (prepare → host chroot steps → finalize) once
    /// left clone_disk.sh calling detect_disks in EVERY phase: detection
    /// classifies the new disk by its emptiness, so in finalize — after the
    /// clone wrote partitions to it — both disks have partitions, the
    /// "empty" classification can never match again, and every deployment
    /// died at the very last ISO step with "Could not detect new (empty) or
    /// old disk". Only executing the real script under the failing phase
    /// catches this class of bug.
    /// </remarks>
    [TestClass]
    public class IsoScriptPhaseTests
    {
        [TestMethod]
        public void CloneDisk_Finalize_Completes_WithoutEmptyDiskDetection()
        {
            string? bash = FindWsl();
            if (bash is null)
            {
                Assert.Inconclusive("WSL not available — clone_disk.sh phase test skipped");
                return;
            }

            string script = LoadEmbeddedScript("clone_disk.sh");
            string common = LoadEmbeddedScript("common.sh");

            // Stage dir on the Windows side, handed to WSL via /mnt.
            string stage = Path.Combine(Path.GetTempPath(), "vmcreate-iso-phase-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "clone_disk.sh"), script.Replace("\r\n", "\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "common.sh"), common.Replace("\r\n", "\n"), new UTF8Encoding(false));
            try
            {
                // Simulate the post-prepare world as finalize finds it: the
                // target mounted (plain dir suffices — findmnt simply finds
                // no source and skips the ESP section) with partitions
                // already written, plus an ESP skeleton so the grub redirect
                // path exercises. No SSH_PUBKEY: automation-user creation is
                // skipped, keeping the sandbox chroot-free.
                string wslStage = ToWslPath(stage);
                (int exit, string output, string error) = RunInWsl(bash, $@"
set -e
mkdir -p /opt/vmcreate
cp {wslStage}/clone_disk.sh {wslStage}/common.sh /opt/vmcreate/
mkdir -p /mnt/new/boot/efi/EFI/BOOT
bash /opt/vmcreate/clone_disk.sh finalize
");

                Assert.AreEqual(0, exit,
                    $"finalize must complete. exit={exit}{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
                StringAssert.Contains(output, "clone_disk.sh finalize completed",
                    "finalize must run to its completion marker");
                Assert.IsFalse(output.Contains("Could not detect new (empty) or old disk"),
                    "finalize must not classify disks by emptiness — after cloning the new disk has partitions");
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { }
                RunInWsl(bash, "umount -lf /mnt/new 2>/dev/null; rm -rf /mnt/new /opt/vmcreate; true");
            }
        }

        private static (int Exit, string Output, string Error) RunInWsl(string wslExe, string script)
        {
            var psi = new ProcessStartInfo
            {
                FileName = wslExe,
                Arguments = "-d Ubuntu -u root -- bash -s",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(script.Replace("\r\n", "\n"));
            p.StandardInput.Close();
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return (p.ExitCode, output, error);
        }

        private static string? FindWsl()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            string wsl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            return File.Exists(wsl) ? wsl : null;
        }

        private static string ToWslPath(string windowsPath) =>
            "/mnt/" + windowsPath[..1].ToLowerInvariant() + windowsPath[2..].Replace('\\', '/');

        private static string LoadEmbeddedScript(string scriptFileName)
        {
            var assembly = typeof(global::VMCreate.InstallLamcoRdpStep).Assembly;
            string suffix = "." + scriptFileName;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidOperationException($"stream for {name} missing");
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
            Assert.Fail($"Embedded script resource '{scriptFileName}' not found in VMCreate assembly.");
            throw new InvalidOperationException("unreachable");
        }
    }
}
