using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public partial class MinecraftInstaller
    {
        public static List<DownloadFile> BuildBasePlan(JObject metadata, string instanceName)
        {
            return PlanBuilder.BuildBasePlan(metadata, instanceName);
        }

        public static List<DownloadFile> BuildAssetsPlan(JObject index)
        {
            return PlanBuilder.BuildAssetsPlan(index);
        }

        internal static void ValidateResourceName(string name, string description = "旧资源映射")
        {
            PlanBuilder.ValidateResourceName(name, description);
        }

        public static bool RulesAllow(JToken rules)
        {
            return PlanBuilder.RulesAllow(rules);
        }

        public static string MavenPath(string coordinate, string classifier = null)
        {
            return PlanBuilder.MavenPath(coordinate, classifier);
        }

        internal static JObject PreparePclLibraryPaths(JObject profile)
        {
            return PlanBuilder.PreparePclLibraryPaths(profile);
        }

        private static class PlanBuilder
        {
            public static List<DownloadFile> BuildBasePlan(JObject metadata, string instanceName)
            {
                var result = new List<DownloadFile>();
                JObject client = metadata.SelectToken("downloads.client") as JObject;
                if (client is null)
                    throw new InvalidDataException("缺少客户端 JAR 信息。");
                result.Add(ToFile(client, "versions/" + instanceName + "/" + instanceName + ".jar", "meta"));
                var selected = new Dictionary<string, List<DownloadFile>>(StringComparer.Ordinal);
                foreach (var library in ((JArray)metadata["libraries"]).OfType<JObject>())
                {
                    if (!RulesAllow(library["rules"]))
                        continue;
                    JObject artifact = library.SelectToken("downloads.artifact") as JObject;
                    if (artifact is not null)
                    {
                        PclLibrarySelection.Add(selected, library, LibraryFile(library, artifact), false);
                    }
                    else if (library["natives"] is null || library["natives"].Type == JTokenType.Null)
                    {
                        PclLibrarySelection.Add(selected, library, LibraryFile(library, new JObject()), false);
                    }
                    string nativeName = (string)library.SelectToken("natives.windows");
                    if (nativeName is not null)
                    {
                        nativeName = nativeName.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                        JObject native = library["downloads"]?["classifiers"]?[nativeName] as JObject;
                        PclLibrarySelection.Add(selected, library, LibraryFile(library, native ?? new JObject(), nativeName), true);
                    }
                }
                result.AddRange(selected.Values.SelectMany(items => items));
                JObject logging = metadata.SelectToken("logging.client.file") as JObject;
                if (logging is not null)
                {
                    ValidateResourceName((string)logging["id"], "日志文件");
                    result.Add(ToFile(logging, "assets/log_configs/" + (string)logging["id"], "meta"));
                }
                return DownloadPlan.Merge(result);
            }

            public static List<DownloadFile> BuildAssetsPlan(JObject index)
            {
                var result = new List<DownloadFile>();
                var objects = index is null ? null : index["objects"] as JObject;
                if (objects is null)
                    throw new InvalidDataException("资源索引缺少有效的 objects 清单。");
                foreach (var item in objects.Properties())
                {
                    JObject entry = item.Value as JObject;
                    if (entry is null)
                        throw new InvalidDataException("资源条目无效：" + item.Name);
                    var hashToken = entry["hash"];
                    var sizeToken = entry["size"];
                    if (hashToken is null || hashToken.Type != JTokenType.String || !Regex.IsMatch((string)hashToken, @"\A[0-9a-fA-F]{40}\z"))
                        throw new InvalidDataException("资源哈希无效：" + item.Name);
                    // Normalize object addresses only; retain the verified index and logical names.
                    string hash = ((string)hashToken).ToLowerInvariant();
                    long size;
                    if (sizeToken is null || sizeToken.Type != JTokenType.Integer || !long.TryParse(sizeToken.ToString(Newtonsoft.Json.Formatting.None), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out size))
                        throw new InvalidDataException("资源大小无效：" + item.Name);
                    if ((bool)(index["virtual"] ?? new JValue(false)) || (bool)(index["map_to_resources"] ?? new JValue(false)))
                        ValidateResourceName(item.Name);
                    string source = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash;
                    var file = new DownloadFile() { RelativePath = "assets/objects/" + hash.Substring(0, 2) + "/" + hash, Url = source, Size = size, Sha1 = hash, Category = "asset" };
                    result.Add(file);
                    // PCL McAssetsListGet: old virtual/resource mappings. Also retain the object
                    // store, making future reuse and modern launcher checks straightforward.
                    if ((bool)(index["virtual"] ?? new JValue(false)))
                        result.Add(new DownloadFile() { RelativePath = "assets/virtual/legacy/" + item.Name, Url = source, Size = file.Size, Sha1 = hash, Category = "asset" });
                    if ((bool)(index["map_to_resources"] ?? new JValue(false)))
                        result.Add(new DownloadFile() { RelativePath = "resources/" + item.Name, Url = source, Size = file.Size, Sha1 = hash, Category = "asset" });
                }
                return DownloadPlan.Merge(result);
            }

            internal static void ValidateResourceName(string name, string description = "旧资源映射")
            {
                if (name is null)
                    throw new InvalidDataException(description + "名称为空。");
                foreach (var part in name.Replace('\\', '/').Split('/'))
                {
                    if (string.IsNullOrEmpty(part) || part == "." || part == ".." || part.Length > 255 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        throw new InvalidDataException(description + "名称无效：" + name);
                }
            }

            public static bool RulesAllow(JToken rules)
            {
                if (rules is null || rules.Type == JTokenType.Null)
                    return true;
                bool required = false;
                foreach (var rule in rules)
                {
                    bool matches = true;
                    var os = rule["os"];
                    if (os is not null)
                    {
                        if (os["name"] is not null)
                            matches = (string)os["name"] == "windows";
                        if (os["version"] is not null)
                            matches = matches && Regex.IsMatch(Environment.OSVersion.Version.ToString(), (string)os["version"]);
                        if (os["arch"] is not null)
                            matches = matches && Regex.IsMatch(Environment.Is64BitOperatingSystem ? "amd64" : "x86", (string)os["arch"]);
                    }
                    JObject features = rule["features"] as JObject;
                    if (features is not null)
                    {
                        foreach (var feature in features.Properties())
                        {
                            // No demo/custom-resolution/quick-play launch features are enabled.
                            if ((bool)feature.Value)
                                matches = false;
                        }
                    }
                    if (matches)
                        required = (string)rule["action"] == "allow";
                }
                return required;
            }

            public static string MavenPath(string coordinate, string classifier = null)
            {
                string extension = "jar";
                string[] parts = coordinate.Split('@');
                if (parts.Length > 2)
                    throw new InvalidDataException("支持库扩展名无效：" + coordinate);
                if (parts.Length == 2)
                    extension = parts[1];
                string[] elements = parts[0].Split(':');
                if (elements.Length < 3 || elements.Length > 4 || !Regex.IsMatch(elements[0], @"\A[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z") || !Regex.IsMatch(extension, @"\A[A-Za-z0-9]+\z"))
                    throw new InvalidDataException("支持库坐标无效：" + coordinate);
                foreach (var component in elements.Skip(1))
                {
                    if (!Regex.IsMatch(component, @"\A[A-Za-z0-9_.+\-]+\z") || component == "." || component == "..")
                        throw new InvalidDataException("支持库坐标含无效路径：" + coordinate);
                }
                if (classifier is null && elements.Length > 3)
                    classifier = elements[3];
                return elements[0].Replace('.', '/') + "/" + elements[1] + "/" + elements[2] + "/" + elements[1] + "-" + elements[2] + (classifier is null ? "" : "-" + classifier) + "." + extension;
            }

            // PCL McLibListGetWithJson uses Maven paths when an artifact omits its path.
            // Destination and source are independent: a custom local path does not change
            // the Maven coordinate used to resolve a missing URL in the declared repository.
            private static DownloadFile LibraryFile(JObject libraryInfo, JObject data, string classifier = null)
            {
                string relative = (string)data["path"];
                if (relative is null)
                    relative = MavenPath((string)libraryInfo["name"], classifier);
                if (string.IsNullOrWhiteSpace(relative))
                    throw new InvalidDataException("支持库下载路径为空：" + (string)libraryInfo["name"]);
                ValidateResourceName(relative, "支持库");
                var file = ToFile(data, "libraries/" + relative, "library");
                if (file.Url is null)
                {
                    string repo = (string)libraryInfo["url"] ?? "https://libraries.minecraft.net/";
                    file.Url = repo.TrimEnd('/') + "/" + MavenPath((string)libraryInfo["name"], classifier);
                }
                return file;
            }

            // Pinned PCL McLibGet always appends .jar and does not parse @extension.
            // Its artifact branch does honor an explicit path. Materialize that path only
            // for extended coordinates, on a clone, before saving or comparing for repair.
            internal static JObject PreparePclLibraryPaths(JObject profile)
            {
                JObject result = (JObject)profile.DeepClone();
                foreach (var libraryInfo in ((JArray)result["libraries"]).OfType<JObject>())
                {
                    string coordinate = (string)libraryInfo["name"];
                    if (coordinate is null || !coordinate.Contains("@") || !RulesAllow(libraryInfo["rules"]))
                        continue;
                    CompletePclArtifactPath(libraryInfo, libraryInfo.SelectToken("downloads.artifact") as JObject, null);
                    string nativeName = (string)libraryInfo.SelectToken("natives.windows");
                    if (nativeName is null)
                        continue;
                    nativeName = nativeName.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    JObject native = libraryInfo["downloads"]?["classifiers"]?[nativeName] as JObject;
                    if (native is null)
                        continue;
                    CompletePclArtifactPath(libraryInfo, native, nativeName);
                    ((JObject)libraryInfo["natives"])["windows"] = nativeName;
                }
                return result;
            }

            private static void CompletePclArtifactPath(JObject libraryInfo, JObject data, string classifier)
            {
                if (data is null)
                    return;
                var item = LibraryFile(libraryInfo, data, classifier);
                if (data["path"] is null || data["path"].Type == JTokenType.Null)
                    data["path"] = item.RelativePath.Substring("libraries/".Length);
                if (data["url"] is null || data["url"].Type == JTokenType.Null)
                    data["url"] = item.Url;
                // PCL reads size with ToString even when it has an explicit path.
                if (data["size"] is null)
                    data["size"] = -1;
            }

        }
    }
}
