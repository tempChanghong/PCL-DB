using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PclDownloadBridge.Core
{

    // PCL ModNet.LoaderDownload deduplicates by normalized LocalPath before starting.
    // Merge compatible checks instead of discarding stronger duplicate declarations.
    internal sealed class DownloadPlan
    {
        private DownloadPlan()
        {
        }

        internal static List<DownloadFile> Merge(IEnumerable<DownloadFile> files, string root = null)
        {
            var result = new List<DownloadFile>();
            var targets = new Dictionary<string, DownloadFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.RelativePath))
                    throw new InvalidDataException("下载文件路径为空。");
                // Planners have no root yet; retain unresolved dot segments until the full
                // batch can be validated against its actual destination, without writes.
                string key = root is null ? item.RelativePath.Replace('\\', '/') : SafeFiles.UnderRoot(root, item.RelativePath);
                DownloadFile existing = null;
                if (targets.TryGetValue(key, out existing))
                {
                    if (existing.Size >= 0L && item.Size >= 0L && existing.Size != item.Size)
                        Conflict(item.RelativePath, "文件大小");
                    if (existing.Size < 0L && item.Size >= 0L)
                        existing.Size = item.Size;
                    existing.Sha1 = MergeHash(existing.Sha1, item.Sha1, item.RelativePath, "SHA-1");
                    existing.Sha512 = MergeHash(existing.Sha512, item.Sha512, item.RelativePath, "SHA-512");
                    existing.Md5 = MergeHash(existing.Md5, item.Md5, item.RelativePath, "MD5");
                    existing.S3SinglePartETag = MergeHash(existing.S3SinglePartETag, item.S3SinglePartETag, item.RelativePath, "S3 ETag");
                }
                else
                {
                    // Never mutate records owned by metadata/component callers. Preserve
                    // the first source/category and ordinary transfer-cache key spelling.
                    DownloadFile @init = new DownloadFile();
                    var copy = (@init.RelativePath = item.RelativePath, @init.Url = item.Url, @init.Size = item.Size, @init.Sha1 = item.Sha1, @init.Sha512 = item.Sha512, @init.EstimatedSize = item.EstimatedSize, @init.AlternateUrls = item.AlternateUrls?.ToArray(), @init.Md5 = item.Md5, @init.S3SinglePartETag = item.S3SinglePartETag, @init.Category = item.Category, @init).@init;
                    targets.Add(key, copy);
                    result.Add(copy);
                }
            }
            return result;
        }

        private static string MergeHash(string first, string incoming, string relative, string algorithm)
        {
            if (string.IsNullOrWhiteSpace(first))
                return string.IsNullOrWhiteSpace(incoming) ? first : incoming;
            if (!string.IsNullOrWhiteSpace(incoming) && !string.Equals(first, incoming, StringComparison.OrdinalIgnoreCase))
                Conflict(relative, algorithm);
            return first;
        }

        private static void Conflict(string relative, string @field)
        {
            throw new InvalidDataException("同一下载文件的" + @field + "声明冲突：" + relative);
        }
    }
}