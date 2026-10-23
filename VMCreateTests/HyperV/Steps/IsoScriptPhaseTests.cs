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
        public void ForceX11_FastPathBlock_SkipsOnlyWhenX11Only()
        {
            // Executes the REAL fast-path block (extracted from the embedded
            // script at test time — no drift) against seeded session files,
            // with its absolute /usr/share paths sandboxed. This is the
            // decision logic that made "Force X11" a no-op rewrite on
            // X11-only desktops like PwnCloudOS.
            string? bash = FindWsl();
            if (bash is null)
            {
                Assert.Inconclusive("WSL not available — force_x11.sh fast-path test skipped");
                return;
            }

            string script = LoadEmbeddedScript("force_x11.sh");
            const string startMarker = "# -- Fast path: desktop is already X11-only";
            int start = script.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "force_x11.sh must carry the fast-path block");
            // The block ends at its 'exit 0' + 'fi' — the skip's exit.
            int exitIdx = script.IndexOf("    exit 0", start, StringComparison.Ordinal);
            Assert.IsTrue(exitIdx > start, "fast-path block must end with an exit 0 skip");
            int blockEnd = script.IndexOf("\nfi", exitIdx, StringComparison.Ordinal);
            Assert.IsTrue(blockEnd > exitIdx, "fast-path block must close");

            string block = script[start..(blockEnd + 3)]
                .Replace("/usr/share/xsessions", "$SANDBOX/usr/share/xsessions")
                .Replace("/usr/share/wayland-sessions", "$SANDBOX/usr/share/wayland-sessions")
                .Replace("exit 0", "echo FAST_PATH_SKIPPED");

            string stage = Path.Combine(Path.GetTempPath(), "vmcreate-forcex11-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "block.sh"),
                "SANDBOX=\"$1\"\n" + block.Replace("\r\n", "\n") + "\n",
                new UTF8Encoding(false));
            try
            {
                string wslStage = ToWslPath(stage);
                // X11-only shape (PwnCloudOS): skip expected.
                (int exit, string output, string error) = RunInWsl(bash, $@"
set -e
S=/tmp/vmcreate-x11-sandbox; rm -rf $S
mkdir -p $S/usr/share/xsessions $S/usr/share/wayland-sessions
printf '[Desktop Entry]\nName=LightDM Xsession\n' > $S/usr/share/xsessions/lightdm-xsession.desktop
restored=0 x11_session=lightdm-xsession bash {wslStage}/block.sh $S
");
                Assert.AreEqual(0, exit, $"block failed: {error}{Environment.NewLine}{output}");
                StringAssert.Contains(output, "FAST_PATH_SKIPPED", "X11-only desktops must take the skip path");

                // Wayland present: no skip.
                (exit, output, error) = RunInWsl(bash, $@"
set -e
S=/tmp/vmcreate-x11-sandbox; rm -rf $S
mkdir -p $S/usr/share/xsessions $S/usr/share/wayland-sessions
printf '[Desktop Entry]\nName=Plasma X11\n' > $S/usr/share/xsessions/plasmax11.desktop
printf '[Desktop Entry]\nName=Plasma\n' > $S/usr/share/wayland-sessions/plasma.desktop
restored=0 x11_session=plasmax11 bash {wslStage}/block.sh $S
");
                Assert.AreEqual(0, exit, $"block failed: {error}{Environment.NewLine}{output}");
                Assert.IsFalse(output.Contains("FAST_PATH_SKIPPED", StringComparison.Ordinal),
                    "a desktop with Wayland sessions must run full enforcement");

                // Previously-disabled Wayland leftovers: no skip (they must
                // be handled by the full path's restore logic).
                (exit, output, error) = RunInWsl(bash, $@"
set -e
S=/tmp/vmcreate-x11-sandbox; rm -rf $S
mkdir -p $S/usr/share/xsessions $S/usr/share/wayland-sessions/disabled
printf '[Desktop Entry]\nName=X\n' > $S/usr/share/xsessions/x.desktop
printf '[Desktop Entry]\nName=Plasma\n' > $S/usr/share/wayland-sessions/disabled/plasma.desktop
restored=0 x11_session=x bash {wslStage}/block.sh $S
");
                Assert.AreEqual(0, exit, $"block failed: {error}{Environment.NewLine}{output}");
                Assert.IsFalse(output.Contains("FAST_PATH_SKIPPED", StringComparison.Ordinal),
                    "disabled Wayland leftovers must run the full path");
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { }
                RunInWsl(bash, "rm -rf /tmp/vmcreate-x11-sandbox; true");
            }
        }

        [TestMethod]
        public void ForceX11_FastPath_PrecedesDisplayManagerWrites()
        {
            // String-level ordering guard: the skip must come before every
            // display-manager config write so an X11-only desktop never gets
            // them, and the writes must still exist after it for Wayland
            // desktops.
            string script = LoadEmbeddedScript("force_x11.sh");

            int fastPath = script.IndexOf("# -- Fast path: desktop is already X11-only", StringComparison.Ordinal);
            int sddm = script.IndexOf("Step 3: Configure SDDM", StringComparison.Ordinal);
            int lightdm = script.IndexOf("Step 4: Configure LightDM", StringComparison.Ordinal);
            int gdm = script.IndexOf("Step 5: Configure GDM", StringComparison.Ordinal);

            Assert.IsTrue(fastPath >= 0 && sddm > fastPath && lightdm > fastPath && gdm > fastPath,
                "the fast-path skip must precede all display-manager config writes");
        }

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
            // Read on background threads with a hard deadline: a wedged
            // guest-side command (e.g. a mount that never returns) must not
            // block ReadToEnd forever — the test fails fast instead.
            string output = "", error = "";
            var reader = Task.Run(() => { output = p.StandardOutput.ReadToEnd(); });
            var errReader = Task.Run(() => { error = p.StandardError.ReadToEnd(); });
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("WSL bash did not exit within 120s — sandboxed script wedged");
            }
            Task.WhenAll(reader, errReader).Wait(5000);
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
