using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 LocalResourceFile / LocalResourceLoaders: enabled suffixes and
    // instance-relative Mods. Metadata readers are local, without network/cache writes.
    public sealed class LocalModEntry
    {
        public string FilePath { get; private set; }
        public string FileName { get; private set; }
        public string EnabledName { get; private set; }
        public bool Enabled { get; private set; }
        public long Length { get; private set; }
        public string Sha256 { get; private set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string LoaderSummary { get; set; } = "未知";
        public string Description { get; set; }
        public string Problem { get; set; }
        public bool IsArchiveValid { get; set; }
        public bool Recognized { get; set; }
        public List<string> Warnings { get; private set; } = new List<string>();
        private readonly List<string> _ids = new List<string>();
        private readonly HashSet<string> _idSet = new HashSet<string>(StringComparer.Ordinal);
        private byte[] _iconData;
        private int _iconWidth;
        private int _iconHeight;
        public int IconWidth
        {
            get
            {
                return _iconWidth;
            }
        }
        public int IconHeight
        {
            get
            {
                return _iconHeight;
            }
        }
        public byte[] IconData
        {
            get
            {
                return _iconData is null ? null : (byte[])_iconData.Clone();
            }
        }
        internal int IconLength
        {
            get
            {
                return _iconData is null ? 0 : _iconData.Length;
            }
        }
        internal void SetIcon(byte[] data)
        {
            _iconData = data;
            var width = default(long);
            var height = default(long);
            if (data is not null && LocalModCatalog.TryIconDimensions(data, ref width, ref height))
            {
                _iconWidth = (int)width;
                _iconHeight = (int)height;
            }
            else
            {
                _iconWidth = 0;
                _iconHeight = 0;
            }
        }
        public IReadOnlyList<string> ModIds
        {
            get
            {
                return _ids.AsReadOnly();
            }
        }
        public bool CanToggle
        {
            get
            {
                return Sha256 is not null && LocalModCatalog.IsSupportedFile(FilePath);
            }
        }
        internal LocalModEntry(string full, long size, string digest)
        {
            FilePath = full;
            FileName = Path.GetFileName(full);
            EnabledName = LocalModCatalog.EnabledFileName(full);
            Enabled = string.Equals(FileName, EnabledName, StringComparison.Ordinal);
            Length = size;
            Sha256 = digest;
            DisplayName = Path.GetFileNameWithoutExtension(EnabledName);
        }
        internal void AddId(string value)
        {
            if (value is not null && (value.Length > 256 || value.Any(ch => char.IsControl(ch))))
                throw new InvalidDataException("Mod ID 超出读取范围或包含无效字符。");
            if (!string.IsNullOrWhiteSpace(value) && _idSet.Add(value))
                _ids.Add(value);
        }
    }

    public sealed class LocalModCatalog
    {
        private const int MetadataLimit = 2 * 1024 * 1024;
        private LocalModCatalog()
        {
        }

        // Remove one terminal state suffix only. A name containing ".old" elsewhere
        // is not renamed, and repeated state suffixes are not silently normalized.
        public static string EnabledFileName(string filePath)
        {
            string name = Path.GetFileName(filePath);
            string extension = Path.GetExtension(name);
            if (extension.Equals(".disabled", StringComparison.OrdinalIgnoreCase) || extension.Equals(".old", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }
            return name;
        }
        public static bool IsSupportedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;
            string extension = Path.GetExtension(EnabledFileName(filePath));
            return new[] { ".jar", ".zip", ".litemod" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
        public static string ModsDirectory(InstalledInstance instance, CancellationToken token = default)
        {
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            string full = Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "mods/entry"));
            ValidatePath(full, true);
            return full;
        }
        public static List<LocalModEntry> Scan(InstalledInstance instance, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string full = ModsDirectory(instance, token);
            var result = new List<LocalModEntry>();
            int iconBudget = 32 * 1024 * 1024;
            if (Directory.Exists(full))
            {
                foreach (var filePath in Directory.EnumerateFiles(full))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSupportedFile(filePath))
                    {
                        var item = Read(filePath, token);
                        if (item.IconLength > iconBudget)
                            item.SetIcon(null);
                        else
                            iconBudget -= item.IconLength;
                        result.Add(item);
                    }
                }
            }
            InstalledInstanceCatalog.EnsureUnchanged(instance, token);
            foreach (var group in result.GroupBy(item => item.EnabledName, StringComparer.OrdinalIgnoreCase).Where(items => items.Count() > 1))
            {
                foreach (var item in group)
                    item.Warnings.Add("同时存在同名的启用或禁用文件，请先确认文件冲突。");
            }
            foreach (var group in result.SelectMany(item => item.ModIds.Select(id => new { Id = id, Item = item })).GroupBy(value => value.Id, StringComparer.Ordinal).Where(items => items.Select(value => value.Item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                foreach (var item in @group.Select(value => value.Item).Distinct())
                    item.Warnings.Add("多个文件声明了同一 Mod ID：" + group.Key);
            }
            return result.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static LocalModEntry Read(string filePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(filePath);
            var result = new LocalModEntry(full, -1, null);
            try
            {
                if (!IsSupportedFile(full))
                    throw new InvalidDataException("不支持的 Mod 文件扩展名。");
                ValidatePath(full, false);
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    string digestValue = Digest(stream, token);
                    result = new LocalModEntry(full, length, digestValue);
                    stream.Position = 0L;
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                    {
                        // ZipArchive reads its central directory lazily. Validate that
                        // structure before distinguishing metadata errors from ZIP errors.
                        int entryCount = archive.Entries.Count;
                        result.IsArchiveValid = true;
                        ReadMetadata(archive, result, token);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is Newtonsoft.Json.JsonException || ex is FormatException)
            {
                result.Problem = DisplayText(ex.Message, 1024, false);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        private static void ValidatePath(string full, bool expectDirectory)
        {
            string current = Path.GetFullPath(full);
            if (expectDirectory && File.Exists(current))
                throw new IOException("文件占用了 Mod 目录。");
            while (current is not null)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Mod 路径包含链接，请选择真实目录。");
                current = Path.GetDirectoryName(current);
            }
        }
        private static string Digest(Stream stream, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                hash.TransformFinalBlock(new byte[] { }, 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }

        private class Identity
        {
            internal string Id;
            internal string Name;
            internal string Version;
            internal string Description;
            internal string Loader;
            internal string Icon;
        }
        private static void ReadMetadata(ZipArchive archive, LocalModEntry result, CancellationToken token)
        {
            // PCL-CE 915ac297 ModLocalComp documents these local metadata formats;
            // loader declarations are displayed, never treated as compatibility proof.
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var relevant = new HashSet<string>(new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info", "litemod.json", "META-INF/MANIFEST.MF" }, StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!relevant.Contains(entry.FullName))
                    continue;
                if (entries.ContainsKey(entry.FullName))
                    throw new InvalidDataException("Mod 元数据条目重复：" + entry.FullName);
                entries.Add(entry.FullName, entry);
            }
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entries.ContainsKey("META-INF/MANIFEST.MF"))
            {
                try
                {
                    manifest = ReadManifest(entries["META-INF/MANIFEST.MF"], token);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is DecoderFallbackException)
                {
                    result.Warnings.Add("无法读取 JAR 清单：" + ex.Message);
                }
            }
            var identities = new List<Identity>();
            var problems = new List<string>();
            bool modern = entries.Keys.Any(name => name == "fabric.mod.json" || name == "quilt.mod.json" || name.EndsWith("mods.toml", StringComparison.Ordinal));
            foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json" })
            {
                if (!entries.ContainsKey(name) || name == "mcmod.info" && modern)
                    continue;
                try
                {
                    string text = ReadText(entries[name], token);
                    switch (name ?? "")
                    {
                        case "fabric.mod.json":
                            {
                                identities.Add(ReadFabric(ParseObject(text)));
                                break;
                            }
                        case "quilt.mod.json":
                            {
                                identities.Add(ReadQuilt(ParseObject(text)));
                                break;
                            }
                        case "META-INF/mods.toml":
                        case "META-INF/neoforge.mods.toml":
                            {
                                identities.AddRange(LocalModToml.Read(text, name.Contains("neoforge") ? "NeoForge" : "Forge"));
                                break;
                            }
                        case "mcmod.info":
                            {
                                identities.AddRange(ReadLegacy(text));
                                break;
                            }
                        case "litemod.json":
                            {
                                identities.Add(ReadLite(ParseObject(text)));
                                break;
                            }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is DecoderFallbackException)
                {
                    problems.Add(name + "：" + ex.Message);
                }
            }
            bool primaryChosen = false;
            foreach (var identity in identities)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(identity.Id))
                    continue;
                result.AddId(identity.Id);
                if (!primaryChosen)
                {
                    if (!string.IsNullOrWhiteSpace(identity.Name))
                        result.DisplayName = DisplayText(identity.Name, 256, true);
                    result.Version = DisplayText(ResolveVersion(identity.Version, manifest), 256, true);
                    result.Description = DisplayText(identity.Description, 4096, false);
                    primaryChosen = true;
                }
            }
            result.Recognized = result.ModIds.Count > 0;
            if (identities.Count > 0)
                result.LoaderSummary = string.Join(" / ", identities.Select(item => item.Loader).Distinct(StringComparer.Ordinal));
            if (!result.Recognized && (manifest.ContainsKey("FMLCorePlugin") || manifest.ContainsKey("FMLModType")))
            {
                result.Recognized = true;
                result.LoaderSummary = "Forge";
                if (manifest.ContainsKey("Implementation-Title"))
                    result.DisplayName = DisplayText(manifest["Implementation-Title"], 256, true);
                if (manifest.ContainsKey("Implementation-Version"))
                    result.Version = DisplayText(manifest["Implementation-Version"], 256, true);
                result.Warnings.Add("此文件通过 JAR 清单识别，未声明常规 Mod ID。");
            }
            if (!result.Recognized)
                result.Warnings.Add("未识别本地 Mod 元数据，兼容性未知。");
            if (problems.Count > 0)
                result.Problem = DisplayText(string.Join("；", problems), 1024, false);
            foreach (var identity in identities)
            {
                byte[] icon = ReadIcon(archive, identity.Icon, token);
                if (icon is not null)
                {
                    result.SetIcon(icon);
                    break;
                }
            }
            if (!string.IsNullOrEmpty(result.Version) && result.Version.Contains("${"))
            {
                result.Version = null;
                result.Warnings.Add("版本包含未解析的构建占位符。");
            }
        }
        private static string IconPath(JToken value)
        {
            if ((value?.Type) is { } arg1 && arg1 == JTokenType.String)
                return (string)value;
            JObject sizes = value as JObject;
            if (sizes is null)
                return null;
            var candidates = new List<KeyValuePair<int, string>>();
            foreach (var item in sizes.Properties())
            {
                int size;
                if (int.TryParse(item.Name, out size) && size > 0 && item.Value.Type == JTokenType.String)
                    candidates.Add(new KeyValuePair<int, string>(size, (string)item.Value));
            }
            return candidates.OrderBy(item => item.Key >= 64 ? 0 : 1).ThenBy(item => Math.Abs(item.Key - 64L)).Select(item => item.Value).FirstOrDefault();
        }
        private static byte[] ReadIcon(ZipArchive archive, string name, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Any(ch => char.IsControl(ch)))
                return null;
            name = name.Replace(@"\", "/");
            if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") || name.Split('/').Any(part => part == ".." || part == "." || string.IsNullOrEmpty(part)))
                return null;
            try
            {
                ZipArchiveEntry[] matches = archive.Entries.Where(entry => (entry.FullName ?? "") == (name ?? "")).Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Length > 1024 * 1024)
                    return null;
                byte[] bytes = ReadBytes(matches[0], token);
                if (!SafeIconDimensions(bytes))
                    return null;
                return bytes;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
            {
                return null;
            }
        }
        internal static bool SafeIconDimensions(byte[] bytes)
        {
            var width = default(long);
            var height = default(long);
            return TryIconDimensions(bytes, ref width, ref height);
        }
        internal static bool TryIconDimensions(byte[] bytes, ref long width, ref long height)
        {
            width = 0L;
            height = 0L;
            if (bytes.Length < 24 || bytes.Length > 1024 * 1024)
                return false;
            if (bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                width = bytes[16] * 16777216L + bytes[17] * 65536L + bytes[18] * 256L + bytes[19];
                height = bytes[20] * 16777216L + bytes[21] * 65536L + bytes[22] * 256L + bytes[23];
            }
            else if (bytes[0] == 255 && bytes[1] == 216)
            {
                // Several real Mod archives store JPEG bytes under an .png name.
                int offset = 2;
                while (offset + 3 < bytes.Length)
                {
                    if (bytes[offset] != 255)
                        return false;
                    while (offset < bytes.Length && bytes[offset] == 255)
                        offset += 1;
                    if (offset >= bytes.Length)
                        return false;
                    int marker = bytes[offset];
                    offset += 1;
                    if (marker == 217 || marker == 218)
                        return false;
                    if (marker == 1 || marker >= 208 && marker <= 215)
                        continue;
                    if (offset + 1 >= bytes.Length)
                        return false;
                    int size = bytes[offset] * 256 + bytes[offset + 1];
                    if (size < 2 || offset + size > bytes.Length)
                        return false;
                    if (marker >= 192 && marker <= 207 && !new[] { 196, 200, 204 }.Contains(marker))
                    {
                        if (size < 8)
                            return false;
                        height = bytes[offset + 3] * 256 + bytes[offset + 4];
                        width = bytes[offset + 5] * 256 + bytes[offset + 6];
                        break;
                    }
                    offset += size;
                }
            }
            return width >= 1L && height >= 1L && width <= 4096L && height <= 4096L;
        }
        private static string DisplayText(string value, int maximum, bool singleLine)
        {
            if (value is null)
                return null;
            if (singleLine)
                value = value.Replace(Constants.vbCr, " ").Replace(Constants.vbLf, " ").Replace('\t', ' ').Trim();
            if (value.Length <= maximum)
                return value;
            int count = maximum - 1;
            if (char.IsHighSurrogate(value[count - 1]))
                count -= 1;
            return value.Substring(0, count) + "…";
        }
        private static string ReadText(ZipArchiveEntry entry, CancellationToken token)
        {
            using (var reader = new StreamReader(new MemoryStream(ReadBytes(entry, token)), new UTF8Encoding(false, true), true))
            {
                return reader.ReadToEnd();
            }
        }
        private static byte[] ReadBytes(ZipArchiveEntry entry, CancellationToken token)
        {
            if (entry.Length > MetadataLimit)
                throw new InvalidDataException("Mod 元数据超过读取范围。");
            using (var source = entry.Open())
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = source.Read(bytes, 0, bytes.Length);
                    if (count == 0)
                        break;
                    if (buffer.Length + count > MetadataLimit)
                        throw new InvalidDataException("Mod 元数据超过读取范围。");
                    buffer.Write(bytes, 0, count);
                }
                while (true);
                return buffer.ToArray();
            }
        }
        private static JObject ParseObject(string text)
        {
            return JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        private static string Scalar(JToken value, bool required = false)
        {
            if (value is null || value.Type == JTokenType.Null)
            {
                if (required)
                    throw new InvalidDataException("缺少 Mod 标识。");
                return null;
            }
            if (value.Type != JTokenType.String)
                throw new InvalidDataException("Mod 文本字段类型无效。");
            string text = (string)value;
            if (required && string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Mod 标识为空。");
            return text;
        }
        private static Identity ReadFabric(JObject json)
        {
            return new Identity() { Id = Scalar(json["id"], true), Name = Scalar(json["name"]), Version = Scalar(json["version"]), Description = Scalar(json["description"]), Loader = "Fabric", Icon = IconPath(json["icon"]) };
        }
        private static Identity ReadQuilt(JObject json)
        {
            JObject loader = json["quilt_loader"] as JObject;
            if (loader is null)
                throw new InvalidDataException("缺少 Quilt 加载器信息。");
            JObject metadata = loader["metadata"] as JObject;
            return new Identity() { Id = Scalar(loader["id"], true), Name = Scalar(metadata?["name"]), Version = Scalar(loader["version"]), Description = Scalar(metadata?["description"]), Loader = "Quilt", Icon = IconPath(metadata?["icon"]) };
        }
        private static Identity ReadLite(JObject json)
        {
            return new Identity() { Id = Scalar(json["name"], true), Name = Scalar(json["displayName"]) ?? Scalar(json["name"]), Version = Scalar(json["version"]), Description = Scalar(json["description"]), Loader = "LiteLoader", Icon = IconPath(json["logoFile"]) };
        }
        private static IEnumerable<Identity> ReadLegacy(string text)
        {
            var json = JToken.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            JArray items = json as JArray;
            if (items is null && json is JObject)
                items = json["modList"] as JArray;
            if (items is null || items.Count == 0 || items.Any(item => !(item is JObject)))
                throw new InvalidDataException("旧版 Mod 信息列表无效。");
            return items.OfType<JObject>().Select(item => new Identity() { Id = Scalar(item["modid"], true), Name = Scalar(item["name"]), Version = Scalar(item["version"]), Description = Scalar(item["description"]), Loader = "Forge", Icon = IconPath(item["logoFile"]) }).ToList();
        }
        private static string ResolveVersion(string value, Dictionary<string, string> manifest)
        {
            if (value == "${file.jarVersion}")
            {
                string resolved = null;
                if (manifest.TryGetValue("Implementation-Version", out resolved))
                    return resolved;
                return null;
            }
            return value;
        }
        private static Dictionary<string, string> ReadManifest(ZipArchiveEntry entry, CancellationToken token)
        {
            byte[] bytes = ReadBytes(entry, token);
            int offset = 0;
            bool hasHeader = false;
            // JAR manifests fold at byte boundaries, including within a UTF-8 code
            // point. Unfold the main section before decoding, as java.util.jar does.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                offset = 3;
            using (var unfolded = new MemoryStream())
            {
                while (offset < bytes.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int finish = offset;
                    while (finish < bytes.Length && bytes[finish] != 10 && bytes[finish] != 13)
                        finish += 1;
                    if (finish == offset)
                        break; // Named entry sections do not supply Mod metadata.
                    if (bytes[offset] == 32)
                    {
                        if (!hasHeader)
                            throw new InvalidDataException("JAR 清单续行缺少字段。");
                        unfolded.Write(bytes, offset + 1, finish - offset - 1);
                    }
                    else
                    {
                        if (hasHeader)
                            unfolded.WriteByte(10);
                        unfolded.Write(bytes, offset, finish - offset);
                        hasHeader = true;
                    }
                    offset = finish;
                    if (offset < bytes.Length && bytes[offset] == 13)
                        offset += 1;
                    if (offset < bytes.Length && bytes[offset] == 10)
                        offset += 1;
                }
                return ReadManifest(new UTF8Encoding(false, true).GetString(unfolded.ToArray()));
            }
        }
        private static Dictionary<string, string> ReadManifest(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string key = null;
            foreach (var line in text.Replace(Constants.vbCrLf, Constants.vbLf).Replace(Constants.vbCr, Constants.vbLf).Split('\n'))
            {
                if (line.Length == 0)
                    break; // Main attributes, never a named entry section.
                if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    if (key is null)
                        throw new InvalidDataException("JAR 清单续行缺少字段。");
                    result[key] += line.Substring(1);
                }
                else
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                        throw new InvalidDataException("JAR 清单字段无效。");
                    key = line.Substring(0, colon);
                    if (result.ContainsKey(key))
                        throw new InvalidDataException("JAR 清单字段重复。");
                    result.Add(key, line.Substring(colon + 1).TrimStart(' '));
                }
            }
            return result;
        }

        // Bounded TOML scalar/table reader used only for [[mods]] metadata. Values in
        // other tables are lexed (so strings/comments cannot fabricate a mods table)
        // but are never interpreted as dependency or compatibility declarations.
        private sealed class LocalModToml
        {
            internal static List<Identity> Read(string text, string loader)
            {
                var result = new List<Identity>();
                Dictionary<string, string> current = null;
                foreach (var statement in Statements(text))
                {
                    string line = statement.Trim();
                    if (line.Length == 0)
                        continue;
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        if (current is not null)
                            result.Add(ToIdentity(current, loader));
                        current = null;
                        string table = line.Trim('[', ']').Trim();
                        if (table == "mods" || table == "\"mods\"" || table == "'mods'")
                            current = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                    else if (current is not null)
                    {
                        int @equals = line.IndexOf('=');
                        if (equals < 1)
                            throw new InvalidDataException("Mod TOML 字段无效。");
                        string key = line.Substring(0, equals).Trim();
                        if (key.StartsWith("\"", StringComparison.Ordinal) || key.StartsWith("'", StringComparison.Ordinal))
                            key = StringValue(key);
                        if (!new[] { "modId", "displayName", "version", "description", "logoFile" }.Contains(key, StringComparer.Ordinal))
                            continue;
                        if (key == "logoFile")
                        {
                            try
                            {
                                if (current.ContainsKey(key))
                                    current[key] = null;
                                else
                                    current.Add(key, StringValue(line.Substring(equals + 1).Trim()));
                            }
                            catch (InvalidDataException)
                            {
                                current[key] = null;
                            }
                            continue;
                        }
                        if (current.ContainsKey(key))
                            throw new InvalidDataException("Mod TOML 字段重复：" + key);
                        current.Add(key, StringValue(line.Substring(equals + 1).Trim()));
                    }
                }
                if (current is not null)
                    result.Add(ToIdentity(current, loader));
                if (result.Count == 0)
                    throw new InvalidDataException("TOML 中没有有效的 mods 信息。");
                return result;
            }
            private static Identity ToIdentity(Dictionary<string, string> values, string loader)
            {
                string id = null;
                string name = null;
                string version = null;
                string description = null;
                string icon = null;
                if (!values.TryGetValue("modId", out id) || string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException("TOML 中缺少 Mod ID。");
                values.TryGetValue("displayName", out name);
                values.TryGetValue("version", out version);
                values.TryGetValue("description", out description);
                values.TryGetValue("logoFile", out icon);
                return new Identity() { Id = id, Name = name, Version = version, Description = description, Loader = loader, Icon = icon };
            }
            private static List<string> Statements(string text)
            {
                var result = new List<string>();
                var buffer = new StringBuilder();
                char quote = '\0';
                bool triple = false;
                bool comment = false;
                bool escaped = false;
                int depth = 0;
                int i = 0;
                while (i < text.Length)
                {
                    char ch = text[i];
                    if (comment)
                    {
                        if (ch != '\n' && ch != '\r')
                        {
                            i += 1;
                            continue;
                        }
                        comment = false;
                    }
                    if (quote != '\0')
                    {
                        if (escaped)
                        {
                            buffer.Append(ch);
                            escaped = false;
                        }
                        else if (quote == '"' && ch == '\\')
                        {
                            buffer.Append(ch);
                            escaped = true;
                        }
                        else if (ch == quote && (!triple || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                        {
                            int quoteCount = 1;
                            if (triple)
                            {
                                quoteCount = 3;
                                while (i + quoteCount < text.Length && text[i + quoteCount] == quote)
                                    quoteCount += 1;
                                // TOML permits one or two literal quotes immediately
                                // before a multiline string's three closing quotes.
                                if (quoteCount > 5)
                                    throw new InvalidDataException("TOML 多行字符串引号无效。");
                            }
                            buffer.Append(ch, quoteCount);
                            i += quoteCount - 1;
                            quote = '\0';
                            triple = false;
                        }
                        else
                        {
                            if (!triple && (ch == '\n' || ch == '\r'))
                                throw new InvalidDataException("TOML 单行字符串未结束。");
                            buffer.Append(ch);
                        }
                    }
                    else if (ch == '#')
                    {
                        comment = true;
                    }
                    else if (ch == '"' || ch == '\'')
                    {
                        quote = ch;
                        triple = i + 2 < text.Length && text[i + 1] == ch && text[i + 2] == ch;
                        buffer.Append(ch);
                        if (triple)
                        {
                            buffer.Append(ch).Append(ch);
                            i += 2;
                        }
                    }
                    else if (ch == '\n' || ch == '\r')
                    {
                        if (depth == 0)
                        {
                            result.Add(buffer.ToString());
                            buffer.Clear();
                        }
                        else
                        {
                            buffer.Append(ch);
                        }
                    }
                    else
                    {
                        if (ch == '[' || ch == '{')
                            depth += 1;
                        if (ch == ']' || ch == '}')
                            depth -= 1;
                        if (depth < 0)
                            throw new InvalidDataException("TOML 括号无效。");
                        buffer.Append(ch);
                    }
                    i += 1;
                }
                if (quote != '\0' || depth != 0)
                    throw new InvalidDataException("TOML 字符串或数组未结束。");
                if (buffer.Length > 0)
                    result.Add(buffer.ToString());
                return result;
            }
            private static string StringValue(string value)
            {
                if (value.Length < 2)
                    throw new InvalidDataException("TOML Mod 信息必须是字符串。");
                char quote = value[0];
                bool triple = value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal);
                if (quote != '"' && quote != '\'')
                    throw new InvalidDataException("TOML Mod 信息必须是字符串。");
                int edge = triple ? 3 : 1;
                if (value.Length < edge * 2 || (value.Substring(value.Length - edge) ?? "") != (new string(quote, edge) ?? ""))
                    throw new InvalidDataException("TOML 字符串未结束。");
                string inner = value.Substring(edge, value.Length - edge * 2).Replace(Constants.vbCrLf, Constants.vbLf).Replace(Constants.vbCr, Constants.vbLf);
                if (triple && inner.StartsWith(Constants.vbLf, StringComparison.Ordinal))
                    inner = inner.Substring(1);
                if (quote == '\'')
                    return inner;
                var result = new StringBuilder();
                int i = 0;
                while (i < inner.Length)
                {
                    char ch = inner[i];
                    if (ch != '\\')
                    {
                        result.Append(ch);
                    }
                    else
                    {
                        i += 1;
                        if (i >= inner.Length)
                            throw new InvalidDataException("TOML 转义未结束。");
                        switch (inner[i])
                        {
                            case 'b':
                                {
                                    result.Append('\b');
                                    break;
                                }
                            case 't':
                                {
                                    result.Append('\t');
                                    break;
                                }
                            case 'n':
                                {
                                    result.Append(Constants.vbLf);
                                    break;
                                }
                            case 'f':
                                {
                                    result.Append('\f');
                                    break;
                                }
                            case 'r':
                                {
                                    result.Append(Constants.vbCr);
                                    break;
                                }
                            case '"':
                                {
                                    result.Append('"');
                                    break;
                                }
                            case '\\':
                                {
                                    result.Append('\\');
                                    break;
                                }
                            case 'u':
                            case 'U':
                                {
                                    int count = inner[i] == 'u' ? 4 : 8;
                                    if (i + count >= inner.Length)
                                        throw new InvalidDataException("TOML Unicode 转义不完整。");
                                    int point;
                                    if (!int.TryParse(inner.Substring(i + 1, count), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out point))
                                        throw new InvalidDataException("TOML Unicode 转义无效。");
                                    if (point < 0 || point > 0x10FFFF || point >= 0xD800 && point <= 0xDFFF)
                                        throw new InvalidDataException("TOML Unicode 码点无效。");
                                    result.Append(char.ConvertFromUtf32(point));
                                    i += count;
                                    break;
                                }

                            case '\n':
                            case '\r':
                            case ' ':
                            case '\t':
                                {
                                    if (!triple)
                                        throw new InvalidDataException("TOML 单行字符串包含换行。");
                                    // A continuation backslash may be followed by spaces
                                    // or tabs, but must reach a newline before other text.
                                    bool hasNewline = inner[i] == '\n' || inner[i] == '\r';
                                    while (i + 1 < inner.Length && (inner[i + 1] == ' ' || inner[i + 1] == '\t' || inner[i + 1] == '\n' || inner[i + 1] == '\r'))
                                    {
                                        i += 1;
                                        hasNewline = hasNewline || inner[i] == '\n' || inner[i] == '\r';
                                    }
                                    if (!hasNewline)
                                        throw new InvalidDataException("TOML 字符串转义无效。");
                                    break;
                                }

                            default:
                                {
                                    throw new InvalidDataException("TOML 字符串转义无效。");
                                }
                        }
                    }
                    i += 1;
                }
                return result.ToString();
            }
        }
    }
}
