using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Downloads HTB VPN configuration files from the HTB API.
    /// </summary>
    public interface IHtbApiClient : IDisposable
    {
        /// <summary>
        /// Display names for each HTB VPN endpoint (used by the UI for status rows).
        /// </summary>
        IReadOnlyList<string> EndpointNames { get; }

        /// <summary>
        /// Downloads VPN keys from all known HTB endpoints using the provided App Token.
        /// Returns a result for each endpoint (success with key content, or failure with error).
        /// </summary>
        Task<List<HtbVpnDownloadResult>> DownloadAllKeysAsync(string apiToken, CancellationToken ct = default);
    }
}
