using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModNet reference: existing-file checks, bounded file concurrency,
    // large-file splitting, no splitting on BMCLAPI, fallback for servers without Range,
    // multi-source retries, verify-before-merge/commit. Standalone async implementation.
    public partial class DownloadEngine : IDisposable
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
