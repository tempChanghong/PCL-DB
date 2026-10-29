using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // PCL ModNet has one connection/speed budget across concurrent LoaderDownloads.
    // Own this for the whole installation; engines own their individual file batches.
    public sealed class DownloadBudget : IDisposable
    {
        internal readonly SemaphoreSlim Connections;
        internal readonly ConcurrentDictionary<string, SemaphoreSlim> Hosts = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, SemaphoreSlim> Transfers = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        internal readonly SemaphoreSlim MirrorGate = new SemaphoreSlim(1);
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly int ConnectionLimit;
        internal readonly int HostLimit;
        internal long LastMirrorRequest;
        internal int ActiveConnections;
        private readonly object _gate = new object();
        private double _due;
        private long _rate;
        private int _batches;
        public long BytesPerSecondLimit { get; set; }

        public DownloadBudget(int connections = 16, long limit = 0L)
        {
            ConnectionLimit = Math.Max(1, Math.Min(connections, 64));
            HostLimit = Math.Min(ConnectionLimit, 16);
            Connections = new SemaphoreSlim(ConnectionLimit);
            BytesPerSecondLimit = limit;
        }
        internal void BeginBatch()
        {
            lock (_gate)
            {
                if (_batches == 0)
                {
                    _due = Clock.Elapsed.TotalSeconds;
                    _rate = BytesPerSecondLimit;
                }
                _batches += 1;
            }
        }
        internal void EndBatch()
        {
            lock (_gate)
                _batches -= 1;
        }
        internal double ReserveBytes(int count)
        {
            long rate = BytesPerSecondLimit;
            if (rate <= 0L)
                return Clock.Elapsed.TotalSeconds;
            lock (_gate)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (_rate != rate)
                {
                    _due = now;
                    _rate = rate;
                }
                _due = Math.Max(now - 0.1d, _due) + count / (double)rate;
                return _due;
            }
        }
        public void Dispose()
        {
            Connections.Dispose();
            MirrorGate.Dispose();
            foreach (var host in Hosts.Values)
                host.Dispose();
            foreach (var transfer in Transfers.Values)
                transfer.Dispose();
        }
    }
}