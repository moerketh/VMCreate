using System.Text.RegularExpressions;

namespace VMCreate.Metadata
{
    /// <summary>
    /// Older VMCreate builds wrote gallery login credentials into VM Notes as
    /// plaintext ("Initial Username: x" / "Initial Password: y"). This finds
    /// them so they can be moved into the encrypted store and stripped from Notes.
    /// </summary>
    public static class LegacyNotesCredentials
    {
        private static readonly Regex UserLine = new(@"^Initial Username:[ \t]*(?<v>.*?)[ \t]*\r?$\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex PasswordLine = new(@"^Initial Password:[ \t]*(?<v>.*?)[ \t]*\r?$\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Extracts legacy credentials from <paramref name="notes"/>. Returns
        /// false when neither line is present. <paramref name="remaining"/> is
        /// the notes with both lines removed.
        /// </summary>
        public static bool TryExtract(string? notes, out string? username, out string? password, out string remaining)
        {
            username = null;
            password = null;
            remaining = notes ?? "";
            if (string.IsNullOrEmpty(notes))
                return false;

            var user = UserLine.Match(notes);
            var pass = PasswordLine.Match(notes);
            if (!user.Success && !pass.Success)
                return false;

            if (user.Success)
                username = user.Groups["v"].Value;
            if (pass.Success)
                password = pass.Groups["v"].Value;

            remaining = PasswordLine.Replace(UserLine.Replace(notes, ""), "").Trim();
            return true;
        }
    }
}
