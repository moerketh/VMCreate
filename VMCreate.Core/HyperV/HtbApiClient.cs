using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate
{
    /// <summary>
    /// Result of attempting to download a single VPN key.
    /// </summary>
    public class HtbVpnDownloadResult
    {
        /// <summary>Display name, e.g. "Labs EU VIP 7".</summary>
        public string EndpointName { get; set; } = null!;

        /// <summary>
        /// The HTB product this key is for, as the v5 API names it
        /// ("labs", "starting_point", "fortresses"); null for an error that
        /// isn't tied to one product.
        /// </summary>
        public string? Product { get; set; }

        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public HtbVpnKey? Key { get; set; }
    }

    /// <summary>
    /// A VPN server HTB has assigned to the account for one product, parsed
    /// from <c>GET /api/v5/connections</c>.
    /// </summary>
    internal sealed record HtbVpnConnection(
        string Product, string DisplayName, long ServerId, string FriendlyName, string GuestFileName);

    /// <summary>
    /// Downloads HTB VPN configuration files from the HTB API using a Bearer
    /// App Token. Mirrors the flow of Athena OS's htb-toolkit (src/vpn.rs):
    /// <list type="number">
    ///   <item><c>GET /api/v5/connections</c>: a <c>data</c> array with one
    ///   entry per product (<c>type</c>), its <c>assigned_server</c> and an
    ///   optional <c>available</c> flag.</item>
    ///   <item><c>GET /api/v4/access/ovpnfile/{server_id}/0</c>: the raw
    ///   UDP .ovpn for that server.</item>
    /// </list>
    /// Only Labs (Machines), Starting Point and Fortresses are fetched, the
    /// products htb-toolkit supports. Academy is not: its VPN sits behind
    /// OAuth SSO and rejects the App Token.
    /// </summary>
    public class HtbApiClient : IHtbApiClient, IDisposable
    {
        private readonly ILogger<HtbApiClient> _logger;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        private const string LabsBase = "https://labs.hackthebox.com";

        /// <summary>
        /// The products fetched, keyed by the v5 <c>type</c> value, with the
        /// display name, guest file prefix and the aliases the CLI's
        /// <c>--htb-vpn</c> filter accepts (htb-toolkit's <c>-v</c> aliases).
        /// </summary>
        internal static readonly IReadOnlyList<(string Type, string DisplayName, string FilePrefix, string[] Aliases)> Products =
            new[]
            {
                ("labs", "Labs", "labs", new[] { "labs", "lab", "machine", "machines" }),
                ("starting_point", "Starting Point", "starting_point", new[] { "starting_point", "starting", "sp" }),
                ("fortresses", "Fortresses", "fortress", new[] { "fortresses", "fortress", "fort" }),
            };

        /// <summary>
        /// Display names shown in the UI status list.
        /// </summary>
        public static readonly string[] EndpointNamesStatic = Products.Select(p => p.DisplayName).ToArray();

        /// <inheritdoc />
        IReadOnlyList<string> IHtbApiClient.EndpointNames => EndpointNamesStatic;

        /// <summary>
        /// Creates an HtbApiClient using a provided <see cref="HttpClient"/> (e.g. from IHttpClientFactory).
        /// The caller is responsible for the HttpClient's lifetime.
        /// </summary>
        public HtbApiClient(HttpClient httpClient, ILogger<HtbApiClient> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = false;
        }

        /// <summary>
        /// Legacy constructor that creates its own HttpClient. Prefer the HttpClient+ILogger overload.
        /// </summary>
        public HtbApiClient(ILogger<HtbApiClient> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Redirects are followed by hand (SendWithAuthRedirectAsync) so the
            // Authorization header survives in-scope hops only.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = true;
        }

        /// <summary>
        /// True when <paramref name="product"/> (a v5 <c>type</c> value) is
        /// selected by a comma-separated filter of product aliases such as
        /// "labs,sp". An empty or null filter selects every product.
        /// </summary>
        public static bool MatchesProductFilter(string? product, string? filter)
        {
            var requested = (filter ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.Length == 0)
                return true;

            var entry = Products.FirstOrDefault(p => string.Equals(p.Type, product, StringComparison.OrdinalIgnoreCase));
            if (entry.Type == null)
                return false;

            return requested.Any(r => entry.Aliases.Contains(r, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Downloads the VPN key of every server HTB has assigned to the
        /// account, for each supported product the account can access.
        /// Returns a result per server (success with key content, or failure
        /// with error); a failed connections query yields one failed result.
        /// </summary>
        public async Task<List<HtbVpnDownloadResult>> DownloadAllKeysAsync(string apiToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API token is required.", nameof(apiToken));

            var results = new List<HtbVpnDownloadResult>();

            List<HtbVpnConnection> connections;
            try
            {
                connections = await GetConnectionsAsync(apiToken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to query HTB VPN connections API");
                results.Add(new HtbVpnDownloadResult { EndpointName = "HTB connections", Success = false, ErrorMessage = ex.Message });
                return results;
            }

            foreach (var conn in connections)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await DownloadOvpnAsync(conn, apiToken, ct));
            }

            return results;
        }

        // ─── Connections discovery (v5) ────────────────────────────────────

        private async Task<List<HtbVpnConnection>> GetConnectionsAsync(string apiToken, CancellationToken ct)
        {
            string url = $"{LabsBase}/api/v5/connections";
            _logger.LogInformation("Querying HTB VPN connections: {Url}", url);

            using var response = await SendWithAuthRedirectAsync(url, apiToken, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // SECURITY: classification, not content — an error body
                // could echo request data or embed rejected key material.
                _logger.LogWarning("HTB connections returned HTTP {Code} (body: {Length} bytes, {Classification})",
                    (int)response.StatusCode, body?.Length ?? 0, ClassifyContent(body));
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} from connections API");
            }

            var connections = ParseConnections(body);
            foreach (var c in connections)
                _logger.LogInformation("{Category}: assigned server {FriendlyName} (id={Id})",
                    c.DisplayName, c.FriendlyName, c.ServerId);
            _logger.LogInformation("Discovered {Count} assigned VPN server(s) from HTB", connections.Count);
            return connections;
        }

        /// <summary>
        /// Parses a <c>/api/v5/connections</c> body the way htb-toolkit does:
        /// <c>data</c> must be an array; entries need a supported <c>type</c>,
        /// <c>available</c> not false (it is absent on most entries), and a
        /// non-null <c>assigned_server</c> with a positive <c>id</c>.
        /// Internal and pure so tests can pin the shape.
        /// </summary>
        internal static List<HtbVpnConnection> ParseConnections(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                // HTB answers removed endpoints with 200 + a message.
                string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                throw new HttpRequestException(message != null
                    ? $"Unexpected connections response: {message}"
                    : "Missing or invalid 'data' array in connections response");
            }

            var connections = new List<HtbVpnConnection>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                    continue;

                var product = Products.FirstOrDefault(p => p.Type == typeProp.GetString());
                if (product.Type == null)
                    continue;

                if (entry.TryGetProperty("available", out var available)
                    && available.ValueKind == JsonValueKind.False)
                    continue;

                if (!entry.TryGetProperty("assigned_server", out var server)
                    || server.ValueKind != JsonValueKind.Object
                    || !server.TryGetProperty("id", out var idProp)
                    || !idProp.TryGetInt64(out long serverId)
                    || serverId <= 0)
                    continue;

                string friendlyName = server.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String
                    ? fn.GetString() ?? $"Server {serverId}"
                    : $"Server {serverId}";

                // One server per product; skip a duplicate the API might repeat.
                if (connections.Any(c => c.Product == product.Type && c.ServerId == serverId))
                    continue;

                connections.Add(new HtbVpnConnection(
                    product.Type,
                    $"{product.DisplayName} {friendlyName}",
                    serverId,
                    friendlyName,
                    $"{product.FilePrefix}_{SanitizeFileName(friendlyName)}.ovpn"));
            }

            return connections;
        }

        /// <summary>
        /// Converts a friendly server name like "EU Free 1" to a safe filename component "eu_free_1".
        /// </summary>
        internal static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unknown";
            var chars = name.Trim().ToLowerInvariant()
                .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            return new string(chars);
        }

        // ─── .ovpn download ────────────────────────────────────────────────

        private async Task<HtbVpnDownloadResult> DownloadOvpnAsync(
            HtbVpnConnection conn, string apiToken, CancellationToken ct)
        {
            // Trailing segment selects the protocol: /0 = UDP, /0/1 = TCP. UDP, as htb-toolkit defaults.
            string url = $"{LabsBase}/api/v4/access/ovpnfile/{conn.ServerId}/0";
            _logger.LogInformation("Downloading {Name} VPN key from {Url}", conn.DisplayName, url);

            try
            {
                using var response = await SendWithAuthRedirectAsync(url, apiToken, ct);
                string content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    string msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    // SECURITY: log classification, not content — the body
                    // could be a rejected-but-valid config; its first 200
                    // chars can embed certificate/key material.
                    _logger.LogWarning("{Name} VPN download failed: {Status} (body: {Length} bytes, {Classification})",
                        conn.DisplayName, msg, content?.Length ?? 0, ClassifyContent(content));
                    return new HtbVpnDownloadResult { EndpointName = conn.DisplayName, Product = conn.Product, Success = false, ErrorMessage = msg };
                }

                var result = ValidateAndReturn(conn.DisplayName, conn.GuestFileName, content);
                result.Product = conn.Product;
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error downloading {Name} VPN key", conn.DisplayName);
                return new HtbVpnDownloadResult { EndpointName = conn.DisplayName, Product = conn.Product, Success = false, ErrorMessage = ex.Message };
            }
        }


        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Sends a GET request, manually following redirects while preserving the Authorization header.
        /// .NET's default HttpClient strips auth headers on redirect which would drop the token on in-scope hops.
        /// The token is only re-sent while the redirect stays on an https://
        /// *.hackthebox.com host; hops to any other origin or scheme are
        /// followed without the Authorization header so the bearer token can
        /// never be forwarded to a third party or downgraded to cleartext.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithAuthRedirectAsync(
            string url, string apiToken, CancellationToken ct, int maxRedirects = 5)
        {
            bool sendAuth = true;

            for (int i = 0; i <= maxRedirects; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (sendAuth)
                    SetAuthHeaders(request, apiToken);

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                int code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location != null)
                {
                    var location = response.Headers.Location;
                    string redirectUrl = location.IsAbsoluteUri
                        ? location.AbsoluteUri
                        : new Uri(new Uri(url), location).AbsoluteUri;

                    sendAuth = IsInScopeRedirect(redirectUrl);

                    _logger.LogDebug("Following redirect {Code} -> {RedirectUrl} (auth: {Auth})", code, redirectUrl, sendAuth);
                    response.Dispose();
                    url = redirectUrl;
                    continue;
                }

                return response;
            }

            throw new HttpRequestException($"Too many redirects (>{maxRedirects}) for {url}");
        }

        /// <summary>
        /// True when a redirect target remains an https:// host under
        /// hackthebox.com, the only origin the App Token may be sent to.
        /// </summary>
        private static bool IsInScopeRedirect(string redirectUrl)
        {
            if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out Uri? target))
                return false;

            return target.Scheme == Uri.UriSchemeHttps
                && (string.Equals(target.Host, "hackthebox.com", StringComparison.OrdinalIgnoreCase)
                    || target.Host.EndsWith(".hackthebox.com", StringComparison.OrdinalIgnoreCase));
        }

        private void SetAuthHeaders(HttpRequestMessage request, string apiToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.TryAddWithoutValidation("User-Agent", ProductInfo.UserAgent);
        }

        private HtbVpnDownloadResult ValidateAndReturn(string name, string guestFileName, string content)
        {
            if (string.IsNullOrWhiteSpace(content) || !content.Contains("remote "))
            {
                // SECURITY: classification only — this fires when the .ovpn
                // heuristic rejects the body; if it false-negatives on a
                // real config, printing the preview would leak its key/cert
                // section to the plaintext log at Warning (above the floor).
                _logger.LogWarning("{Name} response is not a valid .ovpn file: {Classification} ({Length} bytes)",
                    name, ClassifyContent(content), content?.Length ?? 0);
                return new HtbVpnDownloadResult
                {
                    EndpointName = name,
                    Success = false,
                    ErrorMessage = "Response is not a valid .ovpn file (missing 'remote' directive)"
                };
            }

            _logger.LogInformation("Downloaded {Name} VPN key ({Length} bytes)", name, content.Length);
            return new HtbVpnDownloadResult
            {
                EndpointName = name,
                Success = true,
                Key = new HtbVpnKey
                {
                    Name = name,
                    OvpnContent = content,
                    GuestFileName = guestFileName
                }
            };
        }

        /// <summary>
        /// Content-based classification for failure logging: describes what a body
        /// looks like WITHOUT writing any of it to the plaintext log. If a validity
        /// heuristic false-negatives on a real config, the log must not become the
        /// leak — 'unrecognized, {Length} bytes' is safe; a preview is not.
        /// Internal + pure so tests can pin the exact classifications (the
        /// 'PEM/key material' branch exists purely to prevent the leak).
        /// </summary>
        internal static string ClassifyContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "empty";
            string trimmed = content.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                // Distinguish a real JSON payload from an HTML page whose leading
                // whitespace revealed brackets — cheap check, good signal.
                try { using var _ = JsonDocument.Parse(content); return "valid JSON"; }
                catch { /* fall through */ }
            }
            if (trimmed.StartsWith("<!") || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return "HTML";
            if (trimmed.StartsWith("-----BEGIN"))
                return "PEM/key material";
            return "unrecognized";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient?.Dispose();
        }
    }
}
