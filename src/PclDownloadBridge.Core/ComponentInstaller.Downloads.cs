using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    public partial class ComponentInstaller
    {
        private async Task FetchAsync(string root, IEnumerable<DownloadFile> files, string title, CancellationToken token, bool allowReuse = true)
        {
            var items = DownloadPlan.Merge(files, root);
            if (items.Count == 0)
                return;
            var reporter = new InlineProgress(value =>
                {
                    value.Phase = "components";
                    value.Stage = title;
                    value.StageFraction = value.Fraction;
                    value.NetworkBytes += NetworkBytes;
                    value.Reused += Reused;
                    value.DynamicSplits += DynamicSplits;
                    value.Retries += Retries;
                    _progress?.Report(value);
                });
            // Installer-profile dependencies can include the final runtime too, before
            // the final missing-library pass. Apply the same donor rule at every fetch
            // boundary, not only in that final pass, or a bad shared file could already
            // have been copied into work and then acquire a self-consistent local hash.
            foreach (var batch in items.GroupBy(item => allowReuse && ComponentRuntimePublication.HasDigest(item)))
            {
                bool reusable = batch.Key;
                using (var engine = new DownloadEngine(_policy, reporter, reusable ? _reuse : Enumerable.Empty<string>(), _connections, reusable ? _root : null, useComponentCache: reusable, budget: _budget))
                {
                    engine.BytesPerSecondLimit = _limit;
                    await engine.DownloadAsync(root, batch, token).ConfigureAwait(false);
                    NetworkBytes += engine.NetworkBytes;
                    Reused += engine.Reused;
                    DynamicSplits += engine.DynamicSplits;
                    Retries += engine.Retries;
                    FileCount += batch.Count();
                }
            }
        }
        private async Task FetchOptiFineAsync(string root, string relative, ComponentEntry entry, CancellationToken token)
        {
            Func<Task> mirror = async () =>
                {
                    await FetchAsync(root, new[] { new DownloadFile() { RelativePath = relative, Url = entry.Url, Category = "component" } }, "下载 OptiFine（BMCLAPI）", token).ConfigureAwait(false);
                    ValidateOptiFine(SafeFiles.UnderRoot(root, relative));
                };
            Func<Task> official = () => FetchOptiFineOfficialAsync(root, relative, entry, token);
            var failures = new List<Exception>();
            foreach (var download in _policy.PreferOfficial ? (new[] { official, mirror }) : (new[] { mirror, official }))
            {
                try
                {
                    await download().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures.Add(ex);
                    string invalid = SafeFiles.UnderRoot(root, relative);
                    if (File.Exists(invalid))
                        File.Delete(invalid); // owned private payload only
                }
            }
            token.ThrowIfCancellationRequested();
            throw new AggregateException("OptiFine 官方源与 BMCLAPI 均下载失败。", failures);
        }
        private async Task FetchOptiFineOfficialAsync(string root, string relative, ComponentEntry entry, CancellationToken token)
        {
            // PCL's official adloadx -> downloadx resolution, still using the shared engine.
            string page = await _policy.GetTextAsync("https://optifine.net/adloadx?f=" + Uri.EscapeDataString(entry.FileName), token).ConfigureAwait(false);
            var match = Regex.Match(page, @"downloadx\?f=[^""'\s<>]+");
            if (!match.Success)
                throw new InvalidDataException("OptiFine 官方下载链接解析失败。");
            string source = "https://optifine.net/" + System.Net.WebUtility.HtmlDecode(match.Value);
            string path = SafeFiles.UnderRoot(root, relative);
            if (File.Exists(path))
                File.Delete(path); // only this private staging payload
            await FetchAsync(root, new[] { new DownloadFile() { RelativePath = relative, Url = source, Category = "component" } }, "下载 OptiFine（官方源）", token).ConfigureAwait(false);
            ValidateOptiFine(path);
        }
        private void ValidateOptiFine(string path)
        {
            // PCL McDownloadOptiFineSaveLoader uses 64 KiB for a mod, while
            // McDownloadOptiFineLoader requires 300 KiB for standalone installation.
            bool asMod = _selection.Has(ComponentKind.Forge) || _selection.Has(ComponentKind.Fabric) || _selection.Has(ComponentKind.LiteLoader);
            long minimum = (asMod ? 64 : 300) * 1024L;
            if (new FileInfo(path).Length < minimum)
                throw new InvalidDataException("OptiFine 下载文件过小，至少需要 " + (minimum / 1024L).ToString() + " KiB。");
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.GetEntry("optifine/Installer.class") is null)
                    throw new InvalidDataException("下载文件不是有效的 OptiFine 安装器。");
            }
        }
    }
}
