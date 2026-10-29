using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PclDownloadBridge.Core
{

    public sealed partial class LocalModActions
    {
        private sealed class Change
        {
            internal LocalModEntry Entry;
            internal LocalModEntry Replacement;
            internal NativeLease Source;
            internal NativeLease Staged;
            internal string Original;
            internal string Destination;
            internal string Backup;
        }

        public static LocalModActionResult SetEnabledBatch(InstalledInstance instance, IEnumerable<LocalModEntry> entries, bool enabled, CancellationToken token)
        {
            return ChangeBatch(instance, entries, enabled, false, token);
        }
        public static LocalModActionResult DeleteBatch(InstalledInstance instance, IEnumerable<LocalModEntry> entries, CancellationToken token)
        {
            return ChangeBatch(instance, entries, default, true, token);
        }
        private static LocalModActionResult ChangeBatch(InstalledInstance instance, IEnumerable<LocalModEntry> entries, bool? enabled, bool delete, CancellationToken token)
        {
            string directory = LocalModCatalog.ModsDirectory(instance, token);
            var changes = new List<Change>();
            string backup = null;
            using (var gate = new OperationGate(instance, directory, token))
            {
                try
                {
                    var selected = entries.ToList();
                    if (selected.Count == 0)
                        return new LocalModActionResult(directory, 0, 0);
                    if (delete)
                        backup = CreateBackup(directory, gate);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in selected)
                    {
                        var item = HoldOriginal(directory, entry, token);
                        changes.Add(item);
                        if (!seen.Add(item.Original))
                            throw new InvalidDataException("重复的 Mod 选择。");
                        if (!delete)
                            EnsureAliases(directory, entry.EnabledName, item.Original);
                        if (delete)
                        {
                            item.Destination = Child(backup, entry.FileName);
                        }
                        else if (entry.Enabled != enabled.Value)
                        {
                            item.Destination = Child(directory, enabled.Value ? entry.EnabledName : entry.EnabledName + ".disabled");
                        }
                    }
                    foreach (var item in changes.Where(value => value.Destination is not null))
                    {
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                        token.ThrowIfCancellationRequested();
                        if (!delete)
                            EnsureAliases(directory, item.Entry.EnabledName, item.Original);
                        item.Source.Rename(item.Destination);
                    }
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    token.ThrowIfCancellationRequested();
                    foreach (var item in changes)
                    {
                        if (!delete)
                            EnsureAliases(directory, item.Entry.EnabledName, item.Source.CurrentPath);
                    }
                    return new LocalModActionResult(directory, changes.Where(item => item.Destination is not null).Count(), changes.Where(item => item.Destination is null).Count(), backup: backup);
                }
                catch (Exception ex)
                {
                    Rollback(changes, ex);
                    throw;
                }
                finally
                {
                    foreach (var item in changes)
                        item.Source?.Dispose();
                    if (backup is not null)
                    {
                        gate.ReleaseModsDirectory(backup);
                        RemoveEmpty(backup);
                    }
                }
            }
        }

        // Keep PCL's custom-name behaviour, while preserving the exact physical
        // disabled suffix. Do not guess if the platform's original name is absent.
        public static string UpdateFileName(LocalModEntry entry, ModpackRelease current, ModpackRelease release)
        {
            string oldName = current.FileName;
            string newName = release.FileName;
            string local = entry.EnabledName;
            if (string.Equals(local, oldName, StringComparison.OrdinalIgnoreCase))
            {
                local = newName;
            }
            else
            {
                // PageInstanceMod.UpdateMods removes only common leading/trailing
                // dash segments. Keep bracketed translations and the disabled suffix.
                string oldStem = Path.GetFileNameWithoutExtension(oldName);
                string newStem = Path.GetFileNameWithoutExtension(newName);
                var before = oldStem.Split('-').ToList();
                var after = newStem.Split('-').ToList();
                while (before.Count > 0 && after.Count > 0 && (before[0] ?? "") == (after[0] ?? ""))
                {
                    before.RemoveAt(0);
                    after.RemoveAt(0);
                }
                while (before.Count > 0 && after.Count > 0 && (before.Last() ?? "") == (after.Last() ?? ""))
                {
                    before.RemoveAt(before.Count - 1);
                    after.RemoveAt(after.Count - 1);
                }
                string oldPart = before.Count > 0 && after.Count > 0 ? string.Join("-", before) : oldStem;
                string newPart = before.Count > 0 && after.Count > 0 ? string.Join("-", after) : newStem;
                if (oldPart.Length == 0 || local.IndexOf(oldPart, StringComparison.Ordinal) < 0 || local.IndexOf(oldPart, StringComparison.Ordinal) != local.LastIndexOf(oldPart, StringComparison.Ordinal))
                {
                    oldPart = oldStem;
                    newPart = newStem;
                }
                if (oldPart.Length > 0 && local.IndexOf(oldPart, StringComparison.Ordinal) >= 0 && local.IndexOf(oldPart, StringComparison.Ordinal) == local.LastIndexOf(oldPart, StringComparison.Ordinal))
                    local = local.Replace(oldPart, newPart);
            }
            CommunityResources.ValidateFileName(CommunityResourceKind.Mod, local);
            return local + entry.FileName.Substring(entry.EnabledName.Length);
        }

        public static LocalModActionResult ApplyUpdates(InstalledInstance instance, IEnumerable<LocalModUpdate> updates, IEnumerable<string> paths, CancellationToken token)
        {
            return ApplyUpdatesCore(instance, updates.ToList(), paths.ToList(), token, null);
        }
        internal static LocalModActionResult ApplyUpdatesCore(InstalledInstance instance, List<LocalModUpdate> updates, List<string> paths, CancellationToken token, Action<int> beforePublish)
        {
            if (updates.Count == 0 || updates.Count != paths.Count)
                throw new ArgumentException("更新文件与所选 Mod 不符。");
            string directory = LocalModCatalog.ModsDirectory(instance, token);
            var changes = new List<Change>();
            var downloads = new List<NativeLease>();
            string stage = null;
            string backup = null;
            bool committed = false;
            using (var gate = new OperationGate(instance, directory, token))
            {
                try
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int index = 0, loopTo = updates.Count - 1; index <= loopTo; index++)
                    {
                        var update = updates[index];
                        var release = update.Release;
                        if (!update.HasUpdate || !LocalModUpdate.Compatible(instance, update.Current, release))
                            throw new InvalidDataException("更新与实例不兼容，请重新检查。");
                        var item = HoldOriginal(directory, update.Entry, token);
                        changes.Add(item);
                        var source = NativeLease.Open(paths[index], false);
                        downloads.Add(source);
                        CommunityResourceDownload.VerifySource(source.Stream, release, token);
                        var replacement = LocalModCatalog.Read(paths[index], token);
                        if (!replacement.IsArchiveValid || replacement.Problem is not null || item.Entry.ModIds.Count > 0 && !item.Entry.ModIds.Intersect(replacement.ModIds, StringComparer.Ordinal).Any())
                            throw new InvalidDataException("更新文件与原 Mod 身份不符。");
                        item.Replacement = replacement;
                        if (changes.Any(other => !ReferenceEquals(other, item) && other.Replacement is not null && replacement.ModIds.Intersect(other.Replacement.ModIds, StringComparer.Ordinal).Any()))
                            throw new IOException("所选更新包含重复的 Mod ID，请分别检查。");
                        item.Destination = Child(directory, UpdateFileName(item.Entry, update.Current, release));
                        if (!names.Add(LocalModCatalog.EnabledFileName(item.Destination)))
                            throw Conflict(item.Destination);
                        EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Original);
                        foreach (var existing in Directory.EnumerateFiles(directory).Where(file => LocalModCatalog.IsSupportedFile(file) && !Same(file, item.Original)))
                        {
                            var other = LocalModCatalog.Read(existing, token);
                            if (replacement.ModIds.Intersect(other.ModIds, StringComparer.Ordinal).Any())
                                throw new IOException("其他文件已包含更新的 Mod ID：" + other.FileName);
                        }
                    }
                    backup = CreateBackup(directory, gate);
                    stage = Child(directory, ".pcl-db-mod-stage-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    gate.HoldModsDirectory(stage);
                    for (int index = 0, loopTo1 = changes.Count - 1; index <= loopTo1; index++)
                    {
                        var item = changes[index];
                        var replacement = item.Replacement;
                        item.Backup = Child(backup, item.Entry.FileName);
                        item.Staged = NativeLease.Create(Child(stage, Path.GetFileName(item.Destination)));
                        CopyChecked(downloads[index].Stream, item.Staged.Stream, replacement, token);
                    }
                    for (int index = 0, loopTo2 = changes.Count - 1; index <= loopTo2; index++)
                    {
                        var item = changes[index];
                        beforePublish?.Invoke(index);
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                        token.ThrowIfCancellationRequested();
                        EnsureAliases(directory, item.Entry.EnabledName, item.Original);
                        EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Original);
                        item.Source.Rename(item.Backup);
                        item.Staged.Rename(item.Destination);
                        EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Destination);
                    }
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    token.ThrowIfCancellationRequested();
                    foreach (var item in changes)
                    {
                        foreach (var other in Directory.EnumerateFiles(directory).Where(file => LocalModCatalog.IsSupportedFile(file) && !changes.Any(own => Same(own.Destination, file))))
                        {
                            if (item.Replacement.ModIds.Intersect(LocalModCatalog.Read(other, token).ModIds, StringComparer.Ordinal).Any())
                                throw Conflict(other);
                        }
                    }
                    committed = true;
                    return new LocalModActionResult(directory, changes.Count, 0, backup: backup);
                }
                catch (Exception ex)
                {
                    Rollback(changes, ex);
                    throw;
                }
                finally
                {
                    foreach (var item in changes)
                    {
                        if (!committed && item.Staged is not null)
                        {
                            try
                            {
                                item.Staged.DeleteOnClose();
                            }
                            catch (IOException)
                            {
                                // The owned stage is retained if cleanup is blocked.
                            }
                        }
                        item.Staged?.Dispose();
                        item.Source?.Dispose();
                    }
                    foreach (var source in downloads)
                        source.Dispose();
                    if (stage is not null)
                    {
                        gate.ReleaseModsDirectory(stage);
                        RemoveEmpty(stage);
                    }
                    if (backup is not null)
                    {
                        gate.ReleaseModsDirectory(backup);
                        RemoveEmpty(backup);
                    }
                }
            }
        }
        private static Change HoldOriginal(string directory, LocalModEntry entry, CancellationToken token)
        {
            if (entry is null || !entry.CanToggle || !Same(Child(directory, entry.FileName), entry.FilePath))
                throw new InvalidDataException("Mod 不属于当前实例或无法读取，请刷新。");
            var item = new Change() { Entry = entry, Original = entry.FilePath, Source = NativeLease.Open(entry.FilePath, true) };
            try
            {
                CheckDigest(item.Source.Stream, entry.Length, entry.Sha256, token);
                return item;
            }
            catch
            {
                item.Source.Dispose();
                throw;
            }
        }
        private static string CreateBackup(string directory, OperationGate gate)
        {
            // A sibling of Mods cannot be rediscovered as another active Mod by a
            // recursive loader. Both directories remain on the same held volume.
            string parent = Child(Path.GetDirectoryName(directory), ".pcl-db-mod-backups");
            ValidatePath(parent, true);
            Directory.CreateDirectory(parent);
            gate.HoldModsDirectory(parent);
            string backup = Child(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            gate.HoldModsDirectory(backup);
            return backup;
        }
        private static void Rollback(List<Change> changes, Exception cause)
        {
            var failures = new List<Exception>();
            foreach (var item in changes.AsEnumerable().Reverse())
            {
                try
                {
                    // Discard only our held replacement before restoring the original.
                    if (item.Staged is not null && Same(item.Staged.CurrentPath, item.Destination))
                    {
                        item.Staged.DeleteOnClose();
                        item.Staged.Dispose();
                        item.Staged = null;
                    }
                    if (item.Source is not null && !Same(item.Source.CurrentPath, item.Original))
                        item.Source.Rename(item.Original);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
            if (failures.Count > 0)
                throw new IOException("部分文件无法恢复，原文件已保留在运行目录的 .pcl-db-mod-backups 中，请检查后刷新。", new AggregateException(new[] { cause }.Concat(failures)));
        }
    }
}
