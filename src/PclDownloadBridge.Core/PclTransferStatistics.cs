using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PclDownloadBridge.Core
{

    // Direct port of PCL ModNet.RefreshStat: 100ms samples, last 30 weighted
    // from newest to oldest, and a non-decreasing floor based on 85% of last second.
    public class PclTransferStatistics
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<double> _samples = new List<double>();
        private long _lastTime;
        private long _lastBytes;
        private double _speed;
        private double _floor = 256 * 1024;
        public void Sample(long bytes)
        {
            lock (_samples)
            {
                long now = _clock.ElapsedMilliseconds;
                long delta = now - _lastTime;
                if (delta < 100L)
                    return;
                _samples.Insert(0, Math.Max(0d, (bytes - _lastBytes) * 1000.0d / delta));
                if (_samples.Count > 30)
                    _samples.RemoveAt(30);
                _lastBytes = bytes;
                _lastTime = now;
                var total = default(double);
                var weights = default(int);
                for (int i = 0, loopTo = _samples.Count - 1; i <= loopTo; i++)
                {
                    int weight = _samples.Count - i;
                    total += _samples[i] * weight;
                    weights += weight;
                }
                _speed = total / Math.Max(1, weights);
                if (_samples.Count >= 10)
                    _floor = Math.Max(_floor, _samples.Take(10).Average() * 0.85d);
            }
        }
        public double Speed
        {
            get
            {
                lock (_samples)
                    return _speed;
            }
        }
        public bool WantsMoreConnections
        {
            get
            {
                lock (_samples)
                    return _speed < _floor;
            }
        }
    }
}