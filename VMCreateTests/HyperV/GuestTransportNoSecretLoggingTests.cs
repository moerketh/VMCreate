using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Invariant tests over the guest transports' logging: no logger call in
    /// SshTransport / SshGuestShell / PowerShellDirectGuestShell passes the
    /// COMMAND STRING (or its base64 payload) as a template argument.
    /// Command lines embed base64 guest payload (VPN configs with client
    /// certs/private keys, scripts, secrets): whatever reaches the logger
    /// reaches the PLAINTEXT rolling log in %TEMP%, regardless of the
    /// configured level.
    ///
    /// This leak class has been fixed THREE times (SSH exec logging the ssh
    /// {Args}, a diagnostics clone, and the PowerShell Direct 200-char
    /// command preview) — each fix held only until a refactor reopened it.
    /// Source-scanning is the only guard that covers all code paths and all
    /// three files at once; a Moq ILogger verification cannot express "the
    /// argument at this call position is the command variable". This test
    /// makes the FOURTH leak a test-time failure instead of a security
    /// incident.
    ///
    /// Limitation (accepted): it scans the direct pattern
    /// (logger call passing a command-bearing variable), not aliases —
    /// a two-step `var x = command; logger.Log(x)` chain is not caught.
    /// </summary>
    [TestClass]
    public class GuestTransportNoSecretLoggingTests
    {
        // Command/secret-bearing variables whose VALUE must never be passed
        // to a logger call in the transports. Length-only member accesses
        // (`command.Length`, `command?.Length ?? 0`) are the sanctioned
        // pattern and are exempted by the scan.
        private const string CommandVariablePattern =
            @"\b(command|linuxCommand|script|base64|args)\b";

        private static readonly Regex CommandVariableRegex =
            new(CommandVariablePattern, RegexOptions.Compiled);

        private static readonly string[] TransportFiles =
        {
            @"VMCreate\HyperV\SshTransport.cs",
            @"VMCreate\HyperV\SshGuestShell.cs",
            @"VMCreate\HyperV\PowerShellDirectGuestShell.cs",
        };

        /// <summary>
        /// Locates the repository root (the directory containing
        /// VMCreate.sln) by walking up from the test assembly's bin
        /// directory. Test runs need the SOURCE tree because the invariant
        /// is about the source that ships, not a possibly-stale compiled
        /// copy sitting next to the assembly.
        /// </summary>
        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "VMCreate.sln")))
                    return dir;
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
            }
            Assert.Fail(
                $"Could not locate VMCreate.sln starting from {AppDomain.CurrentDomain.BaseDirectory} " +
                "— the no-secret-logging scan cannot run.");
            return null; // unreachable
        }

        [TestMethod]
        public void Transports_NeverLogTheCommandStringItself()
        {
            var violations = new StringBuilder();

            foreach (string relative in TransportFiles)
            {
                string path = Path.Combine(FindRepoRoot(), relative);
                Assert.IsTrue(File.Exists(path),
                    $"Transport source file not found: {path}. Update the scan's file list when files move.");

                string[] lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (!trimmed.StartsWith("_logger.Log", StringComparison.Ordinal)
                        && !trimmed.StartsWith("logger.Log", StringComparison.Ordinal))
                        continue;

                    string statement = CollectStatement(lines, i, out int endLine);

                    // Strip string literals: message templates may contain
                    // {Command}/{Args} placeholders — those are template
                    // NAMES, not argument values, and must not match. But
                    // actual secrets passed as arguments live outside
                    // literals and remain visible in the stripped text.
                    string stripped = StripStringLiterals(statement);

                    bool leakFound = false;
                    foreach (Match match in CommandVariableRegex.Matches(stripped))
                    {
                        string after = stripped.Substring(match.Index + match.Length);
                        // Length-only usage is the sanctioned pattern.
                        if (after.StartsWith(".Length", StringComparison.Ordinal)
                            || after.StartsWith("?.Length", StringComparison.Ordinal))
                            continue;
                        leakFound = true;
                        break;
                    }

                    if (leakFound)
                        violations.AppendLine($"{relative} line {i + 1}-{endLine + 1}: {statement.Trim()}");
                }
            }

            Assert.AreEqual(0, violations.Length,
                "A guest transport passes command/secret-bearing material (command, script, base64, ssh args) " +
                "directly to a logger call. Whatever the logger receives lands in the PLAINTEXT %TEMP% log " +
                "regardless of the configured level. Log target and length only " +
                "(see SshGuestShell.RunCommandInternalAsync). Violations:\n" + violations);
        }

        /// <summary>
        /// Collects a full (possibly multi-line) statement starting at
        /// <paramref name="start"/> by accumulating lines until the
        /// parenthesised call opened on the first line closes again.
        /// Depth is counted on literal-stripped text so semicolons or
        /// parens INSIDE string literals cannot terminate the statement
        /// early (e.g. "HTTP {Code}; details: {Error}").
        /// </summary>
        private static string CollectStatement(string[] lines, int start, out int endLine)
        {
            var sb = new StringBuilder(lines[start]);
            endLine = start;
            int depth = 0;
            for (int i = start; i < lines.Length && i - start < 15; i++)
            {
                if (i > start)
                {
                    sb.Append(' ').Append(lines[i].Trim());
                    endLine = i;
                }
                foreach (char c in StripStringLiterals(lines[i]))
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                }
                if (depth <= 0)
                    break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Removes string-literal CONTENT (keeping delimiters), handles
        /// regular literals with backslash escapes, verbatim literals with
        /// doubled-quote escapes (used by the PowerShell Direct scripts),
        /// and truncates line comments.
        /// </summary>
        private static string StripStringLiterals(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool regular = false, verbatim = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (regular)
                {
                    if (c == '\\') { i++; }
                    else if (c == '"') { regular = false; sb.Append('"'); }
                    continue;
                }
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') i++;
                        else { verbatim = false; sb.Append('"'); }
                    }
                    continue;
                }
                if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    verbatim = true; i++; sb.Append("@\"");
                    continue;
                }
                if (c == '"') { regular = true; sb.Append('"'); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    break; // line comment — nothing code-like after this
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}