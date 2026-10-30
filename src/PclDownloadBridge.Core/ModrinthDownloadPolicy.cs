using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    // The mrpack format specifies a host allowlist and bounded HTTPS redirects.
    // GitHub releases use separate asset hosts after the initial github.com URL.
    internal sealed class ModrinthDownloadPolicy
    {
        private ModrinthDownloadPolicy()
        {
        }
        internal static void ValidateUrl(string value, bool allowLoopback, bool redirect)
        {
            Uri address = null;
            if (string.IsNullOrWhiteSpace(value) || value.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || Strings.AscW(ch) < 0 || Strings.AscW(ch) > 127) || value.Contains(@"\") || System.Text.RegularExpressions.Regex.IsMatch(value, "%(?![0-9A-Fa-f]{2})") || !Uri.TryCreate(value, UriKind.Absolute, out address))
                throw new InvalidDataException("Modrinth 下载地址无效。");
            if (allowLoopback && address.IsLoopback && address.Scheme == "http" && address.UserInfo.Length == 0)
                return;
            if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length > 0 || address.Fragment.Length > 0)
                throw new InvalidDataException("Modrinth 下载地址必须使用标准 HTTPS。");
            bool allowed = new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" }.Contains(address.Host, StringComparer.OrdinalIgnoreCase);
            if (redirect)
                allowed = allowed || new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }.Contains(address.Host, StringComparer.OrdinalIgnoreCase);
            if (!allowed)
                throw new InvalidDataException("Modrinth 下载主机不在允许列表中：" + address.Host);
        }
        internal static HttpClient CreateClient(bool allowLoopback)
        {
            var client = new HttpClient(new RedirectHandler(allowLoopback)) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/9.0.0-beta.1 (local Minecraft installer)");
            return client;
        }
        private sealed class RedirectHandler : DelegatingHandler
        {
            private readonly bool _allowLoopback;
            internal RedirectHandler(bool allowLoopback) : base(new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 16 })
            {
                _allowLoopback = allowLoopback;
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var address = request.RequestUri;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int hop = 0; hop <= 5; hop++)
                {
                    ValidateUrl(address.AbsoluteUri, _allowLoopback, hop > 0);
                    if (!seen.Add(address.AbsoluteUri))
                        throw new InvalidDataException("Modrinth 下载重定向形成循环。");
                    using (var forwarded = new HttpRequestMessage(request.Method, address))
                    {
                        foreach (var header in request.Headers)
                            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        var response = await base.SendAsync(forwarded, token).ConfigureAwait(false);
                        int code = (int)response.StatusCode;
                        if (!new[] { 301, 302, 303, 307, 308 }.Contains(code))
                            return response;
                        try
                        {
                            if (hop == 5 || response.Headers.Location is null)
                                throw new InvalidDataException("Modrinth 下载重定向过多或缺少地址。");
                            address = new Uri(address, response.Headers.Location);
                        }
                        finally
                        {
                            response.Dispose();
                        }
                    }
                }
                throw new InvalidDataException("Modrinth 下载重定向失败。");
            }
        }
    }
}
