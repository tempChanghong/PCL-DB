using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // Private checksum-addressed stores for downloads and declared processor outputs.
    // It never promotes install-only files into the launcher's shared libraries.
    internal class VerifiedComponentCache
    {
        private readonly string _root;
        private readonly List<string> _readRoots;
        private readonly bool _processorOutputs;

        internal VerifiedComponentCache(string root, IEnumerable<string> reuseRoots, bool processorOutputs = false)
        {
            _root = root;
            _readRoots = new[] { root }.Concat(reuseRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _processorOutputs = processorOutputs;
        }

        private static string RelativePath(DownloadFile item, bool processorOutputs = false)
        {
            string algorithm;
            string digest;
            if (!string.IsNullOrWhiteSpace(item.Sha512))
            {
                if (!Regex.IsMatch(item.Sha512, @"\A[0-9a-fA-F]{128}\z"))
                    return null;
                algorithm = "sha512";
                digest = item.Sha512.ToLowerInvariant();
            }
            else if (!string.IsNullOrWhiteSpace(item.Sha1))
            {
                if (!Regex.IsMatch(item.Sha1, @"\A[0-9a-fA-F]{40}\z"))
                    return null;
                algorithm = "sha1";
                digest = item.Sha1.ToLowerInvariant();
            }
            else if (!string.IsNullOrWhiteSpace(item.Md5))
            {
                if (!Regex.IsMatch(item.Md5, @"\A[0-9a-fA-F]{32}\z"))
                    return null;
                algorithm = "md5";
                digest = item.Md5.ToLowerInvariant();
            }
            else if (Regex.IsMatch(item.S3SinglePartETag ?? "", @"\A[0-9a-fA-F]{32}-1\z"))
            {
                algorithm = "s3-etag1";
                digest = item.S3SinglePartETag.Substring(0, 32).ToLowerInvariant();
            }
            else
            {
                // A stable-looking filename or URL is not an integrity declaration.
                return null;
            }
            return ".vanilla-downloader/" + (processorOutputs ? "processor-cache/" : "component-cache/") + algorithm + "/" + digest.Substring(0, 2) + "/" + digest + ".bin";
        }

        internal bool TryRestore(string target, DownloadFile item, CancellationToken token)
        {
            string relative = RelativePath(item, _processorOutputs);
            if (relative is null)
                return false;
            foreach (var readRoot in _readRoots)
            {
                token.ThrowIfCancellationRequested();
                string temporary = null;
                try
                {
                    try
                    {
                        string candidate = SafeFiles.UnderRoot(readRoot, relative);
                        if (!SafeFiles.Matches(candidate, item))
                            continue;
                        temporary = Path.Combine(Path.GetDirectoryName(target), ".pcldb.cache-" + Guid.NewGuid().ToString("N") + ".tmp");
                        CopyChecked(candidate, temporary, item, token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                        // Optional cache reads/copies may fall back. A real destination
                        // commit failure below must remain an error, just as with reuse.
                        continue;
                    }
                    SafeFiles.CommitFile(temporary, target);
                    return true;
                }
                finally
                {
                    DeleteTemporary(temporary);
                }
            }
            return false;
        }

        internal void Save(string source, DownloadFile item, CancellationToken token)
        {
            string relative = RelativePath(item, _processorOutputs);
            if (relative is null)
                return;
            string temporary = null;
            try
            {
                token.ThrowIfCancellationRequested();
                string target = SafeFiles.UnderRoot(_root, relative);
                if (SafeFiles.Matches(target, item))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                temporary = target + ".cache-" + Guid.NewGuid().ToString("N");
                CopyChecked(source, temporary, item, token);
                SafeFiles.CommitFile(temporary, target);
            }
            // Persisting a performance cache must not invalidate a completed download.
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }
            finally
            {
                DeleteTemporary(temporary);
            }
        }

        internal static void ReleasePublishedClient(string root, DownloadFile item)
        {
            string relative = RelativePath(item);
            if (relative is null)
                return;
            try
            {
                string cached = SafeFiles.UnderRoot(root, relative);
                // versions/ now supplies future client reuse; retain this extra copy only
                // when installation was interrupted before publication.
                if (SafeFiles.Matches(cached, item))
                    File.Delete(cached);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Cleanup must not turn a successful publication into an install failure.
            }
        }

        private static void CopyChecked(string source, string temporary, DownloadFile item, CancellationToken token)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    output.Write(buffer, 0, count);
                }
                while (true);
            }
            token.ThrowIfCancellationRequested();
            if (!SafeFiles.Matches(temporary, item))
                throw new InvalidDataException("组件缓存副本校验失败。");
            token.ThrowIfCancellationRequested();
        }

        private static void DeleteTemporary(string temporary)
        {
            if (temporary is null)
                return;
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Never make an otherwise valid install fail because optional cleanup failed.
            }
        }
    }
}