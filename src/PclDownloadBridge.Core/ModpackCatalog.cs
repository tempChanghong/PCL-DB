using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed partial class ModpackCatalogClient : IDisposable
    {
        public const int PageSize = 40;
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

    }
}
