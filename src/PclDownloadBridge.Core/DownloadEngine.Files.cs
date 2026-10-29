using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    public partial class DownloadEngine
    {
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
            if (SafeFiles.Matches(target, item, token))
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
                    reusable = SafeFiles.Matches(candidate, item, token);
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
                            if (!SafeFiles.Matches(copyTemp, item, token))
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
                        bool completeCached = (CanResumeSingle(item) || _modrinthTransfer.Value) && SafeFiles.Matches(merged, item, token);
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
                            if (retryWithoutLength && !SafeFiles.Matches(merged, item, token))
                                await SingleAsync(source, merged, item, token).ConfigureAwait(false);
                        }
                        if (!SafeFiles.Matches(merged, item, token))
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

    }
}
