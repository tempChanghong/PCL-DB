using System;
using System.Diagnostics;
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

        // Cancel invokes all registrations synchronously and can throw their errors.
        // A cleanup notification must not replace the operation's failure/winner or
        // skip its worker join. The same rule applies to response-disposal callbacks
        // running on CancellationTokenSource's deadline timer.
        internal static void NotifyCancellation(Action notification, string root = null)
        {
            try
            {
                notification();
            }
            catch (Exception failure)
            {
                try
                {
                    InstallLog.Failure(root, "取消收尾通知失败", new[] { failure });
                }
                catch
                {
                    // Cancellation still owns its original outcome if logging fails.
                }
                try
                {
                    Trace.TraceWarning("PCL-DB cancellation cleanup: {0}", failure);
                }
                catch
                {
                    // Diagnostic listeners must not make cancellation throw either.
                }
            }
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
