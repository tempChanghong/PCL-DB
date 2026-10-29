using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    public sealed class InstalledWorld
    {
        public string Name { get; private set; }
        public string Folder { get; private set; }
        internal readonly string Identity;
        internal readonly string MarkerHash;
        internal InstalledWorld(string name, string folder, string identity, string hash)
        {
            Name = name;
            Folder = folder;
            Identity = identity;
            MarkerHash = hash;
        }
        public override string ToString()
        {
            return Name;
        }
    }

    // A private fresh instance snapshot binds launcher identity, native isolation,
    // resource type and (for datapacks) an explicitly selected existing world.
    public sealed class ResourceInstallationPlan
    {
        private readonly InstalledInstance _instance;
        private readonly ModpackRelease _release;
        private readonly InstalledWorld _world;
        private readonly string _runIdentity;
        private readonly string _directoryIdentity;
        public string InstanceName
        {
            get
            {
                return _instance.Name;
            }
        }
        public string RunDirectory
        {
            get
            {
                return _instance.RunDirectory;
            }
        }
        public string Destination { get; private set; }
        public string WorldName
        {
            get
            {
                return _world?.Name;
            }
        }
        public bool IsShared
        {
            get
            {
                return !_instance.IsIsolated.GetValueOrDefault();
            }
        }
        public ModpackRelease Release
        {
            get
            {
                return _release.Copy();
            }
        }
        public LauncherInstallTarget LauncherTarget
        {
            get
            {
                return _instance.Target;
            }
        }
        internal ResourceInstallationPlan(InstalledInstance instance, ModpackRelease release, InstalledWorld world, string destination)
        {
            _instance = instance;
            _release = release.Copy();
            _world = world;
            Destination = destination;
            using (var handle = LocalModActions.NativeLease.OpenDirectory(instance.RunDirectory, ref _runIdentity))
            {
            }
            if (Directory.Exists(Path.GetDirectoryName(destination)))
            {
                using (var handle = LocalModActions.NativeLease.OpenDirectory(Path.GetDirectoryName(destination), ref _directoryIdentity))
                {
                }
            }
        }
        private void Validate(CancellationToken token)
        {
            InstalledInstanceCatalog.EnsureUnchanged(_instance, token);
            string identity = null;
            using (var handle = LocalModActions.NativeLease.OpenDirectory(_instance.RunDirectory, ref identity))
            {
                if ((identity ?? "") != (_runIdentity ?? ""))
                    throw new IOException("运行目录已被替换，请重新选择。");
            }
            if (_directoryIdentity is not null)
            {
                using (var handle = LocalModActions.NativeLease.OpenDirectory(Path.GetDirectoryName(Destination), ref identity))
                {
                    if ((identity ?? "") != (_directoryIdentity ?? ""))
                        throw new IOException("资源目录已被替换，请重新选择。");
                }
            }
            string problem = CommunityResourceInstallation.CompatibilityProblem(_release, _instance);
            if (problem is not null)
                throw new InvalidDataException(problem);
            if (_world is not null)
                CommunityResourceInstallation.EnsureWorld(_instance, _world, token);
        }
        public async Task<bool> InstallAsync(string cache, SourcePolicy policy, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L)
        {
            token.ThrowIfCancellationRequested();
            Validate(token);
            progress?.Report(new TransferProgress() { Phase = "archive", Stage = "检查已有文件", StageFraction = 0d });
            // PCL ModNet.CheckExistingFile checks the target before transferring it.
            // Keep native locks on one worker thread; never hold a mutex across Await.
            if (await Task.Run(() => ReuseExisting(token), token).ConfigureAwait(false))
            {
                progress?.Report(new TransferProgress() { Phase = "archive", Stage = "已有文件校验通过", StageFraction = 1d, Completed = 1, Total = 1 });
                progress?.Report(new TransferProgress() { Phase = "commit", Stage = "已存在相同文件", StageFraction = 1d });
                return false;
            }
            var acquisition = new InlineProgress(value =>
                {
                    value.Phase = "archive";
                    value.Stage = "下载并校验文件";
                    value.StageFraction = value.Completed >= value.Total && value.Total > 0 ? 1d : Math.Min(0.99d, value.NetworkBytes / (double)_release.Size);
                    progress?.Report(value);
                });
            string source = await ModpackArchiveInstaller.AcquireAsync(_release, cache, policy, acquisition, token, connections, limit).ConfigureAwait(false);
            progress?.Report(new TransferProgress() { Phase = "commit", Stage = "安装到 " + InstanceName, StageFraction = 0d });
            bool changed = await Task.Run(() => Publish(source, token), token).ConfigureAwait(false);
            progress?.Report(new TransferProgress() { Phase = "commit", Stage = changed ? "资源已安装" : "已存在相同文件", StageFraction = 1d });
            return changed;
        }
        private bool ReuseExisting(CancellationToken token)
        {
            string directory = Path.GetDirectoryName(Destination);
            using (var gate = new LocalModActions.OperationGate(_instance, directory, token, _release.Kind == CommunityResourceKind.Mod ? "MODS" : "RESOURCES"))
            {
                Validate(token);
                if (_world is not null)
                    gate.HoldModsDirectory(_world.Folder);
                using (var marker = _world is null ? null : LocalModActions.NativeLease.Open(Path.Combine(_world.Folder, "level.dat"), false))
                {
                    Validate(token);
                    return CheckExisting(token);
                }
            }
        }
        private bool CheckExisting(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_release.Kind == CommunityResourceKind.Mod)
                LocalModActions.EnsureAliases(Path.GetDirectoryName(Destination), _release.FileName, Destination);
            if (Directory.Exists(Destination))
                throw DestinationConflict();
            if (!File.Exists(Destination))
                return false;
            using (var existing = LocalModActions.NativeLease.Open(Destination, false))
            {
                try
                {
                    CommunityResourceDownload.VerifySource(existing.Stream, _release, token);
                }
                catch (InvalidDataException)
                {
                    // This is the existing target, not a corrupt download cache.
                    throw DestinationConflict();
                }
                if (_release.Kind == CommunityResourceKind.Mod)
                {
                    var entry = LocalModCatalog.Read(Destination, token);
                    if (!entry.IsArchiveValid || !string.IsNullOrEmpty(entry.Problem))
                        throw new InvalidDataException("已有 Mod 无法读取，文件已保留：" + _release.FileName);
                }
                Validate(token);
                return true;
            }
        }
        private IOException DestinationConflict()
        {
            return new IOException("目标已被不同内容的文件或文件夹占用，已有内容已保留：" + _release.FileName);
        }
        internal bool Publish(string source, CancellationToken token, Action beforePublish = null)
        {
            Validate(token);
            if (_release.Kind == CommunityResourceKind.Mod)
            {
                beforePublish?.Invoke();
                Validate(token);
                return LocalModActions.ImportResource(_instance, source, _release, token, () => Validate(token)).ChangedCount > 0;
            }
            string directory = Path.GetDirectoryName(Destination);
            using (var gate = new LocalModActions.OperationGate(_instance, directory, token, "RESOURCES"))
            {
                Validate(token);
                if (_world is not null)
                    gate.HoldModsDirectory(_world.Folder);
                // Only the selected resource subdirectory may be created. Never create
                // or select a world, change isolation, or edit options/level.dat.
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                gate.HoldModsDirectory(directory);
                LocalModActions.NativeLease marker = null;
                try
                {
                    if (_world is not null)
                    {
                        marker = LocalModActions.NativeLease.Open(Path.Combine(_world.Folder, "level.dat"), false);
                        Validate(token);
                    }
                    if (CheckExisting(token))
                        return false;
                    CommunityResourceDownload.Publish(_release, source, Destination, token, _instance, () =>
    {
        beforePublish?.Invoke();
        Validate(token);
    }, () => Validate(token));
                    return true;
                }
                finally
                {
                    marker?.Dispose();
                }
            }
        }
    }

    public sealed class CommunityResourceInstallation
    {
        private CommunityResourceInstallation()
        {
        }
        public static string CompatibilityProblem(ModpackRelease release, InstalledInstance instance)
        {
            if (release is null || instance is null || instance.Problem is not null || instance.RunDirectory is null)
                return "无法确定这个实例的运行目录。";
            if (release.Kind == CommunityResourceKind.Modpack || release.Kind == CommunityResourceKind.Any)
                return "此入口仅用于普通资源。";
            if (!Directory.Exists(instance.RunDirectory))
                return "实际运行目录不存在，请先在启动器中确认。";
            if (release.Kind == CommunityResourceKind.Mod || release.Kind == CommunityResourceKind.DataPack)
            {
                if (instance.MinecraftVersion is null || !release.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
                    return "资源未声明支持这个 Minecraft 版本。";
            }
            if (release.Kind == CommunityResourceKind.Mod)
            {
                string[] installed = Regex.Matches(instance.LoaderSummary, @"(?:\A| \+ )(NeoForge|Forge|Fabric|Quilt|LiteLoader|Rift|ModLoader)(?: |\z)").Cast<Match>().Select(match => match.Groups[1].Value).ToArray();
                if (installed.Length == 0)
                    return "这个实例未识别到 Mod 加载器。";
                string[] required = release.Loaders.Where(loader => new[] { "neoforge", "forge", "fabric", "quilt", "liteloader", "rift", "modloader" }.Contains(loader, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (required.Length > 0 && !required.Intersect(installed, StringComparer.OrdinalIgnoreCase).Any())
                    return "Mod 加载器与实例不匹配。";
            }
            if (release.Kind == CommunityResourceKind.ResourcePack && UsesLegacyTexturePacks(instance.MinecraftVersion) && !release.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
                return "资源未声明支持这个旧版游戏的纹理包格式。";
            return null;
        }
        // Save dialogs and installation must use the same legacy-format boundary.
        public static bool UsesLegacyTexturePacks(string version)
        {
            return Regex.IsMatch(version ?? "", @"\A(?:1\.[0-5](?:\.|\z)|[ab]1\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        public static string ResourceDirectory(ModpackRelease release, InstalledInstance instance, InstalledWorld world = null)
        {
            string problem = CompatibilityProblem(release, instance);
            if (problem is not null)
                throw new InvalidDataException(problem);
            switch (release.Kind)
            {
                case CommunityResourceKind.Mod:
                    {
                        return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "mods/entry"));
                    }
                case CommunityResourceKind.ResourcePack:
                    {
                        return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, (UsesLegacyTexturePacks(instance.MinecraftVersion) ? "texturepacks" : "resourcepacks") + "/entry"));
                    }
                case CommunityResourceKind.Shader:
                    {
                        return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "shaderpacks/entry"));
                    }
                case CommunityResourceKind.DataPack:
                    {
                        if (world is null)
                            throw new InvalidOperationException("请选择要安装数据包的存档。");
                        string expected = Path.Combine(instance.RunDirectory, "saves", world.Name);
                        if (!string.Equals(Path.GetFullPath(expected), world.Folder, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("存档不属于所选实例的运行目录。");
                        return Path.GetDirectoryName(SafeFiles.UnderRoot(world.Folder, "datapacks/entry"));
                    }
            }
            throw new InvalidOperationException("资源类型无效。");
        }
        public static ResourceInstallationPlan Create(ModpackRelease release, InstalledInstance instance, InstalledWorld world, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            var frozen = InstalledInstanceCatalog.Read(instance.Target, instance.Name, token);
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            var snapshot = release.Copy();
            if (snapshot.Kind != CommunityResourceKind.DataPack)
                world = null;
            string directory = ResourceDirectory(snapshot, frozen, world);
            CommunityResources.ValidateFileName(snapshot.Kind, snapshot.FileName);
            if (snapshot.Size <= 0L || snapshot.Sha1 is null && snapshot.Sha512 is null && snapshot.Md5 is null)
                throw new InvalidDataException("资源缺少校验信息。");
            if (world is not null)
                EnsureWorld(frozen, world, token);
            return new ResourceInstallationPlan(frozen, snapshot, world, SafeFiles.UnderRoot(directory, snapshot.FileName));
        }
        public static List<InstalledWorld> Worlds(InstalledInstance instance, CancellationToken token)
        {
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            string saves = Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "saves/entry"));
            var result = new List<InstalledWorld>();
            if (!Directory.Exists(saves))
                return result;
            string identity = null;
            using (var parent = LocalModActions.NativeLease.OpenDirectory(saves, ref identity))
            {
                foreach (var directory in Directory.EnumerateDirectories(saves))
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(Path.Combine(directory, "level.dat")))
                        continue;
                    try
                    {
                        result.Add(ReadWorld(directory, token));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Inaccessible/link candidates are never installed into.
                    }
                }
            }
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            return result.OrderBy(world => world.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        private static InstalledWorld ReadWorld(string directory, CancellationToken token)
        {
            string identity = null;
            using (var handle = LocalModActions.NativeLease.OpenDirectory(directory, ref identity))
            using (var marker = LocalModActions.NativeLease.Open(Path.Combine(directory, "level.dat"), false))
            using (var digest = SHA256.Create())
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = marker.Stream.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    digest.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                digest.TransformFinalBlock(new byte[] { }, 0, 0);
                return new InstalledWorld(Path.GetFileName(directory), Path.GetFullPath(directory), identity, BitConverter.ToString(digest.Hash).Replace("-", ""));
            }
        }
        internal static void EnsureWorld(InstalledInstance instance, InstalledWorld world, CancellationToken token)
        {
            var current = ReadWorld(world.Folder, token);
            if ((current.Identity ?? "") != (world.Identity ?? "") || (current.MarkerHash ?? "") != (world.MarkerHash ?? "") || !string.Equals(current.Folder, Path.Combine(instance.RunDirectory, "saves", current.Name), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("所选存档已变化，请重新选择。");
        }
    }
}
