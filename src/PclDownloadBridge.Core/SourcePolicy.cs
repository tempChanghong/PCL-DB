using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public enum SourcePreference
    {
        Automatic,
        OfficialFirst,
        MirrorFirst
    }

    // PCL DlPreferMojang is session state, independent of the current settings page.
    public class SourceSelectionState
    {
        private int _preferOfficial;
        public bool PreferOfficial
        {
            get
            {
                return Volatile.Read(ref _preferOfficial) != 0;
            }
        }
        internal void ObserveOfficial(TimeSpan elapsed)
        {
            if (elapsed.TotalMilliseconds < 4000d)
                Interlocked.Exchange(ref _preferOfficial, 1);
        }
    }

    // Adapted URL mapping from PCL ModDownload.DlSourceAssetsGet / DlSourceLibraryGet /
    // DlSourceLauncherOrMetaGet at 5ef81e4. No PCL identity, credentials, or signed CDN URLs.
    public class SourcePolicy
    {
        private readonly SourceSelectionState _selection;
        public SourcePolicy(SourceSelectionState selection = null)
        {
            _selection = selection ?? new SourceSelectionState();
        }
        public SourcePreference Preference { get; set; } = SourcePreference.Automatic;
        public SourcePreference? CommunityPreference { get; set; }
        // Legacy callers keep their combined preference; the app sets both explicitly.
        public SourcePreference? VersionPreference { get; set; }
        public SourcePreference EffectiveVersionPreference
        {
            get
            {
                return VersionPreference.GetValueOrDefault(Preference);
            }
        }
        public bool AllowLoopback { get; set; }
        public bool PreferOfficial
        {
            get
            {
                return Preference == SourcePreference.OfficialFirst || Preference == SourcePreference.Automatic && _selection.PreferOfficial;
            }
        }

        public virtual List<string> Candidates(string url, string category)
        {
            var original = new Uri(url, UriKind.Absolute);
            // PCL's legacy loader metadata still contains HTTP Maven links.
            if (original.Scheme == "http" && !original.IsLoopback)
            {
                url = new UriBuilder(original) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
                original = new Uri(url);
            }
            if (original.Scheme != "https" && !(AllowLoopback && original.IsLoopback && original.Scheme == "http"))
                throw new ArgumentException("下载地址必须使用 HTTPS。");
            var mirrors = new List<string>();
            bool community = category == "modrinth-archive" || category == "curseforge-archive";
            if (community && new[] { "cdn.modrinth.com", "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }.Contains(original.Host.ToLowerInvariant()))
            {
                mirrors.Add(new UriBuilder(original) { Host = "mod.mcimirror.top" }.Uri.AbsoluteUri);
            }
            switch (original.Host.ToLowerInvariant() ?? "")
            {
                case "resources.download.minecraft.net":
                    {
                        mirrors.Add("https://bmclapi2.bangbang93.com/assets" + original.PathAndQuery);
                        break;
                    }
                case "libraries.minecraft.net":
                    {
                        mirrors.Add("https://bmclapi2.bangbang93.com/maven" + original.PathAndQuery);
                        mirrors.Add("https://bmclapi2.bangbang93.com/libraries" + original.PathAndQuery);
                        break;
                    }
                case "piston-meta.mojang.com":
                case "piston-data.mojang.com":
                    {
                        // PCL selects the mirror namespace by file category on these hosts.
                        switch (category ?? "")
                        {
                            case "asset":
                                {
                                    mirrors.Add("https://bmclapi2.bangbang93.com/assets" + original.PathAndQuery);
                                    break;
                                }
                            case "library":
                                {
                                    mirrors.Add("https://bmclapi2.bangbang93.com/maven" + original.PathAndQuery);
                                    mirrors.Add("https://bmclapi2.bangbang93.com/libraries" + original.PathAndQuery);
                                    break;
                                }

                            default:
                                {
                                    mirrors.Add("https://bmclapi2.bangbang93.com" + original.PathAndQuery);
                                    break;
                                }
                        }

                        break;
                    }
                case "launchermeta.mojang.com":
                case "launcher.mojang.com":
                    {
                        mirrors.Add("https://bmclapi2.bangbang93.com" + original.PathAndQuery);
                        break;
                    }
                case "meta.fabricmc.net":
                    {
                        mirrors.Add("https://bmclapi2.bangbang93.com/fabric-meta" + original.PathAndQuery);
                        break;
                    }
                case "maven.fabricmc.net":
                case "maven.minecraftforge.net":
                case "files.minecraftforge.net":
                case "maven.neoforged.net":
                    {
                        string path = original.PathAndQuery;
                        if (path.StartsWith("/releases/", StringComparison.Ordinal))
                            path = path.Substring(9);
                        if (path.StartsWith("/maven/", StringComparison.Ordinal))
                            path = path.Substring(6);
                        if (!path.StartsWith("/api/", StringComparison.Ordinal))
                            mirrors.Add("https://bmclapi2.bangbang93.com/maven" + path);
                        break;
                    }
                case "dl.liteloader.com":
                    {
                        if (original.AbsolutePath == "/versions/versions.json")
                        {
                            mirrors.Add("https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json");
                        }
                        else if (original.AbsolutePath.StartsWith("/versions/", StringComparison.Ordinal))
                        {
                            mirrors.Add("https://bmclapi2.bangbang93.com/maven" + original.PathAndQuery.Substring(9));
                        }

                        break;
                    }
            }
            bool officialFirst = category == "version" ? EffectiveVersionPreference != SourcePreference.MirrorFirst : PreferOfficial;
            if (community && CommunityPreference.HasValue)
                officialFirst = CommunityPreference.Value != SourcePreference.MirrorFirst;
            if (!officialFirst)
                return mirrors.Concat(new[] { url }).Distinct().ToList();
            return new[] { url }.Concat(mirrors).Distinct().ToList();
        }

        public async Task<JObject> GetVersionManifestAsync(CancellationToken token)
        {
            const string official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            var urls = Candidates(official, "version");
            // PCL DlClientListMain: auto probes official first (5s), mirror-first waits
            // 30s and official-first waits 60s. A failed source starts fallback at once.
            if (EffectiveVersionPreference == SourcePreference.Automatic)
                urls = new[] { official }.Concat(urls).Distinct().ToList();
            int delay = VersionFallbackDelay();
            var result = await RaceValueAsync(urls, token, null, delay, value =>
    {
        JArray versions = value["versions"] as JArray;
        if (versions is null || versions.Count < 200)
            throw new System.IO.InvalidDataException("Minecraft 版本列表不完整。");
        foreach (var version in versions)
        {
            DateTimeOffset released;
            if (!(version is JObject) || string.IsNullOrWhiteSpace((string)version["id"]) || string.IsNullOrWhiteSpace((string)version["type"]) || !System.Text.RegularExpressions.Regex.IsMatch((string)version["sha1"] ?? "", @"\A[0-9a-fA-F]{40}\z") || !DateTimeOffset.TryParse((string)version["releaseTime"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out released))
                throw new System.IO.InvalidDataException("Minecraft 版本条目无效。");
            CandidatesForValidation((string)version["url"]);
        }
    }, (source, elapsed) => { if ((source ?? "") == official) _selection.ObserveOfficial(elapsed); }).ConfigureAwait(false);
            return (JObject)result;
        }

        private int VersionFallbackDelay()
        {
            return EffectiveVersionPreference == SourcePreference.Automatic ? 5000 : EffectiveVersionPreference == SourcePreference.MirrorFirst ? 30000 : 60000;
        }

        public async Task<JToken> GetVersionListTokenAsync(string url, CancellationToken token, Func<string, JToken> parse = null)
        {
            var urls = Candidates(url, "version");
            return await RaceVersionListTokenAsync(urls, token, parse).ConfigureAwait(false);
        }

        public async Task<JToken> RaceVersionListTokenAsync(IEnumerable<string> urls, CancellationToken token, Func<string, JToken> parse = null)
        {
            // Catalogue-specific parsing must succeed before a source can win.
            return await RaceValueAsync(urls, token, null, VersionFallbackDelay(), parse: (source, body) => parse is null ? JToken.Parse(body) : parse(body)).ConfigureAwait(false);
        }

        // Some official catalogues are HTML/XML while BMCLAPI returns JSON. Parse each
        // before selecting a winner so an invalid preferred response still falls back.
        public async Task<JToken> GetVersionListAsync(string official, string mirror, CancellationToken token, Func<string, JToken> parseOfficial = null, Func<string, JToken> parseMirror = null)
        {
            string[] urls = EffectiveVersionPreference == SourcePreference.MirrorFirst ? (new[] { mirror, official }) : (new[] { official, mirror });
            return await RaceValueAsync(urls, token, null, VersionFallbackDelay(), parse: (source, body) =>
                {
                    var parser = (source ?? "") == (official ?? "") ? parseOfficial : parseMirror;
                    return parser is null ? JToken.Parse(body) : parser(body);
                }).ConfigureAwait(false);
        }

        public async Task<JObject> GetJsonAsync(string url, CancellationToken token, string sha1 = null)
        {
            return await RaceJsonAsync(Candidates(url, "meta"), token, sha1, Preference == SourcePreference.Automatic ? 1500 : 5000).ConfigureAwait(false);
        }

        public async Task<JToken> GetTokenAsync(string url, CancellationToken token)
        {
            return await RaceTokenAsync(Candidates(url, "meta"), token).ConfigureAwait(false);
        }

        public async Task<JToken> RaceTokenAsync(IEnumerable<string> urls, CancellationToken token)
        {
            // Reuse the same validated, cancellable metadata race for JSON arrays.
            return await RaceValueAsync(urls, token, null, Preference == SourcePreference.Automatic ? 1500 : 5000).ConfigureAwait(false);
        }

        public async Task<string> GetTextAsync(string url, CancellationToken token)
        {
            CandidatesForValidation(url);
            url = MetadataUrl(url);
            CandidatesForValidation(url);
            using (var content = await ReadMetadataAsync(url, token).ConfigureAwait(false))
            {
                string value = await content.ReadAsStringAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return value;
            }
        }

        // Checksum sidecars follow file-source preferences. Validate before accepting a
        // race winner so an HTML/error response immediately enables the next source.
        internal async Task<string> GetArtifactChecksumAsync(string url, int digits, CancellationToken token)
        {
            if (digits != 32 && digits != 40)
                throw new ArgumentOutOfRangeException(nameof(digits));
            var result = await RaceValueAsync(Candidates(url, "component"), token, null, Preference == SourcePreference.Automatic ? 1500 : 5000, parse: (source, body) =>
    {
        string value = body.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9a-fA-F]{" + digits.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"}\z"))
            throw new System.IO.InvalidDataException("安装器校验文件格式无效。");
        return new JValue(value.ToLowerInvariant());
    }).ConfigureAwait(false);
            return (string)result;
        }

        // PCL SendRequest separates headers from body reads. Enforce this tool's byte
        // bound while streaming, so a known bad response does not delay source fallback.
        private async Task<ByteArrayContent> ReadMetadataAsync(string url, CancellationToken token)
        {
            const int maximum = 32 * 1024 * 1024;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(45d));
                try
                {
                    using (var client = CreateClient())
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > maximum)
                            throw new System.IO.InvalidDataException("元数据超过 32 MiB 大小限制。");
                        // net48 stream reads may surface disposal/IO errors on cancellation;
                        // close the response and normalize the caller's cancellation below.
                        using (var registration = deadline.Token.Register(() => InstallCleanup.NotifyCancellation(response.Dispose)))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new System.IO.MemoryStream())
                        {
                            var buffer = new byte[16384];
                            do
                            {
                                int count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, maximum - output.Length + 1L), deadline.Token).ConfigureAwait(false);
                                if (count == 0)
                                    break;
                                if (output.Length + count > maximum)
                                    throw new System.IO.InvalidDataException("元数据超过 32 MiB 大小限制。");
                                output.Write(buffer, 0, count);
                            }
                            while (true);
                            token.ThrowIfCancellationRequested();
                            var content = new ByteArrayContent(output.ToArray());
                            content.Headers.ContentType = response.Content.Headers.ContentType;
                            return content;
                        }
                    }
                }
                catch (Exception ex) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("元数据请求已取消。", ex, token);
                }
                catch (Exception ex) when (deadline.IsCancellationRequested)
                {
                    throw new TaskCanceledException("元数据读取超时。", ex);
                }
            }
        }

        // PCL DlSourceLoader starts the preferred loader first, then starts a fallback
        // after a grace period. Return the first validated result and cancel the rest.
        public async Task<JObject> RaceJsonAsync(IEnumerable<string> urls, CancellationToken token, string sha1 = null, int fallbackDelay = 1500)
        {
            // As with PCL's source loaders, finish parsing before accepting a source.
            // A valid JSON array/scalar is still an invalid object response and must
            // enable fallback before the winner cancels the other requests.
            return (JObject)await RaceValueAsync(urls, token, sha1, fallbackDelay, validate: value => { if (!(value is JObject)) throw new System.IO.InvalidDataException("元数据响应必须是 JSON 对象。"); }).ConfigureAwait(false);
        }

        private async Task<JToken> RaceValueAsync(IEnumerable<string> urls, CancellationToken token, string sha1, int fallbackDelay, Action<JToken> validate = null, Action<string, TimeSpan> observed = null, Func<string, string, JToken> parse = null)
        {
            var pending = new List<Task<JToken>>();
            var failures = new List<Exception>();
            var candidates = urls.Distinct().ToList();
            foreach (var url in candidates)
                CandidatesForValidation(url);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var fallbackNow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                for (int i = 0, loopTo = candidates.Count - 1; i <= loopTo; i++)
                    pending.Add(FetchJsonAsync(candidates[i], linked.Token, sha1, i * fallbackDelay, fallbackNow.Task, validate, observed, parse));
                JToken result = null;
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);
                    try
                    {
                        result = await finished.ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                        fallbackNow.TrySetResult(true);
                        if (token.IsCancellationRequested)
                            break;
                    }
                }
                InstallCleanup.NotifyCancellation(linked.Cancel);
                // Observe and join losing requests, including streaming cancellation.
                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (result is null)
                        failures.Add(ex);
                }
                token.ThrowIfCancellationRequested();
                if (result is not null)
                    return result;
            }
            throw new AggregateException("所有元数据来源均失败。", failures);
        }

        private void CandidatesForValidation(string url)
        {
            var uri = new Uri(url, UriKind.Absolute);
            if (uri.Scheme != "https" && !(AllowLoopback && uri.IsLoopback && uri.Scheme == "http"))
                throw new ArgumentException("下载地址必须使用 HTTPS。");
        }

        // Transport seam retains source identity when exercising the selection policy.
        protected virtual string MetadataUrl(string source)
        {
            return source;
        }

        private async Task<JToken> FetchJsonAsync(string source, CancellationToken token, string sha1, int delay, Task fallbackNow, Action<JToken> validate, Action<string, TimeSpan> observed, Func<string, string, JToken> parse)
        {
            if (delay > 0)
                await Task.WhenAny(Task.Delay(delay, token), fallbackNow).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            string url = MetadataUrl(source);
            CandidatesForValidation(url);
            using (var content = await ReadMetadataAsync(url, token).ConfigureAwait(false))
            {
                byte[] bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sha1))
                {
                    using (var hash = System.Security.Cryptography.SHA1.Create())
                    {
                        if (!string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), sha1, StringComparison.OrdinalIgnoreCase))
                            throw new System.IO.InvalidDataException("元数据 SHA-1 校验失败。");
                    }
                }
                // Hash the complete response above, including any BOM. Skip just
                // one UTF-8 preamble for parsing, preserving embedded characters.
                int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                string body = System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
                var result = parse is null ? JToken.Parse(body) : parse(source, body);
                validate?.Invoke(result);
                token.ThrowIfCancellationRequested();
                observed?.Invoke(source, clock.Elapsed);
                return result;
            }
        }

        public static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler() { AutomaticDecompression = System.Net.DecompressionMethods.None, MaxConnectionsPerServer = 16 }) { Timeout = TimeSpan.FromSeconds(45d) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/9.0.0-beta.1 (local Minecraft installer)");
            return client;
        }
    }
}
