using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // Remap aliases are staged after the shared hash-addressed objects. A new shared
    // installation never overwrites existing resources, and rolls back only files it
    // created if final instance publication fails. Repairs retain completed fixes.
    internal sealed class LegacyResourcePublication : IDisposable
    {
        private readonly string _root;
        private readonly string _stage;
        private readonly string _name;
        private readonly List<DownloadFile> _files;
        private readonly bool _isolated;
        private readonly bool _repair;
        private readonly string _description;
        private readonly bool _holdTargets;
        private readonly List<FileStream> _held = new List<FileStream>();
        private readonly List<DownloadFile> _created = new List<DownloadFile>();
        private bool _completed;
        internal LegacyResourcePublication(string root, string stage, string name, List<DownloadFile> files, bool isolated, bool repair, string description = "旧资源", bool holdTargets = false)
        {
            _root = root;
            _stage = stage;
            _name = name;
            _files = files;
            _isolated = isolated;
            _repair = repair;
            _description = description;
            _holdTargets = holdTargets;
        }
        internal static void Validate(string root, IEnumerable<DownloadFile> files, bool repair, string description = "旧资源")
        {
            ValidateCore(root, files, repair, description, CancellationToken.None);
        }
        internal static void ValidateForInstallation(string root, IEnumerable<DownloadFile> files, bool repair, CancellationToken token, string description = "旧资源")
        {
            ValidateCore(root, files, repair, description, token);
        }
        private static void ValidateCore(string root, IEnumerable<DownloadFile> files, bool repair, string description, CancellationToken token)
        {
            var targets = new HashSet<string>(files.Select(item => SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                string target = SafeFiles.UnderRoot(root, item.RelativePath);
                if (Directory.Exists(target))
                    throw new IOException("目录占用了" + description + "文件：" + item.RelativePath);
                string parent = Path.GetDirectoryName(target);
                while (parent is not null)
                {
                    if (targets.Contains(parent) || File.Exists(parent))
                        throw new InvalidDataException(description + "文件与目录路径冲突：" + item.RelativePath);
                    parent = Path.GetDirectoryName(parent);
                }
                if (!repair && File.Exists(target) && !SafeFiles.Matches(target, item, token))
                    throw new IOException("已有" + description + "内容不同，不会覆盖共享文件：" + item.RelativePath);
            }
        }
        internal static void ValidateLayout(string root, IEnumerable<DownloadFile> files)
        {
            var targets = new HashSet<string>(files.Select(item => SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                string parent = Path.GetDirectoryName(target);
                while (parent is not null)
                {
                    if (targets.Contains(parent))
                        throw new InvalidDataException("下载文件与旧资源映射的目录路径冲突。");
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }
        internal List<DownloadFile> StagedFiles()
        {
            return _files.Select(item => new DownloadFile()
            {
                RelativePath = _isolated ? item.RelativePath.Substring(("versions/" + _name + "/").Length) : item.RelativePath,
                Url = item.Url,
                Sha1 = item.Sha1,
                Md5 = item.Md5,
                Size = item.Size,
                Category = "asset"
            }).ToList();
        }
        internal void Publish(CancellationToken token, IInstanceConfigurationGuard guard = null)
        {
            if (_isolated && !_repair)
                return; // Directory.Move publishes the whole payload.
            ValidateCore(_root, _files, _repair, _description, token);
            var staged = StagedFiles();
            for (int i = 0, loopTo = _files.Count - 1; i <= loopTo; i++)
            {
                token.ThrowIfCancellationRequested();
                guard?.Verify();
                var item = _files[i];
                string target = SafeFiles.UnderRoot(_root, item.RelativePath);
                if (_holdTargets && File.Exists(target))
                    HoldTarget(target);
                if (SafeFiles.Matches(target, item, token))
                {
                    token.ThrowIfCancellationRequested();
                    guard?.Verify();
                    DeleteOwnedFile(SafeFiles.UnderRoot(_stage, staged[i].RelativePath));
                    continue;
                }
                token.ThrowIfCancellationRequested();
                guard?.Verify();
                if (!_repair && File.Exists(target))
                    throw new IOException(_description + "目标在安装期间发生变化：" + item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string source = SafeFiles.UnderRoot(_stage, staged[i].RelativePath);
                if (!SafeFiles.Matches(source, item, token))
                    throw new InvalidDataException("待发布的" + _description + "文件校验失败：" + item.RelativePath);
                token.ThrowIfCancellationRequested();
                guard?.Verify();
                if (_repair)
                {
                    SafeFiles.CommitFile(source, target);
                }
                else
                {
                    File.Move(source, target);
                    _created.Add(item);
                    if (_holdTargets)
                    {
                        HoldTarget(target);
                        if (!SafeFiles.Matches(target, item, token))
                            throw new IOException(_description + "目标在发布期间发生变化：" + item.RelativePath);
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            guard?.Verify();
        }
        internal void Complete()
        {
            _completed = true;
        }
        private void HoldTarget(string target)
        {
            // Shared Mods are few, unlike asset aliases. Hold existing or freshly
            // moved files only through the short final commit, allowing game readers
            // while preventing a later step from observing a changed earlier Mod.
            _held.Add(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        public void Dispose()
        {
            // Release our leases before rolling back owned files, including when
            // cancellation was raised by a final content check.
            foreach (var stream in _held)
                stream.Dispose();
            _held.Clear();
            if (_completed || _repair)
                return;
            foreach (var item in _created.AsEnumerable().Reverse())
            {
                try
                {
                    string target = SafeFiles.UnderRoot(_root, item.RelativePath);
                    if (SafeFiles.Matches(target, item))
                        DeleteOwnedFile(target);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    InstallLog.Append(_root, "未完成实例的共享" + _description + "回收失败：" + item.RelativePath + " | " + ex.Message);
                }
            }
        }
        private static void DeleteOwnedFile(string path)
        {
            if (!File.Exists(path))
                return;
            // File.Copy retains read-only attributes. These are our private aliases or
            // newly created, uncommitted shared files, never an existing user's file.
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }
}