using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModModpack: isolated instance, exact loader and overrides.
    // Both formats join the same private Minecraft payload and publication lock.
    internal abstract class ModpackInstallPlan : IDisposable
    {
        internal readonly List<DownloadFile> Files = new List<DownloadFile>();
        protected readonly ZipArchive Archive;
        private readonly FileStream _input;
        protected readonly Dictionary<string, string> OverrideHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal NativeInstanceIsolation NativeConfiguration { get; set; }
        private static readonly string[] NativeConfigPaths = new[] { "PCL/config.v1.yml", "PCL/config.v1.json", ".hmcl/config/instance-game-settings.json", "hmclversion.cfg" };
        internal abstract string Minecraft { get; }
        protected abstract IEnumerable<KeyValuePair<string, ZipArchiveEntry>> OverrideEntries { get; }
        protected virtual bool AllowLayeredOverrides
        {
            get
            {
                return false;
            }
        }
        internal int OverrideCount
        {
            get
            {
                return OverrideEntries.Count();
            }
        }
        protected ModpackInstallPlan(string path, string digest, CancellationToken token)
        {
            _input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if ((CurseForgePackage.HashArchive(_input, token) ?? "") != (digest ?? ""))
                    throw new InvalidDataException("整合包文件在读取后发生变化，请重新选择。");
                Archive = new ZipArchive(_input, ZipArchiveMode.Read, true);
            }
            catch
            {
                _input.Dispose();
                throw;
            }
        }
        internal void ValidateTargets(string name)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] reserved = new[] { name + ".jar", name + ".json", "installation.json", "PCL/CurseForgeInstall.json", "PCL/ModrinthInstall.json" };
            foreach (var relative in Files.Select(item => item.RelativePath).Concat(OverrideEntries.Select(item => item.Key)))
            {
                CurseForgePackage.ValidateRelative(relative);
                if (reserved.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("整合包内容与生成的实例文件冲突：" + relative);
                if (!targets.Add(relative) && !AllowLayeredOverrides)
                    throw new InvalidDataException("整合包下载内容与 overrides 路径冲突：" + relative);
            }
            if (Files.Any(item => string.Equals(item.RelativePath, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("隔离设置只能通过整合包 overrides 合并。");
            if (Files.Any(item => NativeConfigPaths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("原生实例配置只能通过整合包 overrides 合并。");
            targets.UnionWith(reserved);
            targets.Add("PCL/Setup.ini");
            targets.UnionWith(NativeConfigPaths);
            foreach (var relative in targets)
            {
                string parent = relative;
                while (parent.Contains("/"))
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (targets.Contains(parent))
                        throw new InvalidDataException("整合包文件与目录路径冲突：" + parent);
                }
            }
        }
        internal void CopyOverrides(string stage, CancellationToken token)
        {
            // The format parser selects the winning entry of each permitted layer.
            // Apply only after loader work joins, so it cannot race packaged INI/config.
            foreach (var item in OverrideEntries)
            {
                token.ThrowIfCancellationRequested();
                string relative = item.Key;
                var entry = item.Value;
                string target = SafeFiles.UnderRoot(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (string.Equals(relative, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase))
                {
                    using (var input = entry.Open())
                    {
                        string text = CurseForgePackage.ReadText(input, entry.Length, 2 * 1024 * 1024);
                        var lines = text.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n').Where(line =>
                            {
                                int colon = line.IndexOf(':');
                                return colon < 0 || !new[] { "VersionArgumentIndie", "VersionArgumentIndieV2", "IsStar" }.Contains(line.Substring(0, colon).Trim(), StringComparer.OrdinalIgnoreCase);
                            });
                        byte[] bytes = Encoding.UTF8.GetBytes(string.Join(Constants.vbCrLf, lines) + Constants.vbCrLf + "VersionArgumentIndie:1" + Constants.vbCrLf + "VersionArgumentIndieV2:True" + Constants.vbCrLf + "IsStar:False" + Constants.vbCrLf);
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        {
                            OverrideHashes[relative] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                        }
                        token.ThrowIfCancellationRequested();
                        File.WriteAllBytes(target, bytes);
                    }
                }
                else
                {
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, AllowLayeredOverrides ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        long total = 0L;
                        do
                        {
                            token.ThrowIfCancellationRequested();
                            int count = input.Read(buffer, 0, buffer.Length);
                            if (count == 0)
                                break;
                            total += count;
                            if (total > entry.Length)
                                throw new InvalidDataException("整合包解压内容超过声明长度：" + relative);
                            output.Write(buffer, 0, count);
                            sha.TransformBlock(buffer, 0, count, buffer, 0);
                        }
                        while (true);
                        if (total != entry.Length)
                            throw new InvalidDataException("整合包解压内容不完整。");
                        sha.TransformFinalBlock(new byte[] { }, 0, 0);
                        OverrideHashes[relative] = BitConverter.ToString(sha.Hash).Replace("-", "");
                    }
                }
            }
        }
        protected void ValidatePayload(string stage, CancellationToken token)
        {
            if (NativeConfiguration is null)
                PclIsolationPlan.ValidatePackSettings(stage, token);
            else
                NativeConfiguration.ValidateStaged(stage, token);
            foreach (var pair in OverrideHashes)
            {
                token.ThrowIfCancellationRequested();
                using (var stream = File.OpenRead(SafeFiles.UnderRoot(stage, pair.Key)))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    if ((BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") ?? "") != (pair.Value ?? ""))
                        throw new InvalidDataException("整合包覆盖文件在发布前发生变化：" + pair.Key);
                }
            }
            foreach (var item in Files)
            {
                token.ThrowIfCancellationRequested();
                // Deliberately replaced index files were checked before overrides, then
                // checked above against the winning archive entry, not the old digest.
                if (!OverrideHashes.ContainsKey(item.RelativePath) && !SafeFiles.Matches(SafeFiles.UnderRoot(stage, item.RelativePath), item))
                    throw new InvalidDataException("整合包文件在发布前校验失败：" + item.RelativePath);
            }
        }
        internal void RefreshNativeHashes(string stage)
        {
            if (NativeConfiguration is null)
                return;
            foreach (var relative in NativeConfiguration.WrittenPaths)
            {
                if (!OverrideHashes.ContainsKey(relative))
                    continue;
                using (var stream = File.OpenRead(SafeFiles.UnderRoot(stage, relative)))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    OverrideHashes[relative] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                }
            }
        }
        internal abstract void WriteLedger(string stage, CancellationToken token);
        public void Dispose()
        {
            Archive?.Dispose();
            _input.Dispose();
        }
    }

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    internal sealed class ModpackInstallation
    {
        private ModpackInstallation() { }
        internal async static Task<InstallResult> InstallAsync(ModpackInstallPlan plan, SourcePolicy policy, string pclDirectory, string root, string name, IEnumerable<string> reuse, IProgress<TransferProgress> progress, CancellationToken token, ComponentKind? loaderKind, string loaderVersion, int connections, long limit, string javaPath, LauncherInstallTarget launcherTarget = null)
        {
            plan.ValidateTargets(name);
            var installer = new MinecraftInstaller(policy, pclDirectory, launcherTarget: launcherTarget);
            var versions = await installer.VersionsAsync(token).ConfigureAwait(false);
            var entry = versions.SingleOrDefault(item => (item.Id ?? "") == (plan.Minecraft ?? ""));
            if (entry is null)
                throw new InvalidDataException("未找到整合包指定的 Minecraft 版本：" + plan.Minecraft);
            var components = new ComponentSelection() { JavaPath = javaPath };
            if (loaderKind.HasValue)
            {
                var catalogue = await new ComponentCatalog(policy).LoadAsync(loaderKind.Value, plan.Minecraft, token).ConfigureAwait(false);
                var loader = catalogue.SingleOrDefault(item => (item.Version ?? "") == (loaderVersion ?? ""));
                if (loader is null)
                    throw new InvalidDataException("未找到整合包指定的加载器版本，不会替换为其他版本：" + loaderKind.ToString() + " " + loaderVersion);
                components.Entries.Add(loaderKind.Value, loader);
            }
            return await installer.InstallPackAsync(entry, root, name, reuse, progress, token, components, connections, limit, plan).ConfigureAwait(false);
        }
    }
}