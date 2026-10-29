using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 DlClientListBmclapiMain merges extra entries before using the normal
    // installation pipeline. UVMC is a separate public catalogue, not PCL's private list.
    public sealed class SupplementalVersions
    {
        public const string ManifestUrl = "https://zkitefly.github.io/unlisted-versions-of-minecraft/version_manifest.json";
        public const string LibraryReferenceUrl = "https://piston-meta.mojang.com/v1/packages/3c514114d9c2a3ea78f72c4f9fb4eeb56747135a/1.5.1.json";
        public const string LibraryReferenceSha1 = "3c514114d9c2a3ea78f72c4f9fb4eeb56747135a";
        public const string AlphaLibraryReferenceUrl = "https://piston-meta.mojang.com/v1/packages/51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2/inf-20100618.json";
        public const string AlphaLibraryReferenceSha1 = "51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2";
        public const string LegacyAssetIndexUrl = "https://launchermeta.mojang.com/mc-staging/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json";
        public const string LegacyAssetIndexSha1 = "c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729";
        private const string BaseUrl = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/";
        // Initialize before Aliases, which consumes this same read-only registry.
        private static readonly IReadOnlyList<string> SnapshotIds = Array.AsReadOnly(new string[] { "13w12~", "13w10b", "13w10a", "13w09c", "13w09b", "11w47a", "13w09a", "13w07a", "13w11a", "13w06a", "13w05b", "13w05a", "13w04a", "13w03a", "13w02b", "13w02a", "13w01b", "13w01a", "12w50b", "12w50a", "12w49a", "12w42b", "12w42a", "12w41b", "12w41a", "12w40b", "12w40a", "12w39b", "12w39a", "12w38b", "12w38a", "12w37a", "12w36a", "12w34b", "12w34a", "12w32a", "12w30a", "12w30b", "12w30c", "12w30d", "12w30e", "12w27a", "12w26a", "12w25a", "12w24a", "12w23a", "12w23b", "12w22a", "12w21a", "12w21b", "12w19a", "12w18a", "12w17a", "12w16a", "1.2", "12w08a", "12w07a", "12w07b", "12w06a", "12w05a", "12w05b", "12w04a", "12w03a", "12w01a", "11w50a", "11w49a", "11w48a" });
        // Beta classification is independent from its launch recipe (RC1 uses 1.6/Alpha).
        private static readonly IReadOnlyList<string> BetaIds = Array.AsReadOnly(new string[] { "b1.9-pre6", "1.0.0-rc1", "b1.9-pre1", "b1.9-pre2", "b1.9-pre3", "b1.9-pre4", "b1.9-pre5", "b1.8-pre2", "b1.8-pre1-2", "b1.8-pre1-1", "b1.1-1", "b1.1-2" });
        private static readonly IReadOnlyList<string> AlphaIds = Array.AsReadOnly(new string[] { "c0.0.12a_03", "a1.1.1", "inf-20100630-1", "a1.1.0-2", "a1.1.0-1", "a1.0.17_03", "a1.0.16_02", "a1.0.14-1", "a1.0.14-2", "a1.0.13_01-1", "a1.0.13_01-2" });
        private static readonly Dictionary<string, string> Aliases = CreateAliases();

        public static IReadOnlyList<string> HistoricalSnapshotIds
        {
            get
            {
                return SnapshotIds;
            }
        }

        internal static bool IsHistoricalSnapshot(string id)
        {
            return SnapshotIds.Contains(id, StringComparer.Ordinal);
        }

        public static IReadOnlyList<string> HistoricalBetaIds
        {
            get
            {
                return BetaIds;
            }
        }

        internal static bool IsHistoricalBeta(string id)
        {
            return BetaIds.Contains(id, StringComparer.Ordinal);
        }

        public static IReadOnlyList<string> HistoricalAlphaIds
        {
            get
            {
                return AlphaIds;
            }
        }

        internal static bool IsHistoricalAlpha(string id)
        {
            return AlphaIds.Contains(id, StringComparer.Ordinal);
        }

        private static Dictionary<string, string> CreateAliases()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // These catalogue -> metadata identities have been checked individually.
            foreach (var color in new[] { "blue", "red", "purple" })
            {
                result.Add("2point0_" + color, "2.0_" + color);
                result.Add("2.0_" + color, "2.0_" + color);
            }
            foreach (var id in AlphaIds.Concat(BetaIds).Concat(SnapshotIds))
                result.Add(id, id);
            return result;
        }

        private static string CanonicalId(string id)
        {
            string canonical = null;
            return id is not null && Aliases.TryGetValue(id, out canonical) ? canonical : id;
        }

        public static string Label(VersionEntry entry)
        {
            if (entry.Id.StartsWith("2.0_", StringComparison.Ordinal))
                return "愚人节版";
            return entry.Kind == "snapshot" ? "旧快照" : "远古版";
        }

        private static bool ValidKind(string id, string kind)
        {
            if (IsHistoricalSnapshot(id))
                return kind == "snapshot";
            if (IsHistoricalBeta(id))
                return kind == "old_beta";
            if (IsHistoricalAlpha(id))
                return kind == "old_alpha";
            return kind == "snapshot" || kind == "pending";
        }

        public static string DisplayName(VersionEntry entry)
        {
            switch (entry.Id ?? "")
            {
                case "2.0_blue":
                    {
                        return "2.0 (Blue)";
                    }
                case "2.0_red":
                    {
                        return "2.0 (Red)";
                    }
                case "2.0_purple":
                    {
                        return "2.0 (Purple)";
                    }

                default:
                    {
                        return entry.Id;
                    }
            }
        }

        // Descriptions apply only to the three explicit UVMC identities; an ordinary
        // entry named 2.0 does not establish which client variant it contains.
        public static string VariantDescription(VersionEntry entry)
        {
            if (!entry.IsSupplemental)
                return null;
            switch (entry.Id ?? "")
            {
                case "2.0_blue":
                    {
                        return "新增彩蛋功能，不含超级敌对模式";
                    }
                case "2.0_red":
                    {
                        return "新增内容仅含超级敌对模式";
                    }
                case "2.0_purple":
                    {
                        return "新增彩蛋功能与超级敌对模式兼有";
                    }

                default:
                    {
                        return null;
                    }
            }
        }

        // Bounded, optional work. The UI shows the ordinary catalogue before awaiting it.
        // No persistent cache: a failed refresh cannot resurrect an obsolete declaration.
        public static async Task<List<VersionEntry>> LoadAsync(SourcePolicy policy, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3d));
                try
                {
                    string body = await policy.GetTextAsync(ManifestUrl, timeout.Token).ConfigureAwait(false);
                    if (body.Length > 1024 * 1024)
                        throw new InvalidDataException("补充版本列表过大。");
                    return Parse(JObject.Parse(body));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidOperationException && ex.InnerException is ArgumentException)
                {
                    // net48 HttpContent wraps unsupported response charsets this way.
                    token.ThrowIfCancellationRequested();
                    return new List<VersionEntry>();
                }
            }
        }

        public static List<VersionEntry> Parse(JObject manifest)
        {
            JArray versions = manifest["versions"] as JArray;
            if (versions is null || versions.Count > 4096)
                throw new InvalidDataException("补充版本列表格式无效。");
            var parsed = new List<VersionEntry>();
            var released = default(DateTimeOffset);
            foreach (var value in versions.OfType<JObject>())
            {
                string rawId = StringValue(value["id"]);
                string canonical = null;
                if (rawId is null || !Aliases.TryGetValue(rawId, out canonical))
                    continue;
                string kind = StringValue(value["type"]);
                string url = StringValue(value["url"]);
                string sha1 = StringValue(value["sha1"]);
                if (!ValidKind(canonical, kind) || (url ?? "") != (MetadataUrl(canonical) ?? "") || value["sha1"] is not null && !IsSha1(sha1) || !TryRelease(value["releaseTime"], ref released))
                    continue;
                parsed.Add(new VersionEntry() { Id = canonical, SupplementalId = rawId, Kind = kind, Url = url, Sha1 = sha1, Released = released });
            }
            // A contradictory duplicate excludes that identity; array order cannot pick a winner.
            return parsed.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Where(@group => @group.All(entry => SameDeclaration(entry, @group.First()))).Select(@group => @group.First()).ToList();
        }

        public static List<VersionEntry> Merge(IEnumerable<VersionEntry> normal, IEnumerable<VersionEntry> extra)
        {
            var result = normal.ToList();
            var occupied = new HashSet<string>(result.Select(entry => CanonicalId(entry.Id)), StringComparer.OrdinalIgnoreCase);
            foreach (var group in extra.GroupBy(entry => CanonicalId(entry.Id), StringComparer.OrdinalIgnoreCase))
            {
                if (!occupied.Add(group.Key) || !@group.All(entry => SameDeclaration(entry, @group.First())))
                    continue;
                result.Add(@group.First());
            }
            return result;
        }

        private static bool SameDeclaration(VersionEntry left, VersionEntry right)
        {
            return (left.Id ?? "") == (right.Id ?? "") && (left.Url ?? "") == (right.Url ?? "") && (left.Kind ?? "") == (right.Kind ?? "") && string.Equals(left.Sha1, right.Sha1, StringComparison.OrdinalIgnoreCase) && left.Released == right.Released;
        }

        private static string MetadataUrl(string id)
        {
            string slug = id.Replace("2.0_", "2point0_");
            if (IsHistoricalBeta(id) && id.StartsWith("b1.9-pre", StringComparison.Ordinal))
                slug = id.Replace("b1.9-", "b1_9-");
            // b1.8-pre1-2 drops its final revision in the URL; pre1-1 keeps its dots.
            switch (id ?? "")
            {
                case "b1.8-pre2":
                    {
                        slug = "b1_8-pre2";
                        break;
                    }
                case "b1.8-pre1-2":
                    {
                        slug = "b1_8-pre1";
                        break;
                    }
            }
            if (id == "13w12~")
                slug = "13w12-";
            if (id == "1.2")
                slug = "1_2";
            return BaseUrl + slug + "/" + slug + ".json";
        }

        private static string StringValue(JToken value)
        {
            return value is not null && value.Type == JTokenType.String ? (string)value : null;
        }

        private static bool IsSha1(string value)
        {
            return Regex.IsMatch(value ?? "", @"\A[0-9a-fA-F]{40}\z");
        }

        private static bool TryRelease(JToken value, ref DateTimeOffset released)
        {
            if (value is not null && value.Type == JTokenType.Date)
            {
                released = value.ToObject<DateTimeOffset>();
                return true;
            }
            return DateTimeOffset.TryParse(StringValue(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out released);
        }

        private static bool UsesAlphaReference(string id)
        {
            return id == "1.0.0-rc1" || id == "inf-20100630-1";
        }

        private static bool CanSupplyLegacyIndex(string id, JObject metadata)
        {
            // Narrower than PCL McAssetsGetIndex(ReturnLegacyOnError): only these
            // audited profiles, with BOTH properties absent. Null/malformed/download
            // failures must never silently switch the resource tree.
            return UsesAlphaReference(id) && metadata.Property("assetIndex") is null && metadata.Property("assets") is null;
        }

        public static void ValidateMetadata(VersionEntry entry, JObject metadata)
        {
            if ((StringValue(metadata["id"]) ?? "") != (entry.Id ?? "") || metadata["inheritsFrom"] is not null)
                throw new InvalidDataException("版本元数据不是所选原版版本。");
            if (!entry.IsSupplemental)
                return;
            string canonical = null;
            if (!Aliases.TryGetValue(entry.SupplementalId, out canonical) || (canonical ?? "") != (entry.Id ?? "") || (entry.Url ?? "") != (MetadataUrl(canonical) ?? "") || !ValidKind(canonical, entry.Kind) || !string.IsNullOrEmpty(entry.Sha1) && !IsSha1(entry.Sha1))
                throw new InvalidDataException("补充版本身份无效。");
            if (string.IsNullOrWhiteSpace(StringValue(metadata["mainClass"])) || !(metadata["libraries"] is JArray) || !ValidKind(canonical, StringValue(metadata["type"])) || metadata["jar"] is not null)
                throw new InvalidDataException("补充版本元数据结构无效。");
            if (((JArray)metadata["libraries"]).Any(item => !(item is JObject)))
                throw new InvalidDataException("补充版本包含无效的支持库声明。");
            if (((JArray)metadata["libraries"]).OfType<JObject>().Any(library => library.Property("downloads") is not null && !(library["downloads"] is JObject)))
                throw new InvalidDataException("补充版本的支持库下载声明损坏，不能按缺失字段补全。");
            JObject index = metadata["assetIndex"] as JObject;
            if (!CanSupplyLegacyIndex(canonical, metadata) && (index is null || string.IsNullOrWhiteSpace(StringValue(index["id"])) || !IsSha1(StringValue(index["sha1"]))))
                throw new InvalidDataException("补充版本资源索引缺少有效校验。");
            SupplementalLaunchRecipe.ValidateSource(canonical, metadata);
        }

        public static async Task<JObject> PrepareLibrariesAsync(VersionEntry entry, JObject metadata, SourcePolicy policy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            JObject result = (JObject)metadata.DeepClone();
            if (!entry.IsSupplemental)
                return result;
            ValidateMetadata(entry, result);
            if (CanSupplyLegacyIndex(entry.Id, result))
            {
                // PCL 5ef81e4 McAssetsGetIndex's fixed legacy declaration. Keep
                // virtual assets in assets/virtual/legacy even with a private index ID.
                result["assets"] = "legacy";
                result["assetIndex"] = new JObject() { { "id", "legacy" }, { "sha1", LegacyAssetIndexSha1 }, { "size", 134284 }, { "totalSize", 111220701 }, { "url", LegacyAssetIndexUrl } };
            }
            var libraries = ((JArray)result["libraries"]).OfType<JObject>().ToList();
            if (!libraries.Any(library => library.Property("downloads") is null))
            {
                SupplementalLaunchRecipe.ValidateLibraries(entry.Id, result);
                return result;
            }
            bool alpha = UsesAlphaReference(entry.Id);
            if (!alpha && !entry.Id.StartsWith("2.0_", StringComparison.Ordinal))
                throw new InvalidDataException("该历史版本的支持库下载信息不完整，没有对应的已校验参考清单。");
            // UVMC 2.0 profiles retain the original Maven/rules/native declarations,
            // without download records. Match them to the SHA-1 verified Mojang 1.5.1
            // profile, preserving this version's client, arguments and asset index.
            // RC1/Infdev explicitly use the 1.6 Alpha family. Never guess a reference
            // from the version's age or use the 2.0/1.5 wrapper for them.
            var reference = await policy.GetJsonAsync(alpha ? AlphaLibraryReferenceUrl : LibraryReferenceUrl, token, alpha ? AlphaLibraryReferenceSha1 : LibraryReferenceSha1).ConfigureAwait(false);
            if ((StringValue(reference["id"]) ?? "") != ((alpha ? "inf-20100618" : "1.5.1") ?? "") || !(reference["libraries"] is JArray))
                throw new InvalidDataException("旧版支持库参考清单无效。");
            foreach (var library in libraries)
            {
                token.ThrowIfCancellationRequested();
                if (library.Property("downloads") is not null)
                    continue;
                JObject declaration = (JObject)library.DeepClone();
                declaration.Remove("downloads");
                var matches = new List<JToken>();
                foreach (var candidate in ((JArray)reference["libraries"]).OfType<JObject>())
                {
                    JObject shape = (JObject)candidate.DeepClone();
                    shape.Remove("downloads");
                    if (JToken.DeepEquals(shape, declaration) && candidate["downloads"] is JObject)
                        matches.Add(candidate["downloads"]);
                }
                if (matches.Count == 0 || !matches.All(item => JToken.DeepEquals(item, matches[0])))
                    throw new InvalidDataException("补充版本的支持库声明与已校验参考不一致：" + StringValue(library["name"]));
                library["downloads"] = matches[0].DeepClone();
            }
            token.ThrowIfCancellationRequested();
            SupplementalLaunchRecipe.ValidateLibraries(entry.Id, result);
            return result;
        }

        public static void ValidatePlan(IEnumerable<DownloadFile> files)
        {
            foreach (var file in files)
            {
                if (!IsSha1(file.Sha1) || file.Size < 0L)
                    throw new InvalidDataException("补充版本文件缺少有效的 SHA-1 或大小：" + file.RelativePath);
            }
        }
    }
}