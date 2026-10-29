using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    // Outer catalogue archives have strict declared sizes. This is deliberately
    // separate from mrpack inner fileSize's advisory-size compatibility behavior.
    public sealed class ModpackArchivePolicy
    {
        private ModpackArchivePolicy()
        {
        }
        public static void Validate(string value, ModpackPlatform platform, bool allowLoopback)
        {
            if (platform != ModpackPlatform.CurseForge && platform != ModpackPlatform.Modrinth)
                throw new InvalidDataException("整合包平台无效。");
            Uri address = null;
            if (string.IsNullOrWhiteSpace(value) || value.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)) || value.Contains(@"\") || !Uri.TryCreate(value, UriKind.Absolute, out address))
                throw new InvalidDataException("整合包下载地址无效。");
            if (allowLoopback && address.IsLoopback && address.Scheme == "http" && string.IsNullOrEmpty(address.UserInfo))
                return;
            if (address.Scheme != "https" || !address.IsDefaultPort || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Fragment))
                throw new InvalidDataException("整合包下载地址必须使用标准 HTTPS。");
            string[] allowed = platform == ModpackPlatform.Modrinth ? (new[] { "cdn.modrinth.com" }) : platform == ModpackPlatform.CurseForge ? (new[] { "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }) : (new string[] { });
            if (!allowed.Contains(address.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包下载主机与内容平台不匹配。");
        }
        internal static HttpClient CreateClient(ModpackPlatform platform, bool allowLoopback)
        {
            var client = new HttpClient(new ArchiveRedirects(platform, allowLoopback)) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/8.0.0-beta.1 (modpack archive)");
            return client;
        }
        private class ArchiveRedirects : DelegatingHandler
        {
            private readonly ModpackPlatform _platform;
            private readonly bool _loopback;
            public ArchiveRedirects(ModpackPlatform platform, bool loopback) : base(new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 16 })
            {
                _platform = platform;
                _loopback = loopback;
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var address = request.RequestUri;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int hop = 0; hop <= 5; hop++)
                {
                    Validate(address.AbsoluteUri, _platform, _loopback);
                    if (!seen.Add(address.AbsoluteUri))
                        throw new InvalidDataException("整合包下载重定向形成循环。");
                    using (var forwarded = new HttpRequestMessage(request.Method, address))
                    {
                        foreach (var header in request.Headers)
                            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        var response = await base.SendAsync(forwarded, token).ConfigureAwait(false);
                        if (!new[] { 301, 302, 303, 307, 308 }.Contains((int)response.StatusCode))
                            return response;
                        try
                        {
                            if (hop == 5 || response.Headers.Location is null)
                                throw new InvalidDataException("整合包下载重定向过多或缺少地址。");
                            address = new Uri(address, response.Headers.Location);
                        }
                        finally
                        {
                            response.Dispose();
                        }
                    }
                }
                throw new InvalidDataException("整合包下载重定向失败。");
            }
        }
    }

    public sealed class ModpackArchiveInstaller
    {
        private ModpackArchiveInstaller()
        {
        }
        public static async Task<string> AcquireAsync(ModpackRelease release, string root, SourcePolicy policy, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L)
        {
            token.ThrowIfCancellationRequested();
            release = release.Copy();
            ModpackArchivePolicy.Validate(release.Url, release.Platform, policy.AllowLoopback);
            if (release.Size <= 0L || release.Sha1 is null && release.Sha512 is null && release.Md5 is null)
                throw new InvalidDataException("归档缺少有效大小或校验摘要。");
            string key;
            using (var sha = SHA256.Create())
            {
                key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(release.Platform.ToString() + "|" + release.ProjectId + "|" + release.Id + "|" + release.Sha1 + "|" + release.Sha512 + "|" + release.Md5))).Replace("-", "").ToLowerInvariant();
            }
            string relative = release.Kind == CommunityResourceKind.Modpack ? ".vanilla-downloader/pack-archives/" + key + "/original." + (release.Platform == ModpackPlatform.Modrinth ? "mrpack" : "zip") : ".vanilla-downloader/community-files/" + release.Kind.ToString() + "/" + key + "/" + release.FileName;
            var file = new DownloadFile() { RelativePath = relative, Url = release.Url, Size = release.Size, Sha1 = release.Sha1, Sha512 = release.Sha512, Md5 = release.Md5, Category = release.Platform == ModpackPlatform.Modrinth ? "modrinth-archive" : "curseforge-archive" };
            if (release.Platform == ModpackPlatform.CurseForge)
            {
                file.AlternateUrls = new[] { "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }.Select(host => new UriBuilder(release.Url) { Host = host }).Select(address => address.Uri.AbsoluteUri).Where(address => (address ?? "") != (release.Url ?? "")).ToArray();
            }
            string leasePath = SafeFiles.UnderRoot(root, ".vanilla-downloader/pack-archives/" + key + "/acquire.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(leasePath));
            using (var lease = await AcquireLeaseAsync(leasePath, token).ConfigureAwait(false))
            using (var budget = new DownloadBudget(connections, limit))
            using (var engine = new DownloadEngine(policy, progress, Enumerable.Empty<string>(), connections, root, useComponentCache: true, budget: budget))
            {
                await engine.DownloadAsync(root, new[] { file }, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            string path = SafeFiles.UnderRoot(root, relative);
            if (!SafeFiles.Matches(path, file, token))
                throw new InvalidDataException("整合包归档校验未通过。");
            return path;
        }
        private static async Task<FileStream> AcquireLeaseAsync(string path, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (clock.Elapsed < TimeSpan.FromMinutes(3d))
                {
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            while (true);
        }

        // Online and local acquisition converge at the same content detection,
        // parsers and installers; catalogue display metadata never supplies dependencies.
        public static object ReadPackage(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ModpackFormats.Detect(path) == ModpackFormat.Modrinth)
                return ModrinthPackage.Read(path, token);
            return CurseForgePackage.Read(path, token);
        }
        private static object ReadSelectedPackage(string path, ModpackRelease release, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // PCL's detail page hands a completed archive to ModpackInstall. Our cache is
            // shared across retries: bind that handoff to the selected release while a
            // read lease prevents changes through detection, parsing and SHA-256 capture.
            // The import plan checks that captured SHA-256 again after optional selection.
            using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (lease.Length != release.Size)
                    throw new InvalidDataException("整合包归档在下载后发生变化，请重试。");
                var hashes = new List<Tuple<HashAlgorithm, string>>();
                try
                {
                    if (release.Sha1 is not null)
                        hashes.Add(Tuple.Create<HashAlgorithm, string>(SHA1.Create(), release.Sha1));
                    if (release.Sha512 is not null)
                        hashes.Add(Tuple.Create<HashAlgorithm, string>(SHA512.Create(), release.Sha512));
                    if (release.Md5 is not null)
                        hashes.Add(Tuple.Create<HashAlgorithm, string>(MD5.Create(), release.Md5));
                    if (hashes.Count == 0)
                        throw new InvalidDataException("归档缺少校验摘要。");
                    var buffer = new byte[65536];
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        int count = lease.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                            break;
                        foreach (var hash in hashes)
                            hash.Item1.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    while (true);
                    foreach (var hash in hashes)
                    {
                        hash.Item1.TransformFinalBlock(new byte[] { }, 0, 0);
                        if (!string.Equals(BitConverter.ToString(hash.Item1.Hash).Replace("-", ""), hash.Item2, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("整合包归档在下载后发生变化，请重试。");
                    }
                    token.ThrowIfCancellationRequested();
                    return ReadPackage(path, token);
                }
                finally
                {
                    foreach (var hash in hashes)
                        hash.Item1.Dispose();
                }
            }
        }
        public static async Task<InstallResult> InstallAsync(ModpackRelease release, string root, string name, SourcePolicy policy, string pclDirectory, IEnumerable<string> reuse, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L, string javaPath = null, Func<ModrinthPackage, CancellationToken, Task<IEnumerable<string>>> chooseOptional = null, LauncherInstallTarget launcherTarget = null)
        {
            release = release.Copy();
            token.ThrowIfCancellationRequested();
            if (release.Kind != CommunityResourceKind.Modpack)
                throw new InvalidOperationException("普通资源请使用文件下载，不会创建游戏实例。");
            launcherTarget?.ValidateDestination(root);
            InstallPreflight.ValidateDestination(root, name, false);
            string blocked = ModpackCompatibility.GetInstallBlockReason(release);
            if (blocked is not null)
                throw new NotSupportedException(blocked);
            var archiveBytes = default(long);
            var archiveReused = default(int);
            var archiveSplits = default(int);
            var archiveProgress = new InlineProgress(value =>
                {
                    archiveBytes = Math.Max(archiveBytes, value.NetworkBytes);
                    archiveReused = Math.Max(archiveReused, value.Reused);
                    archiveSplits = Math.Max(archiveSplits, value.DynamicSplits);
                    value.StageFraction = value.Total > 0 && value.Completed >= value.Total ? 1d : Math.Min(0.95d, value.NetworkBytes / (double)release.Size);
                    value.Phase = "archive";
                    value.Stage = "下载并校验整合包归档";
                    progress?.Report(value);
                });
            string path = await AcquireAsync(release, root, policy, archiveProgress, token, connections, limit).ConfigureAwait(false);
            progress?.Report(new TransferProgress() { Phase = "archive", Stage = "整合包归档已校验", StageFraction = 1d, NetworkBytes = archiveBytes, Reused = archiveReused, DynamicSplits = archiveSplits });
            var parsed = await Task.Run(() => ReadSelectedPackage(path, release, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            InstallResult result;
            if (release.Platform == ModpackPlatform.Modrinth)
            {
                ModrinthPackage package = parsed as ModrinthPackage;
                if (package is null)
                    throw new InvalidDataException("下载文件与所选 Modrinth 格式不符。");
                ModpackCompatibility.ValidateManifest(release, package.Minecraft, package.LoaderKind);
                var selected = chooseOptional is null ? Enumerable.Empty<string>() : await chooseOptional(package, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                result = await new ModrinthInstaller(policy, pclDirectory, launcherTarget).InstallAsync(package, root, name, reuse, selected, progress, token, connections, limit, javaPath).ConfigureAwait(false);
            }
            else
            {
                CurseForgePackage package = parsed as CurseForgePackage;
                if (package is null)
                    throw new InvalidDataException("下载文件与所选 CurseForge 格式不符。");
                ModpackCompatibility.ValidateManifest(release, package.Minecraft, package.LoaderKind);
                result = await new CurseForgeInstaller(policy, pclDirectory, launcherTarget: launcherTarget).InstallAsync(package, root, name, reuse, package.References.Where(file => !file.Required).Select(file => file.FileId), progress, token, connections, limit, javaPath).ConfigureAwait(false);
            }
            result.NetworkBytes += archiveBytes;
            result.Reused += archiveReused;
            result.DynamicSplits += archiveSplits;
            return result;
        }
        private class InlineProgress : IProgress<TransferProgress>
        {
            private readonly Action<TransferProgress> _report;
            public InlineProgress(Action<TransferProgress> report)
            {
                _report = report;
            }
            public void Report(TransferProgress value)
            {
                _report(value);
            }
        }
    }
}
