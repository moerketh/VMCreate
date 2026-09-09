using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Runs <c>bash -n</c> (parse-only) over every embedded guest script.
    /// <para>
    /// These scripts deploy to VMs as root — a syntax error must fail the
    /// build, not the deployment (the exact failure class the
    /// HyperVVmCreator comment describes: a broken script ships green
    /// because nothing parses it). This test locates a bash binary
    /// (WSL, Git-for-Windows, or Cygwin) and skips loudly with a warning
    /// when none exists, so CI on windows-latest (Git Bash) enforces it
    /// while a bare dev box does not go red.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class EmbeddedScriptSyntaxTests
    {
        private static readonly object BashLock = new();
        private static string? _bashPath;
        private static bool _bashSearched;

        private static string? FindBash()
        {
            if (_bashSearched) return _bashPath;
            lock (BashLock)
            {
                if (_bashSearched) return _bashPath;

                // 1. PATH (covers Git-for-Windows bash.exe and WSL's
                //    C:\Windows\System32\bash.exe on machines where it is present).
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), "bash.exe");
                        if (File.Exists(candidate))
                        {
                            _bashPath = candidate;
                            break;
                        }
                    }
                    catch (ArgumentException) { /* malformed PATH entry — skip */ }
                }

                // 2. Well-known install roots not always on PATH.
                if (_bashPath is null)
                {
                    string[] roots =
                    {
                        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Git\bin\bash.exe"),
                        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Git\usr\bin\bash.exe"),
                        Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\bash.exe"),
                    };
                    foreach (var candidate in roots)
                    {
                        if (File.Exists(candidate))
                        {
                            _bashPath = candidate;
                            break;
                        }
                    }
                }

                _bashSearched = true;
            }
            return _bashPath;
        }

        /// <summary>
        /// WSL's System32\bash.exe launcher runs inside a Linux VM and cannot
        /// open Windows paths — it needs the /mnt/&lt;drive&gt;/ form. Git Bash and
        /// Cygwin bash take Windows paths natively.
        /// </summary>
        private static bool IsWslBash(string bashPath) =>
            string.Equals(bashPath,
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\bash.exe"),
                StringComparison.OrdinalIgnoreCase);

        private static string ToWslPath(string windowsPath)
        {
            // "C:\Users\x\f.sh" -> "/mnt/c/Users/x/f.sh" (default WSL mount).
            var root = Path.GetPathRoot(windowsPath);
            if (root is null || root.Length < 3 || root[1] != ':')
                throw new NotSupportedException($"cannot convert '{windowsPath}' to a WSL path");
            var drive = char.ToLowerInvariant(root[0]);
            var rest = windowsPath.Substring(3).Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }

        [TestMethod]
        public void InstallLamcoScript_ParsesAsValidBash()
        {
            AssertScriptParses("install_lamco.sh");
        }

        [TestMethod]
        public void EnableAutologinScript_ParsesAsValidBash()
        {
            AssertScriptParses("enable_autologin.sh");
        }

        private static void AssertScriptParses(string scriptFileName)
        {
            var script = LoadEmbeddedScript(scriptFileName);

            StringAssert.StartsWith(script, "#!/bin/bash",
                "the script must start with a bash shebang");

            var bash = FindBash();
            if (bash is null)
            {
                Assert.Inconclusive(
                    "No bash.exe found (PATH, Program Files\\Git, System32) — " +
                    "cannot syntax-check '" + scriptFileName + "'. Install Git-for-Windows " +
                    "or WSL so this guard actually runs in this environment.");
            }

            // Write to a real file: bash -n reads the whole file and heredoc
            // parsing depends on line structure; a path with a .sh extension
            // matches the guest-side execution shape.
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                "vmcreate-lint-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
            try
            {
                File.WriteAllText(tempPath, script, new System.Text.UTF8Encoding(false));

                // WSL bash cannot open Windows paths — hand it the /mnt form.
                var lintTarget = IsWslBash(bash) ? ToWslPath(tempPath) : tempPath;

                var psi = new ProcessStartInfo
                {
                    FileName = bash,
                    Arguments = "-n \"" + lintTarget.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("failed to start bash");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(30000);

                if (process.ExitCode != 0)
                {
                    Assert.Fail(
                        $"bash -n failed for '{scriptFileName}' (exit {process.ExitCode}): {stderr.Trim()}");
                }
                Assert.AreEqual(string.Empty, stderr.Trim(),
                    $"bash -n emitted diagnostics for '{scriptFileName}': {stderr.Trim()}");
            }
            finally
            {
                try { File.Delete(tempPath); } catch (IOException) { /* best effort */ }
            }
        }

        private static string LoadEmbeddedScript(string scriptFileName)
        {
            // The test assembly references VMCreate.dll, where the scripts
            // are embedded. Match by suffix like ScriptResourceLoader does.
            var assembly = typeof(global::VMCreate.InstallLamcoRdpStep).Assembly;
            var suffix = "." + scriptFileName;
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