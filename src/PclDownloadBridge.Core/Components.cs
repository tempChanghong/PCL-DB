using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public enum ComponentKind
    {
        Forge,
        NeoForge,
        Fabric,
        FabricApi,
        OptiFine,
        OptiFabric,
        LiteLoader
    }

    public class ComponentEntry
    {
        public ComponentKind Kind { get; set; }
        public string Version { get; set; }
        public string Minecraft { get; set; }
        public bool Stable { get; set; } = true;
        public string Url { get; set; }
        public string FileName { get; set; }
        public string Sha1 { get; set; }
        public string Md5 { get; set; }
        public long Size { get; set; } = -1;
        public string RequiredForge { get; set; }
        public JObject Data { get; set; }
        public ComponentEntry Copy()
        {
            ComponentEntry result = (ComponentEntry)MemberwiseClone();
            result.Data = (JObject)Data?.DeepClone();
            return result;
        }
        public string Title
        {
            get
            {
                return Kind == ComponentKind.FabricApi || Kind == ComponentKind.OptiFabric ? (string)Data?["name"] : Version;
            }
        }
    }

    public class ComponentSelection
    {
        public readonly Dictionary<ComponentKind, ComponentEntry> Entries = new Dictionary<ComponentKind, ComponentEntry>();
        public string JavaPath { get; set; }
        public ComponentSelection Copy()
        {
            var result = new ComponentSelection() { JavaPath = JavaPath };
            foreach (KeyValuePair<ComponentKind, ComponentEntry> pair in Entries)
            {
                if (pair.Value is null)
                    throw new InvalidDataException("组件条目不能为空。");
                result.Entries.Add(pair.Key, pair.Value.Copy());
            }
            return result;
        }
        public ComponentEntry Item(ComponentKind kind)
        {
            ComponentEntry value = null;
            Entries.TryGetValue(kind, out value);
            return value;
        }
        public bool Has(ComponentKind kind)
        {
            return Entries.ContainsKey(kind);
        }
        public string Description
        {
            get
            {
                return string.Join(" + ", Entries.OrderBy(v => v.Key).Select(v => ComponentCatalog.Label(v.Key) + " " + v.Value.Version));
            }
        }
        // PCL PageDownloadInstall.Load*GetError and IsOptiFineSuitForForge.
        public string Unavailable(ComponentKind kind, string mc, ComponentEntry candidate = null)
        {
            if (kind == ComponentKind.Forge)
            {
                if (Has(ComponentKind.NeoForge))
                    return "与 NeoForge 不兼容";
                if (Has(ComponentKind.Fabric))
                    return "与 Fabric 不兼容";
                if (Has(ComponentKind.OptiFine) && candidate is not null && !OptiFineFits(Item(ComponentKind.OptiFine), candidate, mc))
                    return "仅兼容 OptiFine 指定的 Forge 版本";
            }
            else if (kind == ComponentKind.NeoForge)
            {
                if (Has(ComponentKind.Forge) || Has(ComponentKind.Fabric) || Has(ComponentKind.LiteLoader))
                    return "与已选模组加载器不兼容";
                if (Has(ComponentKind.OptiFine))
                    return "与 OptiFine 不兼容";
            }
            else if (kind == ComponentKind.Fabric)
            {
                if (Has(ComponentKind.Forge) || Has(ComponentKind.NeoForge) || Has(ComponentKind.LiteLoader))
                    return "与已选模组加载器不兼容";
                if (Has(ComponentKind.OptiFine) && CompareMc(mc, "1.20.4") > 0)
                    return "此版本的 Fabric 与 OptiFine 不兼容";
            }
            else if (kind == ComponentKind.LiteLoader)
            {
                if (Has(ComponentKind.Fabric) || Has(ComponentKind.NeoForge))
                    return "与已选模组加载器不兼容";
            }
            else if (kind == ComponentKind.FabricApi)
            {
                if (!Has(ComponentKind.Fabric))
                    return "需要安装 Fabric";
            }
            else if (kind == ComponentKind.OptiFabric)
            {
                if (!Has(ComponentKind.Fabric) || !Has(ComponentKind.OptiFine))
                    return "需要安装 OptiFine 与 Fabric";
                if (MinecraftVersionRules.ReleaseFamily(mc) < 160)
                    return "老版本需要手动安装 OptiFabric Origins";
            }
            else if (kind == ComponentKind.OptiFine)
            {
                if (Has(ComponentKind.NeoForge))
                    return "与 NeoForge 不兼容";
                if (Has(ComponentKind.Fabric) && CompareMc(mc, "1.20.4") > 0)
                    return "此版本的 OptiFine 与 Fabric 不兼容";
                if (Has(ComponentKind.Forge) && candidate is not null && !OptiFineFits(candidate, Item(ComponentKind.Forge), mc))
                    return "仅兼容特定版本的 Forge";
            }
            return null;
        }
        public void Validate(string mc)
        {
            foreach (var pair in Entries)
            {
                if (pair.Key != pair.Value.Kind || (pair.Value.Minecraft ?? "") != (mc ?? ""))
                    throw new InvalidDataException("组件不适用于所选 Minecraft 版本。");
                string issue = Unavailable(pair.Key, mc, pair.Value);
                if (issue is not null)
                    throw new InvalidDataException(ComponentCatalog.Label(pair.Key) + "：" + issue);
            }
            if (Has(ComponentKind.Fabric) && Has(ComponentKind.OptiFine) && !Has(ComponentKind.OptiFabric))
                throw new InvalidDataException("Fabric + OptiFine 需要选择兼容的 OptiFabric。");
        }
        public static int CompareMc(string a, string b)
        {
            if (a is null || b is null)
                return StringComparer.OrdinalIgnoreCase.Compare(a, b);
            // Reuse the fixed PCL MCUtils comparator, including numeric suffixes and pre/rc ordering.
            return PclLibrarySelection.CompareVersion(a, b);
        }
        public static bool OptiFineFits(ComponentEntry opt, ComponentEntry forge, string mc)
        {
            if (CompareMc(mc, "1.13") >= 0 && CompareMc(mc, "1.14.3") <= 0)
                return false;
            if (opt.RequiredForge is null)
                return false;
            if (opt.RequiredForge.Trim().Length == 0)
                return true;
            if (opt.RequiredForge.Contains("."))
                return (forge.Version ?? "") == (opt.RequiredForge ?? "");
            return (forge.Version.Split('.').Last() ?? "") == (opt.RequiredForge ?? "");
        }
    }

    // Primary references: PCL ModDownload Dl*List and McDownload*Loader at 5ef81e4.
    public class ComponentCatalog
    {
        private readonly SourcePolicy _policy;
        public ComponentCatalog(SourcePolicy policy)
        {
            _policy = policy;
        }
        public static string Label(ComponentKind kind)
        {
            return kind == ComponentKind.FabricApi ? "Fabric API" : kind.ToString();
        }
        public static string Icon(ComponentKind kind)
        {
            switch (kind)
            {
                case ComponentKind.Forge:
                    {
                        return "Anvil";
                    }
                case ComponentKind.NeoForge:
                    {
                        return "NeoForge";
                    }
                case ComponentKind.Fabric:
                case ComponentKind.FabricApi:
                    {
                        return "Fabric";
                    }
                case ComponentKind.OptiFine:
                    {
                        return "GrassPath";
                    }
                case ComponentKind.OptiFabric:
                    {
                        return "OptiFabric";
                    }

                default:
                    {
                        return "Egg";
                    }
            }
        }
        public async Task<List<ComponentEntry>> LoadAsync(ComponentKind kind, string mc, CancellationToken token)
        {
            var result = new List<ComponentEntry>();
            switch (kind)
            {
                case ComponentKind.Fabric:
                    {
                        var data = await _policy.GetVersionListTokenAsync("https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mc), token, ParseFabricMetadata).ConfigureAwait(false);
                        foreach (var item in data)
                            result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = (string)item["loader"]["version"], Stable = (bool)item["loader"]["stable"] });
                        break;
                    }
                case ComponentKind.FabricApi:
                    {
                        string project = "P7dR8mSH";
                        string url = "https://api.modrinth.com/v2/project/" + project + "/version?loaders=" + Uri.EscapeDataString("[\"fabric\"]") + "&game_versions=" + Uri.EscapeDataString(new JArray(mc).ToString(Newtonsoft.Json.Formatting.None));
                        var data = await _policy.GetVersionListTokenAsync(url, token, body => ParseFabricApiMetadata(body, mc, _policy.AllowLoopback)).ConfigureAwait(false);
                        foreach (JObject item in data)
                        {
                            var file = SelectFabricApiFile(item);
                            result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = (string)item["version_number"], Stable = (string)item["version_type"] == "release", Url = (string)file["url"], FileName = (string)file["filename"], Sha1 = (string)file["hashes"]["sha1"], Size = (long)file["size"], Data = item });
                        }

                        break;
                    }
                case ComponentKind.OptiFabric:
                    {
                        // PCL DlOptiFabricLoader uses CurseForge project 322385, not a
                        // similarly-named Modrinth project. No upstream API credentials used.
                        if (MinecraftVersionRules.ReleaseFamily(mc) < 160 || ComponentSelection.CompareMc(mc, "1.20.4") > 0)
                            return result;
                        int offset = 0;
                        var seen = new HashSet<long>();
                        do
                        {
                            string publicUrl = "https://www.curseforge.com/api/v1/mods/322385/files?pageSize=50&pageIndex=" + offset / 50;
                            string mirrorUrl = "https://mod.mcimirror.top/curseforge/v1/mods/322385/files?pageSize=50&index=" + offset;
                            string[] sources = _policy.EffectiveVersionPreference != SourcePreference.MirrorFirst ? (new[] { publicUrl, mirrorUrl }) : (new[] { mirrorUrl, publicUrl });
                            long[] acceptedIds = seen.ToArray();
                            var data = await _policy.RaceVersionListTokenAsync(sources.SelectMany(u => _policy.Candidates(u, "version")).Distinct(), token, body => ParseOptiFabricPage(body, mc, acceptedIds)).ConfigureAwait(false);
                            JArray files = (JArray)data["data"];
                            foreach (JObject item in files)
                            {
                                if (!seen.Add((long)item["id"]))
                                    throw new InvalidDataException("OptiFabric 列表返回了重复的分页。");
                                if (!item["gameVersions"].Values<string>().Contains(mc))
                                    continue;
                                if (item["projectId"] is not null && (int)item["projectId"] != 322385)
                                    throw new InvalidDataException("OptiFabric 项目编号不匹配。");
                                long id = (long)item["id"];
                                string fileName = (string)item["fileName"];
                                var hash = SelectOptiFabricHash(item);
                                int algorithm = hash is null ? 0 : (int)hash["algo"];
                                string checksum = ((string)hash?["value"])?.ToLowerInvariant();
                                string url = (string)item["downloadUrl"];
                                if (string.IsNullOrWhiteSpace(url))
                                    url = "https://edge.forgecdn.net/files/" + id / 1000L + "/" + id % 1000L + "/" + Uri.EscapeDataString(fileName);
                                string version = fileName.Replace("optifabric-", "").Replace(".jar", "").TrimStart('v');
                                item["name"] = (string)item["displayName"];
                                long size = (long)item["fileLength"];
                                result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = version, Stable = (int)item["releaseType"] == 1, Url = url, FileName = fileName, Sha1 = algorithm == 1 ? checksum : null, Md5 = algorithm == 2 ? checksum : null, Size = size == 0L ? -1L : size, Data = item });
                            }
                            offset += files.Count;
                            if (files.Count < 50)
                                break;
                            if (offset >= 2000)
                                throw new InvalidDataException("OptiFabric 文件列表超过合理范围。");
                        }
                        while (true);
                        break;
                    }
                case ComponentKind.Forge:
                    {
                        var data = await _policy.GetVersionListAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", "https://bmclapi2.bangbang93.com/forge/minecraft/" + Uri.EscapeDataString(mc.Replace("-", "_")), token, body => ParseForgeMetadata(body, mc), ParseForgeMirrorMetadata).ConfigureAwait(false);
                        var coordinates = new HashSet<string>(StringComparer.Ordinal);
                        if (data is not null)
                        {
                            foreach (var item in data)
                            {
                                var file = item["files"].FirstOrDefault(f => (string)f["category"] == "installer" && (string)f["format"] == "jar");
                                if (file is null)
                                    continue;
                                var release = ForgeRelease.Parse((string)item["version"], (string)item["branch"], mc);
                                if (release is null || !coordinates.Add(release.FileVersion))
                                    continue;
                                string coordinate = mc.Replace("-", "_") + "-" + release.FileVersion;
                                string checksum = (string)file["hash"];
                                ComponentEntry @init = new ComponentEntry();
                                result.Add((@init.Kind = kind, @init.Minecraft = mc, @init.Version = release.VersionName, @init.Url = "https://maven.minecraftforge.net/net/minecraftforge/forge/" + coordinate + "/forge-" + coordinate + "-installer.jar", @init.Sha1 = (checksum?.Length) is { } arg1 && arg1 == 40 ? checksum : null, @init.Md5 = (checksum?.Length) is { } arg2 && arg2 == 32 ? checksum : null, @init.Data = (JObject)item, @init).@init);
                            }
                        }
                        result = result.OrderByDescending(v => SortKey(v.Version), StringComparer.Ordinal).ToList();
                        break;
                    }
                case ComponentKind.NeoForge:
                    {
                        if (ComponentSelection.CompareMc(mc, "1.20.1") < 0)
                            return result;
                        string package = mc == "1.20.1" ? "forge" : "neoforge";
                        Func<string, JToken> parse = body => ParseNeoForgeMetadata(body, package == "forge");
                        var data = await _policy.GetVersionListAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/" + package, "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/" + package, token, parse, parse).ConfigureAwait(false);
                        var releases = data["versions"].Values<string>().Select(value => NeoForgeRelease.Parse(value, package == "forge")).Where(value => value is not null && (value.Minecraft ?? "") == (mc ?? "")).OrderByDescending(value => value.NumericVersion).ThenByDescending(value => SortKey(value.VersionName), StringComparer.OrdinalIgnoreCase);
                        foreach (var release in releases)
                            result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = release.VersionName, Stable = release.Stable, Url = "https://maven.neoforged.net/releases/net/neoforged/" + package + "/" + release.ApiName + "/" + package + "-" + release.ApiName + "-installer.jar" });
                        break;
                    }
                case ComponentKind.OptiFine:
                    {
                        var data = await _policy.GetVersionListAsync("https://optifine.net/downloads", "https://bmclapi2.bangbang93.com/optifine/versionList", token, body => ParseOptiFinePage(body), ParseOptiFineMirrorMetadata).ConfigureAwait(false);
                        foreach (JObject item in data)
                        {
                            if ((((string)item["mcversion"]).Replace("1.8.0", "1.8").Replace("1.9.0", "1.9") ?? "") != (mc ?? ""))
                                continue;
                            string required = ((string)item["forge"] ?? "").Replace("Forge ", "").Replace("#", "");
                            if (required.Contains("N/A"))
                                required = null;
                            result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = (((string)item["type"]).Replace("HD_U", "").Trim('_') + " " + (string)item["patch"]).Trim(), Stable = !((string)item["patch"]).Contains("pre"), Url = "https://bmclapi2.bangbang93.com/optifine/" + (string)item["mcversion"] + "/" + (string)item["type"] + "/" + (string)item["patch"], FileName = (string)item["filename"], RequiredForge = required, Data = item });
                        }
                        result = result.OrderByDescending(v => v.Stable).ThenByDescending(v => SortKey(v.Version), StringComparer.Ordinal).ToList();
                        break;
                    }
                case ComponentKind.LiteLoader:
                    {
                        if (ComponentSelection.CompareMc(mc, "1.12.2") > 0 || mc.StartsWith("1.5", StringComparison.Ordinal) || mc.StartsWith("1.6", StringComparison.Ordinal))
                            return result;
                        var data = await _policy.GetVersionListTokenAsync("https://dl.liteloader.com/versions/versions.json", token, body => ParseLiteLoaderMetadata(body, mc)).ConfigureAwait(false);
                        var game = data["versions"]?[mc];
                        var latest = ((game?["artefacts"]) ?? (game?["snapshots"]))?["com.mumfrey:liteloader"]?["latest"];
                        if (latest is not null)
                            result.Add(new ComponentEntry() { Kind = kind, Minecraft = mc, Version = (string)latest["version"], Stable = !string.Equals((string)latest["stream"], "SNAPSHOT", StringComparison.OrdinalIgnoreCase), Data = (JObject)latest });
                        break;
                    }
            }
            return result;
        }

        // PCL's per-source loaders reject missing required fields before reporting
        // success. Preserve valid empty arrays and ignore unrelated extra fields.
        private static JToken ParseFabricMetadata(string body)
        {
            var entries = JArray.Parse(body);
            foreach (var value in entries)
            {
                JObject item = value as JObject;
                var loader = item is null ? null : item["loader"] as JObject;
                if (loader is null)
                    throw new InvalidDataException("Fabric 版本条目缺少 loader 对象。");
                ValidateCatalogueText(loader, "version", "Fabric");
                var stable = loader["stable"];
                if (stable is null || stable.Type != JTokenType.Boolean)
                    throw new InvalidDataException("Fabric 版本条目的 stable 字段无效。");
            }
            return entries;
        }

        private static JToken ParseFabricApiMetadata(string body, string mc, bool allowLoopback)
        {
            JArray entries;
            // Keep the original UTC/offset strings. The default DateTime conversion
            // followed by a string cast can lose the zone before publication sorting.
            using (var input = new StringReader(body))
            using (var reader = new Newtonsoft.Json.JsonTextReader(input) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
            {
                entries = JArray.Load(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != Newtonsoft.Json.JsonToken.Comment)
                        throw new InvalidDataException("Fabric API 版本列表含有多余内容。");
                }
            }
            var accepted = new List<JObject>();
            foreach (var value in entries)
            {
                JObject item = value as JObject;
                if (item is null)
                    throw new InvalidDataException("Fabric API 版本条目格式无效。");
                ValidateCatalogueText(item, "project_id", "Fabric API");
                if ((string)item["project_id"] != "P7dR8mSH")
                    throw new InvalidDataException("Fabric API 项目编号不匹配。");
                // Like PCL's compatibility filter, verify each result before listing it.
                // This endpoint supplies exact game/loader arrays instead of relying
                // on the display-name version ranges used by PCL's global catalogue.
                if (!CatalogueContains(item, "game_versions", mc) || !CatalogueContains(item, "loaders", "fabric"))
                    continue;
                foreach (var @field in new[] { "name", "version_number", "version_type" })
                    ValidateCatalogueText(item, @field, "Fabric API");
                var published = item["date_published"];
                DateTimeOffset instant;
                if (published is null || published.Type != JTokenType.String || !DateTimeOffset.TryParse((string)published, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out instant))
                    throw new InvalidDataException("Fabric API 发布时间无效。");
                var file = SelectFabricApiFile(item);
                if (file is null)
                    continue; // PCL also omits versions without a download.
                SafeFiles.ValidatePathSegment((string)file["filename"]);
                ValidateCatalogueText(file, "url", "Fabric API");
                Uri address = null;
                if (!Uri.TryCreate((string)file["url"], UriKind.Absolute, out address) || address.Scheme != "https" && !(allowLoopback && address.IsLoopback && address.Scheme == "http"))
                    throw new InvalidDataException("Fabric API 下载地址无效。");
                ReadCatalogueInteger(file, "size", "Fabric API", 1L);
                JObject hashes = file["hashes"] as JObject;
                if (hashes is null)
                    throw new InvalidDataException("Fabric API 校验信息格式无效。");
                var sha1 = hashes["sha1"];
                if (sha1 is not null && sha1.Type != JTokenType.Null && (sha1.Type != JTokenType.String || !Regex.IsMatch((string)sha1, @"\A[0-9a-fA-F]{40}\z")))
                    throw new InvalidDataException("Fabric API SHA-1 校验字段无效。");
                accepted.Add(item);
            }
            // PCL FabricApi_Loaded orders by publication time before auto-selection.
            return new JArray(accepted.OrderByDescending(item => DateTimeOffset.Parse((string)item["date_published"], System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static bool CatalogueContains(JObject item, string @field, string expected)
        {
            JArray values = item[@field] as JArray;
            if (values is null || values.Any(value => value.Type != JTokenType.String))
                throw new InvalidDataException("Fabric API 的 " + @field + " 字段无效。");
            return values.Values<string>().Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static JObject SelectFabricApiFile(JObject item)
        {
            JArray files = item["files"] as JArray;
            if (files is null)
                throw new InvalidDataException("Fabric API 文件列表格式无效。");
            JObject primary = null;
            JObject fallback = null;
            foreach (var value in files)
            {
                JObject file = value as JObject;
                if (file is null)
                    throw new InvalidDataException("Fabric API 文件条目格式无效。");
                ValidateCatalogueText(file, "filename", "Fabric API");
                var selected = file["primary"];
                if (selected is not null && selected.Type != JTokenType.Null && selected.Type != JTokenType.Boolean)
                    throw new InvalidDataException("Fabric API primary 字段无效。");
                if (selected is not null && selected.Type == JTokenType.Boolean && (bool)selected)
                {
                    if (primary is not null)
                        throw new InvalidDataException("Fabric API 声明了多个主文件。");
                    primary = file;
                }
                if (fallback is null && IsRuntimeApiJar((string)file["filename"]))
                    fallback = file;
            }
            if (primary is null)
                return fallback;
            if (!IsRuntimeApiJar((string)primary["filename"]))
                throw new InvalidDataException("Fabric API 主文件不是运行用 JAR。");
            return primary;
        }

        private static bool IsRuntimeApiJar(string filename)
        {
            return filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase);
        }

        private static JToken ParseOptiFineMirrorMetadata(string body)
        {
            var entries = JArray.Parse(body);
            foreach (var value in entries)
            {
                JObject item = value as JObject;
                if (item is null)
                    throw new InvalidDataException("OptiFine 版本条目格式无效。");
                foreach (var @field in new[] { "mcversion", "type", "patch", "filename" })
                    ValidateCatalogueText(item, @field, "OptiFine");
                var forge = item["forge"];
                // PCL accepts both dotted Forge versions and legacy numeric builds.
                if (forge is not null && forge.Type != JTokenType.Null && forge.Type != JTokenType.String && forge.Type != JTokenType.Integer)
                    throw new InvalidDataException("OptiFine 版本条目的 forge 字段无效。");
            }
            return entries;
        }

        private static void ValidateCatalogueText(JObject item, string @field, string catalogue)
        {
            var value = item[@field];
            if (value is null || value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value))
                throw new InvalidDataException(catalogue + " 版本条目的 " + @field + " 字段无效。");
        }

        private static JObject SelectOptiFabricHash(JObject item)
        {
            // PCL ResourceVersion: prefer SHA-1, otherwise use the declared MD5.
            JArray hashes = item["hashes"] as JArray;
            if (hashes is null)
                return null;
            return (JObject)(hashes.FirstOrDefault(hash => (int)hash["algo"] == 1) ?? hashes.FirstOrDefault(hash => (int)hash["algo"] == 2));
        }

        private static JToken ParseOptiFabricPage(string body, string mc, IEnumerable<long> acceptedIds)
        {
            var data = JObject.Parse(body);
            JArray files = data["data"] as JArray;
            if (files is null || files.Count > 50)
                throw new InvalidDataException("OptiFabric 分页格式或大小无效。");
            // Each racing source owns its tentative IDs. Only the accepted page may
            // update the caller's set after all losing requests have finished.
            var ids = new HashSet<long>(acceptedIds);
            foreach (var value in files)
            {
                JObject item = value as JObject;
                if (item is null)
                    throw new InvalidDataException("OptiFabric 文件条目格式无效。");
                long id = ReadCatalogueInteger(item, "id", "OptiFabric", 1L);
                if (!ids.Add(id))
                    throw new InvalidDataException("OptiFabric 列表返回了重复的分页。");
                JArray games = item["gameVersions"] as JArray;
                if (games is null || games.Any(game => game.Type != JTokenType.String))
                    throw new InvalidDataException("OptiFabric 游戏版本列表格式无效。");
                if (!games.Values<string>().Contains(mc))
                    continue;
                if (item["projectId"] is not null && ReadCatalogueInteger(item, "projectId", "OptiFabric", 1L) != 322385L)
                    throw new InvalidDataException("OptiFabric 项目编号不匹配。");
                ValidateCatalogueText(item, "fileName", "OptiFabric");
                ValidateCatalogueText(item, "displayName", "OptiFabric");
                ReadCatalogueInteger(item, "fileLength", "OptiFabric", -1);
                if (ReadCatalogueInteger(item, "releaseType", "OptiFabric") > int.MaxValue)
                    throw new InvalidDataException("OptiFabric 发布类型无效。");
                var url = item["downloadUrl"];
                if (url is not null && url.Type != JTokenType.Null && url.Type != JTokenType.String)
                    throw new InvalidDataException("OptiFabric 下载地址字段无效。");
                var hashes = item["hashes"];
                if (hashes is null || hashes.Type == JTokenType.Null)
                    continue;
                if (!(hashes is JArray))
                    throw new InvalidDataException("OptiFabric 校验列表格式无效。");
                foreach (var hashValue in hashes)
                {
                    JObject hash = hashValue as JObject;
                    if (hash is null)
                        throw new InvalidDataException("OptiFabric 校验条目格式无效。");
                    long algorithm = ReadCatalogueInteger(hash, "algo", "OptiFabric");
                    if (algorithm > int.MaxValue)
                        throw new InvalidDataException("OptiFabric 校验类型无效。");
                    if (algorithm != 1L)
                        continue;
                    ValidateCatalogueText(hash, "value", "OptiFabric");
                    if (!Regex.IsMatch((string)hash["value"], @"\A[0-9a-fA-F]{40}\z"))
                        throw new InvalidDataException("OptiFabric SHA-1 校验字段无效。");
                }
                var selected = SelectOptiFabricHash(item);
                if (selected is not null && (int)selected["algo"] == 2)
                {
                    ValidateCatalogueText(selected, "value", "OptiFabric");
                    if (!Regex.IsMatch((string)selected["value"], @"\A[0-9a-fA-F]{32}\z"))
                        throw new InvalidDataException("OptiFabric MD5 校验字段无效。");
                }
            }
            return data;
        }

        private static long ReadCatalogueInteger(JObject item, string @field, string catalogue, long minimum = 0L)
        {
            var value = item[@field];
            if (value is null || value.Type != JTokenType.Integer || (long)value < minimum)
                throw new InvalidDataException(catalogue + " 版本条目的 " + @field + " 字段无效。");
            return (long)value;
        }

        private static JToken ParseLiteLoaderMetadata(string body, string mc)
        {
            var data = JObject.Parse(body);
            JObject versions = data["versions"] as JObject;
            if (versions is null)
                throw new InvalidDataException("LiteLoader 缺少 versions 对象。");
            if (versions[mc] is null)
                return data;
            JObject game = versions[mc] as JObject;
            var releases = game is null ? null : (game["artefacts"] ?? game["snapshots"]) as JObject;
            var package = releases is null ? null : releases["com.mumfrey:liteloader"] as JObject;
            var latest = package is null ? null : package["latest"] as JObject;
            if (latest is null)
                throw new InvalidDataException("LiteLoader 所选游戏版本缺少 latest 对象。");
            foreach (var @field in new[] { "version", "stream", "tweakClass" })
                ValidateCatalogueText(latest, @field, "LiteLoader");
            JArray libraries = latest["libraries"] as JArray;
            if (libraries is null || libraries.Any(library => !(library is JObject)))
                throw new InvalidDataException("LiteLoader 支持库列表格式无效。");
            return data;
        }

        private static JToken ParseForgeMirrorMetadata(string body)
        {
            var entries = JArray.Parse(body);
            foreach (var value in entries)
            {
                JObject item = value as JObject;
                if (item is null)
                    throw new InvalidDataException("Forge 版本条目格式无效。");
                JArray files = item["files"] as JArray;
                if (files is null)
                    throw new InvalidDataException("Forge 文件列表格式无效。");
                foreach (JObject file in files)
                {
                    if ((string)file["category"] != "installer" || (string)file["format"] != "jar")
                        continue;
                    var hash = file["hash"];
                    if (hash is null || hash.Type == JTokenType.Null)
                        continue;
                    if (hash.Type != JTokenType.String)
                        throw new InvalidDataException("Forge 安装器校验字段格式无效。");
                    string checksum = ((string)hash).Trim();
                    if (checksum.Length > 0 && !ForgelikeInstallerChecksum.IsChecksum(checksum))
                        throw new InvalidDataException("Forge 安装器校验字段格式无效。");
                    file["hash"] = checksum;
                }
            }
            return entries;
        }

        private static JToken ParseForgeMetadata(string body, string mc)
        {
            var xml = XDocument.Parse(body);
            if (xml.Root is null || xml.Root.Name.LocalName != "metadata")
                throw new InvalidDataException("Forge Maven 版本列表无效。");
            var result = new JArray();
            string game = mc.Replace("-", "_");
            foreach (var value in xml.Descendants("version").Select(x => x.Value).Where(v => v.StartsWith(game + "-", StringComparison.Ordinal)))
            {
                var release = ForgeRelease.Parse(value.Substring(game.Length + 1), null, mc);
                if (release is null)
                    continue;
                result.Add(new JObject() { { "version", release.VersionName }, { "branch", release.Branch }, { "files", new JArray(new JObject() { { "category", "installer" }, { "format", "jar" } }) } });
            }
            return result;
        }
        private static JToken ParseNeoForgeMetadata(string body, bool legacy = false)
        {
            // Same version-name extraction as PCL GetNeoForgeEntries, across official
            // versions arrays and BMCLAPI's Maven details response.
            JToken.Parse(body);
            var values = Regex.Matches(body, "(?<=\")" + NeoForgeRelease.Pattern + "(?=\")", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Value).Where(value => NeoForgeRelease.Parse(value, legacy) is not null).Distinct().ToList();
            if (values.Count == 0)
                throw new InvalidDataException("NeoForge 版本列表为空或格式无效。");
            return new JObject() { { "versions", new JArray(values) } };
        }

        public static string SortKey(string value)
        {
            return Regex.Replace(value, @"\d+", m => m.Value.PadLeft(8, '0'));
        }
        public static JArray ParseOptiFinePage(string page)
        {
            // PCL DlOptiFineListOfficialMain: official filenames and colForge values.
            var names = Regex.Matches(page, @"(?<=OptiFine_)[0-9A-Za-z_.]+(?=\.jar[""'])").Cast<Match>().Select(m => m.Value).ToList();
            var forge = Regex.Matches(page, "(?<=colForge'>)[^<]*").Cast<Match>().Select(m => m.Value).ToList();
            if (names.Count == 0)
                throw new InvalidDataException("OptiFine 官方版本列表解析失败。");
            var entries = new JArray();
            var seen = new HashSet<string>();
            for (int i = 0, loopTo = names.Count - 1; i <= loopTo; i++)
            {
                string[] parts = names[i].Split('_');
                if (parts.Length < 4 || !seen.Add(names[i]))
                    continue;
                bool preview = names[i].Contains("_pre");
                string @type = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                string fileName = (preview ? "preview_" : "") + "OptiFine_" + names[i] + ".jar";
                entries.Add(new JObject() { { "mcversion", parts[0] }, { "type", type }, { "patch", parts.Last() }, { "filename", fileName }, { "forge", forge.Count == names.Count ? forge[i] : "N/A" } });
            }
            return entries;
        }
    }
}