using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    public partial class DownloadEngine
    {
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

    }
}
