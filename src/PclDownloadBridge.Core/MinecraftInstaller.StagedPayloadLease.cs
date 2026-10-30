using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PclDownloadBridge.Core
{
    public partial class MinecraftInstaller
    {
        // Keep already checked pack files stable while later files and final
        // guards run. Windows requires every child handle closed before Move.
        private sealed class StagedPayloadLease : IDisposable
        {
            private readonly List<FileStream> _held = new List<FileStream>();

            internal StagedPayloadLease(string root, string stageRelative, CancellationToken token)
            {
                try
                {
                    var pending = new Stack<string>();
                    pending.Push(stageRelative);
                    while (pending.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = pending.Pop();
                        string directory = SafeFiles.UnderRoot(root, relative);
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("安装临时目录包含链接。");
                        // Validate each directory before descending; recursive
                        // framework enumeration could otherwise follow a junction.
                        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            token.ThrowIfCancellationRequested();
                            string child = relative + "/" + Path.GetFileName(entry);
                            string path = SafeFiles.UnderRoot(root, child);
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                throw new IOException("安装临时目录包含链接。");
                            if ((attributes & FileAttributes.Directory) != 0)
                                pending.Push(child);
                            else
                                _held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                        }
                    }
                    token.ThrowIfCancellationRequested();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (var stream in _held)
                    stream.Dispose();
                _held.Clear();
            }
        }
    }
}
