using System;
using System.IO;
using System.Reflection;

namespace VMCreate
{
    /// <summary>
    /// Loads embedded bash install scripts (the <c>.sh</c> files under
    /// <c>HyperV/Steps/Scripts/</c>) from the assembly manifest.
    /// <para>
    /// The scripts live as real <c>.sh</c> files rather than C# verbatim
    /// strings so they stay lintable (<c>bash -n</c>) and diffable. The
    /// loader matches resources by file name (scanning the manifest rather
    /// than building a logical name), normalizes CRLF to LF for the guest,
    /// and fails loudly when the resource is missing — a missing or renamed
    /// script must never silently deploy an empty file.
    /// </para>
    /// </summary>
    internal static class ScriptResourceLoader
    {
        /// <summary>
        /// Loads the embedded script with the given file name
        /// (e.g. <c>install_lamco.sh</c>), normalized to LF line endings.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The resource is not embedded in the assembly.
        /// </exception>
        public static string Load(string scriptFileName)
        {
            if (string.IsNullOrWhiteSpace(scriptFileName))
                throw new ArgumentException("Script file name must be provided.", nameof(scriptFileName));

            var assembly = typeof(ScriptResourceLoader).Assembly;
            var suffix = "." + scriptFileName;
            string? matched = null;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    matched = name;
                    break;
                }
            }

            if (matched is null)
            {
                var available = string.Join(", ", assembly.GetManifestResourceNames());
                throw new InvalidOperationException(
                    $"Embedded script resource '{scriptFileName}' not found in {assembly.GetName().Name}. " +
                    $"Available resources: {available}");
            }

            using var stream = assembly.GetManifestResourceStream(matched)
                ?? throw new InvalidOperationException($"Embedded resource stream '{matched}' could not be opened.");
            using var reader = new StreamReader(stream, leaveOpen: true);
            // The .sh files are authored with LF endings and .gitattributes
            // pins them, but a checkout on Windows can still hand us CRLF
            // (e.g. a hand-edited copy). Bash tolerates CRLF poorly; the
            // guest must always receive LF.
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
    }
}