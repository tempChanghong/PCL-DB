using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    public sealed class ModpackSearchPreview
    {
        public IReadOnlyList<ModpackProject> Projects { get; private set; }
        public ModpackPlatform[] Pending { get; private set; }
        internal ModpackSearchPreview(IEnumerable<ModpackProject> items, IEnumerable<ModpackPlatform> pendingSources)
        {
            Projects = items.ToList().AsReadOnly();
            Pending = pendingSources.ToArray();
        }
    }

    // PCL ResourceSearcher.SearchResult: one cursor per source, append-only results,
    // partial failure, and a stable 40-item presentation page. No fuzzy identity merge.
    public sealed class ModpackSearchSession
    {
        // Zero-based presentation pages. Source cursors may still advance while the
        // last legal page is incomplete; a full final page must never offer page 501.
        public const int MaxPage = 500;
        private readonly ModpackQuery _query;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);
        private readonly List<ModpackProject> _results = new List<ModpackProject>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<ModpackPlatform, int> _offsets = new Dictionary<ModpackPlatform, int>();
        private readonly HashSet<ModpackPlatform> _ended = new HashSet<ModpackPlatform>();
        private readonly Dictionary<ModpackPlatform, string> _errors = new Dictionary<ModpackPlatform, string>();
        public ModpackSearchSession(ModpackQuery query)
        {
            _query = query.Copy();
            // Validate even when a category excludes both sources.
            ModpackCatalogClient.SearchUrl(ModpackPlatform.Modrinth, _query, 0);
            string[] tags = _query.Category.Split('/');
            foreach (var platform in new[] { ModpackPlatform.CurseForge, ModpackPlatform.Modrinth })
            {
                if (!_query.Platforms.HasFlag(platform))
                    continue;
                if (_query.Category != "/" && string.IsNullOrEmpty(tags[platform == ModpackPlatform.CurseForge ? 0 : 1]))
                    continue;
                _offsets[platform] = 0;
            }
            // PCL ResourceSearcher distinguishes an unsupported category from no hits.
            if (_offsets.Count == 0)
                throw new ArgumentException(_query.Platforms.ToString() + " 不支持所选的" + CommunityResources.Name(_query.Kind) + "类型，请更改来源或类型。");
        }
        // PCL opens details with Loader.Input.GameVersion, not the editable form.
        public ModpackQuery Query
        {
            get
            {
                return _query.Copy();
            }
        }
        public bool HasMore
        {
            get
            {
                return _offsets.Keys.Any(platform => !_ended.Contains(platform) && !_errors.ContainsKey(platform));
            }
        }
        public int Count
        {
            get
            {
                return _results.Count;
            }
        }
        public string ErrorMessage
        {
            get
            {
                return string.Join(Constants.vbCrLf, _errors.Select(pair => pair.Key.ToString() + "：" + pair.Value));
            }
        }
        public ModpackPlatform[] FailedPlatforms
        {
            get
            {
                return _errors.Keys.ToArray();
            }
        }
        public bool HasNext(int page)
        {
            if (page < 0 || page > MaxPage)
                return false;
            if (page == MaxPage)
                return _results.Count < (page + 1) * ModpackCatalogClient.PageSize && HasMore;
            return _results.Count > (page + 1) * ModpackCatalogClient.PageSize || HasMore;
        }
        private class FetchResult
        {
            public ModpackPlatform Platform;
            public ModpackCatalogPage Page;
            public string Failure;
        }
        private static async Task<FetchResult> FetchAsync(ModpackCatalogClient client, ModpackPlatform platform, ModpackQuery query, int offset, CancellationToken token)
        {
            try
            {
                return new FetchResult() { Platform = platform, Page = await client.SearchAsync(platform, query, offset, token).ConfigureAwait(false) };
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return new FetchResult() { Platform = platform, Failure = ex.GetBaseException().Message };
            }
        }
        public async Task<List<ModpackProject>> GetPageAsync(int page, ModpackCatalogClient client, CancellationToken token, IProgress<ModpackSearchPreview> progress = null)
        {
            if (page < 0 || page > MaxPage)
                throw new ArgumentOutOfRangeException(nameof(page));
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                int target = (page + 1) * ModpackCatalogClient.PageSize;
                for (int round = 1; round <= 4; round++)
                {
                    if (_results.Count >= target || !HasMore)
                        break;
                    ModpackPlatform[] active = _offsets.Keys.Where(platform => !_ended.Contains(platform) && !_errors.ContainsKey(platform)).ToArray();
                    FetchResult[] batches;
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var pending = active.Select(platform => new { Platform = platform, Work = FetchAsync(client, platform, _query, _offsets[platform], linked.Token) }).ToList();
                        var started = pending.Select(item => item.Work).ToArray();
                        try
                        {
                            if (progress is not null && pending.Count > 1)
                            {
                                var completed = new List<FetchResult>();
                                while (pending.Count > 0)
                                {
                                    var work = await Task.WhenAny(pending.Select(item => item.Work)).ConfigureAwait(false);
                                    var batch = await work.ConfigureAwait(false);
                                    pending.RemoveAll(item => ReferenceEquals(item.Work, work));
                                    completed.Add(batch);
                                    token.ThrowIfCancellationRequested();
                                    if (pending.Count > 0)
                                    {
                                        var freshPreview = completed.Where(item => item.Page is not null).SelectMany(item => item.Page.Projects).GroupBy(item => item.Key, StringComparer.Ordinal).Select(@group => @group.First()).Where(item => !_seen.Contains(item.Key)).OrderByDescending(item => Relevance(item.Name)).ThenByDescending(item => item.Downloads).ThenBy(item => item.Key, StringComparer.Ordinal);
                                        var preview = _results.Concat(freshPreview).Skip(page * ModpackCatalogClient.PageSize).Take(ModpackCatalogClient.PageSize);
                                        progress.Report(new ModpackSearchPreview(preview, pending.Select(item => item.Platform)));
                                    }
                                }
                                batches = completed.ToArray();
                            }
                            else
                            {
                                batches = await Task.WhenAll(started).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // Keep the client and session gate owned until every source
                            // has joined, including when publishing a preview fails.
                            InstallCleanup.NotifyCancellation(linked.Cancel);
                            try
                            {
                                await Task.WhenAll(started).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Observe cleanup faults without replacing the original
                                // preview failure or the caller's cancellation identity.
                            }
                            token.ThrowIfCancellationRequested();
                            throw;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    var fresh = new List<ModpackProject>();
                    foreach (var batch in batches)
                    {
                        if (batch.Failure is not null)
                        {
                            _errors[batch.Platform] = batch.Failure;
                            continue;
                        }
                        var value = batch.Page;
                        _offsets[batch.Platform] += value.Returned;
                        if (value.Returned == 0 || _offsets[batch.Platform] >= value.Total)
                            _ended.Add(batch.Platform);
                        foreach (var project in value.Projects)
                        {
                            if (_seen.Add(project.Key))
                                fresh.Add(project);
                        }
                        // Repeated non-empty raw pages must not create an endless next button.
                        if (value.Projects.Count > 0 && value.Projects.All(project => _results.Any(previous => (previous.Key ?? "") == (project.Key ?? ""))))
                        {
                            _ended.Add(batch.Platform);
                            _errors[batch.Platform] = "平台分页返回了重复结果，请重新搜索。";
                        }
                    }
                    _results.AddRange(fresh.OrderByDescending(project => Relevance(project.Name)).ThenByDescending(project => project.Downloads).ThenBy(project => project.Key, StringComparer.Ordinal));
                }
                return _results.Skip(page * ModpackCatalogClient.PageSize).Take(ModpackCatalogClient.PageSize).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }
        private int Relevance(string name)
        {
            string text = _query.Text.Trim();
            if (string.IsNullOrEmpty(text))
                return 0;
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return 2;
            return name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
        }
    }
}
