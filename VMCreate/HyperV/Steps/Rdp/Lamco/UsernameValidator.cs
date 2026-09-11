using System.Text.RegularExpressions;

namespace VMCreate
{
    /// <summary>
    /// Validates Linux usernames before they are substituted into guest
    /// scripts (e.g. <c>__AUTOLOGIN_USER__</c> in the Lamco provisioning).
    /// <para>
    /// The substituted values flow into double-quoted assignments in
    /// root-run bash scripts, and the gallery field can be sourced from
    /// distro mirror pages (not purely first-party). A hostile value must
    /// fail validation on the host, never reach the guest.
    /// </para>
    /// <para>
    /// Matches the distribution-level conventions (Debian <c>adduser</c>
    /// and RHEL <c>useradd</c>): must start with a lowercase letter or
    /// underscore; the rest may contain lowercase letters, digits,
    /// underscores and hyphens; 1-32 characters total.
    /// </summary>
    public static partial class UsernameValidator
    {
        // ^[a-z_][a-z0-9_-]{0,31}$ — lowercase-anchored, no shell metachars,
        // no quotes/whitespace/path separators possible by construction.
        [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
        private static partial Regex LinuxUsernameRegex();

        /// <summary>
        /// Returns true when the value is a safe Linux username: starts with
        /// a lowercase letter or underscore, then only lowercase letters,
        /// digits, underscores or hyphens, at most 32 characters.
        /// </summary>
        public static bool IsValidLinuxUsername(string? username)
            => !string.IsNullOrEmpty(username) && LinuxUsernameRegex().IsMatch(username);
    }
}