using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed class ModrinthReference
    {
        public string Path { get; private set; }
        public string Sha1 { get; private set; }
        public string Sha512 { get; private set; }
        public long EstimatedSize { get; private set; }
        public string Client { get; private set; }
        public ReadOnlyCollection<string> Downloads { get; private set; }
        internal ModrinthReference(JObject value)
        {
            Path = Text(value, "path");
            CurseForgePackage.ValidateRelative(Path);
            JObject hashes = value["hashes"] as JObject;
            if (hashes is null)
                throw new InvalidDataException("Modrinth 文件缺少 hashes：" + Path);
            Sha1 = Text(hashes, "sha1");
            Sha512 = Text(hashes, "sha512");
            if (!Regex.IsMatch(Sha1, @"\A[0-9a-fA-F]{40}\z") || !Regex.IsMatch(Sha512, @"\A[0-9a-fA-F]{128}\z"))
                throw new InvalidDataException("Modrinth 文件的 SHA-1 / SHA-512 无效：" + Path);
            EstimatedSize = IntegerValue(value, "fileSize", 0L);
            Client = "required";
            var environment = value["env"];
            if (environment is not null)
            {
                if (!(environment is JObject))
                    throw new InvalidDataException("Modrinth env 字段无效。");
                foreach (var side in new[] { "client", "server" })
                {
                    // PCL installs by env.client; HMCL exports optional files without
                    // a server field. Still validate server whenever it is supplied.
                    if (side == "server" && environment[side] is null)
                        continue;
                    string mode = Text((JObject)environment, side);
                    if (!new[] { "required", "optional", "unsupported" }.Contains(mode, StringComparer.Ordinal))
                        throw new InvalidDataException("Modrinth 环境声明无效：" + side);
                    if (side == "client")
                        Client = mode;
                }
            }
            JArray urls = value["downloads"] as JArray;
            if (urls is null || urls.Count == 0 || urls.Count > 16)
                throw new InvalidDataException("Modrinth 下载地址数量无效：" + Path);
            var addresses = new List<string>();
            foreach (var url in urls)
            {
                if (url.Type != JTokenType.String)
                    throw new InvalidDataException("Modrinth 下载地址必须是文本。");
                ModrinthDownloadPolicy.ValidateUrl((string)url, false, false);
                if (!addresses.Contains((string)url, StringComparer.Ordinal))
                    addresses.Add((string)url);
            }
            Downloads = addresses.AsReadOnly();
        }
        internal DownloadFile ToDownload()
        {
            return new DownloadFile()
            {
                RelativePath = Path,
                Url = Downloads[0],
                AlternateUrls = Downloads.Skip(1).ToArray(),
                EstimatedSize = EstimatedSize,
                Sha1 = Sha1,
                Sha512 = Sha512,
                Category = "modrinth"
            };
        }
        internal static string Text(JObject value, string @field, bool required = true)
        {
            var item = value[@field];
            if (item is null && !required)
                return null;
            if (item is null || item.Type != JTokenType.String || required && string.IsNullOrWhiteSpace((string)item))
                throw new InvalidDataException("Modrinth " + @field + " 文本字段无效。");
            if (((string)item).Length > 4096)
                throw new InvalidDataException("Modrinth " + @field + " 文本字段过长。");
            return (string)item;
        }
        internal static long IntegerValue(JObject value, string @field, long minimum)
        {
            var item = value[@field];
            if (item is null || item.Type != JTokenType.Integer)
                throw new InvalidDataException("Modrinth " + @field + " 整数字段无效。");
            long result = (long)item;
            if (result < minimum)
                throw new InvalidDataException("Modrinth " + @field + " 超出范围。");
            return result;
        }
    }

    // PCL ModModpack.InstallPackModrinth at 5ef81e4: dependency IDs, client env,
    // downloads[], overrides then client-overrides, single wrapping folder support.
    public sealed class ModrinthPackage
    {
        public string SourcePath { get; private set; }
        public string Sha256 { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Summary { get; private set; }
        public string Minecraft { get; private set; }
        public ComponentKind? LoaderKind { get; private set; }
        public string LoaderVersion { get; private set; }
        public ReadOnlyCollection<ModrinthReference> References { get; private set; }
        public ReadOnlyCollection<string> OverrideFiles { get; private set; }
        internal readonly ReadOnlyDictionary<string, string> OverrideSources;
        private ModrinthPackage(string path, string digest, ZipArchive archive)
        {
            SourcePath = path;
            Sha256 = digest;
            // Large payloads are streamed by ModpackInstallPlan, not loaded as text.
            var names = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            if (archive.Entries.Count > 20000)
                throw new InvalidDataException("整合包条目超过 20000 项限制。");
            foreach (var entry in archive.Entries)
            {
                string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                CurseForgePackage.ValidateRelative(relative);
                if (names.ContainsKey(relative))
                    throw new InvalidDataException("整合包存在重复或大小写冲突的路径：" + relative);
                if ((((((entry.ExternalAttributes & (unchecked((int)0xF0000000))))) == (unchecked((int)0xA0000000))) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("整合包包含链接条目：" + relative);
                names.Add(relative, entry);
            }
            string[] candidates = names.Keys.Contains("modrinth.index.json", StringComparer.Ordinal) ? (new[] { "modrinth.index.json" }) : names.Keys.Where(key => key.EndsWith("/modrinth.index.json", StringComparison.Ordinal) && key.Count(ch => ch == '/') == 1).ToArray();
            // Match PCL's root precedence, then require a unique wrapping folder.
            if (candidates.Length != 1)
                throw new InvalidDataException("未找到唯一的 Modrinth modrinth.index.json。");
            string index = candidates[0];
            string prefix = index.Substring(0, index.Length - "modrinth.index.json".Length);
            JObject manifest;
            using (var input = names[index].Open())
            {
                manifest = JObject.Parse(CurseForgePackage.ReadText(input, names[index].Length, 8 * 1024 * 1024), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }
            if (ModrinthReference.IntegerValue(manifest, "formatVersion", 1L) != 1L || ModrinthReference.Text(manifest, "game") != "minecraft")
                throw new NotSupportedException("不支持的 Modrinth 整合包格式或游戏。");
            Name = ModrinthReference.Text(manifest, "name").Trim();
            Version = ModrinthReference.Text(manifest, "versionId");
            Summary = ModrinthReference.Text(manifest, "summary", false) ?? "";
            JObject dependencies = manifest["dependencies"] as JObject;
            if (dependencies is null)
                throw new InvalidDataException("Modrinth 整合包缺少 dependencies。");
            Minecraft = ModrinthReference.Text(dependencies, "minecraft");
            SafeFiles.ValidateInstanceName(Minecraft);
            foreach (var pair in dependencies.Properties())
            {
                ComponentKind kind;
                switch (pair.Name ?? "")
                {
                    case "minecraft":
                        {
                            continue;
                        }
                    case "forge":
                        {
                            kind = ComponentKind.Forge;
                            break;
                        }
                    case "neoforge":
                    case "neo-forge":
                        {
                            kind = ComponentKind.NeoForge;
                            break;
                        }
                    case "fabric-loader":
                        {
                            kind = ComponentKind.Fabric;
                            break;
                        }

                    default:
                        {
                            throw new NotSupportedException("暂不支持整合包依赖：" + pair.Name + "；不会跳过所需加载器。");
                        }
                }
                if (LoaderKind.HasValue)
                    throw new InvalidDataException("整合包声明了多个加载器。");
                LoaderKind = kind;
                LoaderVersion = ModrinthReference.Text(dependencies, pair.Name);
                if (!Regex.IsMatch(LoaderVersion, @"\A[0-9][A-Za-z0-9.+_-]{0,100}\z"))
                    throw new InvalidDataException("整合包必须指定固定的加载器版本。");
            }
            JArray files = manifest["files"] as JArray;
            var refs = new List<ModrinthReference>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files is null || files.Count > 10000)
                throw new InvalidDataException("整合包文件清单无效或超过 10000 项。");
            foreach (var item in files)
            {
                JObject value = item as JObject;
                if (value is null)
                    throw new InvalidDataException("Modrinth 文件条目无效。");
                var reference = new ModrinthReference(value);
                if (!targets.Add(reference.Path))
                    throw new InvalidDataException("Modrinth 清单路径重复或大小写冲突：" + reference.Path);
                refs.Add(reference);
            }
            References = refs.AsReadOnly();
            var winning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in new[] { "overrides/", "client-overrides/" })
            {
                string start = prefix + layer;
                foreach (var pair in names.Where(item => !CurseForgePackage.IsDirectory(item.Value) && item.Key.StartsWith(start, StringComparison.OrdinalIgnoreCase)))
                {
                    string relative = pair.Key.Substring(start.Length);
                    CurseForgePackage.ValidateRelative(relative);
                    winning[relative] = pair.Value.FullName;
                }
            }
            // Check ZIP topology as well as the final client view. Server-only files
            // are never applied, but cannot smuggle a link or escaping archive path.
            ValidateParents(names.Keys, key => !CurseForgePackage.IsDirectory(names[key]));
            // PCL skips unchosen optional files before checking their destination.
            // Check required content here; the install plan checks the actual choices
            // together before metadata requests or any game-directory writes.
            ValidateParents(refs.Where(item => item.Client == "required").Select(item => item.Path).Concat(winning.Keys).Distinct(StringComparer.OrdinalIgnoreCase), key => true);
            OverrideSources = new ReadOnlyDictionary<string, string>(winning);
            OverrideFiles = winning.Keys.ToList().AsReadOnly();
        }
        private static void ValidateParents(IEnumerable<string> paths, Func<string, bool> isFile)
        {
            var targets = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var relative in targets)
            {
                string parent = relative;
                while (parent.Contains("/"))
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (targets.Contains(parent) && isFile(parent))
                        throw new InvalidDataException("整合包文件与目录路径冲突：" + parent);
                }
            }
        }
        public static ModrinthPackage Read(string path, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string digest = CurseForgePackage.HashArchive(input, token);
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    var result = new ModrinthPackage(path, digest, archive);
                    token.ThrowIfCancellationRequested();
                    return result;
                }
            }
        }
    }
}