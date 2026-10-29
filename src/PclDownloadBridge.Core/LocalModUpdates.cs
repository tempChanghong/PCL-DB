using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PclDownloadBridge.Core
{

    public sealed class LocalModFingerprint
    {
        public readonly LocalModEntry Entry;
        public readonly string Sha1;
        public readonly string Md5;
        public readonly uint CurseForge;
        private LocalModFingerprint(LocalModEntry entry, string sha1, string md5, uint fingerprint)
        {
            Entry = entry;
            Sha1 = sha1;
            Md5 = md5;
            CurseForge = fingerprint;
        }
        public static List<LocalModFingerprint> Read(InstalledInstance instance, IEnumerable<LocalModEntry> entries, CancellationToken token)
        {
            string directory = LocalModCatalog.ModsDirectory(instance, token);
            var result = new List<LocalModFingerprint>();
            foreach (var modFile in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!modFile.IsArchiveValid || !modFile.CanToggle)
                    continue;
                if (!string.Equals(Path.GetFullPath(Path.Combine(directory, modFile.FileName)), modFile.FilePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mod 不属于当前实例。");
                using (var held = LocalModActions.NativeLease.Open(modFile.FilePath, false))
                using (var sha1 = SHA1.Create())
                using (var md5 = MD5.Create())
                using (var sha256 = SHA256.Create())
                {
                    var stream = held.Stream;
                    var buffer = new byte[65536];
                    long filtered = 0L;
                    if (stream.Length != modFile.Length)
                        throw new InvalidDataException("Mod 文件已变化，请刷新列表。");
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        int count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                            break;
                        sha1.TransformBlock(buffer, 0, count, buffer, 0);
                        md5.TransformBlock(buffer, 0, count, buffer, 0);
                        sha256.TransformBlock(buffer, 0, count, buffer, 0);
                        for (int index = 0, loopTo = count - 1; index <= loopTo; index++)
                        {
                            if (!White(buffer[index]))
                                filtered += 1L;
                        }
                    }
                    while (true);
                    sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (!string.Equals(Hex(sha256.Hash), modFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Mod 内容已变化，请刷新列表。");
                    stream.Position = 0L;
                    uint hash = 1U ^ (uint)(filtered & 0xFFFFFFFFL);
                    uint word = 0U;
                    int shift = 0;
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        int count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                            break;
                        for (int index = 0, loopTo1 = count - 1; index <= loopTo1; index++)
                        {
                            byte value = buffer[index];
                            if (White(value))
                                continue;
                            word = word | (uint)value << shift;
                            shift += 8;
                            if (shift != 32)
                                continue;
                            word = Multiply(word);
                            word = word ^ word >> 24;
                            word = Multiply(word);
                            hash = Multiply(hash) ^ word;
                            word = 0U;
                            shift = 0;
                        }
                    }
                    while (true);
                    if (shift > 0)
                        hash = Multiply(hash ^ word);
                    hash = hash ^ hash >> 13;
                    hash = Multiply(hash);
                    hash = hash ^ hash >> 15;
                    result.Add(new LocalModFingerprint(modFile, Hex(sha1.Hash), Hex(md5.Hash), hash));
                }
            }
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            return result;
        }
        private static bool White(byte value)
        {
            return value == 9 || value == 10 || value == 13 || value == 32;
        }
        private static uint Multiply(uint value)
        {
            return (uint)(value * 0x5BD1E995UL & 0xFFFFFFFFUL);
        }
        private static string Hex(byte[] value)
        {
            return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        }
        public bool Matches(ModpackRelease release)
        {
            return release is not null && release.Size == Entry.Length && (release.Sha1 is not null || release.Md5 is not null) && (release.Sha1 is null || string.Equals(release.Sha1, Sha1, StringComparison.OrdinalIgnoreCase)) && (release.Md5 is null || string.Equals(release.Md5, Md5, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class LocalModUpdate
    {
        public readonly LocalModEntry Entry;
        public readonly ModpackProject Project;
        private readonly ModpackRelease _current;
        private readonly ModpackRelease _release;
        public LocalModUpdate(LocalModEntry entry, ModpackProject project, ModpackRelease current, ModpackRelease release)
        {
            Entry = entry;
            Project = project;
            _current = current.Copy();
            _release = release is null ? null : release.Copy();
        }
        public ModpackRelease Current
        {
            get
            {
                return _current.Copy();
            }
        }
        public ModpackRelease Release
        {
            get
            {
                return _release is null ? null : _release.Copy();
            }
        }
        public bool HasUpdate
        {
            get
            {
                return _release is not null;
            }
        }
        public static bool Compatible(InstalledInstance instance, ModpackRelease current, ModpackRelease release)
        {
            if (release is null || current is null || current.Platform != release.Platform || (current.ProjectId ?? "") != (release.ProjectId ?? "") || (current.Id ?? "") == (release.Id ?? "") || current.Kind != CommunityResourceKind.Mod || release.Kind != CommunityResourceKind.Mod || release.Published <= current.Published || CommunityResourceInstallation.CompatibilityProblem(release, instance) is not null)
                return false;
            var before = current.Loaders.Select(value => value.ToLowerInvariant()).Distinct().OrderBy(value => value);
            var after = release.Loaders.Select(value => value.ToLowerInvariant()).Distinct().OrderBy(value => value);
            if (!before.SequenceEqual(after))
                return false;
            if (release.Size == current.Size && (release.Sha1 is not null && string.Equals(release.Sha1, current.Sha1, StringComparison.OrdinalIgnoreCase) || release.Md5 is not null && string.Equals(release.Md5, current.Md5, StringComparison.OrdinalIgnoreCase)))
                return false;
            return true;
        }
    }

    public sealed class LocalModUpdateCheckResult
    {
        public readonly IReadOnlyList<LocalModUpdate> Matches;
        public readonly IReadOnlyList<string> Errors;
        public LocalModUpdateCheckResult(IEnumerable<LocalModUpdate> matches, IEnumerable<string> errors = null)
        {
            Matches = matches.ToList().AsReadOnly();
            Errors = (errors ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
        }
    }
}