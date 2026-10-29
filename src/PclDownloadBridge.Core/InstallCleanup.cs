using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // PCL logs cleanup failures separately instead of replacing the install outcome.
    // Only this installation's fresh private work/payload tree may be removed here.
    internal sealed class InstallCleanup
    {
        private InstallCleanup()
        {
        }

        internal static void DeleteOwnedDirectory(string root, string relative)
        {
            if (!Regex.IsMatch(relative, @"\A\.vanilla-downloader/(?:components/[0-9a-f]{32}|staging/[0-9a-f]{32}/payload)\z"))
            {
                throw new ArgumentException("不是安装任务拥有的临时目录。", nameof(relative));
            }
            try
            {
                string target = SafeFiles.UnderRoot(root, relative);
                if (!Directory.Exists(target))
                    return;
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("安装临时目录包含链接。");
                Directory.Delete(target, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogIncomplete(root, relative, ex);
            }
        }

        private static void LogIncomplete(string root, string relative, Exception failure)
        {
            InstallLog.Append(root, "安装临时文件清理未完成：" + relative + " | " + failure.Message);
        }
    }
}