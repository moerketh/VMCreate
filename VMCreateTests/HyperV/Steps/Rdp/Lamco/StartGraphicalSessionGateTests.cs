using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Behavioral tests for the readiness gate of
    /// <c>start_graphical_session.sh</c>, executed in WSL bash with
    /// <c>sudo</c>, <c>systemctl</c>, <c>journalctl</c>, <c>ss</c> and
    /// <c>sleep</c> stubbed on PATH. Both verdicts it used to get wrong were
    /// observed live on Parrot 7.4:
    /// <list type="bullet">
    /// <item>consent pending behind a realistic journal (far more than a
    /// pipe buffer after the match): under the script's pipefail,
    /// <c>journalctl | grep -q</c> returned 141 on a match, so the consent
    /// verdict was unreachable and the step reported a 120 s timeout.</item>
    /// <item>only the early vsock hold listening (server blocked on the
    /// consent dialog): <c>ss -l | grep :3389</c> matched the hold's socket
    /// and reported "listeners bound".</item>
    /// </list>
    /// Skipped (Inconclusive) where WSL is unavailable. Not parallelized:
    /// concurrent wsl.exe launches from parallel tests intermittently
    /// returned empty output with exit 0.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public sealed class StartGraphicalSessionGateTests
    {
        private static string? _wsl;

        [ClassInitialize]
        public static void FindWsl(TestContext _)
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
                    _wsl = wsl;
            }
            catch { /* unusable WSL: tests will be inconclusive */ }
        }

        [TestMethod]
        public void Gate_ConsentPendingBehindLargeJournal_ReportsConsent()
        {
            var output = RunScenario("consent");
            StringAssert.Contains(output, "SESSION_START_RESULT=consent",
                $"a consent prompt in the journal must be seen despite pipefail; output:{Environment.NewLine}{output}");
        }

        [TestMethod]
        public void Gate_OnlyEarlyVsockHoldListening_IsNotReady()
        {
            var output = RunScenario("hold");
            Assert.IsFalse(output.Contains("SESSION_START_RESULT=ok", StringComparison.Ordinal),
                $"the early vsock hold is not the server — no listener verdict from it; output:{Environment.NewLine}{output}");
            StringAssert.Contains(output, "SESSION_START_RESULT=degraded");
        }

        [TestMethod]
        public void Gate_ServerTcpListenerBound_ReportsOk()
        {
            var output = RunScenario("ready");
            StringAssert.Contains(output, "SESSION_START_RESULT=ok",
                $"the server's own TCP 3389 listener is the readiness proof; output:{Environment.NewLine}{output}");
        }

        [TestMethod]
        public void Gate_WayrdpVsockListener_ReportsOk()
        {
            // wayrdp serves vsock-only by default (no TCP listener): its own
            // vsock socket is the readiness proof.
            var output = RunScenario("wayrdp", "wayrdp.service");
            StringAssert.Contains(output, "SESSION_START_RESULT=ok",
                $"wayrdp's own vsock 3389 listener is the readiness proof; output:{Environment.NewLine}{output}");
        }

        [TestMethod]
        public void Gate_WayrdpBackend_IgnoresLamcoTcpListener()
        {
            // A lamco listener must not satisfy a wayrdp deployment's gate.
            var output = RunScenario("ready", "wayrdp.service");
            StringAssert.Contains(output, "SESSION_START_RESULT=degraded",
                $"only the wayrdp process proves a wayrdp deploy ready; output:{Environment.NewLine}{output}");
        }

        // ── harness ───────────────────────────────────────────────────

        private static string RunScenario(string scenario, string? rdpUnit = null)
        {
            if (_wsl is null)
                Assert.Inconclusive("WSL not available — start_graphical_session gate test skipped");

            // The step substitutes the desktop user the same way; root exists
            // in every WSL distro. Left unsubstituted, the unit defaults to lamco.
            string script = LoadEmbeddedScript("start_graphical_session.sh")
                .Replace("__AUTOLOGIN_USER__", "root");
            if (rdpUnit is not null)
                script = script.Replace("__RDP_SERVER_UNIT__", rdpUnit);

            string stage = Path.Combine(Path.GetTempPath(),
                "vmcreate-session-gate-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "session.sh"), script.Replace("\r\n", "\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "harness.sh"), BuildHarness(scenario), new UTF8Encoding(false));
            try
            {
                string wslStage = "/mnt/" + stage[..1].ToLowerInvariant() + stage[2..].Replace('\\', '/');
                var (exit, output, error) = RunWsl(_wsl!, $"bash {wslStage}/harness.sh", 120000);
                Assert.AreEqual(0, exit,
                    $"sandbox harness failed. stderr:{Environment.NewLine}{error}{Environment.NewLine}stdout:{Environment.NewLine}{output}");
                StringAssert.Contains(output, "__EXIT=",
                    $"the harness produced no output — WSL did not run it. stderr:{Environment.NewLine}{error}");
                return output;
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Stubs: the session is up (every is-active answers "active"); the
        /// journal carries the consent line only in the consent scenario,
        /// followed by far more than a pipe buffer of KWin noise; plain
        /// <c>ss -l</c> always shows the early hold's vsock socket, and the
        /// server's TCP listener exists only in the ready scenario.
        /// </summary>
        private static string BuildHarness(string scenario) => $@"#!/bin/bash
set -u
ROOT=$(mktemp -d)
SC='{scenario}'
export ROOT SC
DIR=$(dirname ""$0"")
mkdir -p ""$ROOT/bin""
cat > ""$ROOT/bin/sudo"" <<'STUB'
#!/bin/bash
while [ $# -gt 0 ]; do
  case ""$1"" in
    -u) shift 2;;
    -n) shift;;
    *=*) export ""$1""; shift;;
    *) break;;
  esac
done
exec ""$@""
STUB
cat > ""$ROOT/bin/systemctl"" <<'STUB'
#!/bin/bash
for a in ""$@""; do [ ""$a"" = is-active ] && {{ echo active; exit 0; }}; done
exit 0
STUB
cat > ""$ROOT/bin/journalctl"" <<'STUB'
#!/bin/bash
[ ""$SC"" = consent ] && echo 'INFO libei: No restore token found, permission dialog will appear'
yes 'kwin_wayland_drm: Failed to create framebuffer' | head -n 50000
exit 0
STUB
cat > ""$ROOT/bin/ss"" <<'STUB'
#!/bin/bash
case ""$*"" in
  *-ltnp*) [ ""$SC"" = ready ] && echo 'LISTEN 0 128 127.0.0.1:3389 0.0.0.0:* users:((""lamco-rdp-serve"",pid=1,fd=13))';;
  *--vsock*-p*) if [ ""$SC"" = wayrdp ]; then echo 'v_str LISTEN 0 0 *:3389 *:* users:((""wayrdp"",pid=2,fd=9))'
                else echo 'v_str LISTEN 0 0 *:3389 *:* users:((""python3"",pid=3,fd=4))'; fi;;
  *) echo 'v_str LISTEN 0 0 *:3389 *:*';;
esac
exit 0
STUB
printf '#!/bin/bash\nexit 0\n' > ""$ROOT/bin/sleep""
chmod +x ""$ROOT/bin""/*
PATH=""$ROOT/bin:$PATH"" bash ""$DIR/session.sh""
echo ""__EXIT=$?""
rm -rf ""$ROOT""
".Replace("\r\n", "\n");

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
