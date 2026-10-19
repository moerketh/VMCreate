using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VMCreate
{
    /// <summary>
    /// Attaches the VMCREATE_GITHUB_TOKEN PAT to an HttpClient — but ONLY
    /// for GitHub hosts: the same HTTP stack downloads gallery images from
    /// arbitrary third-party mirrors, and the token must never be sent
    /// there. Needed because the vmcreate-iso repo (and its release assets)
    /// are private: unauthenticated API calls 404 and asset redirects
    /// (objects.githubusercontent.com) demand the header too.
    /// </summary>
    internal static class GitHubAssetAuth
    {
        private static readonly string[] GitHubHosts =
        {
            "api.github.com",
            "github.com",
            "www.github.com",
            "objects.githubusercontent.com",
            "codeload.github.com",
        };

        internal static void ApplyIfGithub(HttpClient client, Uri uri)
        {
            string? token = Environment.GetEnvironmentVariable("VMCREATE_GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) return;
            if (uri.Host is null) return;

            foreach (string host in GitHubHosts)
            {
                if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                    return;
                }
            }
        }

        /// <summary>True when the URI is a private-repo release-asset API
        /// URL (api.github.com/.../releases/assets/...), which serves the
        /// bytes only under an octet-stream Accept header.</summary>
        internal static bool IsReleaseAssetApiUrl(Uri uri) =>
            string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);
    }
}
