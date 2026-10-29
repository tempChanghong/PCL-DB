using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public partial class ComponentInstaller
    {
        public static JObject ReadFabricMod(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                return ReadJson(archive, "fabric.mod.json");
            }
        }
        public static JObject MergeProfile(JObject basis, JObject addition)
        {
            JObject result = (JObject)basis.DeepClone();
            JObject overlay = (JObject)addition.DeepClone();
            overlay.Remove("time");
            overlay.Remove("releaseTime");
            overlay.Remove("id");
            string oldArguments = (string)result["minecraftArguments"];
            string newArguments = (string)overlay["minecraftArguments"];
            if (oldArguments is not null && newArguments is not null)
            {
                // A full legacy launch string already includes vanilla arguments.
                overlay["minecraftArguments"] = newArguments.Contains("${auth_player_name}") ? newArguments : oldArguments + " " + newArguments;
            }
            // PCL reads legacy and structured game arguments together. Keep conditional
            // objects intact instead of flattening them or dropping the whole JVM section.
            if (oldArguments is not null && overlay.SelectToken("arguments.jvm") is not null && result.SelectToken("arguments.jvm") is null)
            {
                // The presence of arguments.jvm switches PCL out of its legacy fallback.
                // Carry that fallback forward when a loader adds JVM options to old MC.
                JObject structured = result["arguments"] as JObject;
                if (structured is null)
                {
                    structured = new JObject();
                    result["arguments"] = structured;
                }
                structured["jvm"] = new JArray("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump", "-Djava.library.path=${natives_directory}", "-cp", "${classpath}");
            }
            // Arguments are ordered tokens: equal option names or values may repeat.
            result.Merge(overlay, new JsonMergeSettings() { MergeArrayHandling = MergeArrayHandling.Concat });
            JArray libraries = result["libraries"] as JArray;
            if (libraries is not null)
            {
                var ordered = new List<JObject>();
                foreach (JObject libraryInfo in libraries)
                {
                    foreach (var part in LibraryParts(libraryInfo))
                    {
                        // PCL McLibListGetWithJson keys ordinary and native entries separately.
                        string key = PclLibrarySelection.Identity(part, part["natives"] is not null) + ":" + ((part["rules"]?.ToString()) ?? "");
                        int previous = ordered.FindIndex(v => ((string)v["_mergeKey"] ?? "") == (key ?? ""));
                        part["_mergeKey"] = key;
                        if (previous < 0)
                        {
                            ordered.Add(part);
                        }
                        else if (!MinecraftInstaller.RulesAllow(part["rules"]) || part["natives"] is not null && (string)part.SelectToken("natives.windows") is null)
                        {
                            ordered[previous] = part; // Preserve inactive-platform metadata without resolving its files.
                        }
                        else if (PclLibrarySelection.CompareVersion(PclLibrarySelection.ProfileVersion(part), PclLibrarySelection.ProfileVersion(ordered[previous])) >= 0)
                        {
                            ordered[previous] = part;
                        }
                    }
                }
                foreach (var libraryInfo in ordered)
                    libraryInfo.Remove("_mergeKey");
                result["libraries"] = new JArray(ordered);
            }
            result.Remove("inheritsFrom");
            result.Remove("jar");
            result.Remove("_comment_");
            return result;
        }

        private static IEnumerable<JObject> LibraryParts(JObject libraryInfo)
        {
            JObject copy = (JObject)libraryInfo.DeepClone();
            if (copy["natives"] is null || copy["natives"].Type == JTokenType.Null)
            {
                copy.Remove("natives"); // PCL also clears explicit null native markers.
                yield return copy;
                yield break;
            }
            JObject downloads = copy["downloads"] as JObject;
            if (downloads?["artifact"] is not null && downloads["artifact"].Type != JTokenType.Null)
            {
                // A combined declaration represents two independently overridable files.
                // Emit separate PCL-readable entries, so upgrading one preserves the other.
                JObject runtime = (JObject)copy.DeepClone();
                runtime.Remove("natives");
                runtime.Remove("extract");
                ((JObject)runtime["downloads"]).Remove("classifiers");
                yield return runtime;
            }
            downloads?.Remove("artifact");
            yield return copy;
        }

        public static JObject LiteProfile(ComponentEntry entry)
        {
            JArray libs = (JArray)entry.Data["libraries"].DeepClone();
            libs.Add(new JObject() { { "name", "com.mumfrey:liteloader:" + entry.Version }, { "url", "https://dl.liteloader.com/versions/" } });
            return new JObject() { { "libraries", libs }, { "mainClass", "net.minecraft.launchwrapper.Launch" }, { "arguments", new JObject() { { "game", new JArray("--tweakClass", (string)entry.Data["tweakClass"]) } } } };
        }
        public static Dictionary<string, string> ProcessorVariables(JObject profile, string work, string installer, string clientJar)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { { "SIDE", "client" }, { "ROOT", work }, { "LIBRARY_DIR", Path.Combine(work, "libraries") }, { "INSTALLER", installer }, { "MINECRAFT_JAR", clientJar }, { "MINECRAFT_VERSION", (string)profile["minecraft"] } };
            foreach (JProperty data in (profile["data"] as JObject ?? new JObject()).Properties())
            {
                string value = (string)data.Value["client"];
                if (value is null)
                    continue;
                if (value.StartsWith("'", StringComparison.Ordinal))
                {
                    value = value.Trim('\'');
                }
                else if (value.StartsWith("[", StringComparison.Ordinal))
                {
                    value = SafeFiles.UnderRoot(work, "libraries/" + MinecraftInstaller.MavenPath(value.Trim('[', ']')));
                }
                else if (value.StartsWith("/", StringComparison.Ordinal))
                {
                    value = SafeFiles.UnderRoot(work, "data/" + value.TrimStart('/'));
                }
                values[data.Name] = value;
            }
            return values;
        }
        public static string Resolve(string value, Dictionary<string, string> variables, string work)
        {
            if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
                return SafeFiles.UnderRoot(work, "libraries/" + MinecraftInstaller.MavenPath(value.Trim('[', ']')));
            return Regex.Replace(value, @"\{([A-Z_]+)\}", m =>
                {
                    if (!variables.ContainsKey(m.Groups[1].Value))
                        throw new InvalidDataException("未知安装器变量：" + m.Value);
                    return variables[m.Groups[1].Value];
                });
        }
        private static JObject ReadJson(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null || entry.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("安装器元数据无效：" + name);
            using (var reader = new StreamReader(entry.Open()))
            {
                return JObject.Parse(reader.ReadToEnd());
            }
        }
        private static void ExtractEntry(ZipArchiveEntry entry, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = entry.Open())
            using (var output = File.Create(target))
            {
                input.CopyTo(output);
            }
        }
        private static string ManifestMainClass(string jar)
        {
            using (var zip = ZipFile.OpenRead(jar))
            using (var reader = new StreamReader(zip.GetEntry("META-INF/MANIFEST.MF").Open()))
            {
                string manifest = reader.ReadToEnd().Replace(Constants.vbCrLf + " ", "").Replace(Constants.vbLf + " ", "");
                var match = Regex.Match(manifest, @"(?m)^Main-Class:\s*([^\r\n]+)");
                if (!match.Success)
                    throw new InvalidDataException("安装处理器缺少 Main-Class。");
                return match.Groups[1].Value.Trim();
            }
        }
    }
}
