using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Resolves the profile photo URL for an X (Twitter) handle at runtime,
    /// so that changes to the profile picture are picked up automatically.
    /// Falls back to a static URL when the profile page cannot be fetched.
    /// </summary>
    public static class XProfilePhoto
    {
        /// <summary>
        /// Uses the X syndication endpoint to extract the profile image URL
        /// for the given handle. Returns a <c>pbs.twimg.com</c> URL sized at
        /// 200×200, suitable for a list-view icon.
        /// </summary>
        /// <param name="client">An <see cref="HttpClient"/> to use for the request.</param>
        /// <param name="handle">The X handle without the @ prefix, e.g. <c>kalilinux</c>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <c>pbs.twimg.com/profile_images/…_200x200.jpg</c> URL, or <c>null</c> on failure.</returns>
        public static async Task<string?> ResolveAsync(
            HttpClient client,
            string handle,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // The syndication endpoint returns server-rendered HTML that includes
                // profile_images URLs, unlike the main x.com SPA shell.
                var syndicationUrl = $"https://syndication.twitter.com/srv/timeline-profile/screen-name/{handle}";
                var request = new HttpRequestMessage(HttpMethod.Get, syndicationUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", "VMCreate/1.0");

                var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract the first pbs.twimg.com/profile_images URL from the page
                var match = Regex.Match(html,
                    @"(https?://pbs\.twimg\.com/profile_images/[^\s""'<>\\]+)",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    return null;

                var imageUrl = match.Groups[1].Value;

                // Replace the size suffix (e.g. _normal, _bigger) with _200x200 for a
                // crisp icon in the gallery list without downloading the full-size image.
                imageUrl = Regex.Replace(imageUrl, @"_(normal|bigger|mini|200x200|400x400)(\.\w+)$", "_200x200$2");

                return imageUrl;
            }
            catch
            {
                return null;
            }
        }
    }
}
