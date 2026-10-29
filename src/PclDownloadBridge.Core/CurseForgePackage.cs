using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed class CurseForgeReference
    {
        public long ProjectId { get; private set; }
        public long FileId { get; private set; }
        public bool Required { get; private set; }
        internal CurseForgeReference(long project, long file, bool required)
        {
            ProjectId = project;
            FileId = file;
            Required = required;
        }
    }

    // Pinned PCL ModModpack: root/one-folder manifest, exact loader versions,
    // required/optional file IDs and overrides. Parsing never writes a game directory.
    public sealed class CurseForgePackage
    {
        public string SourcePath { get; private set; }
        public string Sha256 { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Minecraft { get; private set; }
        public ComponentKind? LoaderKind { get; private set; }
        public string LoaderVersion { get; private set; }
        public ReadOnlyCollection<CurseForgeReference> References { get; private set; }
        public ReadOnlyCollection<string> OverrideFiles { get; private set; }
        internal readonly string BaseFolder;
        internal readonly string OverridesFolder;
        private CurseForgePackage(string path, string digest, ZipArchive archive)
        {
            SourcePath = path;
            Sha256 = digest;
            // Payloads have no fixed byte cap. Installation streams each override
            // and checks its declared length without buffering the whole entry.
            var names = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            if (archive.Entries.Count > 20000)
                throw new InvalidDataException("整合包条目超过 20000 项限制。");
            foreach (var entry in archive.Entries)
            {
                string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                ValidateRelative(relative);
                if (names.ContainsKey(relative))
                    throw new InvalidDataException("整合包存在重复或大小写冲突的路径：" + relative);
                if ((((((entry.ExternalAttributes & (unchecked((int)0xF0000000))))) == (unchecked((int)0xA0000000))) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("整合包包含链接条目：" + relative);
                names.Add(relative, entry);
            }
            foreach (var pair in names)
            {
                string parent = pair.Key;
                while (parent.Contains("/"))
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (names.ContainsKey(parent) && !IsDirectory(names[parent]))
                        throw new InvalidDataException("整合包文件与目录路径冲突：" + parent);
                }
            }
            var manifests = names.Keys.Where(key => key == "manifest.json" || key.EndsWith("/manifest.json", StringComparison.Ordinal) && key.Count(ch => ch == '/') == 1).ToList();
            string manifestName = manifests.Contains("manifest.json") ? "manifest.json" : manifests.Count == 1 ? manifests[0] : null;
            if (manifestName is null)
                throw new InvalidDataException("未找到唯一的 CurseForge manifest.json；此入口仅支持 CurseForge ZIP。");
            BaseFolder = manifestName.Substring(0, manifestName.Length - "manifest.json".Length);
            JObject manifest;
            using (var input = names[manifestName].Open())
            {
                manifest = JObject.Parse(ReadText(input, names[manifestName].Length, 2 * 1024 * 1024), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }
            if ((string)manifest["manifestType"] != "minecraftModpack" || Number(manifest, "manifestVersion") != 1L)
                throw new InvalidDataException("不支持的 CurseForge 整合包格式版本。");
            Name = (TextValue(manifest, "name", false) ?? Path.GetFileNameWithoutExtension(path)).Trim();
            if (Name.Length == 0)
                Name = "CurseForge 整合包";
            Version = TextValue(manifest, "version", false) ?? "";
            JObject game = manifest["minecraft"] as JObject;
            if (game is null)
                throw new InvalidDataException("整合包未提供 Minecraft 信息。");
            Minecraft = TextValue(game, "version");
            SafeFiles.ValidateInstanceName(Minecraft);
            JArray loaders = game["modLoaders"] as JArray;
            if (loaders is null || loaders.Count > 1)
                throw new InvalidDataException("整合包加载器声明无效；目前支持一个 Forge、NeoForge 或 Fabric 加载器。");
            if (loaders.Count == 1)
            {
                JObject loader = loaders[0] as JObject;
                if (loader is null)
                    throw new InvalidDataException("整合包加载器条目无效。");
                string id = TextValue(loader, "id");
                foreach (var prefix in new[] { "forge-", "neoforge-", "fabric-" })
                {
                    if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    LoaderKind = prefix == "forge-" ? ComponentKind.Forge : prefix == "neoforge-" ? ComponentKind.NeoForge : ComponentKind.Fabric;
                    LoaderVersion = id.Substring(prefix.Length);
                }
                if (!LoaderKind.HasValue || !System.Text.RegularExpressions.Regex.IsMatch(LoaderVersion ?? "", @"\A[0-9][A-Za-z0-9.+_-]{0,100}\z"))
                    throw new NotSupportedException("不支持或未固定版本的整合包加载器：" + id);
            }
            JArray files = manifest["files"] as JArray;
            if (files is null || files.Count > 10000)
                throw new InvalidDataException("整合包文件清单无效或超过 10000 项。");
            var refs = new List<CurseForgeReference>();
            var ids = new HashSet<long>();
            foreach (var item in files)
            {
                JObject value = item as JObject;
                if (value is null)
                    throw new InvalidDataException("整合包文件条目无效。");
                long project = Number(value, "projectID");
                long file = Number(value, "fileID");
                if (!ids.Add(file))
                    throw new InvalidDataException("整合包重复声明文件 ID：" + file);
                var required = value["required"];
                if (required is not null && required.Type != JTokenType.Boolean)
                    throw new InvalidDataException("整合包 required 字段无效。");
                refs.Add(new CurseForgeReference(project, file, required is null || (bool)required));
            }
            References = refs.AsReadOnly();
            OverridesFolder = (TextValue(manifest, "overrides", false) ?? "overrides").TrimEnd('/');
            ValidateRelative(OverridesFolder);
            string prefixPath = BaseFolder + OverridesFolder + "/";
            OverrideFiles = names.Where(pair => pair.Key.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase) && !IsDirectory(pair.Value)).Select(pair => pair.Key.Substring(prefixPath.Length)).ToList().AsReadOnly();
            foreach (var relative in OverrideFiles)
                ValidateRelative(relative);
        }
        public static CurseForgePackage Read(string path, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string digest = HashArchive(input, token);
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    return new CurseForgePackage(path, digest, archive);
                }
            }
        }
        internal static string HashArchive(Stream input, CancellationToken token)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                sha.TransformFinalBlock(new byte[] { }, 0, 0);
                input.Position = 0L;
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static bool IsDirectory(ZipArchiveEntry entry)
        {
            return entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith(@"\", StringComparison.Ordinal);
        }
        internal static void ValidateRelative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.StartsWith("/", StringComparison.Ordinal) || value.Contains(@"\"))
                throw new InvalidDataException("整合包路径无效：" + value);
            foreach (var segment in value.Split('/'))
                SafeFiles.ValidatePathSegment(segment);
        }
        internal static long Number(JObject value, string @field, long minimum = 1L)
        {
            var item = value[@field];
            if (item is null || item.Type != JTokenType.Integer)
                throw new InvalidDataException("CurseForge " + @field + " 字段无效。");
            long result = (long)item;
            if (result < minimum)
                throw new InvalidDataException("CurseForge " + @field + " 超出范围。");
            return result;
        }
        internal static string TextValue(JObject value, string @field, bool required = true)
        {
            var item = value[@field];
            if (item is null || item.Type == JTokenType.Null)
            {
                if (!required)
                    return null;
            }
            else if (item.Type == JTokenType.String && (!required || !string.IsNullOrWhiteSpace((string)item)))
            {
                return (string)item;
            }
            throw new InvalidDataException("CurseForge " + @field + " 文本字段无效。");
        }
        internal static string ReadText(Stream input, long length, int maximum)
        {
            if (length > maximum)
                throw new InvalidDataException("整合包配置文件过大。");
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[8192];
                do
                {
                    int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, maximum - bytes.Length + 1L));
                    if (count == 0)
                        break;
                    if (bytes.Length + count > maximum)
                        throw new InvalidDataException("整合包配置文件过大。");
                    bytes.Write(buffer, 0, count);
                }
                while (true);
                if (bytes.Length != length)
                    throw new InvalidDataException("整合包配置长度不匹配。");
                bytes.Position = 0L;
                using (var reader = new StreamReader(bytes, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}