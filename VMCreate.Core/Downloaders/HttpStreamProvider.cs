using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate
{
    public interface IHttpStreamProvider
    {
        Task<HttpResponseMessage> GetResponseAsync(string uri, CancellationToken cancellationToken);
    }

    public class HttpStreamProvider : IHttpStreamProvider
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<HttpStreamProvider> _logger;

        public HttpStreamProvider(IHttpClientFactory clientFactory, ILogger<HttpStreamProvider> logger)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<HttpResponseMessage> GetResponseAsync(string uri, CancellationToken cancellationToken)
        {
            HttpClient client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);
            // Private-repo GitHub assets need the PAT; scoped to GitHub
            // hosts only (third-party mirrors must never see it). Asset
            // API URLs serve the bytes only under an octet-stream Accept.
            var requestUri = new Uri(uri);
            GitHubAssetAuth.ApplyIfGithub(client, requestUri);
            if (GitHubAssetAuth.IsReleaseAssetApiUrl(requestUri))
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));

            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string finalUri = response.RequestMessage!.RequestUri!.ToString();
            _logger.LogInformation("Final URI after redirects: {FinalUri}", finalUri);

            long? contentLength = response.Content.Headers.ContentLength;
            _logger.LogInformation("Content-Length: {ContentLength} bytes", contentLength);

            return response;
        }
    }
}
