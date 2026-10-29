using System;

namespace PclDownloadBridge.Core
{

    public class InlineProgress : IProgress<TransferProgress>
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
