using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // PCL McInstallLoadersGet migrates checked runtime libraries out of its private
    // component tree. Keep that boundary, but never identify a hashless shared file
    // by length alone. Local digests describe this payload, not upstream metadata.
    internal sealed class ComponentRuntimePublication : IDisposable
    {
        private readonly string _root;
        private readonly List<FileStream> _held = new List<FileStream>();
        private readonly List<DownloadFile> _frozen = new List<DownloadFile>();
        internal ComponentRuntimePublication(string root)
        {
            _root = root;
        }
        private static DownloadFile Snapshot(DownloadFile item)
        {
            // Final publication owns its target and integrity declaration, not
            // a caller's mutable plan. No URL or other download state is needed.
            return new DownloadFile()
            {
                RelativePath = item.RelativePath,
                Size = item.Size,
                Sha1 = item.Sha1,
                Sha512 = item.Sha512,
                Md5 = item.Md5,
                S3SinglePartETag = item.S3SinglePartETag,
                Category = item.Category
            };
        }
        internal static bool HasDigest(DownloadFile item)
        {
            return !string.IsNullOrWhiteSpace(item.Sha1) || !string.IsNullOrWhiteSpace(item.Sha512) || !string.IsNullOrWhiteSpace(item.Md5) || !string.IsNullOrWhiteSpace(item.S3SinglePartETag);
        }
        internal List<DownloadFile> FrozenFiles
        {
            get
            {
                return _frozen.Select(Snapshot).ToList();
            }
        }
        internal void Publish(string work, IEnumerable<DownloadFile> files, CancellationToken token)
        {
            foreach (var supplied in files)
            {
                token.ThrowIfCancellationRequested();
                var item = Snapshot(supplied);
                string source = SafeFiles.UnderRoot(work, item.RelativePath);
                string destination = SafeFiles.UnderRoot(_root, item.RelativePath);
                bool hashless = !HasDigest(item);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!SafeFiles.Matches(source, item, token))
                        throw new InvalidDataException("组件支持库校验失败：" + item.RelativePath);
                    var expected = hashless ? Freeze(input, item, token) : item;
                    token.ThrowIfCancellationRequested();
                    bool existed = File.Exists(destination);
                    if (existed)
                    {
                        var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                        try
                        {
                            if (SafeFiles.Matches(destination, expected, token))
                            {
                                token.ThrowIfCancellationRequested();
                                _held.Add(held);
                                held = null;
                                _frozen.Add(expected);
                                continue;
                            }
                            if (hashless)
                                throw new IOException("已有共享支持库内容不同，不会覆盖无上游摘要的文件：" + item.RelativePath);
                        }
                        finally
                        {
                            held?.Dispose();
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    string temporary = destination + ".component-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        input.Position = 0L;
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
                        if (!SafeFiles.Matches(temporary, expected, token))
                            throw new InvalidDataException("组件支持库副本校验失败：" + item.RelativePath);
                        token.ThrowIfCancellationRequested();
                        // An absent target must stay create-only, even if another writer
                        // creates it between the check above and this same-volume rename.
                        if (existed)
                        {
                            SafeFiles.CommitFile(temporary, destination);
                        }
                        else
                        {
                            File.Move(temporary, destination);
                        }
                        var held = new FileStream(SafeFiles.UnderRoot(_root, item.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                        try
                        {
                            if (!SafeFiles.Matches(destination, expected, token))
                                throw new IOException("组件支持库在发布期间发生变化：" + item.RelativePath);
                            token.ThrowIfCancellationRequested();
                            _held.Add(held);
                            held = null;
                            _frozen.Add(expected);
                        }
                        finally
                        {
                            held?.Dispose();
                        }
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }
            }
        }
        private static DownloadFile Freeze(FileStream input, DownloadFile item, CancellationToken token)
        {
            input.Position = 0L;
            using (var hash = SHA512.Create())
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                hash.TransformFinalBlock(new byte[] { }, 0, 0);
                token.ThrowIfCancellationRequested();
                return new DownloadFile()
                {
                    RelativePath = item.RelativePath,
                    Size = input.Length,
                    Sha512 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant(),
                    Category = "library"
                };
            }
        }
        public void Dispose()
        {
            foreach (var held in _held)
                held.Dispose();
            _held.Clear();
        }

        // Work was removed after Apply. Every checked runtime must retain its
        // original expectation, whether declared upstream or computed locally;
        // reacquiring a lease must never bless current shared bytes anew.
        internal sealed class PublishedLease : IDisposable
        {
            private readonly string _root;
            private readonly List<DownloadFile> _files;
            private readonly List<FileStream> _held = new List<FileStream>();
            internal PublishedLease(string root, IEnumerable<DownloadFile> files, CancellationToken token)
            {
                _root = root;
                _files = files.Select(Snapshot).ToList();
                try
                {
                    foreach (var item in _files)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = SafeFiles.UnderRoot(root, item.RelativePath);
                        _held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                    Verify(token);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
            internal void Verify(CancellationToken token)
            {
                foreach (var item in _files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!SafeFiles.Matches(SafeFiles.UnderRoot(_root, item.RelativePath), item, token))
                        throw new IOException("组件支持库在实例发布前发生变化：" + item.RelativePath);
                    token.ThrowIfCancellationRequested();
                }
            }
            public void Dispose()
            {
                foreach (var held in _held)
                    held.Dispose();
                _held.Clear();
            }
        }
    }
}