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

    // PCL NetFile.TryBeginThread: prefer unfinished work, then split the largest
    // remaining piece 60/40, never split below 256 KiB, and avoid too many connecting
    // workers. Adds a persistent interval journal so dynamic boundaries survive restart.
    internal class PclSegmentScheduler
    {
        internal delegate Task SendRange(string url, long? first, long? last, CancellationToken token, Func<HttpResponseMessage, Task> body);
        private readonly object _gate = new object();
        private readonly List<Piece> _parts = new List<Piece>();
        private readonly string _cache;
        private readonly long _size;
        private readonly SendRange _send;
        private readonly Func<int, CancellationToken, Task> _received;
        private readonly Func<bool> _grow;
        private readonly int _limit;
        private readonly Action _split;
        private class Piece
        {
            public long First;
            public long Last;
            public long Done;
            public int Phase;
            public Task Work;
            public long Remaining
            {
                get
                {
                    return Last - First + 1L - Done;
                }
            }
        }
        internal PclSegmentScheduler(string cache, long size, SendRange sender, Func<int, CancellationToken, Task> received, Func<bool> grow, int limit, Action split)
        {
            _cache = cache;
            _size = size;
            _send = sender;
            _received = received;
            _grow = grow;
            _limit = limit;
            _split = split;
        }
        private string PartPath(Piece piece)
        {
            return SafeFiles.UnderRoot(_cache, "segment-" + piece.First + ".part");
        }
        private void Load()
        {
            string journal = Path.Combine(_cache, "segments.json");
            try
            {
                if (File.Exists(journal))
                {
                    var saved = JObject.Parse(File.ReadAllText(journal));
                    if (saved["size"] is null || saved["size"].Type != JTokenType.Integer)
                        throw new InvalidDataException("分片总长度无效。");
                    if ((long)saved["size"] != _size)
                        throw new InvalidDataException("分片大小已变化。");
                    JArray parts = saved["parts"] as JArray;
                    if (parts is null || parts.Count == 0 || parts.Count > 128)
                        throw new InvalidDataException("分片列表无效。");
                    foreach (var part in parts)
                    {
                        if (!(part is JObject) || part["first"] is null || part["last"] is null || part["first"].Type != JTokenType.Integer || part["last"].Type != JTokenType.Integer)
                            throw new InvalidDataException("分片范围格式无效。");
                        _parts.Add(new Piece() { First = (long)part["first"], Last = (long)part["last"] });
                    }
                }
                else
                {
                    _parts.Add(new Piece() { First = 0L, Last = _size - 1L });
                }
                if (_parts.Count == 0 || _parts.Count > 128)
                    throw new InvalidDataException("分片数无效。");
                var cursor = default(long);
                foreach (var piece in _parts.OrderBy(p => p.First))
                {
                    if (piece.First != cursor || piece.Last < piece.First || piece.Last >= _size)
                        throw new InvalidDataException("分片范围不连续。");
                    cursor = piece.Last + 1L;
                    string path = PartPath(piece);
                    piece.Done = File.Exists(path) ? new FileInfo(path).Length : 0L;
                    if (piece.Done > piece.Last - piece.First + 1L)
                    {
                        // An interrupted resize journal can leave an old worker's longer
                        // tail on disk. It is safe to truncate only this known segment.
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                        {
                            stream.SetLength(piece.Last - piece.First + 1L);
                        }
                        piece.Done = piece.Last - piece.First + 1L;
                    }
                }
                if (cursor != _size)
                    throw new InvalidDataException("分片未覆盖完整文件。");
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is Newtonsoft.Json.JsonException || ex is OverflowException)
            {
                Clear(_cache);
                _parts.Clear();
                _parts.Add(new Piece() { First = 0L, Last = _size - 1L });
            }
            Save();
        }
        private void Save()
        {
            var json = new JObject() { { "size", _size }, { "parts", new JArray(_parts.Select(p => new JObject() { { "first", p.First }, { "last", p.Last } })) } };
            string temp = Path.Combine(_cache, "segments.tmp");
            File.WriteAllText(temp, json.ToString());
            SafeFiles.CommitFile(temp, Path.Combine(_cache, "segments.json"));
        }
        internal async Task RunAsync(string url, string destination, CancellationToken token)
        {
            Load();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Exception failure = null;
                try
                {
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            var broken = _parts.FirstOrDefault(p => p.Work is not null && (p.Work.IsFaulted || p.Work.IsCanceled));
                            if (broken is not null)
                            {
                                failure = broken.Work.IsCanceled ? new IOException("分片连接超时或被取消。") : broken.Work.Exception.GetBaseException();
                                break;
                            }
                            var active = _parts.Where(p => p.Work is not null && !p.Work.IsCompleted).ToList();
                            // Start persisted pending ranges before allocating new work.
                            foreach (var pending in _parts.Where(p => p.Work is null && p.Remaining > 0L).Take(Math.Max(0, _limit - active.Count)).ToList())
                                pending.Work = TransferAsync(url, pending, linked.Token);
                            if (_parts.All(p => p.Remaining == 0L && (p.Work is null || p.Work.IsCompleted)))
                                break;
                            active = _parts.Where(p => p.Work is not null && !p.Work.IsCompleted).ToList();
                            int preparing = active.Where(p => p.Phase < 2).Count();
                            int downloading = active.Where(p => p.Phase == 2).Count();
                            if (active.Count < _limit && _parts.Count < 128 && preparing <= downloading && downloading > 0 && _grow.Invoke())
                            {
                                var largest = active.OrderByDescending(p => p.Remaining).FirstOrDefault();
                                if (largest is not null && largest.Remaining >= 256 * 1024)
                                {
                                    long start = largest.Last - (long)Math.Round(Math.Floor(largest.Remaining * 0.4d)) + 1L;
                                    var extra = new Piece() { First = start, Last = largest.Last };
                                    largest.Last = start - 1L;
                                    _parts.Add(extra);
                                    Save(); // Persist the boundary before issuing its request.
                                    _split.Invoke();
                                    extra.Work = TransferAsync(url, extra, linked.Token);
                                }
                            }
                        }
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    while (true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                linked.Cancel();
                Task[] workers = _parts.Where(p => p.Work is not null).Select(p => p.Work).ToArray();
                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (failure is null)
                        failure = ex;
                }
                token.ThrowIfCancellationRequested();
                if (failure is not null)
                    throw failure;
            }
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var piece in _parts.OrderBy(p => p.First))
                {
                    using (var input = File.OpenRead(PartPath(piece)))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }
        private async Task TransferAsync(string url, Piece piece, CancellationToken token)
        {
            long requestFirst = piece.First + piece.Done;
            long requestLast = piece.Last;
            await _send.Invoke(url, requestFirst, requestLast, token, async response =>
    {
        var range = response.Content.Headers.ContentRange;
        var length = response.Content.Headers.ContentLength;
                // Reject unreliable range headers before opening a retained fragment.
                // The caller joins these workers before falling back to a full request.
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) || !range.From.HasValue || !range.To.HasValue || !range.Length.HasValue || range.From.Value != requestFirst || range.To.Value != requestLast || range.Length.Value != _size || length.HasValue && length.Value != requestLast - requestFirst + 1L)
            throw new RangeUnavailableException();
        piece.Phase = 1;
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new FileStream(PartPath(piece), FileMode.Append, FileAccess.Write, FileShare.Read, 65536, true))
        {
            var buffer = new byte[65536];
            while (true)
            {
                long remaining;
                lock (_gate)
                    remaining = piece.Remaining;
                if (remaining <= 0L)
                    break;
                int count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), token).ConfigureAwait(false);
                if (count == 0)
                    throw new IOException("分片连接提前结束。");
                lock (_gate)
                {
                    int usable = (int)Math.Min(count, piece.Remaining);
                    output.Write(buffer, 0, usable);
                    piece.Done += usable;
                    piece.Phase = 2;
                }
                await _received.Invoke(count, token).ConfigureAwait(false);
            }
        }
    }).ConfigureAwait(false);
        }
        internal static void Clear(string cache)
        {
            if (!Directory.Exists(cache))
                return;
            foreach (var path in Directory.EnumerateFiles(cache, "segment-*.part"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\Asegment-\d+\.part\z"))
                    File.Delete(SafeFiles.UnderRoot(cache, Path.GetFileName(path)));
            }
            foreach (var name in new[] { "segments.json", "segments.tmp" })
            {
                string path = SafeFiles.UnderRoot(cache, name);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    internal class RangeUnavailableException : IOException
    {
        public RangeUnavailableException() : base("下载源不支持可靠的 Range 分段，改用单连接。")
        {
        }
    }
}