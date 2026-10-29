using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // Local instances are distinct from remote VersionEntry catalogue records.
    public sealed class InstalledInstance
    {
        public string Name { get; private set; }
        public string InstancePath { get; private set; }
        public LauncherInstallTarget Target { get; private set; }
        public string MinecraftVersion { get; set; }
        public string MinecraftKind { get; set; } = "unknown";
        public string LoaderSummary { get; set; } = "未知";
        public string RunDirectory { get; set; }
        public string IsolationSummary { get; set; } = "无法确定";
        public bool? IsIsolated { get; set; }
        public bool IsModpack { get; set; }
        public bool IsVanillaRepairCandidate { get; set; }
        public string MetadataSha256 { get; set; }
        public string Problem { get; set; }
        public List<string> Warnings { get; private set; } = new List<string>();
        internal string ConfigurationFingerprint { get; set; }
        internal InstalledInstance(LauncherInstallTarget target, string name)
        {
            Target = target;
            Name = name;
            InstancePath = Path.Combine(target.Root, "versions", name);
        }
    }

    // PCL 5ef81e4 McInstance / PathIndie / JsonObject and PCL-CE McInstance.PathIndie.
    // This reader deliberately omits their state/cache writes and isolation migration.
    public sealed partial class InstalledInstanceCatalog
    {
        private InstalledInstanceCatalog()
        {
        }

        public static List<InstalledInstance> Scan(LauncherInstallTarget target, CancellationToken token)
        {
            if (target is null)
                throw new ArgumentNullException(nameof(target));
            token.ThrowIfCancellationRequested();
            target.ValidateDestination(target.Root);
            string versions = SafeFiles.UnderRoot(target.Root, "versions/entry");
            versions = Path.GetDirectoryName(versions);
            ValidateDirectory(versions);
            var result = new List<InstalledInstance>();
            if (!Directory.Exists(versions))
                return result;
            foreach (var directory in Directory.EnumerateDirectories(versions))
            {
                token.ThrowIfCancellationRequested();
                result.Add(Read(target, Path.GetFileName(directory), token));
            }
            token.ThrowIfCancellationRequested();
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static InstalledInstance Read(LauncherInstallTarget target, string name, CancellationToken token)
        {
            if (target is null)
                throw new ArgumentNullException(nameof(target));
            token.ThrowIfCancellationRequested();
            var item = new InstalledInstance(target, name);
            try
            {
                SafeFiles.ValidateInstanceName(name);
                target.ValidateDestination(target.Root);
                ValidateDirectory(item.InstancePath);
                var inputs = new InstanceConfigurationInputs();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                JObject raw = null;
                JObject vanilla = null;
                bool components = false;
                bool modable = false;
                var metadata = ReadProfile(target.Root, name, inputs, visited, token, ref raw, ref vanilla);
                string version = raw.Property("inheritsFrom") is not null ? MetadataInterpreter.GameVersion(metadata) : MetadataInterpreter.GameVersion(raw);
                if (version is null)
                    version = MetadataInterpreter.GameVersion(metadata);
                item.MinecraftVersion = version;
                item.MinecraftKind = MinecraftVersionCategory.Classify(version, vanilla);
                if (version is null)
                    item.Warnings.Add("无法确定 Minecraft 版本。");
                var loaders = MetadataInterpreter.DetectLoaders(metadata, raw);
                item.LoaderSummary = loaders.Count == 0 ? "原版" : string.Join(" + ", loaders);
                components = loaders.Count > 0;
                modable = loaders.Any(value => !value.StartsWith("OptiFine", StringComparison.Ordinal));
                var original = ((raw["patches"] as JArray)?.OfType<JObject>().SingleOrDefault(patch => Scalar(patch["id"]) == "game")) ?? raw;
                string mainClass = Scalar(metadata["mainClass"]);
                if (!components && mainClass != "net.minecraft.client.main.Main" && mainClass != "net.minecraft.client.Minecraft" && mainClass != "com.mojang.rubydung.RubyDung")
                {
                    item.LoaderSummary = "自定义组件";
                    components = true;
                    item.Warnings.Add("启动入口暂不识别，不提供原版补全。");
                }
                string instanceRelative = "versions/" + name + "/";
                foreach (var marker in new[] { "modpack.cfg", "PCL/ModrinthInstall.json", "PCL/CurseForgeInstall.json", "modrinth.index.json", "manifest.json" })
                {
                    // Receipts can contain every pack file. Identity only needs their
                    // presence, so do not load arbitrarily large receipt bodies.
                    if (File.Exists(SafeFiles.UnderRoot(target.Root, instanceRelative + marker)))
                        item.IsModpack = true;
                }
                var entry = new VersionEntry() { Id = version ?? name, Kind = Scalar(metadata["type"]) };
                if (target.Kind == ExternalLauncherKind.Pcl)
                {
                    // Fixed PCL ModLoaders flags contain exactly these four types.
                    // CE's extra loader support must not change PCL's default policy.
                    bool pclModable = loaders.Any(loader => Regex.IsMatch(loader, @"\A(?:Forge|NeoForge|Fabric|LiteLoader)(?: |\z)"));
                    bool pclComponents = pclModable || loaders.Any(loader => loader.StartsWith("OptiFine", StringComparison.Ordinal));
                    bool isolated = ReadPclIsolation(target, name, entry, metadata, pclComponents, pclModable, inputs);
                    item.IsIsolated = isolated;
                    item.RunDirectory = isolated ? item.InstancePath : target.Root;
                }
                else
                {
                    var isolation = NativeInstanceIsolation.Inspect(target, name, entry, metadata, components, modable, inputs);
                    item.IsIsolated = isolation.Isolated;
                    item.RunDirectory = isolation.RunDirectory;
                }
                ValidateDirectory(item.RunDirectory);
                item.IsolationSummary = item.IsIsolated.Value ? "版本隔离" : Same(item.RunDirectory, target.Root) ? "共享游戏目录" : "自定义运行目录";
                if (!item.IsIsolated.Value)
                    item.Warnings.Add("此实例使用共享或自定义运行目录，文件可能同时被其他实例使用。");
                bool hasPatches = raw.Property("patches") is not null;
                bool simplePatch = !hasPatches || target.Kind == ExternalLauncherKind.Hmcl && ((JArray)raw["patches"]).Count == 1 && Scalar(original["id"]) == "game";
                item.IsVanillaRepairCandidate = version is not null && !components && !item.IsModpack && simplePatch && raw.Property("inheritsFrom") is null && original.Property("inheritsFrom") is null && Regex.IsMatch(Scalar(original.SelectToken("downloads.client.sha1")) ?? "", @"\A[0-9a-fA-F]{40}\z") && original["libraries"] is JArray && original["assetIndex"] is JObject;
                item.MetadataSha256 = HashBytes(inputs.Read(SafeFiles.UnderRoot(target.Root, instanceRelative + name + ".json")));
                item.ConfigurationFingerprint = inputs.Fingerprint();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is Newtonsoft.Json.JsonException || ex is InvalidOperationException)
            {
                item.Problem = ex.Message;
                item.IsVanillaRepairCandidate = false;
            }
            token.ThrowIfCancellationRequested();
            return item;
        }

        // Re-read the exact frozen launcher context before an action. This also
        // detects inherited JSON, source configuration and previously absent markers.
        public static void EnsureUnchanged(InstalledInstance instance, CancellationToken token)
        {
            if (instance is null)
                throw new ArgumentNullException(nameof(instance));
            var current = Read(instance.Target, instance.Name, token);
            if ((bool)((current.Problem is not null || instance.ConfigurationFingerprint is null || (current.ConfigurationFingerprint ?? "") != (instance.ConfigurationFingerprint ?? "") || (current.MetadataSha256 ?? "") != (instance.MetadataSha256 ?? "") || !Same(current.RunDirectory, instance.RunDirectory) ? true : instance.IsIsolated is var arg1 && current.IsIsolated is { } arg2 && arg1.HasValue ? arg2 != arg1.Value : (bool?)null).GetValueOrDefault() || current.IsModpack != instance.IsModpack))
            {
                throw new InvalidDataException("实例或启动器设置已变化，请刷新列表后重试。");
            }
        }

        private static JObject ReadProfile(string root, string name, InstanceConfigurationInputs inputs, HashSet<string> visited, CancellationToken token, ref JObject raw, ref JObject vanilla)
        {
            token.ThrowIfCancellationRequested();
            SafeFiles.ValidateInstanceName(name);
            if (visited.Count >= 32 || !visited.Add(name))
                throw new InvalidDataException("版本继承循环或层级过多。");
            byte[] bytes = inputs.Read(SafeFiles.UnderRoot(root, "versions/" + name + "/" + name + ".json"));
            if (bytes is null)
                throw new InvalidDataException("缺少版本配置：" + name + ".json");
            raw = JObject.Parse(InstanceConfigurationInputs.Text(bytes), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            vanilla = null;
            JObject own = (JObject)raw.DeepClone();
            var patchesToken = own["patches"];
            if (patchesToken is not null)
            {
                JArray patches = patchesToken as JArray;
                if (patches is null || patches.Count == 0 || patches.Any(patch => !(patch is JObject)))
                    throw new InvalidDataException("HMCL 版本分支无效。");
                var games = patches.OfType<JObject>().Where(patch => Scalar(patch["id"]) == "game").ToList();
                if (games.Count != 1)
                    throw new InvalidDataException("HMCL 原版分支不唯一。");
                own = (JObject)games[0].DeepClone();
                vanilla = (JObject)own.DeepClone();
                foreach (var patch in patches.OfType<JObject>().Where(value => !ReferenceEquals(value, games[0])))
                    own.Merge(patch, new JsonMergeSettings() { MergeArrayHandling = MergeArrayHandling.Concat });
                own["clientVersion"] = Scalar(games[0]["version"]);
            }
            string parent = Scalar(own["inheritsFrom"]);
            if (parent is not null)
            {
                JObject parentRaw = null;
                var parentJson = ReadProfile(root, parent, inputs, visited, token, ref parentRaw, ref vanilla);
                string parentVersion = MetadataInterpreter.GameVersion(parentJson);
                parentJson.Merge(own, new JsonMergeSettings() { MergeArrayHandling = MergeArrayHandling.Concat });
                if (Scalar(raw["clientVersion"]) is null && parentVersion is not null)
                    parentJson["clientVersion"] = parentVersion;
                own = parentJson;
            }
            else if (vanilla is null)
            {
                vanilla = (JObject)own.DeepClone();
            }
            visited.Remove(name);
            token.ThrowIfCancellationRequested();
            return own;
        }

        private static bool ReadPclIsolation(LauncherInstallTarget target, string name, VersionEntry entry, JObject metadata, bool components, bool modable, InstanceConfigurationInputs inputs)
        {
            string globalPath = target.ConfigPath ?? Path.Combine(target.Workspace, "PCL/Setup.ini");
            string globalText = InstanceConfigurationInputs.Text(inputs.Read(globalPath));
            string instance = SafeFiles.UnderRoot(target.Root, "versions/" + name + "/PCL/Setup.ini");
            string text = InstanceConfigurationInputs.Text(inputs.Read(instance));
            string value = IniValue(text, "VersionArgumentIndieV2");
            bool enabled;
            if (value is not null)
            {
                if (!bool.TryParse(value, out enabled))
                    throw new InvalidDataException("已有实例的版本隔离设置无效。");
                return enabled;
            }
            value = IniValue(text, "VersionArgumentIndie");
            if (value is not null)
            {
                int legacy;
                if (!int.TryParse(value, out legacy) || legacy < -1 || legacy > 2)
                    throw new InvalidDataException("已有实例的旧版隔离设置无效。");
                if (legacy > 0)
                    return legacy == 1;
            }
            string path = Path.GetDirectoryName(instance);
            string instanceRoot = Path.GetDirectoryName(path);
            string mods = SafeFiles.UnderRoot(instanceRoot, "mods/entry");
            string saves = SafeFiles.UnderRoot(instanceRoot, "saves/entry");
            mods = Path.GetDirectoryName(mods);
            saves = Path.GetDirectoryName(saves);
            if (Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any() || Directory.Exists(saves) && Directory.EnumerateDirectories(saves).Any())
                return true;
            int strategy = 4;
            value = IniValue(globalText, "LaunchArgumentIndieV2");
            if (value is not null && (!int.TryParse(value, out strategy) || strategy < 0 || strategy > 4))
                throw new InvalidDataException("PCL 默认隔离策略无效。");
            modable = modable || IniValue(text, "DisplayType") == "2";
            string kind = Scalar(metadata["type"]) ?? entry.Kind;
            bool nonRelease = kind == "old_beta" || kind == "old_alpha" || !components && !string.Equals(kind, "release", StringComparison.OrdinalIgnoreCase);
            switch (strategy)
            {
                case 0:
                    {
                        return false;
                    }
                case 1:
                    {
                        return modable;
                    }
                case 2:
                    {
                        return nonRelease;
                    }
                case 3:
                    {
                        return modable || nonRelease;
                    }

                default:
                    {
                        return true;
                    }
            }
        }
        private static string IniValue(string text, string key)
        {
            if (text is null)
                return null;
            string value = null;
            foreach (var line in text.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n'))
            {
                int split = line.IndexOf(':');
                if (split > 0 && (line.Substring(0, split) ?? "") == (key ?? ""))
                    value = line.Substring(split + 1).Trim();
            }
            return value;
        }
        private static string Scalar(JToken value)
        {
            if (value is null || value.Type == JTokenType.Null)
                return null;
            if (value.Type != JTokenType.String)
                throw new InvalidDataException("版本文本字段类型无效。");
            return (string)value;
        }
        private static string HashBytes(byte[] bytes)
        {
            if (bytes is null)
                return null;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            }
        }
        private static void ValidateDirectory(string full)
        {
            if (string.IsNullOrWhiteSpace(full))
                throw new InvalidDataException("游戏目录为空。");
            string parent = Path.GetFullPath(full);
            while (parent is not null)
            {
                if (File.Exists(parent))
                    throw new IOException("文件占用了游戏目录：" + parent);
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录包含链接，请选择真实目录：" + parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        public static string FolderPath(InstalledInstance instance, string relative, CancellationToken token = default)
        {
            EnsureUnchanged(instance, token);
            string full = string.IsNullOrEmpty(relative) ? instance.InstancePath : SafeFiles.UnderRoot(instance.RunDirectory, relative + "/entry");
            if (!string.IsNullOrEmpty(relative))
                full = Path.GetDirectoryName(full);
            ValidateDirectory(full);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("此文件夹尚不存在。");
            return full;
        }
        private static bool Same(string first, string second)
        {
            return first is not null && second is not null && string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}