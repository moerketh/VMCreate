using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Extracts icons that are embedded as resources inside a gallery assembly
    /// to a local user-scoped cache folder, and returns a stable <c>file:///</c>
    /// URI that WPF's <see cref="System.Windows.Data.IValueConverter"/> and
    /// SharpVectors can load without any external network access.
    /// </summary>
    public static class GalleryIcons
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "Icons");

        private static readonly object _lock = new object();

        /// <summary>
        /// Returns a local <c>file:///</c> URI for the embedded resource icon if one exists,
        /// otherwise returns an empty string. Only built-in (embedded) icons are used;
        /// no network requests are made.
        /// </summary>
        public static Task<string> ResolveLogoUriAsync(Assembly assembly, string fileName)
        {
            return Task.FromResult(TryGetLocalUri(assembly, fileName) ?? "");
        }

        /// <summary>
        /// Extracts <paramref name="fileName"/> from <paramref name="assembly"/>'s
        /// embedded resources and returns a <c>file:///</c> URI pointing to the
        /// locally cached copy.  The file is extracted once per user account;
        /// subsequent calls return the cached path immediately.
        /// </summary>
        /// <param name="assembly">
        ///     The assembly in which the icon was embedded (use
        ///     <c>typeof(MyLoader).Assembly</c> in each loader class).
        /// </param>
        /// <param name="fileName">
        ///     The leaf file name of the embedded resource, e.g. <c>nixos.svg</c>.
        ///     The search is case-insensitive and matches by suffix so that you
        ///     do not need to know the full dotted resource name.
        /// </param>
        /// <returns>An absolute <c>file:///</c> URI string.</returns>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when no matching embedded resource is found.
        /// </exception>
        public static string GetLocalUri(Assembly assembly, string fileName)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));

            string fullResourceName = FindResourceName(assembly, fileName);
            string assemblyKey = SanitizeName(assembly.GetName().Name ?? "unknown");

            Directory.CreateDirectory(CacheDir);
            string cacheFile = Path.Combine(CacheDir, assemblyKey + "_" + fileName);

            lock (_lock)
            {
                if (!File.Exists(cacheFile))
                {
                    using var stream = assembly.GetManifestResourceStream(fullResourceName)
                        ?? throw new InvalidOperationException(
                            $"Could not open stream for embedded resource '{fullResourceName}'.");
                    using var fs = File.Create(cacheFile);
                    stream.CopyTo(fs);
                }
            }

            return new Uri(cacheFile).AbsoluteUri;
        }

        /// <summary>
        /// Like <see cref="GetLocalUri"/> but returns <see langword="null"/> instead
        /// of throwing when the embedded resource is missing or extraction fails.
        /// Loaders can use <c>?? fallbackHttpUrl</c> to keep an external URL as
        /// a secondary option.
        /// </summary>
        public static string TryGetLocalUri(Assembly assembly, string fileName)
        {
            try
            {
                return GetLocalUri(assembly, fileName);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ //

        private static string FindResourceName(Assembly assembly, string fileName)
        {
            string suffix = "." + fileName;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            throw new InvalidOperationException(
                $"Embedded resource '{fileName}' not found in assembly '{assembly.GetName().Name}'. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        private static string SanitizeName(string name)
            => name.Replace(' ', '_').Replace(',', '_').Replace('=', '_');
    }
}
