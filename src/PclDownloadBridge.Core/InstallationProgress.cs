using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // Concurrent branches report snapshots; totals are cumulative per branch, never
    // summed twice when another branch emits. Call the UI observer under one lock.
    internal sealed class InstallationProgress
    {
        private readonly IProgress<TransferProgress> _target;
        private readonly DownloadBudget _budget;
        private readonly object _gate = new object();
        private readonly Dictionary<string, TransferProgress> _values = new Dictionary<string, TransferProgress>();
        private readonly Dictionary<string, double> _phases = new Dictionary<string, double>() { { "metadata", 1d }, { "files", 0d }, { "components", 0d }, { "processors", 0d }, { "commit", 0d } };
        private readonly int _supportCount;
        private readonly int _assetCount;
        internal InstallationProgress(IProgress<TransferProgress> target, DownloadBudget budget, int supportCount, int assetCount)
        {
            _target = target;
            _budget = budget;
            _supportCount = supportCount;
            _assetCount = assetCount;
        }
        internal IProgress<TransferProgress> Branch(string name)
        {
            return new InlineProgress(value => Record(name, value));
        }
        internal void ComponentsComplete()
        {
            lock (_gate)
            {
                _phases["components"] = 1d;
                _phases["processors"] = 1d;
                Record("components", new TransferProgress()
                {
                    Phase = "processors",
                    Stage = "组件安装完成",
                    StageFraction = 1d,
                    NetworkBytes = Snapshot("components").NetworkBytes,
                    Reused = Snapshot("components").Reused,
                    DynamicSplits = Snapshot("components").DynamicSplits,
                    Retries = Snapshot("components").Retries
                });
            }
        }
        internal void Failed(string phase)
        {
            lock (_gate)
                _target?.Report(new TransferProgress()
                {
                    Phase = phase,
                    Stage = "安装失败",
                    StageFraction = _phases[phase],
                    PhaseFractions = new Dictionary<string, double>(_phases),
                    Detail = "正在结束其余并行任务",
                    NetworkBytes = _values.Values.Sum(v => v.NetworkBytes),
                    Reused = _values.Values.Sum(v => v.Reused),
                    ActiveConnections = Volatile.Read(ref _budget.ActiveConnections)
                });
        }
        private TransferProgress Snapshot(string name)
        {
            TransferProgress result = null;
            if (_values.TryGetValue(name, out result))
                return result;
            return new TransferProgress();
        }
        private void Record(string name, TransferProgress value)
        {
            lock (_gate)
            {
                _values[name] = value;
                if (name == "components")
                {
                    _phases[value.Phase] = Math.Max(_phases[value.Phase], Math.Min(0.99d, value.StageFraction));
                }
                int supportDone = this.Snapshot("client").Completed + this.Snapshot("libraries").Completed + this.Snapshot("pack").Completed;
                int assetsDone = this.Snapshot("assets").Completed + this.Snapshot("mapped").Completed;
                _phases["files"] = 14d / 32.0d * (_supportCount == 0 ? 1d : supportDone / (double)_supportCount) + 18d / 32.0d * (_assetCount == 0 ? 1d : assetsDone / (double)_assetCount);
                string phase = name == "components" ? value.Phase : "files";
                _target?.Report(new TransferProgress()
                {
                    Phase = phase,
                    Stage = name == "components" ? value.Stage : "下载原版游戏与资源文件",
                    StageFraction = name == "components" && value.StageFraction < 0d ? -1 : _phases[phase],
                    PhaseFractions = new Dictionary<string, double>(_phases),
                    PhaseTotal = name == "components" ? value.Total : _supportCount + _assetCount,
                    PhaseCompleted = name == "components" ? value.Completed : supportDone + assetsDone,
                    Detail = value.Detail,
                    CurrentFile = value.CurrentFile,
                    Source = value.Source,
                    NetworkBytes = _values.Values.Sum(v => v.NetworkBytes),
                    Reused = _values.Values.Sum(v => v.Reused),
                    Retries = _values.Values.Sum(v => v.Retries),
                    DynamicSplits = _values.Values.Sum(v => v.DynamicSplits),
                    ActiveConnections = Volatile.Read(ref _budget.ActiveConnections),
                    BytesPerSecond = _values.Where(p => p.Value.Completed < p.Value.Total).Sum(p => p.Value.BytesPerSecond),
                    Completed = supportDone + assetsDone + this.Snapshot("components").Completed,
                    Total = _supportCount + _assetCount + this.Snapshot("components").Total,
                    KnownBytes = _values.Values.Sum(v => v.KnownBytes),
                    VerifiedBytes = _values.Values.Sum(v => v.VerifiedBytes)
                });
            }
        }
    }
}