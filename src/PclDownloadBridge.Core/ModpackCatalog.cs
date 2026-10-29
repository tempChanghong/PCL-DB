using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    [Flags]
    public enum ModpackPlatform
    {
        CurseForge = 1,
        Modrinth = 2,
        All = 3
    }

    // PCL 5ef81e4 PageResource / ResourceSearcher / ResourceVersion. Catalogue
    // identities are platform-qualified and never inferred from a display name.
    public sealed class ModpackQuery
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public string Loader { get; set; } = "";
        public string Text { get; set; } = "";
        public string Minecraft { get; set; } = "";
        public string Category { get; set; } = "/";
        public ModpackPlatform Platforms { get; set; } = ModpackPlatform.All;
        public ModpackQuery Copy()
        {
            return (ModpackQuery)MemberwiseClone();
        }
    }

    public sealed class ModpackProject
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public string PreferredVersionId { get; set; }
        public ModpackPlatform Platform { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string Summary { get; set; }
        public string IconUrl { get; set; }
        public string Website { get; set; }
        public long Downloads { get; set; }
        public DateTimeOffset Updated { get; set; }
        public string[] GameVersions { get; set; } = Array.Empty<string>();
        // Advisory project declarations. File compatibility and install identity
        // continue to come exclusively from the selected ModpackRelease/archive.
        public string[] Tags { get; set; } = Array.Empty<string>();
        public string[] Loaders { get; set; } = Array.Empty<string>();
        public string Key
        {
            get
            {
                return Platform.ToString() + ":" + Id + (Kind == CommunityResourceKind.Modpack ? "" : ":" + Kind.ToString());
            }
        }
    }

    public sealed class ModpackRelease
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public ResourceDependency[] Dependencies { get; set; } = Array.Empty<ResourceDependency>();
        public ModpackPlatform Platform { get; set; }
        public string ProjectId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public DateTimeOffset Published { get; set; }
        // Advisory platform statistics; absent or malformed counters cannot invalidate
        // an otherwise verified archive. A known zero remains distinct from unknown.
        public long? Downloads { get; set; }
        public string ReleaseType { get; set; }
        public string[] GameVersions { get; set; } = Array.Empty<string>();
        // Missing compatibility metadata may defer to the archive, but an explicitly
        // unusable declaration must remain distinguishable from missing metadata.
        public string GameVersionMetadataError { get; set; }
        public string[] Loaders { get; set; } = Array.Empty<string>();
        public string FileName { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public string Md5 { get; set; }
        public ModpackRelease Copy()
        {
            ModpackRelease result = (ModpackRelease)MemberwiseClone();
            result.GameVersions = (string[])GameVersions.Clone();
            result.Loaders = (string[])Loaders.Clone();
            result.Dependencies = Dependencies.Select(item => new ResourceDependency() { ProjectId = item.ProjectId, VersionId = item.VersionId }).ToArray();
            return result;
        }
    }

    public sealed class ModpackCatalogPage
    {
        public List<ModpackProject> Projects { get; set; } = new List<ModpackProject>();
        public int Returned { get; set; }
        public long Total { get; set; }
    }

    public sealed partial class ModpackCatalogClient : IDisposable
    {
        public const int PageSize = 40;
        private readonly HttpClient _client;
        private readonly string _key;
        private readonly int _communitySource;
        public ModpackCatalogClient(HttpMessageHandler handler = null, string apiKey = null, int communitySource = 2)
        {
            _client = new HttpClient(handler ?? new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, MaxConnectionsPerServer = 4 }) { Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/8.0.0-beta.1 (modpack catalogue)");
            _key = apiKey ?? Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY");
            if (communitySource < 0 || communitySource > 2)
                throw new ArgumentOutOfRangeException(nameof(communitySource));
            _communitySource = communitySource;
        }

        public static string SearchUrl(ModpackPlatform platform, ModpackQuery query, int offset)
        {
            if (query is not null)
            {
                CommunityResources.Name(query.Kind);
                if (query.Loader is null || !new[] { "", "fabric", "forge", "neoforge", "quilt", "liteloader" }.Contains(query.Loader, StringComparer.Ordinal))
                    throw new ArgumentException("加载器无效。");
            }
            if (query is null || offset < 0 || query.Text is null || query.Category is null || query.Minecraft is null || (int)query.Platforms < 1 || (int)query.Platforms > 3)
                throw new ArgumentException("搜索条件无效。");
            string[] categories = query.Category.Split('/');
            if (categories.Length != 2 || query.Text.Length > 256 || query.Minecraft.Length > 100)
                throw new ArgumentException("搜索条件过长或分类无效。");
            string text = Uri.EscapeDataString(query.Text.Trim());
            string game = Uri.EscapeDataString(query.Minecraft.Trim());
            if (platform == ModpackPlatform.CurseForge)
            {
                string url = "https://api.curseforge.com/v1/mods/search?gameId=432&classId=" + CommunityResources.ClassId(query.Kind).ToString() + "&sortField=2&sortOrder=desc&pageSize=40&index=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(query.Loader))
                    url += "&modLoaderType=" + new Dictionary<string, string>() { { "forge", "1" }, { "liteloader", "3" }, { "fabric", "4" }, { "quilt", "5" }, { "neoforge", "6" } }[query.Loader];
                if (!string.IsNullOrEmpty(text))
                    url += "&searchFilter=" + text;
                if (!string.IsNullOrEmpty(game))
                    url += "&gameVersion=" + game;
                if (!string.IsNullOrEmpty(categories[0]))
                    url += "&categoryId=" + Uri.EscapeDataString(categories[0]);
                return url;
            }
            if (platform != ModpackPlatform.Modrinth)
                throw new ArgumentException("请选择一个内容平台。");
            var facets = new JArray();
            facets.Add(new JArray("project_type:" + CommunityResources.ProjectType(query.Kind)));
            if (!string.IsNullOrEmpty(query.Loader))
                facets.Add(new JArray("categories:" + query.Loader));
            if (!string.IsNullOrEmpty(categories[1]))
                facets.Add(new JArray("categories:" + categories[1]));
            if (!string.IsNullOrEmpty(game))
                facets.Add(new JArray("versions:" + query.Minecraft.Trim()));
            return "https://api.modrinth.com/v2/search?limit=40&index=relevance&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&query=" + text + "&facets=" + Uri.EscapeDataString(facets.ToString(Newtonsoft.Json.Formatting.None));
        }

        public async Task<ModpackCatalogPage> SearchAsync(ModpackPlatform platform, ModpackQuery query, int offset, CancellationToken token)
        {
            var data = await JsonAsync(SearchUrl(platform, query, offset), token).ConfigureAwait(false);
            var items = ArrayValue(data, platform == ModpackPlatform.Modrinth ? "hits" : "data");
            if (items.Count > PageSize)
                throw new InvalidDataException("搜索返回的条目数超过请求上限。");
            var result = new ModpackCatalogPage() { Returned = items.Count };
            result.Total = platform == ModpackPlatform.Modrinth ? Number(data, "total_hits") : Number(ObjectValue(data, "pagination"), "totalCount");
            foreach (var item in items.OfType<JObject>())
            {
                if (!CommunityResources.Matches(platform, item, query.Kind))
                    continue;
                var project = ParseProject(platform, item, true, query.Kind);
                FixResourceWebsite(project, item);
                result.Projects.Add(project);
            }
            return result;
        }

        public async Task<ModpackProject> GetProjectAsync(ModpackPlatform platform, string id, CancellationToken token, CommunityResourceKind kind = CommunityResourceKind.Modpack)
        {
            ValidateId(platform, id);
            string url = (platform == ModpackPlatform.Modrinth ? "https://api.modrinth.com/v2/project/" : "https://api.curseforge.com/v1/mods/") + id;
            var data = await JsonAsync(url, token).ConfigureAwait(false);
            if (platform == ModpackPlatform.CurseForge)
                data = ObjectValue(data, "data");
            if (!CommunityResources.Matches(platform, data, kind))
                throw new InvalidDataException("项目的资源类型不匹配。");
            var result = ParseProject(platform, data, false, kind);
            FixResourceWebsite(result, data);
            if ((result.Id ?? "") != (id ?? ""))
                throw new InvalidDataException("返回的整合包项目 ID 不匹配。");
            return result;
        }

        private static void FixResourceWebsite(ModpackProject project, JObject data)
        {
            if (project.Kind == CommunityResourceKind.Modpack)
                return;
            string slug = Uri.EscapeDataString(Text(data, "slug"));
            if (project.Platform == ModpackPlatform.Modrinth)
            {
                project.Website = "https://modrinth.com/" + (project.Kind == CommunityResourceKind.DataPack ? "datapack" : CommunityResources.ProjectType(project.Kind)) + "/" + slug;
            }
            else
            {
                string section = project.Kind == CommunityResourceKind.Mod ? "mc-mods" : project.Kind == CommunityResourceKind.ResourcePack ? "texture-packs" : project.Kind == CommunityResourceKind.Shader ? "shaders" : "data-packs";
                project.Website = "https://www.curseforge.com/minecraft/" + section + "/" + slug;
            }
        }

        public async Task<ModpackProject> ResolveDependencyAsync(ModpackPlatform platform, ResourceDependency dependency, CancellationToken token)
        {
            string id = dependency.ProjectId;
            var kind = CommunityResourceKind.Any;
            if (platform == ModpackPlatform.Modrinth && !string.IsNullOrEmpty(dependency.VersionId))
            {
                ValidateId(platform, dependency.VersionId);
                var version = await JsonAsync("https://api.modrinth.com/v2/version/" + dependency.VersionId, token).ConfigureAwait(false);
                if ((Text(version, "id") ?? "") != (dependency.VersionId ?? "") || !string.IsNullOrEmpty(id) && (Text(version, "project_id") ?? "") != (id ?? ""))
                    throw new InvalidDataException("前置版本所属项目不匹配。");
                id = Text(version, "project_id");
                JArray loaders = version["loaders"] as JArray;
                if (loaders is not null && loaders.Values<string>().Contains("datapack") && !loaders.Values<string>().Any(value => new[] { "fabric", "forge", "neoforge", "quilt", "liteloader" }.Contains(value)))
                    kind = CommunityResourceKind.DataPack;
            }
            var project = await GetProjectAsync(platform, id, token, kind).ConfigureAwait(false);
            project.PreferredVersionId = dependency.VersionId;
            return project;
        }

        public async Task<List<ModpackRelease>> GetVersionsAsync(ModpackProject project, CancellationToken token)
        {
            ValidateId(project.Platform, project.Id);
            var result = new List<ModpackRelease>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var fetched = new HashSet<string>(StringComparer.Ordinal);
            int offset = 0;
            for (int page = 0; page <= 99; page++)
            {
                token.ThrowIfCancellationRequested();
                JArray items;
                long total;
                if (project.Platform == ModpackPlatform.Modrinth)
                {
                    // PCL requests all versions, then builds the game-version filter locally.
                    var root = await TokenAsync("https://api.modrinth.com/v2/project/" + project.Id + "/version?include_changelog=false", token).ConfigureAwait(false);
                    items = root as JArray;
                    if (items is null)
                        throw new InvalidDataException("Modrinth 版本列表无效。");
                    total = items.Count;
                }
                else
                {
                    var root = await JsonAsync("https://api.curseforge.com/v1/mods/" + project.Id + "/files?pageSize=50&index=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                    items = ArrayValue(root, "data");
                    total = Number(ObjectValue(root, "pagination"), "totalCount");
                    if (items.Count > 50)
                        throw new InvalidDataException("CurseForge 版本分页无效。");
                }
                var rawIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items.OfType<JObject>())
                {
                    var release = ParseRelease(project, item);
                    rawIds.Add(Text(item, "id"));
                    if (release is not null && (string.IsNullOrEmpty(project.PreferredVersionId) || (release.Id ?? "") == (project.PreferredVersionId ?? "")) && seen.Add(release.Id))
                        result.Add(release);
                }
                offset += items.Count;
                if (rawIds.Count > 0 && rawIds.All(id => fetched.Contains(id)))
                    throw new InvalidDataException("整合包版本分页返回了重复结果，请重试。");
                fetched.UnionWith(rawIds);
                if (project.Platform == ModpackPlatform.Modrinth || offset >= total || items.Count == 0)
                    break;
                if (rawIds.Count == 0)
                    throw new InvalidDataException("整合包版本分页没有前进，请重试。");
                if (page == 99)
                    throw new InvalidDataException("整合包版本过多，请缩小查询范围。");
            }
            return result.OrderByDescending(item => item.Published).ToList();
        }

        public static ModpackRelease ParseRelease(ModpackProject project, JObject data)
        {
            var result = new ModpackRelease() { Kind = project.Kind, Platform = project.Platform, ProjectId = project.Id, Id = Text(data, "id"), Name = Text(data, project.Platform == ModpackPlatform.Modrinth ? "name" : "displayName") };
            ValidateId(project.Platform, result.Id);
            if (project.Platform == ModpackPlatform.Modrinth)
            {
                if ((Text(data, "project_id") ?? "") != (project.Id ?? ""))
                    throw new InvalidDataException("Modrinth 版本所属项目不匹配。");
                if (!string.IsNullOrEmpty(Text(data, "status", false)) && Text(data, "status") != "listed" && Text(data, "status") != "archived")
                    return null;
                if (!CommunityResources.FileKind(project, Strings(data, "loaders")).HasValue)
                    return null;
                var candidates = ArrayValue(data, "files").OfType<JObject>().Where(file => (project.Kind == CommunityResourceKind.Modpack ? Text(file, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) : CommunityResources.AcceptsFile(project.Kind, Text(file, "filename"))) && !new[] { "sources-jar", "dev-jar", "javadoc-jar", "signature" }.Contains(Text(file, "file_type", false))).ToList();
                var primary = candidates.Where(file => (bool)(file["primary"] ?? new JValue(false))).ToList();
                var chosen = primary.Count == 1 ? primary[0] : candidates.Count == 1 ? candidates[0] : null;
                if (chosen is null)
                    return null;
                result.FileName = Text(chosen, "filename");
                result.Url = Text(chosen, "url");
                result.Size = Number(chosen, "size");
                var hashes = ObjectValue(chosen, "hashes");
                result.Sha1 = Digest(hashes, "sha1", 40);
                result.Sha512 = Digest(hashes, "sha512", 128);
                result.Version = Text(data, "version_number");
                result.Published = DateValue(data, "date_published");
                result.ReleaseType = Text(data, "version_type");
                result.GameVersions = Strings(data, "game_versions");
                result.Loaders = Strings(data, "loaders");
                if (project.Kind != CommunityResourceKind.Modpack)
                    result.Dependencies = ReadDependencies(project.Platform, data);
            }
            else
            {
                if ((Text(data, "modId") ?? "") != (project.Id ?? ""))
                    throw new InvalidDataException("CurseForge 文件所属项目不匹配。");
                if (Flag(data, "isServerPack", false))
                    return null;
                if (!Flag(data, "isAvailable", true))
                    return null;
                if (Flag(data, "isEarlyAccessContent", false))
                    return null;
                if (data["fileStatus"] is not null && Number(data, "fileStatus") != 4L)
                    return null;
                result.FileName = Text(data, "fileName");
                if (!CommunityResources.AcceptsFile(project.Kind, result.FileName) || project.Kind == CommunityResourceKind.Modpack && !result.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return null;
                result.Url = Text(data, "downloadUrl", false);
                if (string.IsNullOrEmpty(result.Url))
                {
                    // PCL ResourceVersion.FromPlatformJson retains public files whose
                    // Core API downloadUrl is null. Preserve exact file identity and
                    // escaping; the normal size, hash and CDN path checks still apply.
                    long fileIdNumber = long.Parse(result.Id, System.Globalization.CultureInfo.InvariantCulture);
                    result.Url = "https://edge.forgecdn.net/files/" + (fileIdNumber / 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + (fileIdNumber % 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(result.FileName);
                }
                result.Size = Number(data, "fileLength");
                result.Version = result.Name;
                result.Published = DateValue(data, "fileDate");
                result.ReleaseType = Number(data, "releaseType") == 1L ? "release" : Number(data, "releaseType") == 2L ? "beta" : "alpha";
                string[] raw = ReadCurseForgeTags(data["gameVersions"], result);
                result.Loaders = raw.Where(value => new[] { "Forge", "NeoForge", "Fabric", "Quilt" }.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (project.Kind != CommunityResourceKind.Modpack)
                {
                    result.GameVersionMetadataError = null;
                    result.Dependencies = ReadDependencies(project.Platform, data);
                }
                foreach (var hash in ArrayValue(data, "hashes").OfType<JObject>())
                {
                    long algorithm = Number(hash, "algo");
                    if (algorithm == 1L)
                    {
                        string sha1 = Digest(new JObject() { { "sha1", Text(hash, "value") } }, "sha1", 40);
                        if (result.Sha1 is not null && (result.Sha1 ?? "") != (sha1 ?? ""))
                            throw new InvalidDataException("归档 SHA-1 声明冲突。");
                        result.Sha1 = sha1;
                    }
                    if (algorithm == 2L)
                    {
                        string md5 = Digest(new JObject() { { "md5", Text(hash, "value") } }, "md5", 32);
                        if (result.Md5 is not null && (result.Md5 ?? "") != (md5 ?? ""))
                            throw new InvalidDataException("归档 MD5 声明冲突。");
                        result.Md5 = md5;
                    }
                }
            }
            result.Downloads = ReadDownloadCount(data[project.Platform == ModpackPlatform.Modrinth ? "downloads" : "downloadCount"]);
            // The archive is stored under a fixed cache name, not the remote filename.
            // Keep ordinary long archive names while rejecting path-bearing metadata.
            if (result.FileName.Length > 255 || (result.FileName ?? "") != (result.FileName.Trim() ?? "") || result.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("整合包归档文件名无效。");
            if (result.Kind != CommunityResourceKind.Modpack)
                CommunityResources.ValidateFileName(result.Kind, result.FileName);
            if (result.Size <= 0L)
                return null;
            if (result.Sha1 is null && result.Sha512 is null && result.Md5 is null)
                return null;
            ModpackArchivePolicy.Validate(result.Url, result.Platform, false);
            if (result.Platform == ModpackPlatform.CurseForge)
            {
                long id = long.Parse(result.Id, System.Globalization.CultureInfo.InvariantCulture);
                var address = new Uri(result.Url);
                string expected = "/files/" + (id / 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + (id % 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + result.FileName;
                if ((Uri.UnescapeDataString(address.AbsolutePath) ?? "") != (expected ?? ""))
                    throw new InvalidDataException("CurseForge 下载地址与指定文件不匹配。");
            }
            return result;
        }

        private static ResourceDependency[] ReadDependencies(ModpackPlatform platform, JObject data)
        {
            var result = new List<ResourceDependency>();
            JArray values = data["dependencies"] as JArray;
            if (values is null)
                return Array.Empty<ResourceDependency>();
            if (values.Count > 256)
                throw new InvalidDataException("前置声明过多。");
            foreach (var item in values.OfType<JObject>())
            {
                if (platform == ModpackPlatform.Modrinth)
                {
                    if (Text(item, "dependency_type", false) != "required")
                        continue;
                    string project = Text(item, "project_id", false);
                    string version = Text(item, "version_id", false);
                    if (!string.IsNullOrEmpty(project))
                        ValidateId(platform, project);
                    if (!string.IsNullOrEmpty(version))
                        ValidateId(platform, version);
                    if (!string.IsNullOrEmpty(project) || !string.IsNullOrEmpty(version))
                        result.Add(new ResourceDependency() { ProjectId = project, VersionId = version });
                }
                else if (Number(item, "relationType") == 3L)
                {
                    string id = Text(item, "modId");
                    ValidateId(platform, id);
                    result.Add(new ResourceDependency() { ProjectId = id });
                }
            }
            string owner = Text(data, platform == ModpackPlatform.Modrinth ? "project_id" : "modId");
            string versionId = Text(data, "id");
            return result.Where(item => (item.ProjectId ?? "") != (owner ?? "") && (platform != ModpackPlatform.Modrinth || (item.VersionId ?? "") != (versionId ?? ""))).GroupBy(item => item.ProjectId + ":" + item.VersionId, StringComparer.Ordinal).Select(@group => @group.First()).ToArray();
        }

        private static long? ReadDownloadCount(JToken value)
        {
            if (value is null || value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                return default;
            decimal number;
            if (!decimal.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) || number < 0m || number > long.MaxValue || decimal.Truncate(number) != number)
                return default;
            return (long)Math.Round(number);
        }

        private static string[] ReadCurseForgeTags(JToken declaration, ModpackRelease release)
        {
            // Fixed PCL ResourceVersion.FromPlatformJson distinguishes loader labels
            // from Minecraft versions, then normalizes previews for display. Here the
            // original exact IDs must survive for the archive/manifest identity check.
            if (declaration is null || declaration.Type == JTokenType.Null)
                return Array.Empty<string>();
            JArray values = declaration as JArray;
            if (values is null || values.Any(value => value.Type != JTokenType.String || ((string)value).Length > 100))
            {
                release.GameVersionMetadataError = "CurseForge 文件的 Minecraft 版本声明无效，无法核对整合包清单。请刷新重试或选择其他版本。";
                return Array.Empty<string>();
            }
            string[] tags = values.Values<string>().Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            release.GameVersions = tags.Where(IsExactMinecraftVersion).ToArray();
            if (release.GameVersions.Length == 0)
            {
                release.GameVersionMetadataError = "CurseForge 文件未声明可识别的 Minecraft 版本，无法核对整合包清单。请刷新重试或选择其他版本。";
            }
            return tags;
        }

        internal static bool IsExactMinecraftVersion(string value)
        {
            // McVersion.IsStandardFormat / GetVanillaName at PCL 5ef81e4 are the
            // modern/pre/rc/year-snapshot basis. Anchor the entire tag: never extract
            // a version-like substring from Forge, Java or other mixed labels.
            const string modern = @"\A(?:1\.[0-9]+(?:\.[0-9]+)?|(?:2[6-9]|[3-9][0-9])\.[0-9]+(?:\.[0-9]+)?)(?:-(?:pre|rc|snapshot-?)[1-9][0-9]*| Pre-Release(?: [1-9][0-9]*)?)?(?:_unobfuscated)?\z";
            const string weekly = @"\A[0-9]{2}w[0-9]{2}[a-z]\z";
            const string historical = @"\A(?:[ab]1\.[0-9]+(?:\.[0-9]+)?(?:_[0-9]+[a-z]?)?(?:-?pre[1-9][0-9]*)?|c0\.[0-9]+(?:\.[0-9]+)?[a-z]?(?:_[0-9]+[a-z]?)?|(?:in|inf|rd)-[0-9]{6,8})\z";
            var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            return Regex.IsMatch(value, modern, options) || Regex.IsMatch(value, weekly, options) || Regex.IsMatch(value, historical, options) || value == "2.0";
        }

        private static ModpackProject ParseProject(ModpackPlatform platform, JObject data, bool search, CommunityResourceKind kind)
        {
            var result = new ModpackProject() { Platform = platform, Kind = kind == CommunityResourceKind.Any ? CommunityResources.Infer(platform, data) : kind };
            if (platform == ModpackPlatform.Modrinth)
            {
                result.Id = Text(data, search ? "project_id" : "id");
                result.Name = Text(data, "title");
                result.Summary = Text(data, "description", false);
                result.IconUrl = Text(data, "icon_url", false);
                result.Website = "https://modrinth.com/modpack/" + Uri.EscapeDataString(Text(data, "slug"));
                result.Downloads = Number(data, "downloads");
                result.Updated = DateValue(data, search ? "date_modified" : "updated");
                result.GameVersions = Strings(data, search ? "versions" : "game_versions");
            }
            else
            {
                result.Id = Text(data, "id");
                result.Name = Text(data, "name");
                result.Summary = Text(data, "summary", false);
                result.IconUrl = data["logo"] as JObject is null ? "" : Text((JObject)data["logo"], "thumbnailUrl", false);
                result.Website = "https://www.curseforge.com/minecraft/modpacks/" + Uri.EscapeDataString(Text(data, "slug"));
                result.Downloads = Number(data, "downloadCount");
                result.Updated = DateValue(data, "dateModified");
            }
            ValidateId(platform, result.Id);
            result.Slug = Text(data, "slug");
            if (platform == ModpackPlatform.CurseForge)
                result.GameVersions = ModpackProjectMetadata.ReadVersions(result, data);
            result.Tags = ModpackProjectMetadata.ReadTags(platform, data["categories"], result.Kind);
            result.Loaders = ModpackProjectMetadata.ReadLoaders(result, data);
            return result;
        }

        private async Task<JObject> JsonAsync(string url, CancellationToken token)
        {
            JObject result = await TokenAsync(url, token).ConfigureAwait(false) as JObject;
            if (result is null)
                throw new InvalidDataException("平台返回的 JSON 对象无效。");
            return result;
        }
        private async Task<JToken> TokenAsync(string url, CancellationToken token, JObject body = null)
        {
            var urls = new List<string>() { url };
            if (url.StartsWith("https://api.curseforge.com/", StringComparison.Ordinal))
            {
                urls.Clear();
                if (!string.IsNullOrWhiteSpace(_key))
                    urls.Add(url);
                // PCL DlModRequest / DlSourceModGet use the Core API and its MCIM
                // mirror. Website /api/v1 file lists have projectId/dateCreated and
                // omit downloadUrl/hashes, so HTTP 200 there is not a Core response.
                urls.Add(url.Replace("https://api.curseforge.com/", "https://mod.mcimirror.top/curseforge/"));
            }
            if (url.StartsWith("https://api.modrinth.com/", StringComparison.Ordinal))
                urls.Add(url.Replace("https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/"));
            if (_communitySource == 0)
                urls = urls.OrderByDescending(address => new Uri(address).Host == "mod.mcimirror.top").ToList();
            var failures = new List<string>();
            foreach (var address in urls)
            {
                bool mirror = new Uri(address).Host == "mod.mcimirror.top";
                for (int attempt = 1, loopTo = mirror ? 2 : 1; attempt <= loopTo; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    bool retry = false;
                    try
                    {
                        return await FetchTokenAsync(address, mirror || _communitySource == 1 ? 10 : _communitySource == 2 ? 30 : 15, token, body).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        failures.Add(new Uri(address).Host + "：" + (ex is OperationCanceledException ? "请求超时" : ex.GetBaseException().Message));
                        CatalogueHttpException http = ex as CatalogueHttpException;
                        retry = http is not null ? http.Transient : ex is HttpRequestException || ex is OperationCanceledException || ex is IOException && !(ex is InvalidDataException);
                    }
                    if (!retry || !mirror || attempt == 2)
                        break;
                    // Fixed PCL DlModRequest repeats its ten-second MCIM request.
                    // Keep retries bounded and cancellable, never retry bad metadata.
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            throw new IOException(string.Join("；", failures.Distinct()));
        }
        private class CatalogueHttpException : HttpRequestException
        {
            internal readonly bool Transient;
            internal CatalogueHttpException(int code, string reason) : base("HTTP " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(reason) ? "" : " (" + reason.Substring(0, Math.Min(reason.Length, 256)) + ")"))
            {
                Transient = code == 408 || code == 429 || code >= 500;
            }
        }
        private async Task<JToken> FetchTokenAsync(string address, int seconds, CancellationToken token, JObject body = null)
        {
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, address))
                {
                    if (body is not null)
                        request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json");
                    deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                    if (new Uri(address).Host == "api.curseforge.com" && !string.IsNullOrWhiteSpace(_key))
                        request.Headers.TryAddWithoutValidation("x-api-key", _key);
                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new CatalogueHttpException((int)response.StatusCode, response.ReasonPhrase);
                        using (var registration = deadline.Token.Register(() => InstallCleanup.NotifyCancellation(response.Dispose)))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            var block = new byte[8192];
                            do
                            {
                                int read = await input.ReadAsync(block, 0, block.Length, deadline.Token).ConfigureAwait(false);
                                if (read == 0)
                                    break;
                                if (buffer.Length + read > 16L * 1024L * 1024L)
                                    throw new InvalidDataException("平台响应超过大小限制。");
                                buffer.Write(block, 0, read);
                            }
                            while (true);
                            token.ThrowIfCancellationRequested();
                            return JToken.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        }
                    }
                }
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                // Closing a net48 response may interrupt reads with an IO/disposal
                // exception. Keep the caller's cancellation identity across platforms.
                throw new OperationCanceledException("平台请求已取消。", ex, token);
            }
        }
        private static void ValidateId(ModpackPlatform platform, string id)
        {
            if (platform != ModpackPlatform.Modrinth && platform != ModpackPlatform.CurseForge)
                throw new ArgumentException("平台无效。");
            if (id is null || !Regex.IsMatch(id, platform == ModpackPlatform.Modrinth ? @"\A[A-Za-z0-9]{1,32}\z" : @"\A[1-9][0-9]{0,12}\z"))
                throw new InvalidDataException("平台项目或文件 ID 无效。");
        }
        private static string Text(JObject data, string @field, bool required = true)
        {
            var value = data[@field];
            if (value is null || value.Type == JTokenType.Null)
            {
                if (!required)
                    return "";
                throw new InvalidDataException("平台缺少字段：" + @field);
            }
            if (value.Type != JTokenType.String && value.Type != JTokenType.Integer)
                throw new InvalidDataException("平台文本字段无效：" + @field);
            string result = (string)value;
            if (result.Length > 8192 || required && string.IsNullOrWhiteSpace(result))
                throw new InvalidDataException("平台文本字段无效：" + @field);
            return result;
        }
        private static long Number(JObject data, string @field)
        {
            if (data[@field] is not null && data[@field].Type == JTokenType.Float)
            {
                decimal numericValue;
                if (decimal.TryParse(data[@field].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numericValue) && numericValue >= 0m && numericValue <= long.MaxValue && decimal.Truncate(numericValue) == numericValue)
                    return (long)Math.Round(numericValue);
                throw new InvalidDataException("平台数值字段无效：" + @field);
            }
            long result;
            if (!long.TryParse(Text(data, @field), out result) || result < 0L)
                throw new InvalidDataException("平台数值字段无效：" + @field);
            return result;
        }
        private static bool Flag(JObject data, string @field, bool fallback)
        {
            // MCIM can serialize an optional Core API flag as explicit JSON null.
            // Give it the same default as an omitted flag; preserve explicit values.
            var value = data[@field];
            if (value is null || value.Type == JTokenType.Null)
                return fallback;
            if (value.Type != JTokenType.Boolean)
                throw new InvalidDataException("平台布尔字段无效：" + @field);
            return (bool)value;
        }
        private static JObject ObjectValue(JObject data, string @field)
        {
            JObject result = data[@field] as JObject;
            if (result is null)
                throw new InvalidDataException("平台对象字段无效：" + @field);
            return result;
        }
        private static JArray ArrayValue(JObject data, string @field)
        {
            JArray result = data[@field] as JArray;
            if (result is null)
                throw new InvalidDataException("平台数组字段无效：" + @field);
            return result;
        }
        private static string[] Strings(JObject data, string @field)
        {
            JArray array = data[@field] as JArray;
            if (array is null)
                return Array.Empty<string>();
            if (array.Any(value => value.Type != JTokenType.String || ((string)value).Length > 100))
                throw new InvalidDataException("平台版本或分类字段无效。");
            return array.Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private static DateTimeOffset DateValue(JObject data, string @field)
        {
            var value = data[@field];
            DateTimeOffset result;
            if (value is null || !DateTimeOffset.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out result))
                throw new InvalidDataException("平台日期字段无效：" + @field);
            return result;
        }
        private static string Digest(JObject data, string @field, int length)
        {
            string value = Text(data, @field, false).ToLowerInvariant();
            if (string.IsNullOrEmpty(value))
                return null;
            if (!Regex.IsMatch(value, @"\A[0-9a-f]{" + length + @"}\z"))
                throw new InvalidDataException("归档摘要无效：" + @field);
            return value;
        }
        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
