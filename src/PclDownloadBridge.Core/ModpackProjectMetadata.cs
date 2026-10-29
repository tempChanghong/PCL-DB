using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ResourceProject.FromPlatformJson tags / loader declarations.
    // Optional presentation metadata must not reject an otherwise usable project.
    public sealed class ModpackProjectMetadata
    {
        private ModpackProjectMetadata()
        {
        }

        private static readonly Dictionary<int, string> CurseForgeTags = new Dictionary<int, string>() { { 4484, "多人" }, { 4479, "硬核" }, { 4483, "战斗" }, { 4478, "任务" }, { 4472, "科技" }, { 4473, "魔法" }, { 4475, "冒险" }, { 4476, "探索" }, { 4477, "小游戏" }, { 4474, "科幻" }, { 4736, "空岛" }, { 5128, "原版改良" }, { 4487, "FTB" }, { 4480, "基于地图" }, { 4481, "轻量" }, { 4482, "大型" }, { 406, "世界元素" }, { 407, "生物群系" }, { 410, "维度" }, { 408, "矿物/资源" }, { 409, "天然结构" }, { 412, "科技" }, { 415, "管道/物流" }, { 4843, "自动化" }, { 417, "能源" }, { 4558, "红石" }, { 436, "食物/烹饪" }, { 416, "农业" }, { 414, "运输" }, { 420, "仓储" }, { 419, "魔法" }, { 422, "冒险" }, { 424, "装饰" }, { 411, "生物" }, { 434, "装备" }, { 6814, "性能优化" }, { 9026, "创造模式" }, { 423, "信息显示" }, { 435, "服务器" }, { 5191, "改良" }, { 421, "支持库" }, { 403, "原版风" }, { 400, "写实风" }, { 401, "现代风" }, { 402, "中世纪" }, { 399, "蒸汽朋克" }, { 5244, "含字体" }, { 404, "动态效果" }, { 4465, "兼容 Mod" }, { 393, "16x" }, { 394, "32x" }, { 395, "64x" }, { 396, "128x" }, { 397, "256x" }, { 398, "超高清" }, { 5193, "数据包" }, { 6553, "写实风" }, { 6554, "幻想风" }, { 6555, "原版风" }, { 6948, "冒险" }, { 6949, "幻想" }, { 6950, "支持库" }, { 6952, "魔法" }, { 6946, "Mod 相关" }, { 6951, "科技" }, { 6953, "实用" } };
        private static readonly Dictionary<string, string> ModrinthTags = new Dictionary<string, string>(StringComparer.Ordinal) { { "technology", "科技" }, { "magic", "魔法" }, { "adventure", "冒险" }, { "utility", "实用" }, { "optimization", "性能优化" }, { "vanilla-like", "原版风" }, { "realistic", "写实风" }, { "multiplayer", "多人" }, { "challenging", "硬核" }, { "combat", "战斗" }, { "quests", "任务" }, { "kitchen-sink", "水槽包" }, { "lightweight", "轻量" }, { "worldgen", "世界元素" }, { "food", "食物/烹饪" }, { "game-mechanics", "游戏机制" }, { "transportation", "运输" }, { "storage", "仓储" }, { "decoration", "装饰" }, { "mobs", "生物" }, { "equipment", "装备" }, { "social", "服务器" }, { "library", "支持库" }, { "simplistic", "简洁" }, { "tweaks", "改良" }, { "8x-", "极简" }, { "16x", "16x" }, { "32x", "32x" }, { "48x", "48x" }, { "64x", "64x" }, { "128x", "128x" }, { "256x", "256x" }, { "512x+", "超高清" }, { "audio", "含声音" }, { "fonts", "含字体" }, { "models", "含模型" }, { "gui", "含 UI" }, { "locale", "含语言" }, { "core-shaders", "核心着色器" }, { "modded", "兼容 Mod" }, { "fantasy", "幻想风" }, { "semi-realistic", "半写实风" }, { "cartoon", "卡通风" }, { "colored-lighting", "彩色光照" }, { "path-tracing", "路径追踪" }, { "pbr", "PBR" }, { "reflections", "反射" }, { "iris", "Iris" }, { "optifine", "OptiFine" }, { "vanilla", "原版可用" } };
        // The first four follow PCLCS/Constants.cs flag order. Quilt/Rift/vanilla are
        // display-only extensions already represented by this application's release labels.
        private static readonly string[] LoaderOrder = new[] { "Forge", "LiteLoader", "Fabric", "NeoForge", "Quilt", "Rift", "原版" };
        private static readonly Dictionary<string, string> LoaderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "forge", "Forge" }, { "liteloader", "LiteLoader" }, { "fabric", "Fabric" }, { "fabric-loader", "Fabric" }, { "neoforge", "NeoForge" }, { "neo-forge", "NeoForge" }, { "quilt", "Quilt" }, { "quilt-loader", "Quilt" }, { "rift", "Rift" }, { "minecraft", "原版" }, { "vanilla", "原版" }, { "原版", "原版" } };

        public static string[] ReadTags(ModpackPlatform platform, JToken categories, CommunityResourceKind kind = CommunityResourceKind.Modpack)
        {
            var result = new List<string>();
            if (platform == ModpackPlatform.Modrinth)
            {
                foreach (var category in OptionalStrings(categories))
                {
                    if (kind == CommunityResourceKind.ResourcePack && new[] { "decoration", "mobs", "equipment" }.Contains(category, StringComparer.Ordinal))
                        continue;
                    string translated = null;
                    if (ModrinthTags.TryGetValue(category, out translated) && !result.Contains(translated))
                        result.Add(translated);
                }
            }
            else if (platform == ModpackPlatform.CurseForge)
            {
                JArray array = categories as JArray;
                if (array is null)
                    return Array.Empty<string>();
                var ids = new HashSet<int>();
                foreach (var category in array.OfType<JObject>())
                {
                    var value = category["id"];
                    int id;
                    if (value is not null && value.Type == JTokenType.Integer && int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
                        ids.Add(id);
                }
                foreach (var id in ids.OrderByDescending(value => value))
                {
                    string translated = null;
                    if (CurseForgeTags.TryGetValue(id, out translated))
                        result.Add(translated);
                }
            }
            return result.ToArray();
        }

        public static string[] ReadLoaders(ModpackProject project, JObject data)
        {
            if (project.Platform == ModpackPlatform.Modrinth)
                return NormalizeLoaders(OptionalStrings(data["loaders"]).Concat(OptionalStrings(data["categories"])));
            return NormalizeLoaders(ClientFiles(project, data).SelectMany(file => OptionalStrings(file["gameVersions"])));
        }

        public static string[] ReadVersions(ModpackProject project, JObject data)
        {
            // PCL ResourceProject unions latestFiles and latestFilesIndexes (#3599).
            // These are optional display declarations, never archive authorization or
            // the version binding used by installation. Do not infer a supported range.
            if (project.Platform != ModpackPlatform.CurseForge)
                return Array.Empty<string>();
            var values = ClientFiles(project, data).SelectMany(file => OptionalStrings(file["gameVersions"])).ToList();
            JArray indexes = data["latestFilesIndexes"] as JArray;
            if (indexes is not null)
            {
                foreach (var entry in indexes.OfType<JObject>())
                {
                    var fileId = entry["fileId"];
                    var name = entry["filename"];
                    var game = entry["gameVersion"];
                    long number;
                    if (fileId is null || fileId.Type != JTokenType.Integer || !long.TryParse(fileId.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0L)
                        continue;
                    if (!AcceptsFileName(project, name) || game is null || game.Type != JTokenType.String || ((string)game).Length > 100)
                        continue;
                    values.Add(((string)game).Trim());
                }
            }
            return values.Where(ModpackCatalogClient.IsExactMinecraftVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool AcceptsFileName(ModpackProject project, JToken name)
        {
            if (name is null || name.Type != JTokenType.String)
                return false;
            string value = (string)name;
            return value.Length <= 255 && (value ?? "") == (value.Trim() ?? "") && value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && CommunityResources.AcceptsFile(project.Kind, value) && (project.Kind != CommunityResourceKind.Modpack || value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<JObject> ClientFiles(ModpackProject project, JObject data)
        {
            JArray files = data["latestFiles"] as JArray;
            if (project.Platform != ModpackPlatform.CurseForge || files is null)
                yield break;
            foreach (var file in files.OfType<JObject>())
            {
                var owner = file["modId"];
                var name = file["fileName"];
                var status = file["fileStatus"];
                if (owner is null || owner.Type != JTokenType.Integer && owner.Type != JTokenType.String || (owner.ToString() ?? "") != (project.Id ?? ""))
                    continue;
                if (!AcceptsFileName(project, name))
                    continue;
                if (!FlagIs(file, "isServerPack", false, false) || !FlagIs(file, "isAvailable", true, true) || !FlagIs(file, "isEarlyAccessContent", false, false))
                    continue;
                if (status is not null && status.Type != JTokenType.Null && (status.Type != JTokenType.Integer || status.ToString() != "4"))
                    continue;
                yield return file;
            }
        }

        public static string LoaderText(IEnumerable<string> loaders)
        {
            return string.Join(" / ", NormalizeLoaders(loaders));
        }
        public static string CompactLoaderText(IEnumerable<string> loaders, IEnumerable<string> versions)
        {
            string[] names = NormalizeLoaders(loaders);
            var newest = new Version(9999, 0);
            var numeric = new List<Version>();
            foreach (var value in versions ?? Enumerable.Empty<string>())
            {
                Version version = null;
                if (Version.TryParse(value, out version))
                    numeric.Add(version);
            }
            if (numeric.Count > 0)
                newest = numeric.Max();
            // PCL ToResourceItem: omit redundant loader combinations when all
            // mainstream loaders of that era are represented. Tooltip stays complete.
            if (names.Length > 1 && names.Contains("Forge") && (newest < new Version(1, 14) || names.Contains("Fabric")) && (newest < new Version(1, 20) || names.Contains("NeoForge")))
                return "";
            return string.Join(" / ", names);
        }

        private static string[] NormalizeLoaders(IEnumerable<string> loaders)
        {
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in loaders ?? Enumerable.Empty<string>())
            {
                string canonical = null;
                if (value is not null && value.Length <= 100 && LoaderAliases.TryGetValue(value.Trim(), out canonical))
                    accepted.Add(canonical);
            }
            return LoaderOrder.Where(loader => accepted.Contains(loader)).ToArray();
        }

        private static IEnumerable<string> OptionalStrings(JToken value)
        {
            JArray array = value as JArray;
            if (array is null)
                yield break;
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String && ((string)item).Length <= 100)
                    yield return ((string)item).Trim();
            }
        }

        private static bool FlagIs(JObject data, string @field, bool expected, bool fallback)
        {
            var value = data[@field];
            if (value is null || value.Type == JTokenType.Null)
                return fallback == expected;
            return value.Type == JTokenType.Boolean && (bool)value == expected;
        }
    }
}