using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Result of attempting to download a single VPN key.
    /// </summary>
    public class HtbVpnDownloadResult
    {
        public string EndpointName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public HtbVpnKey Key { get; set; }
    }

    /// <summary>
    /// Downloads HTB VPN configuration files from the HTB API using a Bearer token.
    /// 
    /// Labs flow (from htb-toolkit):
    ///   1. GET /api/v4/connections → parse assigned server IDs for lab, starting_point, fortresses
    ///   2. GET /api/v4/access/ovpnfile/{server_id}/0  → raw .ovpn content
    /// 
    /// Academy flow:
    ///   GET /api/v2/vpn-servers/key/download?type=regular&amp;protocol=udp → raw .ovpn content
    /// </summary>
    public class HtbApiClient : IHtbApiClient, IDisposable
    {
        private readonly ILogger<HtbApiClient> _logger;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        private const string LabsBase = "https://labs.hackthebox.com";
        private const string AcademyBase = "https://academy.hackthebox.com";

        /// <summary>
        /// Display names shown in the UI status list.
        /// </summary>
        public static readonly string[] EndpointNamesStatic = { "Labs", "Starting Point", "Academy" };

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

            // Use a custom handler that preserves Authorization headers across redirects.
            // Academy redirects (e.g. path normalization) and the default HttpClient
            // strips auth headers on redirect, causing the request to hit a login page.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = true;
        }

        /// <summary>
        /// Downloads VPN keys from all known HTB endpoints using the provided App Token.
        /// Returns a result for each endpoint (success with key content, or failure with error).
        /// </summary>
        public async Task<List<HtbVpnDownloadResult>> DownloadAllKeysAsync(string apiToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API token is required.", nameof(apiToken));

            var results = new List<HtbVpnDownloadResult>();

            // ── Labs + Starting Point: use /api/v4/connections to discover assigned servers ──
            try
            {
                var connections = await GetLabConnectionsAsync(apiToken, ct);

                foreach (var conn in connections)
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(await DownloadLabOvpnAsync(conn.Name, conn.ServerId, apiToken, conn.GuestFileName, ct));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to query Labs connections API");
                results.Add(new HtbVpnDownloadResult { EndpointName = "Labs", Success = false, ErrorMessage = ex.Message });
            }

            // ── Academy ──
            ct.ThrowIfCancellationRequested();
            results.Add(await DownloadAcademyOvpnAsync(apiToken, ct));

            return results;
        }

        // ─── Labs connections discovery ────────────────────────────────────

        private record LabConnection(string Name, long ServerId, string GuestFileName);

        private async Task<List<LabConnection>> GetLabConnectionsAsync(string apiToken, CancellationToken ct)
        {
            string url = $"{LabsBase}/api/v4/connections";
            _logger.LogInformation("Querying Labs connections: {Url}", url);

            using var response = await SendWithAuthRedirectAsync(url, apiToken, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Labs connections returned HTTP {Code}: {Body}",
                    (int)response.StatusCode, Truncate(body, 300));
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} from connections API");
            }

            var connections = new List<LabConnection>();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            // Log all top-level section names from the connections response for discovery
            var sectionNames = new List<string>();
            foreach (var prop in data.EnumerateObject())
                sectionNames.Add(prop.Name);
            _logger.LogDebug("Connections API sections: {Sections}", string.Join(", ", sectionNames));

            // For each known section, log its property names to help discover server-list fields
            foreach (string key in new[] { "lab", "starting_point", "fortresses", "endgames", "pro_labs", "competitive", "release_arena" })
            {
                if (data.TryGetProperty(key, out var sec) && sec.ValueKind == JsonValueKind.Object)
                {
                    var propNames = new List<string>();
                    foreach (var p in sec.EnumerateObject())
                        propNames.Add($"{p.Name}({p.Value.ValueKind})");
                    _logger.LogDebug("Section '{Key}' properties: {Props}", key, string.Join(", ", propNames));
                }
            }

            // lab (Machines)
            TryParseAllServers(data, "lab", "Labs", "labs", connections);
            // starting_point
            TryParseAllServers(data, "starting_point", "Starting Point", "starting_point", connections);
            // fortresses
            TryParseAllServers(data, "fortresses", "Fortresses", "fortress", connections);
            // endgames
            TryParseAllServers(data, "endgames", "Endgames", "endgames", connections);
            // pro_labs
            TryParseAllServers(data, "pro_labs", "Pro Labs", "pro_labs", connections);
            // competitive
            TryParseAllServers(data, "competitive", "Competitive", "competitive", connections);
            // release_arena
            TryParseAllServers(data, "release_arena", "Release Arena", "release_arena", connections);

            _logger.LogInformation("Discovered {Count} assigned VPN server(s) from Labs API", connections.Count);
            return connections;
        }

        /// <summary>
        /// Parses all available VPN servers for a given category from the connections API response.
        /// Falls back to the single assigned_server if no servers array is present.
        /// </summary>
        private void TryParseAllServers(
            JsonElement data, string jsonKey, string displayName, string filePrefix,
            List<LabConnection> connections)
        {
            if (!data.TryGetProperty(jsonKey, out var section))
                return;

            if (!section.TryGetProperty("can_access", out var canAccess) || !canAccess.GetBoolean())
            {
                _logger.LogDebug("{Name}: can_access = false, skipping", displayName);
                return;
            }

            int countBefore = connections.Count;

            // Enumerate all available servers if the API exposes them
            if (section.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.TryGetProperty("id", out var idProp))
                    {
                        long serverId = idProp.GetInt64();
                        string friendlyName = server.TryGetProperty("friendly_name", out var fn)
                            ? fn.GetString() : $"Server {serverId}";
                        string safeName = SanitizeFileName(friendlyName);

                        _logger.LogInformation("{Category}: server {FriendlyName} (id={Id})",
                            displayName, friendlyName, serverId);
                        connections.Add(new LabConnection(
                            $"{displayName} {friendlyName}",
                            serverId,
                            $"{filePrefix}_{safeName}.ovpn"));
                    }
                }
            }

            // Fallback: use the single assigned_server when no servers array is present
            if (connections.Count == countBefore)
            {
                if (section.TryGetProperty("assigned_server", out var server)
                    && server.ValueKind != JsonValueKind.Null
                    && server.TryGetProperty("id", out var idProp))
                {
                    long serverId = idProp.GetInt64();
                    string friendlyName = server.TryGetProperty("friendly_name", out var fn)
                        ? fn.GetString() : "unknown";
                    string safeName = SanitizeFileName(friendlyName);
                    _logger.LogInformation("{Category}: assigned server = {FriendlyName} (id={Id})",
                        displayName, friendlyName, serverId);
                    connections.Add(new LabConnection(
                        $"{displayName} {friendlyName}",
                        serverId,
                        $"{filePrefix}_{safeName}.ovpn"));
                }
                else
                {
                    _logger.LogDebug("{Name}: no servers found", displayName);
                }
            }
        }

        /// <summary>
        /// Converts a friendly server name like "EU Free 1" to a safe filename component "eu_free_1".
        /// </summary>
        private static string SanitizeFileName(string name) =>
            name.Trim().Replace(' ', '_').Replace('-', '_').ToLowerInvariant();

        // ─── Labs .ovpn download ───────────────────────────────────────────

        private async Task<HtbVpnDownloadResult> DownloadLabOvpnAsync(
            string name, long serverId, string apiToken, string guestFileName, CancellationToken ct)
        {
            // TCP flag: 0 = UDP, 1 = TCP — we use UDP
            string url = $"{LabsBase}/api/v4/access/ovpnfile/{serverId}/0";
            _logger.LogInformation("Downloading {Name} VPN key from {Url}", name, url);

            try
            {
                using var response = await SendWithAuthRedirectAsync(url, apiToken, ct);
                string content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    string msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    _logger.LogWarning("{Name} VPN download failed: {Status} — {Body}", name, msg, Truncate(content, 200));
                    return new HtbVpnDownloadResult { EndpointName = name, Success = false, ErrorMessage = msg };
                }

                return ValidateAndReturn(name, guestFileName, content);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error downloading {Name} VPN key", name);
                return new HtbVpnDownloadResult { EndpointName = name, Success = false, ErrorMessage = ex.Message };
            }
        }

        // ─── Academy .ovpn download ────────────────────────────────────────

        private async Task<HtbVpnDownloadResult> DownloadAcademyOvpnAsync(string apiToken, CancellationToken ct)
        {
            const string name = "Academy";
            const string guestFileName = "academy.ovpn";
            string url = $"{AcademyBase}/api/v2/vpn-servers/key/download?type=regular&protocol=udp";

            _logger.LogInformation("Downloading {Name} VPN key from {Url}", name, url);

            try
            {
                using var response = await SendWithAuthRedirectAsync(url, apiToken, ct);
                string content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    string msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    _logger.LogWarning("{Name} VPN download failed: {Status} — {Body}", name, msg, Truncate(content, 200));
                    return new HtbVpnDownloadResult { EndpointName = name, Success = false, ErrorMessage = msg };
                }

                // Academy may return JSON-wrapped content — try to unwrap
                if (content.TrimStart().StartsWith("{"))
                {
                    _logger.LogDebug("Academy response looks like JSON, attempting to unwrap");
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        // Try common wrapper patterns: { "data": "..ovpn.." } or { "key": "..ovpn.." }
                        foreach (string prop in new[] { "data", "key", "ovpn", "content", "config" })
                        {
                            if (doc.RootElement.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                            {
                                string inner = val.GetString();
                                if (!string.IsNullOrEmpty(inner) && inner.Contains("remote "))
                                {
                                    _logger.LogInformation("Extracted .ovpn from JSON property '{Prop}'", prop);
                                    content = inner;
                                    break;
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Not valid JSON — proceed with raw content validation
                    }
                }

                // Check if content is HTML (login redirect)
                if (content.TrimStart().StartsWith("<!") || content.TrimStart().StartsWith("<html"))
                {
                    _logger.LogWarning("Academy returned HTML (likely auth redirect). First 300 chars: {Preview}",
                        Truncate(content, 300));
                    return new HtbVpnDownloadResult
                    {
                        EndpointName = name,
                        Success = false,
                        ErrorMessage = "Academy uses OAuth SSO — download your .ovpn manually from academy.hackthebox.com and use Browse below"
                    };
                }

                return ValidateAndReturn(name, guestFileName, content);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error downloading {Name} VPN key", name);
                return new HtbVpnDownloadResult { EndpointName = name, Success = false, ErrorMessage = ex.Message };
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Sends a GET request, manually following redirects while preserving the Authorization header.
        /// .NET's default HttpClient strips auth headers on redirect which breaks Academy auth.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithAuthRedirectAsync(
            string url, string apiToken, CancellationToken ct, int maxRedirects = 5)
        {
            for (int i = 0; i <= maxRedirects; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                SetAuthHeaders(request, apiToken);

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                int code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location != null)
                {
                    var location = response.Headers.Location;
                    string redirectUrl = location.IsAbsoluteUri
                        ? location.AbsoluteUri
                        : new Uri(new Uri(url), location).AbsoluteUri;

                    _logger.LogDebug("Following redirect {Code} -> {RedirectUrl}", code, redirectUrl);
                    response.Dispose();
                    url = redirectUrl;
                    continue;
                }

                return response;
            }

            throw new HttpRequestException($"Too many redirects (>{maxRedirects}) for {url}");
        }

        private void SetAuthHeaders(HttpRequestMessage request, string apiToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.TryAddWithoutValidation("User-Agent", "VMCreate");
        }

        private HtbVpnDownloadResult ValidateAndReturn(string name, string guestFileName, string content)
        {
            if (string.IsNullOrWhiteSpace(content) || !content.Contains("remote "))
            {
                _logger.LogWarning("{Name} response is not a valid .ovpn file. Preview: {Preview}",
                    name, Truncate(content, 300));
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

        private static string Truncate(string s, int maxLen) =>
            string.IsNullOrEmpty(s) ? "(empty)" :
            s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient?.Dispose();
        }
    }
}
