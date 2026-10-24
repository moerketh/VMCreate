using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Behavioral tests for the first-boot vsock gate of
    /// <c>install_xrdp_postboot.sh</c>, executed in WSL bash with
    /// <c>systemctl</c>, <c>ss</c> and <c>sleep</c> stubbed on PATH. The
    /// gate decides between vsock / TCP-recovery / unverified, and every
    /// verdict must reflect an OBSERVED listener (or an observed daemon
    /// for the TCP recovery) — never an "active service" assumption:
    /// <list type="bullet">
    /// <item>vsock requires the AF_VSOCK listener probe to pass (a daemon
    /// that swallowed the vsock config and serves nothing is not
    /// vsock).</item>
    /// <item>an unstarted/mid-start daemon gets one restart on the vsock
    /// config before any TCP fallback (first boot can simply be
    /// slow).</item>
    /// <item>the TCP recovery restores the chroot half's backup, requires
    /// the daemon to come back, and deletes the backup once the transport
    /// is settled — a lingering backup lets a later re-run restore and
    /// restart over a live RDP session.</item>
    /// </list>
    /// String-order tests cannot pin any of this; these run the gate.
    /// Skipped (Inconclusive) where WSL is unavailable, like the other
    /// script-behavior suites. Not parallelized: every scenario stages
    /// /etc/xrdp in the one WSL distro, so concurrent runs would race.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public sealed class InstallXrdpPostBootGateTests
    {
        private static string? _bash;

        [ClassInitialize]
        public static void FindBash(TestContext _)
        {
            if (!OperatingSystem.IsWindows())
                return;
            string wsl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl))
                return;
            // wsl.exe existing proves nothing (GitHub runners ship it with
            // no distro); probe the distro itself — unusable means skip.
            try
            {
                var probe = RunWsl(wsl, "echo ok", 15000);
                if (probe.Exit == 0 && probe.Output.Trim() == "ok")
                    _bash = wsl;
            }
            catch { /* unusable WSL: tests will be inconclusive */ }
        }

        [TestMethod]
        public void Gate_VsocklessBuild_RestoresTcpAndReportsTcp()
        {
            // A: the daemon runs but never serves AF_VSOCK — recovery must
            // restore the TCP backup, bring the daemon back, report tcp,
            // and delete the backup.
            var r = RunScenario("A");
            AssertTransport(r, "tcp");
            AssertIniPort(r, "port=3389", "the TCP backup must be restored");
            AssertBackupGone(r);
        }

        [TestMethod]
        public void Gate_SlowStart_RecoversVsockOnRestart()
        {
            // B: the daemon is mid-start when probed and serves vsock once
            // restarted on the vsock config — Enhanced Session must NOT be
            // given up for the TCP fallback.
            var r = RunScenario("B");
            AssertTransport(r, "vsock");
            AssertIniPort(r, "port=vsock://-1:3389", "the vsock config must stay");
            AssertBackupGone(r);
        }

        [TestMethod]
        public void Gate_DaemonDiesOnRetry_IsUnverifiedNotVsock()
        {
            // C: the daemon runs without the listener and dies on the
            // retry restart — the verdict must be unverified (no listener
            // was ever observed), never vsock, and the backup is settled.
            var r = RunScenario("C");
            AssertTransport(r, "unverified");
            AssertBackupGone(r);
        }

        [TestMethod]
        public void Gate_HealthyVsock_KeepsConfigAndSettlesBackup()
        {
            // The plain healthy path: listener already up on the first
            // probe — vsock, no restart, backup deleted.
            var r = RunScenario("D");
            AssertTransport(r, "vsock");
            AssertIniPort(r, "port=vsock://-1:3389", "the vsock config must stay");
            AssertBackupGone(r);
        }

        [TestMethod]
        public void Gate_VsocklessBuild_NoSystemctl_ReportsUnverifiedWithoutRewriting()
        {
            // A vsock-less build whose manager cannot restart the daemon
            // (broken/non-PID1 systemd — what "no working systemctl"
            // looks like on a real guest). Nothing can be verified or
            // recovered: the gate must report unverified, must not claim
            // tcp, and settles the backup either way. (An environment
            // with literally no systemctl binary cannot be staged in
            // WSL Ubuntu — /bin is symlinked to /usr/bin — so the
            // broken-manager variant pins the same gate branch.)
            var r = RunScenario("A-brokenmanager");
            AssertTransport(r, "unverified");
            AssertBackupGone(r);
        }

        // ── harness ───────────────────────────────────────────────────

        private sealed record ScenarioResult(int Exit, string Output, string IniPort, bool BackupExists);

        /// <summary>
        /// Runs the gate section of the real script in a throwaway WSL
        /// sandbox: /etc/xrdp/xrdp.ini carries the chroot-written
        /// vsock-only config (staged at the real path, with anything
        /// present saved and restored), the .vmcreate-bak the TCP
        /// original, and the scenario stubs drive systemctl/ss behavior
        /// (A=never vsock, B=serves vsock after restart, C=dies after
        /// restart, D=healthy, A-nosystemctl=no manager). The harness is
        /// a file executed as <c>bash &lt;file&gt;</c>, not stdin —
        /// heredocs inside a stdin script interact badly with the WSL
        /// bridge.
        /// </summary>
        private static ScenarioResult RunScenario(string scenario)
        {
            if (_bash is null)
                Assert.Inconclusive("WSL not available — install_xrdp_postboot gate test skipped");

            string script = LoadEmbeddedScript("install_xrdp_postboot.sh");

            // Stage on the Windows side; WSL reads it through /mnt.
            string stage = Path.Combine(Path.GetTempPath(),
                "vmcreate-xrdp-gate-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stage);
            string gateOnly = ExtractGateSection(script);
            File.WriteAllText(Path.Combine(stage, "gate.sh"), gateOnly.Replace("\r\n", "\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "harness.sh"), BuildHarness(scenario), new UTF8Encoding(false));
            try
            {
                string wslStage = "/mnt/" + stage[..1].ToLowerInvariant() + stage[2..].Replace('\\', '/');
                var (exit, output, error) = RunWsl(_bash, $"bash {wslStage}/harness.sh", 60000);
                Assert.AreEqual(0, exit,
                    $"sandbox harness failed. stderr:{Environment.NewLine}{error}{Environment.NewLine}stdout:{Environment.NewLine}{output}");

                string iniPort = Between(output, "__INI=", "\n");
                bool backup = output.Contains("__BAK=present", StringComparison.Ordinal);
                return new ScenarioResult(exit, output, iniPort, backup);
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// The per-scenario sandbox harness. Runs next to gate.sh; the
        /// scenario is baked in so no quoting crosses the WSL bridge.
        /// </summary>
        private static string BuildHarness(string scenario) => $@"#!/bin/bash
set -u
ROOT=$(mktemp -d)
SC='{scenario}'
        export ROOT SC
        DIR=$(dirname ""$0"")
SAVED=""""

# The gate resolves /etc/xrdp/xrdp.ini — stage the configs at the real
# path (root), saving anything that was there before.
for f in /etc/xrdp/xrdp.ini /etc/xrdp/xrdp.ini.vmcreate-bak; do
  if [ -e ""$f"" ]; then mv ""$f"" ""$f.vmcreate-harness-save""; SAVED=""$SAVED $f""; fi
done
restore() {{
  rm -f /etc/xrdp/xrdp.ini /etc/xrdp/xrdp.ini.vmcreate-bak
  for f in $SAVED; do
    if [ -e ""$f.vmcreate-harness-save"" ]; then mv ""$f.vmcreate-harness-save"" ""$f""; fi
  done
  rm -rf ""$ROOT""
}}
trap restore EXIT

mkdir -p /etc/xrdp ""$ROOT/bin""
printf '[Globals]\nport=vsock://-1:3389\n' > /etc/xrdp/xrdp.ini
printf '[Globals]\nport=3389\n' > /etc/xrdp/xrdp.ini.vmcreate-bak

cat > ""$ROOT/bin/systemctl"" <<'STUB'
#!/bin/bash
M=""$ROOT/restarted""
if [ ""$1"" = is-active ] && [ ""$2"" = xrdp ]; then
  case ""$SC"" in
    A|A-brokenmanager|D) exit 0;;
    B) [ -f ""$M"" ] && exit 0 || exit 3;;
    C) [ -f ""$M"" ] && exit 3 || exit 0;;
  esac
fi
if [ ""$1"" = restart ] || [ ""$1"" = start ]; then
  [ ""$SC"" = A-brokenmanager ] && exit 1
  touch ""$M""
fi
exit 0
STUB
cat > ""$ROOT/bin/ss"" <<'STUB'
#!/bin/bash
M=""$ROOT/restarted""
case ""$SC"" in
  D) echo 'v_str LISTEN 0 0 *:3389 *:*';;
  B) [ -f ""$M"" ] && echo 'v_str LISTEN 0 0 *:3389 *:*';;
esac
exit 0
STUB
printf '#!/bin/bash\nexit 0\n' > ""$ROOT/bin/sleep""
chmod +x ""$ROOT/bin""/*

PATH=""$ROOT/bin:$PATH"" CHROOT_MODE=0 RESULT=already bash ""$DIR/gate.sh""
echo ""__EXIT=$?""
echo ""__INI=$(grep -m1 '^port=' /etc/xrdp/xrdp.ini || echo none)""
[ -f /etc/xrdp/xrdp.ini.vmcreate-bak ] && echo ""__BAK=present"" || echo ""__BAK=gone""
".Replace("\r\n", "\n");

        /// <summary>
        /// Cuts the script down to what the gate needs: the transport
        /// section (ini resolution + first-boot gate) through to the
        /// empty-ini check — the package-manager install section that
        /// precedes it never runs in the sandbox.
        /// </summary>
        private static string ExtractGateSection(string script)
        {
            int start = script.IndexOf("# ── Enhanced Session transport", StringComparison.Ordinal);
            Assert.IsTrue(start > 0, "transport section marker not found in the script");
            int end = script.IndexOf("if [ -z \"$INI\" ]; then", StringComparison.Ordinal);
            Assert.IsTrue(end > start, "gate section boundary (empty-ini check) not found after the transport marker");
            return script[start..end];
        }

        private static void AssertTransport(ScenarioResult r, string expected)
            => StringAssert.Contains(r.Output, $"XRDP_POSTBOOT_TRANSPORT={expected}",
                $"expected transport {expected}; output was:{Environment.NewLine}{r.Output}");

        private static void AssertIniPort(ScenarioResult r, string expected, string because)
            => StringAssert.Contains(r.IniPort ?? "", expected, because);

        private static void AssertBackupGone(ScenarioResult r)
            => Assert.IsFalse(r.BackupExists,
                "the backup must be deleted once the transport is settled — a lingering backup lets a later re-run restore-and-restart over a live session");

        private static string Between(string s, string start, string end)
        {
            int i = s.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return "";
            i += start.Length;
            int j = s.IndexOf(end, i, StringComparison.Ordinal);
            return j < 0 ? s[i..] : s[i..j];
        }

        private static (int Exit, string Output, string Error) RunWsl(string wslExe, string command, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = wslExe,
                Arguments = "-d Ubuntu -u root -- " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = "", error = "";
            var reader = Task.Run(() => { output = p.StandardOutput.ReadToEnd(); });
            var errReader = Task.Run(() => { error = p.StandardError.ReadToEnd(); });
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("WSL bash did not exit in time — sandboxed gate wedged");
            }
            Task.WhenAll(reader, errReader).Wait(5000);
            return (p.ExitCode, output, error);
        }

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
