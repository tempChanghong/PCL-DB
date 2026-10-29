using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    // After a checked target has been committed, cache cleanup must not restart the
    // transfer or replace its outcome. Recovery of invalid/incomplete data stays strict.
    internal sealed class TransferCleanup
    {
        private TransferCleanup()
        {
        }

        internal static void DeleteCommitted(string root, string key)
        {
            if (!Regex.IsMatch(key, @"\A[0-9a-f]{64}\z"))
                throw new ArgumentException("无效的传输缓存标识。", nameof(key));
            string relative = ".vanilla-downloader/transfers/" + key;
            try
            {
                string cache = Path.GetDirectoryName(SafeFiles.UnderRoot(root, relative + "/entry"));
                if (!Directory.Exists(cache))
                    return;
                foreach (var path in Directory.EnumerateFiles(cache, "segment-*.part").ToArray())
                {
                    string name = Path.GetFileName(path);
                    if (Regex.IsMatch(name, @"\Asegment-\d+\.part\z"))
                        DeleteFile(root, relative + "/" + name);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogIncomplete(root, relative, ex);
            }
            foreach (var name in new[] { "segments.json", "segments.tmp", "0.part", "1.part", "2.part", "3.part" })
                DeleteFile(root, relative + "/" + name);
        }

        private static void DeleteFile(string root, string relative)
        {
            try
            {
                string path = SafeFiles.UnderRoot(root, relative);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogIncomplete(root, relative, ex);
            }
        }

        private static void LogIncomplete(string root, string relative, Exception failure)
        {
            try
            {
                string log = SafeFiles.UnderRoot(root, ".vanilla-downloader/download.log");
                File.AppendAllText(log, DateTimeOffset.Now.ToString("o") + " 下载缓存清理未完成：" + relative + " | " + failure.Message + Constants.vbCrLf, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Logging is optional once the verified target is published.
            }
        }
    }
}