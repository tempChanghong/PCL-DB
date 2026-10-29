using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed partial class ModpackCatalogClient
    {
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
    }
}
