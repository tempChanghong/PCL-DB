using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModNet reference: existing-file checks, bounded file concurrency,
    // large-file splitting, no splitting on BMCLAPI, fallback for servers without Range,
    // multi-source retries, verify-before-merge/commit. Standalone async implementation.
    public class DownloadEngine : IDisposable
    {
        private readonly SourcePolicy _policy;
        private readonly HttpClient _client = SourcePolicy.CreateClient();
        private readonly HttpClient _modrinthClient;
        private readonly AsyncLocal<bool> _modrinthTransfer = new AsyncLocal<bool>();
        private readonly AsyncLocal<ModpackPlatform?> _archivePlatform = new AsyncLocal<ModpackPlatform?>();
        private readonly HttpClient _modrinthArchiveClient;
        private readonly HttpClient _curseforgeArchiveClient;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly IProgress<TransferProgress> _progress;
        private readonly List<string> _reuseRoots;
        private readonly string _cacheRoot;
        private VerifiedComponentCache _componentCache;
        private readonly int _connectionLimit;
        private long _networkBytes;
        private int _completed;
        private int _reused;
        private int _total;
        private long _knownBytes;
        private long _verifiedBytes;
        private int _retries;
        private string _source;
        private string _currentFile;
        private long _lastReport;
        private readonly object _reportLock = new object();
        private readonly PclTransferStatistics _statistics = new PclTransferStatistics();
        private int _splits;
        private readonly AsyncLocal<ResponseState> _responseState = new AsyncLocal<ResponseState>();
        private readonly AsyncLocal<bool> _canFallback = new AsyncLocal<bool>();
        public TimeSpan SlowSourceGrace { get; set; } = TimeSpan.FromSeconds(12d);
        public long MinimumUsefulSpeed { get; set; } = 16 * 1024;
        private class ResponseState
        {
            public Stopwatch Clock = Stopwatch.StartNew();
            public long Bytes;
            public CancellationTokenSource Deadline;
            public bool AllowFallback;
        }
        private readonly DownloadBudget _budget;
        private readonly bool _ownsBudget;
        public long BytesPerSecondLimit
        {
            get
            {
                return _budget.BytesPerSecondLimit;
            }
            set
            {
                _budget.BytesPerSecondLimit = value;
            }
        }
        public int DynamicSplits
        {
            get
            {
                return Volatile.Read(ref _splits);
            }
        }
        public int Retries
        {
            get
            {
                return Volatile.Read(ref _retries);
            }
        }
        public long NetworkBytes
        {
            get
            {
                return Interlocked.Read(ref _networkBytes);
            }
        }
        public int Reused
        {
            get
            {
                return _reused;
            }
        }

        public DownloadEngine(SourcePolicy policy, IProgress<TransferProgress> progress, IEnumerable<string> reuseRoots, int connections = 16, string cacheRoot = null, bool useComponentCache = false, DownloadBudget budget = null)
        {
            _policy = policy;
            _modrinthClient = ModrinthDownloadPolicy.CreateClient(policy.AllowLoopback);
            _modrinthArchiveClient = ModpackArchivePolicy.CreateClient(ModpackPlatform.Modrinth, policy.AllowLoopback);
            _curseforgeArchiveClient = ModpackArchivePolicy.CreateClient(ModpackPlatform.CurseForge, policy.AllowLoopback);
            _progress = progress;
            _reuseRoots = reuseRoots.Concat(string.IsNullOrWhiteSpace(cacheRoot) ? Enumerable.Empty<string>() : (new[] { cacheRoot })).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cacheRoot = cacheRoot;
            if (useComponentCache)
                EnableVerifiedInstallCache();
            _ownsBudget = budget is null;
            _budget = budget ?? new DownloadBudget(connections);
            _connectionLimit = _budget.ConnectionLimit;
        }

        // Called between joined batches, never while their workers are running.
        internal void EnableVerifiedInstallCache()
        {
            if (string.IsNullOrWhiteSpace(_cacheRoot))
                throw new ArgumentException("启用安装缓存需要指定游戏根目录。", "cacheRoot");
            if (_componentCache is null)
                _componentCache = new VerifiedComponentCache(_cacheRoot, _reuseRoots);
        }

        public async Task DownloadAsync(string root, IEnumerable<DownloadFile> files, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var queue = new ConcurrentQueue<DownloadFile>(DownloadPlan.Merge(files, root));
            token.ThrowIfCancellationRequested();
            _budget.BeginBatch();
            try
            {
                _total = queue.Count;
                _knownBytes = queue.Sum(f => Math.Max(0L, f.Size >= 0L ? f.Size : f.EstimatedSize));
                _verifiedBytes = 0L;
                _completed = 0;
                var workers = new List<Task>();
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    for (int i = 1, loopTo = Math.Min(_connectionLimit, Math.Max(1, queue.Count)); i <= loopTo; i++)
                        workers.Add(WorkerAsync(root, queue, linked));
                    try
                    {
                        await Task.WhenAll(workers).ConfigureAwait(false);
                    }
                    catch
                    {
                        token.ThrowIfCancellationRequested();
                        // PCL logs the complete exception group before choosing the
                        // task's primary failure. Keep all sources and retries here.
                        InstallLog.Failure(_cacheRoot, "下载批次失败", workers.Select(task => (Exception)task.Exception));
                        var failure = workers.Where(t => t.Exception is not null).SelectMany(t => t.Exception.Flatten().InnerExceptions).FirstOrDefault(e => !(e is OperationCanceledException));
                        if (failure is not null)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                        throw;
                    }
                }
                token.ThrowIfCancellationRequested();
                Report("文件下载完成", true);
            }
            finally
            {
                _budget.EndBatch();
            }
        }

        private async Task WorkerAsync(string root, ConcurrentQueue<DownloadFile> queue, CancellationTokenSource linked)
        {
            DownloadFile item = null;
            while (queue.TryDequeue(out item))
            {
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    _modrinthTransfer.Value = item.Category == "modrinth";
                    _archivePlatform.Value = item.Category == "modrinth-archive" ? ModpackPlatform.Modrinth : item.Category == "curseforge-archive" ? ModpackPlatform.CurseForge : default(ModpackPlatform?);
                    _currentFile = item.RelativePath;
                    await DownloadOneAsync(root, item, linked.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _verifiedBytes, Math.Max(0L, item.Size >= 0L ? item.Size : item.EstimatedSize));
                    Interlocked.Increment(ref _completed);
                    Report(item.RelativePath);
                }
                catch
                {
                    InstallCleanup.NotifyCancellation(linked.Cancel, _cacheRoot);
                    throw;
                }
            }
        }

        private async Task DownloadOneAsync(string root, DownloadFile item, CancellationToken token)
        {
            string cacheKey;
            using (var sha = SHA256.Create())
            {
                cacheKey = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(item.Url + "|" + item.Sha1 + "|" + item.Size + (item.Md5 is null ? "" : "|" + item.Md5) + (item.S3SinglePartETag is null ? "" : "|s3-etag1:" + item.S3SinglePartETag) + (item.Sha512 is null ? "" : "|sha512:" + item.Sha512)))).Replace("-", "").ToLowerInvariant();
            }
            // Asset aliases share a transfer cache even when their final paths differ.
            // Hold one cache owner through validation/publication, not a network connection slot.
            var transfer = _budget.Transfers.GetOrAdd(cacheKey, key => new SemaphoreSlim(1));
            await transfer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await DownloadOneExclusiveAsync(root, item, cacheKey, token).ConfigureAwait(false);
            }
            finally
            {
                transfer.Release();
            }
        }

        private async Task DownloadOneExclusiveAsync(string root, DownloadFile item, string cacheKey, CancellationToken token)
        {
            string target = SafeFiles.UnderRoot(root, item.RelativePath);
            if (SafeFiles.Matches(target, item))
            {
                _componentCache?.Save(target, item, token);
                Interlocked.Increment(ref _reused);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (_componentCache is not null && _componentCache.TryRestore(target, item, token))
            {
                _componentCache.Save(target, item, token);
                _source = "本地已校验缓存";
                Interlocked.Increment(ref _reused);
                return;
            }
            foreach (var candidate in ReuseCandidates(item))
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool reusable;
                try
                {
                    reusable = SafeFiles.Matches(candidate, item);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                if (reusable)
                {
                    string copyTemp = Path.Combine(Path.GetDirectoryName(target), ".pcldb.reuse-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            File.Copy(candidate, copyTemp);
                            token.ThrowIfCancellationRequested();
                            if (!SafeFiles.Matches(copyTemp, item))
                                throw new IOException("复用文件在复制期间发生变化。");
                            token.ThrowIfCancellationRequested();
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // PCL CheckExistingFiles falls back when an optional copy fails.
                            // Keep cancellation and the actual target commit outside this filter.
                            continue;
                        }
                        SafeFiles.CommitFile(copyTemp, target);
                        _componentCache?.Save(target, item, token);
                        Interlocked.Increment(ref _reused);
                        return;
                    }
                    finally
                    {
                        DeleteReuseCopy(copyTemp);
                    }
                }
            }
            string cache = SafeFiles.UnderRoot(_cacheRoot ?? root, ".vanilla-downloader/transfers/" + cacheKey + "/entry");
            cache = Path.GetDirectoryName(cache);
            Directory.CreateDirectory(cache);
            var failures = new List<Exception>();
            var sources = new[] { item.Url }.Concat(item.AlternateUrls ?? (new string[] { })).SelectMany(url => _policy.Candidates(url, item.Category)).Distinct(StringComparer.Ordinal).ToList();
            for (int sourceIndex = 0, loopTo = sources.Count - 1; sourceIndex <= loopTo; sourceIndex++)
            {
                string source = sources[sourceIndex];
                _source = new Uri(source).Host;
                _canFallback.Value = sourceIndex < sources.Count - 1;
                for (int attempt = 0; attempt <= 1; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    Exception failure = null;
                    string merged = SafeFiles.UnderRoot(cache, "complete.tmp");
                    try
                    {
                        bool completeCached = (CanResumeSingle(item) || _modrinthTransfer.Value) && SafeFiles.Matches(merged, item);
                        if (completeCached)
                        {
                            _source = "本地已校验续传缓存";
                        }
                        else
                        {
                            var transferItem = item;
                            if (_modrinthTransfer.Value)
                            {
                                transferItem = DownloadPlan.Merge(new[] { item })[0];
                                // The manifest size is advisory. Discover the server's
                                // actual length for Range/resume; hashes remain authoritative.
                                using (var probe = CancellationTokenSource.CreateLinkedTokenSource(token))
                                {
                                    probe.CancelAfter(TimeSpan.FromSeconds(8d));
                                    try
                                    {
                                        await WithResponseCoreAsync(source, default, default, probe.Token, response =>
                                            {
                                                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                                                    transferItem.Size = response.Content.Headers.ContentLength.Value;
                                                return Task.FromResult(0);
                                            }, HttpMethod.Head).ConfigureAwait(false);
                                    }
                                    catch (Exception) when (!token.IsCancellationRequested)
                                    {
                                        // HEAD may be unsupported; an ordinary GET still works.
                                    }
                                }
                            }
                            bool retryWithoutLength = false;
                            try
                            {
                                await TransferAsync(source, transferItem, cache, merged, token).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (!token.IsCancellationRequested && _modrinthTransfer.Value && transferItem.Size >= 0L && (ex is IOException || ex is InvalidDataException))
                            {
                                // A misleading HEAD length must not reject valid hashed bytes.
                                retryWithoutLength = true;
                            }
                            if (retryWithoutLength && !SafeFiles.Matches(merged, item))
                                await SingleAsync(source, merged, item, token).ConfigureAwait(false);
                        }
                        if (!SafeFiles.Matches(merged, item))
                            throw new InvalidDataException("大小或哈希校验失败：" + item.RelativePath);
                        token.ThrowIfCancellationRequested();
                        SafeFiles.CommitFile(merged, target);
                        _componentCache?.Save(target, item, token);
                        if (completeCached)
                            Interlocked.Increment(ref _reused);
                        TransferCleanup.DeleteCommitted(_cacheRoot ?? root, cacheKey);
                        return;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _retries);
                        failure = ex;
                        failures.Add(new IOException(source + "：" + ex.Message, ex));
                        // Bad hashes must not poison future resume attempts.
                        if (ex is InvalidDataException)
                        {
                            if (File.Exists(merged))
                                File.Delete(merged);
                            PclSegmentScheduler.Clear(cache);
                            for (int part = 0; part <= 3; part++)
                            {
                                string partPath = Path.Combine(cache, part + ".part");
                                if (File.Exists(partPath))
                                    File.Delete(partPath);
                            }
                        }
                    }
                    if (failure is SlowSourceException)
                        break;
                    if (failure is not null)
                        await Task.Delay(400 * (attempt + 1), token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            throw new AggregateException("下载失败：" + item.RelativePath, failures);
        }

        private async Task TransferAsync(string source, DownloadFile item, string cache, string merged, CancellationToken token)
        {
            bool canSplit = item.Size >= 8L * 1024L * 1024L && (!string.IsNullOrWhiteSpace(item.Sha1) || !string.IsNullOrWhiteSpace(item.Sha512)) && !new Uri(source).Host.Contains("bmclapi");
            if (canSplit)
            {
                bool unsupported = false;
                try
                {
                    await SegmentedAsync(source, item, cache, merged, token).ConfigureAwait(false);
                }
                catch (RangeUnavailableException)
                {
                    unsupported = true;
                }
                if (!unsupported)
                    return;
            }
            await SingleAsync(source, merged, item, token).ConfigureAwait(false);
        }

        private static void DeleteReuseCopy(string temporary)
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // An occupied private copy must not replace cancellation or the real error.
            }
        }

        private IEnumerable<string> ReuseCandidates(DownloadFile item)
        {
            // PCL CheckExistingFile requires an upstream size or hash before searching
            // other folders. A matching filename alone does not identify their content.
            if (item.Size < 0L && string.IsNullOrWhiteSpace(item.Sha1) && string.IsNullOrWhiteSpace(item.Md5) && string.IsNullOrWhiteSpace(item.Sha512))
                return Enumerable.Empty<string>();
            var candidates = new List<string>();
            foreach (var reuseRoot in _reuseRoots)
            {
                try
                {
                    candidates.Add(SafeFiles.UnderRoot(reuseRoot, item.RelativePath));
                    // PCL CheckExistingFiles searches other version directories for matching
                    // client JARs. Staged client downloads have only a basename as relative path.
                    if (item.RelativePath.IndexOfAny(new[] { '/', '\\' }) < 0 && item.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        string versions = Path.Combine(reuseRoot, "versions");
                        if (Directory.Exists(versions))
                        {
                            foreach (var version in Directory.EnumerateDirectories(versions))
                            {
                                string name = Path.GetFileName(version);
                                string versionRoot = Path.GetDirectoryName(SafeFiles.UnderRoot(reuseRoot, "versions/" + name + "/entry"));
                                // PCL searches every matching extension in the version folder,
                                // including clients whose basename differs from the folder.
                                foreach (var jar in Directory.EnumerateFiles(versionRoot, "*.jar", SearchOption.TopDirectoryOnly))
                                {
                                    if (!string.Equals(Path.GetExtension(jar), ".jar", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    candidates.Add(SafeFiles.UnderRoot(reuseRoot, "versions/" + name + "/" + Path.GetFileName(jar)));
                                }
                            }
                        }
                    }
                    if (item.Category == "asset" && !string.IsNullOrEmpty(item.Sha1))
                        candidates.Add(SafeFiles.UnderRoot(reuseRoot, "assets/objects/" + item.Sha1.Substring(0, 2) + "/" + item.Sha1));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // An optional PCL source folder being unavailable must not block downloading.
                }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool CanResumeSingle(DownloadFile item)
        {
            if (item.Size <= 0L)
                return false;
            if (!string.IsNullOrWhiteSpace(item.Sha512))
                return Regex.IsMatch(item.Sha512, @"\A[0-9a-fA-F]{128}\z");
            if (!string.IsNullOrWhiteSpace(item.Sha1))
                return Regex.IsMatch(item.Sha1, @"\A[0-9a-fA-F]{40}\z");
            return Regex.IsMatch(item.Md5 ?? "", @"\A[0-9a-fA-F]{32}\z") || Regex.IsMatch(item.S3SinglePartETag ?? "", @"\A[0-9a-fA-F]{32}-1\z");
        }

        private async Task SingleAsync(string url, string target, DownloadFile item, CancellationToken token)
        {
            var offset = default(long);
            if (CanResumeSingle(item) && File.Exists(target))
            {
                long retained = new FileInfo(target).Length;
                if (retained > 0L && retained < item.Size)
                    offset = retained;
            }
            bool unsupported = false;
            try
            {
                await SingleRequestAsync(url, target, item.Size, offset, token).ConfigureAwait(false);
            }
            catch (RangeUnavailableException) when (offset > 0L)
            {
                unsupported = true;
            }
            if (unsupported)
                await SingleRequestAsync(url, target, item.Size, 0L, token).ConfigureAwait(false);
        }

        private async Task SingleRequestAsync(string url, string target, long size, long offset, CancellationToken token)
        {
            await WithResponseAsync(url, offset > 0L ? new long?(offset) : default, offset > 0L ? new long?(size - 1L) : default, token, async response =>
    {
        if (offset > 0L && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new RangeUnavailableException();
        response.EnsureSuccessStatusCode();
        long start = offset;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            var length = response.Content.Headers.ContentLength;
            if (size < 0L || range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) || !range.From.HasValue || !range.To.HasValue || !range.Length.HasValue || range.From.Value != offset || range.To.Value != size - 1L || range.Length.Value != size || length.HasValue && length.Value != size - offset)
            {
                if (offset > 0L)
                    throw new RangeUnavailableException();
                throw new InvalidDataException("服务器返回了无效的文件区间。");
            }
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
                    // A server may ignore Range: use its full response, never append it.
                    start = 0L;
        }
        else if (offset > 0L)
        {
            throw new RangeUnavailableException();
        }
        if (size >= 0L && response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > size - start)
            throw new InvalidDataException("服务器返回的文件超过预期大小。");
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new FileStream(target, start > 0L ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            if (start > 0L)
            {
                if (output.Length != start)
                    throw new IOException("续传缓存大小在连接期间发生变化。");
                output.Position = start;
                Report("正在续传文件 · 已保留 " + start + " 字节", true);
            }
            await CopyAsync(input, output, token, size >= 0L ? size - start : long.MaxValue).ConfigureAwait(false);
            if (size >= 0L && output.Length != size)
                throw new IOException("文件连接提前结束，保留已接收内容以便续传。");
        }
    }).ConfigureAwait(false);
        }

        private async Task SegmentedAsync(string url, DownloadFile item, string cache, string target, CancellationToken token)
        {
            var scheduler = new PclSegmentScheduler(cache, item.Size, WithResponseAsync, AccountBytesAsync, () =>
    {
        _statistics.Sample(NetworkBytes);
        return _statistics.WantsMoreConnections && _budget.ActiveConnections < _connectionLimit;
    }, Math.Min(16, _connectionLimit), () => Interlocked.Increment(ref _splits));
            await scheduler.RunAsync(url, target, token).ConfigureAwait(false);
        }

        private Task WithResponseAsync(string url, long? first, long? last, CancellationToken token, Func<HttpResponseMessage, Task> body)
        {
            return WithResponseCoreAsync(url, first, last, token, body, HttpMethod.Get);
        }
        private async Task WithResponseCoreAsync(string url, long? first, long? last, CancellationToken token, Func<HttpResponseMessage, Task> body, HttpMethod @method)
        {
            var host = _budget.Hosts.GetOrAdd(new Uri(url).Host, key => new SemaphoreSlim(_budget.HostLimit));
            await host.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _budget.Connections.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (new Uri(url).Host.IndexOf("bmclapi", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // PCL starter threads sleep 100ms after starting BMCLAPI requests.
                        await _budget.MirrorGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            long delay = 100L - (_budget.Clock.ElapsedMilliseconds - _budget.LastMirrorRequest);
                            if (delay > 0L)
                                await Task.Delay((int)delay, token).ConfigureAwait(false);
                            _budget.LastMirrorRequest = _budget.Clock.ElapsedMilliseconds;
                        }
                        finally
                        {
                            _budget.MirrorGate.Release();
                        }
                    }
                    Interlocked.Increment(ref _budget.ActiveConnections);
                    try
                    {
                        using (var request = new HttpRequestMessage(@method, url))
                        {
                            if (first.HasValue)
                                request.Headers.Range = new RangeHeaderValue(first, last);
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                timeout.CancelAfter(TimeSpan.FromMinutes(3d));
                                var client = _archivePlatform.Value.HasValue ? _archivePlatform.Value is { } arg1 && arg1 == ModpackPlatform.Modrinth ? _modrinthArchiveClient : _curseforgeArchiveClient : _modrinthTransfer.Value ? _modrinthClient : _client;
                                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                                {
                                    // Body deadline measures inactivity, not total file length.
                                    timeout.CancelAfter(TimeSpan.FromSeconds(45d));
                                    // Pass the deadline to reads through cancellation closing the response.
                                    using (var registration = timeout.Token.Register(() => InstallCleanup.NotifyCancellation(response.Dispose, _cacheRoot)))
                                    {
                                        var previous = _responseState.Value;
                                        _responseState.Value = new ResponseState() { Deadline = timeout, AllowFallback = _canFallback.Value };
                                        try
                                        {
                                            await body(response).ConfigureAwait(false);
                                        }
                                        finally
                                        {
                                            _responseState.Value = previous;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _budget.ActiveConnections);
                    }
                }
                finally
                {
                    _budget.Connections.Release();
                }
            }
            finally
            {
                host.Release();
            }
        }

        private async Task CopyAsync(Stream input, Stream output, CancellationToken token, long maximum = long.MaxValue)
        {
            var buffer = new byte[65536];
            long written = 0L;
            while (true)
            {
                int count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (count == 0)
                    break;
                written += count;
                if (written > maximum)
                    throw new InvalidDataException("服务器返回的数据超过预期长度。");
                await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                await AccountBytesAsync(count, token).ConfigureAwait(false);
            }
        }

        private async Task AccountBytesAsync(int count, CancellationToken token)
        {
            Interlocked.Add(ref _networkBytes, count);
            var response = _responseState.Value;
            if (response is not null)
            {
                response.Bytes += count;
                if (response.AllowFallback && BytesPerSecondLimit == 0L && response.Clock.Elapsed >= SlowSourceGrace && response.Bytes / Math.Max(0.1d, response.Clock.Elapsed.TotalSeconds) < MinimumUsefulSpeed)
                    throw new SlowSourceException();
                // A successful read resets inactivity. Our own bandwidth wait is not
                // network inactivity; suspend its timer until the next read can start.
                response.Deadline.CancelAfter(Timeout.Infinite);
            }
            try
            {
                await WaitForBandwidthAsync(count, token).ConfigureAwait(false);
            }
            finally
            {
                if (response is not null && !response.Deadline.IsCancellationRequested)
                    response.Deadline.CancelAfter(TimeSpan.FromSeconds(45d));
            }
            _statistics.Sample(NetworkBytes);
            Report("正在传输文件");
        }

        private async Task WaitForBandwidthAsync(int count, CancellationToken token)
        {
            if (BytesPerSecondLimit <= 0L)
                return;
            double due = _budget.ReserveBytes(count);
            do
            {
                token.ThrowIfCancellationRequested();
                double wait = (due - _budget.Clock.Elapsed.TotalSeconds) * 1000d;
                if (wait <= 0d)
                    break;
                _statistics.Sample(NetworkBytes);
                Report("正在按速度上限传输文件");
                await Task.Delay((int)Math.Round(Math.Ceiling(Math.Min(wait, 100d))), token).ConfigureAwait(false);
            }
            while (true);
        }

        private class SlowSourceException : IOException
        {
            public SlowSourceException() : base("下载源持续低速，切换到备选来源。")
            {
            }
        }

        private void Report(string detail, bool force = false)
        {
            if (_progress is null)
                return;
            lock (_reportLock)
            {
                if (!force && _clock.ElapsedMilliseconds - _lastReport < 120L)
                    return;
                _lastReport = _clock.ElapsedMilliseconds;
                _progress.Report(new TransferProgress() { Stage = "下载并校验", Detail = detail, CurrentFile = _currentFile, Source = _source, Retries = _retries, KnownBytes = _knownBytes, VerifiedBytes = Interlocked.Read(ref _verifiedBytes), Completed = _completed, Total = _total, NetworkBytes = NetworkBytes, BytesPerSecond = _statistics.Speed, Reused = _reused, ActiveConnections = _budget.ActiveConnections, DynamicSplits = _splits, Fraction = _total == 0 ? 0d : _completed / (double)_total });
            }
        }

        public void Dispose()
        {
            _modrinthArchiveClient.Dispose();
            _curseforgeArchiveClient.Dispose();
            _modrinthClient.Dispose();
            _client.Dispose();
            if (_ownsBudget)
                _budget.Dispose();
        }
    }
}
