using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public partial class ComponentInstaller
    {
        private async Task<JObject> InstallForgelikeAsync(ComponentEntry entry, JObject metadata, string work, string clientJar, CancellationToken token)
        {
            string installerRelative = "installers/" + entry.Kind.ToString().ToLowerInvariant() + ".jar";
            var download = new DownloadFile() { RelativePath = installerRelative, Url = entry.Url, Sha1 = entry.Sha1, Md5 = entry.Md5, Category = "component" };
            string label = ComponentCatalog.Label(entry.Kind);
            if (string.IsNullOrWhiteSpace(download.Sha1) && string.IsNullOrWhiteSpace(download.Md5))
                Emit("components", "读取 " + label + " 安装器校验信息", "校验信息就绪后下载安装器", 0.05d);
            await ForgelikeInstallerChecksum.ResolveAsync(_policy, download, token, label).ConfigureAwait(false);
            await FetchAsync(work, new[] { download }, "下载 " + ComponentCatalog.Label(entry.Kind) + " 安装器", token).ConfigureAwait(false);
            string installerPath = SafeFiles.UnderRoot(work, installerRelative);
            JObject profile;
            JObject version;
            using (var zip = ZipFile.OpenRead(installerPath))
            {
                profile = ReadJson(zip, "install_profile.json");
                if (profile["minecraft"] is not null && ((string)profile["minecraft"] ?? "") != (entry.Minecraft ?? ""))
                    throw new InvalidDataException("安装器不适用于所选游戏版本。");
                if (zip.GetEntry("version.json") is null)
                {
                    // PCL Forge installation method B: versionInfo + bundled universal JAR.
                    version = profile["versionInfo"] as JObject;
                    if (version is null)
                        throw new InvalidDataException("Forge 安装器缺少版本配置。");
                    JObject install = profile["install"] as JObject;
                    if (install is not null)
                    {
                        var bundled = zip.GetEntry((string)install["filePath"]);
                        if (bundled is null)
                            throw new InvalidDataException("旧 Forge 安装器缺少主文件。");
                        ExtractEntry(bundled, SafeFiles.UnderRoot(work, "libraries/" + MinecraftInstaller.MavenPath((string)install["path"])));
                    }
                }
                else
                {
                    version = ReadJson(zip, "version.json");
                }
                foreach (var embedded in zip.Entries.Where(e => e.FullName.StartsWith("maven/", StringComparison.Ordinal) && !e.FullName.EndsWith("/", StringComparison.Ordinal)))
                    ExtractEntry(embedded, SafeFiles.UnderRoot(work, "libraries/" + embedded.FullName.Substring(6)));
                foreach (JProperty data in (profile["data"] as JObject ?? new JObject()).Properties())
                {
                    string value = (string)data.Value["client"];
                    if (value is not null && value.StartsWith("/", StringComparison.Ordinal))
                    {
                        var embedded = zip.GetEntry(value.TrimStart('/'));
                        if (embedded is null)
                            throw new InvalidDataException("安装器缺少数据：" + value);
                        ExtractEntry(embedded, SafeFiles.UnderRoot(work, "data/" + value.TrimStart('/')));
                    }
                }
            }
            var files = new List<DownloadFile>();
            foreach (var libraryInfo in (profile["libraries"] as JArray ?? new JArray()).Concat(version["libraries"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (!MinecraftInstaller.RulesAllow(libraryInfo["rules"]))
                    continue;
                JObject artifact = libraryInfo.SelectToken("downloads.artifact") as JObject;
                string relative = (string)artifact?["path"] ?? MinecraftInstaller.MavenPath((string)libraryInfo["name"]);
                string url = (string)artifact?["url"] ?? ((string)libraryInfo["url"] ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + relative;
                var item = new DownloadFile() { RelativePath = "libraries/" + relative, Url = url, Sha1 = (string)artifact?["sha1"], Size = artifact?["size"] is null ? -1 : (long)artifact["size"], Category = "library" };
                if (!string.IsNullOrWhiteSpace(url))
                    files.Add(item);
            }
            var variables = ProcessorVariables(profile, work, installerPath, clientJar);
            JObject mappings = metadata.SelectToken("downloads.client_mappings") as JObject;
            if (variables.ContainsKey("MOJMAPS") && mappings is not null)
            {
                string relative = variables["MOJMAPS"].Substring(work.TrimEnd('\\').Length + 1);
                files.Add(new DownloadFile() { RelativePath = relative, Url = (string)mappings["url"], Sha1 = (string)mappings["sha1"], Size = (long)mappings["size"], Category = "meta" });
            }
            // Combine all integrity declarations before filtering embedded/reusable files.
            // Otherwise an earlier weak match can hide conflicts or change the first URL.
            files = DownloadPlan.Merge(files, work).Where(item => !SafeFiles.Matches(SafeFiles.UnderRoot(work, item.RelativePath), item, token)).ToList();
            await FetchAsync(work, files, "下载 " + ComponentCatalog.Label(entry.Kind) + " 支持库", token).ConfigureAwait(false);
            var processors = (profile["processors"] as JArray ?? new JArray()).OfType<JObject>().Where(p => p["sides"] is null || p["sides"].Values<string>().Contains("client")).ToList();
            if (processors.Count > 0)
            {
                string java = JavaRuntime.Find(_selection.JavaPath, Math.Max(8, (int)(metadata.SelectToken("javaVersion.majorVersion") ?? new JValue(8L))));
                var outputCache = new VerifiedComponentCache(_root, _reuse, processorOutputs: true);
                for (int i = 0, loopTo = processors.Count - 1; i <= loopTo; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var processor = processors[i];
                    var arguments = ((JArray)processor["args"]).Values<string>().Select(a => Resolve(a, variables, work)).ToList();
                    var outputs = ProcessorOutputs(processor["outputs"] as JObject, variables, work);
                    // Only a complete, nonempty SHA-1 declaration can discharge execution.
                    // Some modern NeoForge profiles have no outputs: always run those.
                    bool cacheable = outputs.Count > 0 && outputs.All(item => Regex.IsMatch(item.Value.Sha1 ?? "", @"\A[0-9a-fA-F]{40}\z"));
                    Emit("processors", "安装 " + ComponentCatalog.Label(entry.Kind), "处理器 " + (i + 1) + "/" + processors.Count + " · " + (string)processor["jar"], i / (double)processors.Count);
                    token.ThrowIfCancellationRequested();
                    bool reusedOutputs = false;
                    if (cacheable)
                    {
                        foreach (var output in outputs)
                        {
                            token.ThrowIfCancellationRequested();
                            if (SafeFiles.Matches(output.Key, output.Value, token))
                                continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(output.Key));
                            outputCache.TryRestore(output.Key, output.Value, token);
                        }
                        reusedOutputs = outputs.All(item => SafeFiles.Matches(item.Key, item.Value, token));
                        if (reusedOutputs)
                            Emit("processors", "安装 " + ComponentCatalog.Label(entry.Kind), "处理器 " + (i + 1) + "/" + processors.Count + " · 已校验输出，跳过重复执行", (i + 1) / (double)processors.Count);
                    }
                    token.ThrowIfCancellationRequested();
                    // PCL predownloads official mappings: skip their redundant network processor.
                    bool mapsTask = arguments.Contains("DOWNLOAD_MOJMAPS") && variables.ContainsKey("MOJMAPS") && File.Exists(variables["MOJMAPS"]);
                    if (!mapsTask && !reusedOutputs)
                    {
                        string jar = SafeFiles.UnderRoot(work, "libraries/" + MinecraftInstaller.MavenPath((string)processor["jar"]));
                        var classpath = ((JArray)processor["classpath"]).Values<string>().Select(c => SafeFiles.UnderRoot(work, "libraries/" + MinecraftInstaller.MavenPath(c))).Concat(new[] { jar }).Distinct().ToList();
                        foreach (var file in classpath)
                        {
                            if (!File.Exists(file))
                                throw new FileNotFoundException("处理器支持库缺失：" + file);
                        }
                        var args = new List<string>() { "-Djava.awt.headless=true", "-cp", string.Join(Path.PathSeparator.ToString(), classpath), ManifestMainClass(jar) };
                        args.AddRange(arguments);
                        await JavaRuntime.RunAsync(java, args, work, ProcessorLine, token).ConfigureAwait(false);
                    }
                    foreach (var output in outputs)
                    {
                        token.ThrowIfCancellationRequested();
                        SafeFiles.UnderRoot(work, output.Value.RelativePath); // Recheck links after Java has run.
                        if (!SafeFiles.Matches(output.Key, output.Value, token))
                            throw new InvalidDataException("处理器输出校验失败：" + output.Key);
                    }
                    // Verify every output before persisting any of them. Intermediates may
                    // share a path with a later stage; their content keys stay independent.
                    if (cacheable)
                    {
                        foreach (var output in outputs)
                            outputCache.Save(output.Key, output.Value, token);
                    }
                }
            }
            return version;
        }

        private static List<KeyValuePair<string, DownloadFile>> ProcessorOutputs(JObject declarations, Dictionary<string, string> variables, string work)
        {
            var outputs = new List<KeyValuePair<string, DownloadFile>>();
            string prefix = Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var declaration in (declarations ?? new JObject()).Properties())
            {
                string resolved = Resolve(declaration.Name, variables, work);
                // Java resolves relative paths against work, never the app's current directory.
                string destination = Path.GetFullPath(Path.IsPathRooted(resolved) ? resolved : Path.Combine(work, resolved));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("处理器输出超出安装临时目录：" + resolved);
                string relative = destination.Substring(prefix.Length);
                destination = SafeFiles.UnderRoot(work, relative);
                outputs.Add(new KeyValuePair<string, DownloadFile>(destination, new DownloadFile() { RelativePath = relative, Sha1 = Resolve((string)declaration.Value, variables, work) }));
            }
            return outputs;
        }

    }
}
