using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PclDownloadBridge.Core
{

    public sealed class LocalModActionResult
    {
        public string ModsDirectory { get; private set; }
        public int ChangedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public IReadOnlyList<string> Warnings { get; private set; }
        public string BackupDirectory { get; private set; }
        public LocalModActionResult(string directory, int changed, int skipped, IEnumerable<string> messages = null, string backup = null)
        {
            ModsDirectory = directory;
            ChangedCount = changed;
            SkippedCount = skipped;
            BackupDirectory = backup;
            Warnings = (messages ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToList().AsReadOnly();
        }
    }

    // PCL 5ef81e4 PageInstanceMod.EDMods / InstallMods: PathIndie/mods, terminal
    // .disabled/.old and import-to-enabled names. Imports remain create-only;
    // explicit updates verify and back up originals; launcher settings are read-only.
    public sealed partial class LocalModActions
    {
        private LocalModActions()
        {
        }

        public static LocalModActionResult SetEnabled(InstalledInstance instance, LocalModEntry entry, bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || !entry.CanToggle)
                throw new InvalidDataException("无法读取这个 Mod 的文件内容，请刷新后重试。");
            string directory = LocalModCatalog.ModsDirectory(instance, token);
            string source = Child(directory, entry.FileName);
            if (!Same(source, entry.FilePath))
                throw new InvalidDataException("Mod 不属于当前实例的实际 Mods 目录。");
            using (var gate = new OperationGate(instance, directory, token))
            {
                InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException("Mods 文件夹已不存在，请刷新。");
                gate.HoldModsDirectory(directory);
                using (var lease = NativeLease.Open(source, true))
                {
                    CheckDigest(lease.Stream, entry.Length, entry.Sha256, token);
                    if (entry.Enabled == enabled)
                        return new LocalModActionResult(directory, 0, 1);
                    string destination = Child(directory, enabled ? entry.EnabledName : entry.EnabledName + ".disabled");
                    EnsureAliases(directory, entry.EnabledName, source);
                    if (File.Exists(destination) || Directory.Exists(destination))
                        throw Conflict(destination);
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    token.ThrowIfCancellationRequested();
                    lease.Rename(destination);
                    try
                    {
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                        EnsureAliases(directory, entry.EnabledName, destination);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            lease.Rename(source);
                        }
                        catch (Exception rollback)
                        {
                            throw new IOException("Mod 状态已变化，但原名称被占用，无法自动恢复；现有文件均已保留，请刷新后检查。", new AggregateException(ex, rollback));
                        }
                        throw;
                    }
                    return new LocalModActionResult(directory, 1, 0);
                }
            }
        }

        public static LocalModActionResult ImportFiles(InstalledInstance instance, IEnumerable<string> sourcePaths, CancellationToken token)
        {
            return ImportCore(instance, sourcePaths, token, null);
        }

        internal static LocalModActionResult ImportResource(InstalledInstance instance, string source, ModpackRelease release, CancellationToken token, Action validate = null)
        {
            if (release.Kind != CommunityResourceKind.Mod || (Path.GetFileName(source) ?? "") != (release.FileName ?? ""))
                throw new InvalidDataException("下载文件与所选 Mod 不符。");
            return ImportCore(instance, new[] { source }, token, null, release.Copy(), validate);
        }

        internal static LocalModActionResult ImportFilesForChecks(InstalledInstance instance, IEnumerable<string> sourcePaths, CancellationToken token, Action<int, string> beforePublish)
        {
            return ImportCore(instance, sourcePaths, token, beforePublish);
        }

        private sealed class Input
        {
            internal LocalModEntry Entry;
            internal NativeLease Source;
            internal string Destination;
            internal NativeLease Staged;
        }

        private static LocalModActionResult ImportCore(InstalledInstance instance, IEnumerable<string> sourcePaths, CancellationToken token, Action<int, string> beforePublish, ModpackRelease resource = null, Action validateResource = null)
        {
            token.ThrowIfCancellationRequested();
            if (sourcePaths is null)
                throw new ArgumentNullException(nameof(sourcePaths));
            var paths = sourcePaths.ToList();
            if (paths.Count == 0)
                throw new ArgumentException("请选择要导入的 Mod 文件。");
            string directory = LocalModCatalog.ModsDirectory(instance, token);
            using (var gate = new OperationGate(instance, directory, token))
            {
                validateResource?.Invoke();
                var inputs = new List<Input>();
                var sources = new List<NativeLease>();
                var retained = new List<NativeLease>();
                var owned = new List<NativeLease>();
                var warnings = new List<string>();
                var seen = new Dictionary<string, LocalModEntry>(StringComparer.OrdinalIgnoreCase);
                string stage = null;
                bool createdDirectory = false;
                bool complete = false;
                int skipped = 0;
                try
                {
                    // Every source is held read-only through the batch. A later source
                    // failure therefore happens before any destination is published.
                    foreach (var sourcePath in paths)
                    {
                        token.ThrowIfCancellationRequested();
                        string full = Path.GetFullPath(sourcePath);
                        string extension = Path.GetExtension(full);
                        string enabledExtension = Path.GetExtension(LocalModCatalog.EnabledFileName(full));
                        if (!new[] { ".jar", ".litemod", ".disabled", ".old" }.Contains(extension, StringComparer.OrdinalIgnoreCase) || !new[] { ".jar", ".litemod" }.Contains(enabledExtension, StringComparer.OrdinalIgnoreCase) || !LocalModCatalog.IsSupportedFile(full))
                        {
                            throw new InvalidDataException("请选择 JAR、LiteMod 或带 .disabled/.old 后缀的 Mod；ZIP 整合包请使用整合包导入。");
                        }
                        ValidatePath(full, false);
                        var held = NativeLease.Open(full, false);
                        sources.Add(held);
                        if (resource is not null)
                            CommunityResourceDownload.VerifySource(held.Stream, resource, token);
                        var entry = LocalModCatalog.Read(full, token);
                        if (!entry.IsArchiveValid || !string.IsNullOrEmpty(entry.Problem) || entry.Sha256 is null)
                        {
                            throw new InvalidDataException("无法导入 " + Path.GetFileName(full) + "：" + (entry.Problem ?? "文件不是有效的 Mod 归档。"));
                        }
                        CheckDigest(held.Stream, entry.Length, entry.Sha256, token);
                        warnings.AddRange(entry.Warnings);
                        LocalModEntry previous = null;
                        if (seen.TryGetValue(entry.EnabledName, out previous))
                        {
                            if (previous.Length != entry.Length || !string.Equals(previous.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw Conflict(entry.EnabledName);
                            skipped += 1;
                            continue;
                        }
                        seen.Add(entry.EnabledName, entry);
                        string destination = Child(directory, entry.EnabledName);
                        EnsureAliases(directory, entry.EnabledName, destination);
                        if (Directory.Exists(destination))
                            throw Conflict(destination);
                        if (File.Exists(destination))
                        {
                            var existing = NativeLease.Open(destination, false);
                            retained.Add(existing);
                            try
                            {
                                CheckDigest(existing.Stream, entry.Length, entry.Sha256, token);
                            }
                            catch (InvalidDataException)
                            {
                                throw Conflict(destination);
                            }
                            skipped += 1;
                            continue;
                        }
                        inputs.Add(new Input() { Entry = entry, Source = held, Destination = destination });
                    }
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    token.ThrowIfCancellationRequested();
                    if (inputs.Count == 0)
                    {
                        EnsureBatchAliases(directory, seen.Keys, token);
                        complete = true;
                        return new LocalModActionResult(directory, 0, skipped, warnings);
                    }
                    ValidatePath(directory, true);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        createdDirectory = true;
                    }
                    gate.HoldModsDirectory(directory);
                    stage = Child(directory, ".pcl-db-mod-stage-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    gate.HoldModsDirectory(stage);
                    foreach (var item in inputs)
                    {
                        token.ThrowIfCancellationRequested();
                        string temporary = Child(stage, item.Entry.EnabledName);
                        var output = NativeLease.Create(temporary);
                        owned.Add(output);
                        item.Staged = output;
                        CopyChecked(item.Source.Stream, output.Stream, item.Entry, token);
                    }
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    for (int i = 0, loopTo = inputs.Count - 1; i <= loopTo; i++)
                    {
                        var item = inputs[i];
                        if (resource is not null)
                            EnsureResourceIds(directory, item.Entry, item.Destination, token);
                        token.ThrowIfCancellationRequested();
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                        EnsureAliases(directory, item.Entry.EnabledName, null);
                        beforePublish?.Invoke(i, item.Destination);
                        validateResource?.Invoke();
                        token.ThrowIfCancellationRequested();
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                        ValidatePath(directory, true);
                        ValidatePath(stage, true);
                        EnsureAliases(directory, item.Entry.EnabledName, null);
                        // Native rename refuses an already-created destination, and
                        // the source handle blocks replacement between hash and move.
                        item.Staged.Rename(item.Destination);
                        EnsureAliases(directory, item.Entry.EnabledName, item.Destination);
                        if (resource is not null)
                            EnsureResourceIds(directory, item.Entry, item.Destination, token);
                    }
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    // Recheck earlier publications and identical existing targets as
                    // well: an unrelated process may have created a state alias while
                    // a later file was being copied. Rollback still owns only our files.
                    EnsureBatchAliases(directory, seen.Keys, token);
                    validateResource?.Invoke();
                    token.ThrowIfCancellationRequested();
                    complete = true;
                    return new LocalModActionResult(directory, inputs.Count, skipped, warnings);
                }
                finally
                {
                    // Owned handles still identify the exact files we created, even
                    // after publication. No path-based delete can remove a replacement.
                    foreach (var item in owned)
                    {
                        if (!complete)
                        {
                            try
                            {
                                item.DeleteOnClose();
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                InstallLog.Append(instance.Target.Root, "Mod 导入回收失败，保留新文件：" + item.CurrentPath + " | " + ex.Message);
                            }
                        }
                        item.Dispose();
                    }
                    foreach (var item in retained)
                        item.Dispose();
                    foreach (var item in sources)
                        item.Dispose();
                    if (stage is not null)
                        gate.ReleaseModsDirectory(stage);
                    RemoveEmpty(stage);
                    if (!complete && createdDirectory)
                    {
                        gate.ReleaseModsDirectory(directory);
                        RemoveEmpty(directory);
                    }
                }
            }
        }

        private static void EnsureResourceIds(string directory, LocalModEntry entry, string destination, CancellationToken token)
        {
            if (entry.ModIds.Count == 0)
                return;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFullPath(file), destination, StringComparison.OrdinalIgnoreCase) || !LocalModCatalog.IsSupportedFile(file))
                    continue;
                var existing = LocalModCatalog.Read(file, token);
                if (existing.ModIds.Intersect(entry.ModIds, StringComparer.Ordinal).Any())
                    throw new IOException("已存在相同 Mod ID 的文件，请先在 Mod 管理中处理：" + existing.FileName);
            }
        }

        private static void CopyChecked(FileStream source, FileStream destination, LocalModEntry entry, CancellationToken token)
        {
            source.Position = 0L;
            using (var digest = SHA256.Create())
            {
                var buffer = new byte[65536];
                long total = 0L;
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = source.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    destination.Write(buffer, 0, count);
                    digest.TransformBlock(buffer, 0, count, buffer, 0);
                    total += count;
                }
                while (true);
                digest.TransformFinalBlock(new byte[] { }, 0, 0);
                destination.Flush(true);
                if (total != entry.Length || !string.Equals(Hex(digest.Hash), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mod 副本校验失败：" + entry.FileName);
            }
            token.ThrowIfCancellationRequested();
        }
        private static void CheckDigest(FileStream stream, long length, string expected, CancellationToken token)
        {
            if (stream.Length != length || expected is null)
                throw new InvalidDataException("Mod 文件已变化，请刷新后重试。");
            stream.Position = 0L;
            using (var digest = SHA256.Create())
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    digest.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                digest.TransformFinalBlock(new byte[] { }, 0, 0);
                if (!string.Equals(Hex(digest.Hash), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mod 文件内容与扫描结果不同，请刷新后重试。");
            }
            stream.Position = 0L;
            token.ThrowIfCancellationRequested();
        }
        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }
        private static string Child(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || (Path.GetFileName(name) ?? "") != (name ?? "") || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new InvalidDataException("Mod 文件名无效。");
            return SafeFiles.UnderRoot(directory, name);
        }
        internal static void EnsureAliases(string directory, string enabledName, string exceptPath)
        {
            foreach (var name in new[] { enabledName, enabledName + ".disabled", enabledName + ".old" })
            {
                string candidate = Child(directory, name);
                if (exceptPath is not null && Same(candidate, exceptPath))
                    continue;
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    throw Conflict(candidate);
            }
        }
        private static void EnsureBatchAliases(string directory, IEnumerable<string> enabledNames, CancellationToken token)
        {
            foreach (var name in enabledNames)
            {
                token.ThrowIfCancellationRequested();
                EnsureAliases(directory, name, Child(directory, name));
            }
        }
        private static IOException Conflict(string path)
        {
            return new IOException("存在同名或启停状态冲突，不会覆盖已有文件：" + Path.GetFileName(path));
        }
        private static bool Same(string first, string second)
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        private static void ValidatePath(string path, bool directory)
        {
            string current = Path.GetFullPath(path);
            if (!directory && Path.GetFileName(current).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Mod 路径无效。");
            while (current is not null)
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Mods 路径包含链接，请选择真实目录。");
                    if ((directory || !Same(current, path)) && (attributes & FileAttributes.Directory) == 0)
                        throw new IOException("文件占用了 Mods 目录。");
                }
                current = Path.GetDirectoryName(current);
            }
        }
        private static void RemoveEmpty(string path)
        {
            if (path is null)
                return;
            try
            {
                ValidatePath(path, true);
                if (Directory.Exists(path))
                    Directory.Delete(path, false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Keep unexpected/locked files. Cleanup must never replace the result.
            }
        }

        internal sealed class OperationGate : IDisposable
        {
            private static readonly HashSet<string> Active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly string _identity;
            private Mutex _mutex;
            private bool _ownsMutex;
            private FileStream _rootLock;
            private SafeFileHandle _runDirectory;
            private readonly Dictionary<string, SafeFileHandle> _directories = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            internal OperationGate(InstalledInstance instance, string directory, CancellationToken token, string suffix = "MODS")
            {
                token.ThrowIfCancellationRequested();
                string folderKind = suffix == "MODS" ? "Mods" : "资源";
                string runDirectory = (instance?.RunDirectory) ?? Path.GetDirectoryName(directory);
                if (instance is null && !Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("共享 Mods 目录无效。");
                if (!Directory.Exists(runDirectory))
                    throw new DirectoryNotFoundException("实际运行目录尚不存在，请先在启动器中确认目录。");
                string identity = null;
                _runDirectory = NativeLease.OpenDirectory(runDirectory, ref identity);
                _identity = identity + "/" + suffix;
                lock (Active)
                {
                    if (!Active.Add(_identity))
                    {
                        _runDirectory.Dispose();
                        _runDirectory = null;
                        throw new IOException("另一个任务正在修改这个 " + folderKind + " 文件夹，请稍后重试。");
                    }
                }
                try
                {
                    string key;
                    using (var digest = SHA256.Create())
                    {
                        key = Hex(digest.ComputeHash(Encoding.UTF8.GetBytes(_identity)));
                    }
                    _mutex = new Mutex(false, @"Local\PclDbMods-" + key);
                    try
                    {
                        _ownsMutex = _mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        _ownsMutex = true;
                    }
                    if (!_ownsMutex)
                        throw new IOException("另一个进程正在修改这个 " + folderKind + " 文件夹，请稍后重试。");
                    token.ThrowIfCancellationRequested();
                    if (instance is not null)
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    _directories.Add(Path.GetFullPath(runDirectory), _runDirectory);
                    HoldDirectoryChain(runDirectory);
                    if (instance is not null)
                        HoldDirectoryChain(instance.Target.Root);
                    if (Directory.Exists(directory))
                        HoldModsDirectory(directory);
                    if (instance is not null)
                    {
                        string lockPath = SafeFiles.UnderRoot(instance.Target.Root, ".vanilla-downloader/install.lock");
                        Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
                        try
                        {
                            _rootLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        }
                        catch (IOException ex)
                        {
                            throw new IOException("这个游戏目录正在安装或被其他任务占用，请稍后重试。", ex);
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
                _rootLock?.Dispose();
                _rootLock = null;
                foreach (var handle in _directories.Values)
                    handle.Dispose();
                _directories.Clear();
                _runDirectory?.Dispose();
                _runDirectory = null;
                if (_ownsMutex)
                {
                    _mutex.ReleaseMutex();
                    _ownsMutex = false;
                }
                _mutex?.Dispose();
                _mutex = null;
                lock (Active)
                    Active.Remove(_identity);
            }
            internal void HoldModsDirectory(string directory)
            {
                HoldDirectoryChain(directory);
            }
            internal void ReleaseModsDirectory(string directory)
            {
                string full = Path.GetFullPath(directory);
                SafeFileHandle handle = null;
                if (_directories.TryGetValue(full, out handle))
                {
                    handle.Dispose();
                    _directories.Remove(full);
                }
            }
            private void HoldDirectoryChain(string directory)
            {
                string current = Path.GetFullPath(directory);
                while (current is not null)
                {
                    if (!_directories.ContainsKey(current))
                    {
                        string identity = null;
                        _directories.Add(current, NativeLease.OpenDirectory(current, ref identity));
                    }
                    current = Path.GetDirectoryName(current);
                }
            }
        }

        // A rename is performed on the same read-leased file that was hashed. This
        // prevents a different file being substituted after validation on Windows.
        internal sealed class NativeLease : IDisposable
        {
            internal readonly FileStream Stream;
            internal string CurrentPath { get; set; }
            private NativeLease(string path, bool create, bool rename)
            {
                ValidatePath(path, false);
                uint access = create ? 0xC0010000U : rename ? 0x80010000U : 0x80000000U;
                var handle = CreateFile(NativePath(path), access, 1U, IntPtr.Zero, create ? 1U : 3U, 0x200080U, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException("无法访问 Mod 文件：" + Path.GetFileName(path), new Win32Exception(errorCode));
                }
                try
                {
                    var info = default(FileInformation);
                    if (!GetInformation(handle, ref info))
                        throw new IOException("无法确认 Mod 文件身份。", new Win32Exception(Marshal.GetLastWin32Error()));
                    if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0L)
                        throw new IOException("Mod 文件是链接或目录。");
                    Stream = new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read, 65536, false);
                    handle = null;
                    CurrentPath = path;
                }
                finally
                {
                    handle?.Dispose();
                }
            }
            internal static NativeLease Open(string path, bool rename)
            {
                return new NativeLease(path, false, rename);
            }
            internal static NativeLease Create(string path)
            {
                return new NativeLease(path, true, true);
            }
            internal static SafeFileHandle OpenDirectory(string path, ref string identity)
            {
                ValidatePath(path, true);
                var handle = CreateFile(NativePath(path), 0x80U, 1U, IntPtr.Zero, 3U, 0x2200000U, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException("无法锁定实际运行目录，请关闭占用该目录的操作后重试。", new Win32Exception(errorCode));
                }
                try
                {
                    var info = default(FileInformation);
                    if (!GetInformation(handle, ref info))
                        throw new IOException("无法确认运行目录身份。", new Win32Exception(Marshal.GetLastWin32Error()));
                    if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0L || (info.Attributes & (uint)FileAttributes.Directory) == 0L)
                        throw new IOException("运行目录是链接或文件。");
                    identity = info.VolumeSerial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":" + info.IndexHigh.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + info.IndexLow.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            internal void Rename(string destination)
            {
                ValidatePath(destination, false);
                byte[] bytes = Encoding.Unicode.GetBytes(NativePath(Path.GetFullPath(destination)));
                int lengthOffset = IntPtr.Size * 2;
                int nameOffset = lengthOffset + 4;
                int size = nameOffset + bytes.Length + 2;
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(new byte[size], 0, buffer, size);
                    Marshal.WriteInt32(buffer, 0, 0); // ReplaceIfExists = False.
                    Marshal.WriteIntPtr(buffer, IntPtr.Size, IntPtr.Zero);
                    Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
                    Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
                    if (!SetInformation(Stream.SafeFileHandle, 3, buffer, (uint)size))
                        throw new IOException("无法重命名 Mod，不会覆盖已有文件：" + Path.GetFileName(destination), new Win32Exception(Marshal.GetLastWin32Error()));
                    CurrentPath = destination;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            internal void DeleteOnClose()
            {
                var buffer = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(buffer, 1);
                    if (!SetInformation(Stream.SafeFileHandle, 4, buffer, 4U))
                        throw new IOException("无法回收本次新增的 Mod 文件。", new Win32Exception(Marshal.GetLastWin32Error()));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            public void Dispose()
            {
                Stream.Dispose();
            }
            private static string NativePath(string path)
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return full;
                if (full.StartsWith(@"\\", StringComparison.Ordinal))
                    return @"\\?\UNC\" + full.Substring(2);
                return @"\\?\" + full;
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct FileInformation
            {
                public uint Attributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                public uint VolumeSerial;
                public uint SizeHigh;
                public uint SizeLow;
                public uint Links;
                public uint IndexHigh;
                public uint IndexLow;
            }
            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);
            [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
            private static extern bool GetInformation(SafeFileHandle handle, ref FileInformation info);
            [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
            private static extern bool SetInformation(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
        }
    }
}
