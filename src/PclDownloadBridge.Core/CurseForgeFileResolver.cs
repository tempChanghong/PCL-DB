using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed class CurseForgeResolvedFile
    {
        public CurseForgeReference Reference { get; private set; }
        public DownloadFile Download { get; private set; }
        public string InformationSource { get; private set; }
        public string ChecksumSource { get; private set; }
        internal CurseForgeResolvedFile(CurseForgeReference reference, DownloadFile file, string source, string checksum)
        {
            Reference = reference;
            Download = file;
            InformationSource = source;
            ChecksumSource = checksum;
        }
    }

    public sealed class CurseForgeQueryException : IOException
    {
        public ReadOnlyCollection<string> Failures { get; private set; }
        public CurseForgeQueryException(IEnumerable<string> failures) : this(failures.ToList())
        {
        }
        private CurseForgeQueryException(List<string> failures) : base($"整合包的 {failures.Count} 个文件信息查询未完成，未开始安装。不会跳过缺失或无校验依据的文件。" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(8)) + (failures.Count > 8 ? Environment.NewLine + "其余查询失败记录见日志。" : ""))
        {
            Failures = failures.AsReadOnly();
        }
        public override string ToString()
        {
            return base.ToString() + Environment.NewLine + "CurseForge query failures:" + Environment.NewLine + string.Join(Environment.NewLine, Failures);
        }
    }

    // PCL DlModRequest / InstallPackCurseForge file identity and hash semantics.
    // The public website API is an independent keyless adapter, not PCL credentials.
    public class CurseForgeFileResolver : IDisposable
    {
        private readonly SourcePolicy _policy;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly string _apiKey;
        public CurseForgeFileResolver(SourcePolicy policy, HttpClient client = null, string apiKey = null)
        {
            _policy = policy;
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY");
            _ownsClient = client is null;
            _client = client ?? new HttpClient(new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(20d) };
            if (_ownsClient)
                _client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-DB/1.0 (+https://github.com/tempChanghong/PCL-DB)");
        }
        public async Task<List<CurseForgeResolvedFile>> ResolveAsync(CurseForgePackage package, IEnumerable<long> optionalFileIds, CancellationToken token, IProgress<TransferProgress> progress = null)
        {
            if (package is null)
                throw new ArgumentNullException(nameof(package));
            var selected = new HashSet<long>(optionalFileIds ?? Enumerable.Empty<long>());
            if (selected.Any(id => !package.References.Any(item => !item.Required && item.FileId == id)))
                throw new ArgumentException("可选文件选择不属于此整合包。");
            var references = package.References.Where(item => item.Required || selected.Contains(item.FileId)).ToList();
            var errors = new ConcurrentQueue<string>();
            var results = new CurseForgeResolvedFile[references.Count];
            var completed = default(int);
            using (var gate = new SemaphoreSlim(4))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task[] tasks = references.Select(async (reference, index) =>
                    {
                        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                        try
                        {
                            try
                            {
                                results[index] = await ResolveFileAsync(reference, package.Minecraft, linked.Token).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (!(ex is OperationCanceledException))
                            {
                                errors.Enqueue($"项目 {reference.ProjectId} / 文件 {reference.FileId}：{ex.Message}");
                            }
                            int count = Interlocked.Increment(ref completed);
                            progress?.Report(new TransferProgress() { Phase = "metadata", Stage = "查询整合包文件", Detail = $"已查询 {count}/{references.Count}，失败 {errors.Count}", StageFraction = references.Count == 0 ? 1d : count / (double)references.Count, PhaseTotal = references.Count, PhaseCompleted = count });
                        }
                        catch
                        {
                            linked.Cancel();
                            throw;
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!errors.IsEmpty)
                throw new CurseForgeQueryException(errors);
            return results.ToList();
        }
        public async Task<CurseForgeResolvedFile> ResolveFileAsync(CurseForgeReference reference, string mc, CancellationToken token)
        {
            if (reference is null)
                throw new ArgumentNullException(nameof(reference));
            string path = $"/v1/mods/{reference.ProjectId}/files/{reference.FileId}";
            string publicUrl = "https://www.curseforge.com/api" + path;
            var sources = (_policy.EffectiveVersionPreference == SourcePreference.MirrorFirst ? (new[] { "https://mod.mcimirror.top/curseforge" + path, publicUrl }) : (new[] { publicUrl, "https://mod.mcimirror.top/curseforge" + path })).ToList();
            if (!string.IsNullOrWhiteSpace(_apiKey))
                sources.Insert(0, "https://api.curseforge.com" + path);
            var failures = new List<string>();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    JObject data = (await JsonAsync(source, token).ConfigureAwait(false))["data"] as JObject;
                    if (data is null)
                        throw new InvalidDataException("指定文件不存在或未公开");
                    if (CurseForgePackage.Number(data, "id") != reference.FileId || CurseForgePackage.Number(data, data["modId"] is null ? "projectId" : "modId") != reference.ProjectId)
                        throw new InvalidDataException("返回的项目或文件 ID 不匹配");
                    if (data["gameId"] is not null && CurseForgePackage.Number(data, "gameId") != 432L)
                        throw new InvalidDataException("文件不属于 Minecraft");
                    foreach (var @field in new[] { "isAvailable", "isCompatibleWithClient" })
                    {
                        if (data[@field] is not null && (data[@field].Type != JTokenType.Boolean || !(bool)data[@field]))
                            throw new InvalidDataException("文件不可用于客户端");
                    }
                    if (data["isEarlyAccessContent"] is not null && (data["isEarlyAccessContent"].Type != JTokenType.Boolean || (bool)data["isEarlyAccessContent"]))
                        throw new InvalidDataException("文件需要提前访问权限");
                    if (data["status"] is not null && CurseForgePackage.Number(data, "status") != 4L)
                        throw new InvalidDataException("文件未处于公开发布状态");
                    JArray games = data["gameVersions"] as JArray;
                    if (games is null || games.Any(value => value.Type != JTokenType.String) || !games.Values<string>().Contains(mc))
                        throw new InvalidDataException("文件未声明支持整合包指定的 Minecraft 版本");
                    string filename = CurseForgePackage.TextValue(data, "fileName");
                    SafeFiles.ValidatePathSegment(filename);
                    string folder = "mods/";
                    if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        JObject project = (await JsonAsync(source.Substring(0, source.LastIndexOf("/files/", StringComparison.Ordinal)), token).ConfigureAwait(false))["data"] as JObject;
                        if (project is null || CurseForgePackage.Number(project, "id") != reference.ProjectId)
                            throw new InvalidDataException("无法识别非 JAR 文件类型");
                        long classId = CurseForgePackage.Number(project, "classId");
                        if (!filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || classId != 12L && classId != 6552L)
                            throw new NotSupportedException("当前导入只支持模组 JAR、资源包 ZIP 和光影 ZIP");
                        folder = classId == 12L ? "resourcepacks/" : "shaderpacks/";
                    }
                    var file = new DownloadFile() { RelativePath = folder + filename, Size = CurseForgePackage.Number(data, "fileLength"), Url = publicUrl + "/download", Category = "modpack" };
                    string suppliedUrl = CurseForgePackage.TextValue(data, "downloadUrl", false);
                    if (!string.IsNullOrWhiteSpace(suppliedUrl))
                    {
                        Uri address = null;
                        if (!Uri.TryCreate(suppliedUrl, UriKind.Absolute, out address) || !IsBoundCdn(address, reference.FileId, filename))
                            throw new InvalidDataException("API 下载地址与指定的官方 CDN 文件身份不一致");
                        file.Url = address.AbsoluteUri;
                    }
                    var hashes = data["hashes"];
                    if (hashes is not null && hashes.Type != JTokenType.Null)
                    {
                        if (!(hashes is JArray))
                            throw new InvalidDataException("文件摘要列表无效");
                        foreach (var value in (JArray)hashes)
                        {
                            JObject hash = value as JObject;
                            if (hash is null)
                                throw new InvalidDataException("文件摘要条目无效");
                            long algorithm = CurseForgePackage.Number(hash, "algo");
                            string digest = CurseForgePackage.TextValue(hash, "value").ToLowerInvariant();
                            if (algorithm == 1L || algorithm == 2L)
                            {
                                if (!Regex.IsMatch(digest, algorithm == 1L ? @"\A[0-9a-f]{40}\z" : @"\A[0-9a-f]{32}\z"))
                                    throw new InvalidDataException("文件摘要无效");
                                if (algorithm == 1L)
                                {
                                    if (file.Sha1 is not null && (file.Sha1 ?? "") != (digest ?? ""))
                                        throw new InvalidDataException("SHA-1 声明冲突");
                                    file.Sha1 = digest;
                                }
                                else
                                {
                                    if (file.Md5 is not null && (file.Md5 ?? "") != (digest ?? ""))
                                        throw new InvalidDataException("MD5 声明冲突");
                                    file.Md5 = digest;
                                }
                            }
                        }
                    }
                    string checksumSource = "CurseForge API 摘要";
                    if (file.Sha1 is null && file.Md5 is null)
                    {
                        await BindS3ChecksumAsync(file, reference.FileId, filename, token).ConfigureAwait(false);
                        checksumSource = file.Md5 is not null ? "官方 CDN S3 单段 MD5" : "官方 CDN S3 单分片组合 MD5";
                    }
                    return new CurseForgeResolvedFile(reference, file, new Uri(source).Host, checksumSource);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    token.ThrowIfCancellationRequested();
                    failures.Add(new Uri(source).Host + "：" + ex.Message);
                }
            }
            throw new InvalidDataException(string.Join("；", failures));
        }
        private async Task<JObject> JsonAsync(string url, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20d));
                try
                {
                    return await JsonCoreAsync(url, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("CurseForge 查询已取消。", ex, token);
                }
                catch (OperationCanceledException ex)
                {
                    throw new IOException("CurseForge 文件信息查询超时。", ex);
                }
            }
        }
        private async Task<JObject> JsonCoreAsync(string url, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (new Uri(url).Host == "api.curseforge.com" && !string.IsNullOrWhiteSpace(_apiKey))
                    request.Headers.Add("x-api-key", _apiKey);
                using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var registration = token.Register(() => response.Dispose()))
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        do
                        {
                            int count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                            if (count == 0)
                                break;
                            if (output.Length + count > 2 * 1024 * 1024)
                                throw new InvalidDataException("CurseForge 文件信息超过大小限制");
                            output.Write(buffer, 0, count);
                        }
                        while (true);
                        return JObject.Parse(Encoding.UTF8.GetString(output.ToArray()), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    }
                }
            }
        }
        private async Task BindS3ChecksumAsync(DownloadFile file, long id, string filename, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30d));
                try
                {
                    await BindS3ChecksumCoreAsync(file, id, filename, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("CDN 校验信息查询已取消。", ex, token);
                }
                catch (OperationCanceledException ex)
                {
                    throw new IOException("CDN 校验信息查询超时。", ex);
                }
            }
        }
        private async Task BindS3ChecksumCoreAsync(DownloadFile file, long id, string filename, CancellationToken token)
        {
            string url = file.Url;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        if (response.Headers.Location is null)
                            throw new InvalidDataException("文件重定向缺少地址");
                        var nextUrl = new Uri(new Uri(url), response.Headers.Location);
                        if (!IsCdn(nextUrl))
                            throw new InvalidDataException("文件重定向未指向官方 CDN");
                        url = nextUrl.AbsoluteUri;
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var finalUri = (response.RequestMessage?.RequestUri) ?? new Uri(url);
                    if (!IsBoundCdn(finalUri, id, filename))
                        throw new InvalidDataException("官方 CDN 文件地址与指定 ID/文件名不一致");
                    IEnumerable<string> encryption = null;
                    if (!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength.Value != file.Size || response.Content.Headers.ContentEncoding.Any() || !response.Headers.Server.Any(server => server.Product?.Name == "AmazonS3") || !response.Headers.TryGetValues("x-amz-server-side-encryption", out encryption) || encryption.SingleOrDefault() != "AES256")
                        throw new InvalidDataException("CDN 未提供可确认的单段 S3 校验依据");
                    var etag = response.Headers.ETag;
                    if (etag is null || etag.IsWeak)
                        throw new InvalidDataException("CDN 缺少可靠摘要");
                    string digest = etag.Tag.Trim('"').ToLowerInvariant();
                    if (Regex.IsMatch(digest, @"\A[0-9a-f]{32}\z"))
                    {
                        file.Md5 = digest;
                    }
                    else if (Regex.IsMatch(digest, @"\A[0-9a-f]{32}-1\z"))
                    {
                        file.S3SinglePartETag = digest;
                    }
                    else
                    {
                        throw new InvalidDataException("CDN 多分片或未知 ETag 不能作为完整文件 MD5");
                    }
                    file.Url = finalUri.AbsoluteUri;
                    return;
                }
            }
            throw new InvalidDataException("文件重定向过多");
        }
        private static bool IsCdn(Uri uri)
        {
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && new[] { "mediafilez.forgecdn.net", "edge.forgecdn.net", "media.forgecdn.net" }.Contains(uri.Host);
        }
        private static bool IsBoundCdn(Uri uri, long id, string filename)
        {
            if (!IsCdn(uri))
                return false;
            var match = Regex.Match(uri.AbsolutePath, @"\A/files/([0-9]+)/([0-9]+)/([^/]+)\z");
            return match.Success && long.Parse(match.Groups[1].Value) == id / 1000L && long.Parse(match.Groups[2].Value) == id % 1000L && (Uri.UnescapeDataString(match.Groups[3].Value) ?? "") == (filename ?? "");
        }
        public void Dispose()
        {
            if (_ownsClient)
                _client.Dispose();
        }
    }
}