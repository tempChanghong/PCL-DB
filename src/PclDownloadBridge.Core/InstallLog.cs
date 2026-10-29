using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    // Best-effort cumulative game-directory log, shared by downloads and processors.
    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    internal sealed class InstallLog
    {
        private InstallLog() { }
        private readonly static object Gate = new object();

        internal static void Append(string root, string message)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;
            try
            {
                string path = SafeFiles.UnderRoot(root, ".vanilla-downloader/installer.log");
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Constants.vbCrLf, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Missing permissions, occupied paths or unavailable optional logs must
                // never replace success, cancellation, or the original failure.
            }
        }

        internal static void Failure(string root, string title, IEnumerable<Exception> errors)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;
            try
            {
                Exception[] failures = errors.Where(issue => issue is not null).ToArray();
                if (failures.Length > 0)
                    Append(root, new AggregateException(title, failures).ToString());
            }
            catch
            {
                // Diagnostic collection/formatting is optional too.
            }
        }
    }
}