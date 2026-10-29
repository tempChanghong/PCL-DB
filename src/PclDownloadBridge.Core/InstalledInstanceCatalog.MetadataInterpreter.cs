using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{
    public sealed partial class InstalledInstanceCatalog
    {
        // Interpret the supplied JSON only. Profile/configuration I/O, inheritance
        // merging, isolation, target guards and result publication stay in the reader.
        private static class MetadataInterpreter
        {
            internal static string GameVersion(JObject metadata)
            {
                string explicitVersion = Scalar(metadata["clientVersion"]);
                if (!string.IsNullOrWhiteSpace(explicitVersion))
                    return explicitVersion;
                JArray patches = metadata["patches"] as JArray;
                if (patches is not null)
                {
                    var game = patches.OfType<JObject>().FirstOrDefault(patch => Scalar(patch["id"]) == "game");
                    string version = Scalar(game?["version"]);
                    if (!string.IsNullOrWhiteSpace(version))
                        return version;
                }
                string jar = Scalar(metadata["jar"]);
                if (IsVersionId(jar))
                    return jar;
                string id = Scalar(metadata["id"]);
                return IsVersionId(id) ? id : null;
            }
            private static bool IsVersionId(string value)
            {
                return value is not null && (MinecraftVersionCategory.IsAprilFools(value) || Regex.IsMatch(value, @"\A(?:\d+\.\d+(?:\.\d+)?(?:(?:-pre|-rc)\d+| Pre-Release \d+| Release Candidate \d+|_(?:blue|red|purple))?|\d{2}w\d{2}[a-z].*|(?:rd-|in-|inf-|c0\.|a\d|b\d).*)\z", RegexOptions.IgnoreCase));
            }
            internal static List<string> DetectLoaders(JObject metadata, JObject raw)
            {
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (metadata.Property("labymod_data") is not null)
                {
                    JObject data = metadata["labymod_data"] as JObject;
                    if (data is null)
                        throw new InvalidDataException("LabyMod 版本信息无效。");
                    found["LabyMod"] = ("LabyMod " + Scalar(data["version"])).TrimEnd();
                }
                JArray libraries = metadata["libraries"] as JArray;
                if (metadata["libraries"] is not null && libraries is null)
                    throw new InvalidDataException("版本支持库列表无效。");
                if (libraries is not null)
                {
                    if (libraries.Any(library => !(library is JObject)))
                        throw new InvalidDataException("版本支持库条目无效。");
                    foreach (var library in libraries.OfType<JObject>())
                    {
                        string name = Scalar(library["name"]);
                        if (name is null)
                            continue;
                        string[] parts = name.Split(':');
                        if (parts.Length < 3)
                            continue;
                        string loader = null;
                        switch ((parts[0] + ":" + parts[1]).ToLowerInvariant() ?? "")
                        {
                            case "net.fabricmc:fabric-loader":
                            case "net.legacyfabric:fabric-loader":
                                {
                                    loader = "Fabric";
                                    break;
                                }
                            case "org.quiltmc:quilt-loader":
                                {
                                    loader = "Quilt";
                                    break;
                                }
                            case "net.minecraftforge:forge":
                            case "net.minecraftforge:minecraftforge":
                                {
                                    loader = "Forge";
                                    break;
                                }
                            case "net.neoforged:neoforge":
                            case "net.neoforged:forge":
                                {
                                    loader = "NeoForge";
                                    break;
                                }
                            case "com.mumfrey:liteloader":
                                {
                                    loader = "LiteLoader";
                                    break;
                                }
                            case "optifine:optifine":
                                {
                                    loader = "OptiFine";
                                    break;
                                }
                            case "com.cleanroommc:cleanroom":
                                {
                                    loader = "Cleanroom";
                                    break;
                                }
                        }
                        if (loader is not null)
                            found[loader] = loader + " " + parts[2];
                    }
                }
                JArray patches = raw["patches"] as JArray;
                if (patches is not null)
                {
                    foreach (var patch in patches.OfType<JObject>())
                    {
                        string id = Scalar(patch["id"]);
                        string label = null;
                        switch ((id ?? "").ToLowerInvariant() ?? "")
                        {
                            case "fabric":
                            case "fabric-loader":
                                {
                                    label = "Fabric";
                                    break;
                                }
                            case "forge":
                                {
                                    label = "Forge";
                                    break;
                                }
                            case "neoforge":
                                {
                                    label = "NeoForge";
                                    break;
                                }
                            case "quilt":
                            case "quilt-loader":
                                {
                                    label = "Quilt";
                                    break;
                                }
                            case "liteloader":
                                {
                                    label = "LiteLoader";
                                    break;
                                }
                            case "optifine":
                                {
                                    label = "OptiFine";
                                    break;
                                }
                            case "cleanroom":
                                {
                                    label = "Cleanroom";
                                    break;
                                }
                        }
                        if (label is not null && !found.ContainsKey(label))
                            found[label] = (label + " " + Scalar(patch["version"])).TrimEnd();
                    }
                }
                string main = Scalar(metadata["mainClass"]) ?? "";
                if (main.IndexOf("fabricmc", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("Fabric"))
                    found["Fabric"] = "Fabric";
                if (main.IndexOf("quiltmc", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("Quilt"))
                    found["Quilt"] = "Quilt";
                if (main.IndexOf("neoforged", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("NeoForge"))
                    found["NeoForge"] = "NeoForge";
                string arguments = (metadata["arguments"] ?? metadata["minecraftArguments"])?.ToString();
                if (arguments is not null)
                {
                    if (arguments.IndexOf("optifine.OptiFineTweaker", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("OptiFine"))
                        found["OptiFine"] = "OptiFine";
                    if (arguments.IndexOf("fml", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("Forge") && !found.ContainsKey("NeoForge"))
                        found["Forge"] = "Forge";
                    if (arguments.IndexOf("liteloader", StringComparison.OrdinalIgnoreCase) >= 0 && !found.ContainsKey("LiteLoader"))
                        found["LiteLoader"] = "LiteLoader";
                }
                return found.Values.ToList();
            }

        }
    }
}
