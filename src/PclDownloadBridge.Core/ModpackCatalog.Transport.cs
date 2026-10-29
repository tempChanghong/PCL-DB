using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed partial class ModpackCatalogClient
    {
        private readonly HttpClient _client;
        private readonly string _key;
        private readonly int _communitySource;
        public ModpackCatalogClient(HttpMessageHandler handler = null, string apiKey = null, int communitySource = 2)
        {
            _client = new HttpClient(handler ?? new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, MaxConnectionsPerServer = 4 }) { Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/8.0.0-beta.1 (modpack catalogue)");
            _key = apiKey ?? Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY");
            if (communitySource < 0 || communitySource > 2)
                throw new ArgumentOutOfRangeException(nameof(communitySource));
            _communitySource = communitySource;
        }

        private async Task<JObject> JsonAsync(string url, CancellationToken token)
        {
            JObject result = await TokenAsync(url, token).ConfigureAwait(false) as JObject;
            if (result is null)
                throw new InvalidDataException("平台返回的 JSON 对象无效。");
            return result;
        }
        private async Task<JToken> TokenAsync(string url, CancellationToken token, JObject body = null)
        {
            var urls = new List<string>() { url };
            if (url.StartsWith("https://api.curseforge.com/", StringComparison.Ordinal))
            {
                urls.Clear();
                if (!string.IsNullOrWhiteSpace(_key))
                    urls.Add(url);
                // PCL DlModRequest / DlSourceModGet use the Core API and its MCIM
                // mirror. Website /api/v1 file lists have projectId/dateCreated and
                // omit downloadUrl/hashes, so HTTP 200 there is not a Core response.
                urls.Add(url.Replace("https://api.curseforge.com/", "https://mod.mcimirror.top/curseforge/"));
            }
            if (url.StartsWith("https://api.modrinth.com/", StringComparison.Ordinal))
                urls.Add(url.Replace("https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/"));
            if (_communitySource == 0)
                urls = urls.OrderByDescending(address => new Uri(address).Host == "mod.mcimirror.top").ToList();
            var failures = new List<string>();
            foreach (var address in urls)
            {
                bool mirror = new Uri(address).Host == "mod.mcimirror.top";
                for (int attempt = 1, loopTo = mirror ? 2 : 1; attempt <= loopTo; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    bool retry = false;
                    try
                    {
                        return await FetchTokenAsync(address, mirror || _communitySource == 1 ? 10 : _communitySource == 2 ? 30 : 15, token, body).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        failures.Add(new Uri(address).Host + "：" + (ex is OperationCanceledException ? "请求超时" : ex.GetBaseException().Message));
                        CatalogueHttpException http = ex as CatalogueHttpException;
                        retry = http is not null ? http.Transient : ex is HttpRequestException || ex is OperationCanceledException || ex is IOException && !(ex is InvalidDataException);
                    }
                    if (!retry || !mirror || attempt == 2)
                        break;
                    // Fixed PCL DlModRequest repeats its ten-second MCIM request.
                    // Keep retries bounded and cancellable, never retry bad metadata.
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            throw new IOException(string.Join("；", failures.Distinct()));
        }
        private class CatalogueHttpException : HttpRequestException
        {
            internal readonly bool Transient;
            internal CatalogueHttpException(int code, string reason) : base("HTTP " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(reason) ? "" : " (" + reason.Substring(0, Math.Min(reason.Length, 256)) + ")"))
            {
                Transient = code == 408 || code == 429 || code >= 500;
            }
        }
        private async Task<JToken> FetchTokenAsync(string address, int seconds, CancellationToken token, JObject body = null)
        {
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, address))
                {
                    if (body is not null)
                        request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json");
                    deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                    if (new Uri(address).Host == "api.curseforge.com" && !string.IsNullOrWhiteSpace(_key))
                        request.Headers.TryAddWithoutValidation("x-api-key", _key);
                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new CatalogueHttpException((int)response.StatusCode, response.ReasonPhrase);
                        using (var registration = deadline.Token.Register(() => InstallCleanup.NotifyCancellation(response.Dispose)))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            var block = new byte[8192];
                            do
                            {
                                int read = await input.ReadAsync(block, 0, block.Length, deadline.Token).ConfigureAwait(false);
                                if (read == 0)
                                    break;
                                if (buffer.Length + read > 16L * 1024L * 1024L)
                                    throw new InvalidDataException("平台响应超过大小限制。");
                                buffer.Write(block, 0, read);
                            }
                            while (true);
                            token.ThrowIfCancellationRequested();
                            return JToken.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        }
                    }
                }
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                // Closing a net48 response may interrupt reads with an IO/disposal
                // exception. Keep the caller's cancellation identity across platforms.
                throw new OperationCanceledException("平台请求已取消。", ex, token);
            }
        }
        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
