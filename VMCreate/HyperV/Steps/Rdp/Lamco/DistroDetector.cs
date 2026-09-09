using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Detects the <see cref="LinuxDistro"/> of a running guest VM by reading
    /// <c>/etc/os-release</c> over SSH. Used as a defensive re-check inside
    /// customization steps at execution time (the pre-deployment UI gates on the
    /// <see cref="GalleryItem.LinuxDistro"/> metadata hint instead, since no shell
    /// exists yet on the customization page). Results are cached per VM name for
    /// the lifetime of a deployment run so multiple steps don't re-query.
    /// </summary>
    public static class DistroDetector
    {
        /// <summary>
        /// Reads <c>/etc/os-release</c> on the guest and maps <c>ID</c> /
        /// <c>ID_LIKE</c> to a <see cref="LinuxDistro"/>. Returns
        /// <see cref="LinuxDistro.Unknown"/> if the file is missing or the distro
        /// is not classified. Never throws — detection failures fall back to
        /// Unknown and the caller decides whether to proceed.
        /// </summary>
        public static async Task<LinuxDistro> DetectAsync(IGuestShell shell, CancellationToken ct)
        {
            string release;
            try
            {
                release = await shell.RunCommandAsync(
                    "cat /etc/os-release 2>/dev/null", ct);
            }
            catch
            {
                return LinuxDistro.Unknown;
            }

            if (string.IsNullOrWhiteSpace(release))
                return LinuxDistro.Unknown;

            string id = ExtractField(release, "ID");
            string idLike = ExtractField(release, "ID_LIKE");

            return Classify(id, idLike);
        }

        private static string ExtractField(string osRelease, string field)
        {
            // Lines look like: ID=ubuntu   or   ID="debian"   or   ID_LIKE=debian
            foreach (var line in osRelease.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(field + "="))
                {
                    var value = trimmed.Substring(field.Length + 1).Trim('"', '\'');
                    return value.ToLowerInvariant();
                }
            }
            return string.Empty;
        }

        private static LinuxDistro Classify(string id, string idLike)
        {
            // ALL exact-ID checks BEFORE any ID_LIKE fallback. Parrot ships
            // ID=parrot with ID_LIKE=debian; checking families per-distro
            // (ubuntu||like-ubuntu, then debian||like-debian, ...) made the
            // debian line match Parrot's ID_LIKE before the parrot line was
            // ever reached — the exact Parrot branch was unreachable.
            if (Is(id, "ubuntu")) return LinuxDistro.Ubuntu;
            if (Is(id, "debian")) return LinuxDistro.Debian;
            if (Is(id, "fedora")) return LinuxDistro.Fedora;
            if (Is(id, "opensuse-tumbleweed") || Is(id, "opensuse-leap")
                || Is(id, "opensuse") || Is(id, "suse")) return LinuxDistro.OpenSuse;
            if (Is(id, "parrot")) return LinuxDistro.Parrot;

            // ID_LIKE fallbacks for derivatives (e.g. Linux Mint has
            // ID=linuxmint ID_LIKE=ubuntu; Kali has ID=kali ID_LIKE=debian).
            if (HasLike(idLike, "ubuntu")) return LinuxDistro.Ubuntu;
            if (HasLike(idLike, "debian")) return LinuxDistro.Debian;
            if (HasLike(idLike, "fedora")) return LinuxDistro.Fedora;
            if (HasLike(idLike, "opensuse") || HasLike(idLike, "suse")) return LinuxDistro.OpenSuse;
            if (HasLike(idLike, "parrot")) return LinuxDistro.Parrot;

            return LinuxDistro.Unknown;
        }

        private static bool Is(string id, string expected)
            => string.Equals(id, expected, System.StringComparison.Ordinal);

        private static bool HasLike(string idLike, string expected)
        {
            if (string.IsNullOrEmpty(idLike)) return false;
            foreach (var part in idLike.Split(' '))
                if (Is(part, expected)) return true;
            return false;
        }
    }
}