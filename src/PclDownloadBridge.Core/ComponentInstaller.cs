using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // Adapted from PCL McInstallLoadersGet / McDownloadForgelikeLoader /
    // McDownloadLiteLoaderLoader / MergeJson. Uses the shared PCL-style downloader
    // for installer dependencies, then runs the official install_profile processors.
    public class ComponentInstaller
    {
        private readonly SourcePolicy _policy;
        private readonly ComponentSelection _selection;
        private readonly IProgress<TransferProgress> _progress;
        private readonly string _root;
        private readonly List<string> _reuse;
        private readonly int _connections;
        private readonly long _limit;
        private readonly DownloadBudget _budget;
        public long NetworkBytes { get; set; }
        public int Reused { get; set; }
        public int FileCount { get; set; }
        public int DynamicSplits { get; set; }
        public int Retries { get; set; }
        private readonly Stopwatch _processorClock = Stopwatch.StartNew();
        private long _lastProcessorReport;
        private List<DownloadFile> _publishedRuntimes = new List<DownloadFile>();
        internal ComponentRuntimePublication.PublishedLease LockPublishedRuntimes(CancellationToken token)
        {
            return new ComponentRuntimePublication.PublishedLease(_root, _publishedRuntimes, token);
        }
        public ComponentInstaller(SourcePolicy policy, ComponentSelection selection, string root, IEnumerable<string> reuse, IProgress<TransferProgress> progress, int connections, long limit, DownloadBudget budget = null)
        {
            if (selection is null)
                throw new ArgumentNullException(nameof(selection));
            _policy = policy;
            _selection = selection.Copy();
            _root = root;
            _progress = progress;
            _reuse = reuse.Concat(new[] { root }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _connections = connections;
            _limit = limit;
            _budget = budget;
        }

        public async Task<JObject> ApplyAsync(JObject metadata, string mc, string stage, string name, CancellationToken token, bool isolated = true, bool writePclSettings = true)
        {
            _publishedRuntimes.Clear();
            _selection.Validate(mc);
            ModFiles(_selection, mc); // Reject ambiguous or escaping add-on names before downloading.
            string workRelative = ".vanilla-downloader/components/" + Guid.NewGuid().ToString("N");
            string work = SafeFiles.UnderRoot(_root, workRelative);
            Directory.CreateDirectory(work);
            JObject merged = (JObject)metadata.DeepClone();
            // Every generated file remains in the staging tree until all processors finish.
            // No global launcher_profiles.json or existing instance settings are touched.
            try
            {
                string vanilla = SafeFiles.UnderRoot(work, "versions/" + mc + "/" + mc);
                Directory.CreateDirectory(Path.GetDirectoryName(vanilla));
                File.Copy(Path.Combine(stage, name + ".jar"), vanilla + ".jar");
                File.WriteAllText(vanilla + ".json", metadata.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(work, "launcher_profiles.json"), "{\"profiles\":{}}");
                foreach (var kind in new[] { ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.LiteLoader, ComponentKind.Fabric })
                {
                    var entry = _selection.Item(kind);
                    if (entry is null)
                        continue;
                    JObject profile;
                    if (kind == ComponentKind.Fabric)
                    {
                        Emit("components", "下载 Fabric", "读取启动配置", 0.1d);
                        profile = await _policy.GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mc) + "/" + Uri.EscapeDataString(entry.Version) + "/profile/json", token).ConfigureAwait(false);
                        if (((string)profile["inheritsFrom"] ?? "") != (mc ?? ""))
                            throw new InvalidDataException("Fabric 配置指向其他游戏版本。");
                    }
                    else if (kind == ComponentKind.LiteLoader)
                    {
                        profile = LiteProfile(entry);
                    }
                    else
                    {
                        profile = await InstallForgelikeAsync(entry, metadata, work, vanilla + ".jar", token).ConfigureAwait(false);
                    }
                    merged = MergeProfile(merged, profile);
                }
                var opt = _selection.Item(ComponentKind.OptiFine);
                if (opt is not null)
                {
                    SafeFiles.ValidatePathSegment(opt.FileName);
                    if (_selection.Has(ComponentKind.Forge) || _selection.Has(ComponentKind.Fabric) || _selection.Has(ComponentKind.LiteLoader))
                    {
                        // PCL OptiFineAsMod: LiteLoader stores OptiFine in mods/<mc>/.
                        string relative = "mods/" + (_selection.Has(ComponentKind.LiteLoader) ? mc + "/" : "") + opt.FileName;
                        await FetchOptiFineAsync(stage, relative, opt, token).ConfigureAwait(false);
                    }
                    else
                    {
                        string installer = "installers/optifine.jar";
                        await FetchOptiFineAsync(work, installer, opt, token).ConfigureAwait(false);
                        string jar = SafeFiles.UnderRoot(work, installer);
                        using (var archive = ZipFile.OpenRead(jar))
                        {
                            if (archive.GetEntry("optifine/Installer.class") is null)
                                throw new InvalidDataException("OptiFine 下载文件不是安装器。");
                        }
                        using (var input = typeof(ComponentInstaller).Assembly.GetManifestResourceStream("OptiFineBridge.class"))
                        using (var output = File.Create(Path.Combine(work, "OptiFineBridge.class")))
                        {
                            input.CopyTo(output);
                        }
                        Emit("processors", "安装 OptiFine", "正在生成 OptiFine 客户端", 0.35d);
                        int minimumJava;
                        using (var archive = ZipFile.OpenRead(jar))
                        using (var reader = new BinaryReader(archive.GetEntry("optifine/Installer.class").Open()))
                        {
                            byte[] header = reader.ReadBytes(8);
                            if (header.Length != 8 || header[0] != 0xCA || header[1] != 0xFE || header[2] != 0xBA || header[3] != 0xBE)
                                throw new InvalidDataException("OptiFine 安装器 class 文件头无效。");
                            minimumJava = Math.Max(8, header[6] * 256 + header[7] - 44);
                        }
                        string java = JavaRuntime.Find(_selection.JavaPath, minimumJava);
                        await JavaRuntime.RunAsync(java, new[] { "-Djava.awt.headless=true", "-Duser.home=" + work, "-cp", work + Path.PathSeparator + jar, "OptiFineBridge", work }, work, ProcessorLine, token).ConfigureAwait(false);
                        var profiles = Directory.EnumerateFiles(Path.Combine(work, "versions"), "*.json", SearchOption.AllDirectories).Where(p => !string.Equals(p, vanilla + ".json", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (profiles.Count != 1)
                            throw new InvalidDataException("OptiFine 未生成唯一的启动配置。");
                        merged = MergeProfile(merged, JObject.Parse(File.ReadAllText(profiles[0])));
                    }
                }
                foreach (var kind in new[] { ComponentKind.FabricApi, ComponentKind.OptiFabric })
                {
                    var entry = _selection.Item(kind);
                    if (entry is null)
                        continue;
                    SafeFiles.ValidatePathSegment(entry.FileName);
                    await FetchAsync(stage, new[] { new DownloadFile() { RelativePath = "mods/" + entry.FileName, Url = entry.Url, Sha1 = entry.Sha1, Md5 = entry.Md5, Size = entry.Size, Category = "component" } }, "下载 " + ComponentCatalog.Label(kind), token).ConfigureAwait(false);
                    if (kind == ComponentKind.OptiFabric)
                    {
                        var info = ReadFabricMod(SafeFiles.UnderRoot(stage, "mods/" + entry.FileName));
                        if ((string)info["id"] != "optifabric")
                            throw new InvalidDataException("下载的文件不是 OptiFabric 模组。");
                    }
                }
                // Download all final launch libraries, including legacy Forge/LiteLoader.
                merged = MinecraftInstaller.PreparePclLibraryPaths(merged);
                var libraries = MinecraftInstaller.BuildBasePlan(merged, name).Where(f => f.Category == "library").ToList();
                var missing = new List<DownloadFile>();
                foreach (var libraryInfo in libraries)
                {
                    string local = SafeFiles.UnderRoot(work, libraryInfo.RelativePath);
                    if (!SafeFiles.Matches(local, libraryInfo))
                    {
                        if (string.IsNullOrWhiteSpace(libraryInfo.Url))
                            throw new FileNotFoundException("安装器缺少生成的支持库：" + libraryInfo.RelativePath);
                        missing.Add(libraryInfo);
                    }
                }
                await FetchAsync(work, missing.Where(item => ComponentRuntimePublication.HasDigest(item)), "补全组件支持库", token).ConfigureAwait(false);
                // A same-sized shared hashless file is not a trusted source. Fetch these
                // into this fresh private work tree without root donors or weak caches.
                await FetchAsync(work, missing.Where(item => !ComponentRuntimePublication.HasDigest(item)), "补全组件支持库", token, allowReuse: false).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Emit("processors", "校验安装结果", "校验处理器输出与启动支持库", 0.95d);
                foreach (var libraryInfo in libraries)
                {
                    if (!SafeFiles.Matches(SafeFiles.UnderRoot(work, libraryInfo.RelativePath), libraryInfo))
                        throw new InvalidDataException("组件支持库校验失败：" + libraryInfo.RelativePath);
                }
                // Publish only the checked runtime plan. Embedded processor dependencies and
                // unrelated installer files remain private, even if their paths collide.
                using (var publication = new ComponentRuntimePublication(_root))
                {
                    publication.Publish(work, libraries, token);
                    _publishedRuntimes = publication.FrozenFiles;
                }
                if (IsModable(_selection))
                    Directory.CreateDirectory(Path.Combine(stage, "mods"));
                Directory.CreateDirectory(Path.Combine(stage, "resourcepacks"));
                Directory.CreateDirectory(Path.Combine(stage, "PCL"));
                if (writePclSettings)
                    File.WriteAllText(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentIndie:" + (isolated ? "1" : "2") + Constants.vbCrLf + "VersionArgumentIndieV2:" + isolated.ToString() + Constants.vbCrLf, new UTF8Encoding(false));
                var @record = new JObject() { { "minecraft", mc }, { "components", JArray.FromObject(_selection.Entries.Select(v => new { kind = ComponentCatalog.Label(v.Key), version = v.Value.Version })) } };
                File.WriteAllText(Path.Combine(stage, "installation.json"), @record.ToString(), new UTF8Encoding(false));
                return merged;
            }
            finally
            {
                InstallCleanup.DeleteOwnedDirectory(_root, workRelative);
            }
        }

        internal static bool IsModable(ComponentSelection selection)
        {
            return selection is not null && new[] { ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.Fabric, ComponentKind.LiteLoader }.Any(kind => selection.Has(kind));
        }
        internal static List<DownloadFile> ModFiles(ComponentSelection selection, string mc)
        {
            var result = new List<DownloadFile>();
            if (selection is null)
                return result;
            foreach (var kind in new[] { ComponentKind.OptiFine, ComponentKind.FabricApi, ComponentKind.OptiFabric })
            {
                var entry = selection.Item(kind);
                if (entry is null)
                    continue;
                if (kind == ComponentKind.OptiFine && !(selection.Has(ComponentKind.Forge) || selection.Has(ComponentKind.Fabric) || selection.Has(ComponentKind.LiteLoader)))
                    continue;
                SafeFiles.ValidatePathSegment(entry.FileName);
                string relative = "mods/" + (kind == ComponentKind.OptiFine && selection.Has(ComponentKind.LiteLoader) ? mc + "/" : "") + entry.FileName;
                MinecraftInstaller.ValidateResourceName(relative, "组件 Mods");
                if (result.Any(item => string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("组件 Mods 文件名冲突：" + relative);
                result.Add(new DownloadFile() { RelativePath = relative, Url = entry.Url, Sha1 = entry.Sha1, Md5 = entry.Md5, Size = entry.Size, Category = "component" });
            }
            return result;
        }

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
            files = DownloadPlan.Merge(files, work).Where(item => !SafeFiles.Matches(SafeFiles.UnderRoot(work, item.RelativePath), item)).ToList();
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
                            if (SafeFiles.Matches(output.Key, output.Value))
                                continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(output.Key));
                            outputCache.TryRestore(output.Key, output.Value, token);
                        }
                        reusedOutputs = outputs.All(item => SafeFiles.Matches(item.Key, item.Value));
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
                        if (!SafeFiles.Matches(output.Key, output.Value))
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

        private async Task FetchAsync(string root, IEnumerable<DownloadFile> files, string title, CancellationToken token, bool allowReuse = true)
        {
            var items = DownloadPlan.Merge(files, root);
            if (items.Count == 0)
                return;
            var reporter = new InlineProgress(value =>
                {
                    value.Phase = "components";
                    value.Stage = title;
                    value.StageFraction = value.Fraction;
                    value.NetworkBytes += NetworkBytes;
                    value.Reused += Reused;
                    value.DynamicSplits += DynamicSplits;
                    value.Retries += Retries;
                    _progress?.Report(value);
                });
            // Installer-profile dependencies can include the final runtime too, before
            // the final missing-library pass. Apply the same donor rule at every fetch
            // boundary, not only in that final pass, or a bad shared file could already
            // have been copied into work and then acquire a self-consistent local hash.
            foreach (var batch in items.GroupBy(item => allowReuse && ComponentRuntimePublication.HasDigest(item)))
            {
                bool reusable = batch.Key;
                using (var engine = new DownloadEngine(_policy, reporter, reusable ? _reuse : Enumerable.Empty<string>(), _connections, reusable ? _root : null, useComponentCache: reusable, budget: _budget))
                {
                    engine.BytesPerSecondLimit = _limit;
                    await engine.DownloadAsync(root, batch, token).ConfigureAwait(false);
                    NetworkBytes += engine.NetworkBytes;
                    Reused += engine.Reused;
                    DynamicSplits += engine.DynamicSplits;
                    Retries += engine.Retries;
                    FileCount += batch.Count();
                }
            }
        }
        private async Task FetchOptiFineAsync(string root, string relative, ComponentEntry entry, CancellationToken token)
        {
            Func<Task> mirror = async () =>
                {
                    await FetchAsync(root, new[] { new DownloadFile() { RelativePath = relative, Url = entry.Url, Category = "component" } }, "下载 OptiFine（BMCLAPI）", token).ConfigureAwait(false);
                    ValidateOptiFine(SafeFiles.UnderRoot(root, relative));
                };
            Func<Task> official = () => FetchOptiFineOfficialAsync(root, relative, entry, token);
            var failures = new List<Exception>();
            foreach (var download in _policy.PreferOfficial ? (new[] { official, mirror }) : (new[] { mirror, official }))
            {
                try
                {
                    await download().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures.Add(ex);
                    string invalid = SafeFiles.UnderRoot(root, relative);
                    if (File.Exists(invalid))
                        File.Delete(invalid); // owned private payload only
                }
            }
            token.ThrowIfCancellationRequested();
            throw new AggregateException("OptiFine 官方源与 BMCLAPI 均下载失败。", failures);
        }
        private async Task FetchOptiFineOfficialAsync(string root, string relative, ComponentEntry entry, CancellationToken token)
        {
            // PCL's official adloadx -> downloadx resolution, still using the shared engine.
            string page = await _policy.GetTextAsync("https://optifine.net/adloadx?f=" + Uri.EscapeDataString(entry.FileName), token).ConfigureAwait(false);
            var match = Regex.Match(page, @"downloadx\?f=[^""'\s<>]+");
            if (!match.Success)
                throw new InvalidDataException("OptiFine 官方下载链接解析失败。");
            string source = "https://optifine.net/" + System.Net.WebUtility.HtmlDecode(match.Value);
            string path = SafeFiles.UnderRoot(root, relative);
            if (File.Exists(path))
                File.Delete(path); // only this private staging payload
            await FetchAsync(root, new[] { new DownloadFile() { RelativePath = relative, Url = source, Category = "component" } }, "下载 OptiFine（官方源）", token).ConfigureAwait(false);
            ValidateOptiFine(path);
        }
        private void ValidateOptiFine(string path)
        {
            // PCL McDownloadOptiFineSaveLoader uses 64 KiB for a mod, while
            // McDownloadOptiFineLoader requires 300 KiB for standalone installation.
            bool asMod = _selection.Has(ComponentKind.Forge) || _selection.Has(ComponentKind.Fabric) || _selection.Has(ComponentKind.LiteLoader);
            long minimum = (asMod ? 64 : 300) * 1024L;
            if (new FileInfo(path).Length < minimum)
                throw new InvalidDataException("OptiFine 下载文件过小，至少需要 " + (minimum / 1024L).ToString() + " KiB。");
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.GetEntry("optifine/Installer.class") is null)
                    throw new InvalidDataException("下载文件不是有效的 OptiFine 安装器。");
            }
        }
        public static JObject ReadFabricMod(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                return ReadJson(archive, "fabric.mod.json");
            }
        }
        private void Emit(string phase, string stage, string detail, double fraction)
        {
            _progress?.Report(new TransferProgress() { Phase = phase, Stage = stage, Detail = detail, StageFraction = fraction, NetworkBytes = NetworkBytes, Reused = Reused, DynamicSplits = DynamicSplits, Retries = Retries });
        }
        private void ProcessorLine(string line)
        {
            // Logging is best effort; a blocked log must not stop Java or UI progress.
            lock (this)
            {
                InstallLog.Append(_root, line);
                if (_processorClock.ElapsedMilliseconds - _lastProcessorReport < 120L)
                    return;
                _lastProcessorReport = _processorClock.ElapsedMilliseconds;
            }
            Emit("processors", "运行安装处理器", line, -1);
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

    public class InlineProgress : IProgress<TransferProgress>
    {
        private readonly Action<TransferProgress> _report;
        public InlineProgress(Action<TransferProgress> report)
        {
            _report = report;
        }
        public void Report(TransferProgress value)
        {
            _report(value);
        }
    }

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    public sealed class JavaRuntime
    {
        private JavaRuntime() { }

        public static string Find(string explicitPath, int minimum)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string full = Path.GetFullPath(explicitPath);
                if (!File.Exists(full))
                    throw new FileNotFoundException("找不到所选 Java：" + full);
                if (Major(full) < minimum)
                    throw new InvalidOperationException("安装器需要 Java " + minimum + " 或更高版本。");
                return full;
            }
            var candidates = new List<string>();
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
                candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
            foreach (var directory in Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    candidates.Add(Path.Combine(directory.Trim('"'), "java.exe"));
            }
            foreach (var @base in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks") })
            {
                if (Directory.Exists(@base))
                {
                    foreach (var directory in Directory.EnumerateDirectories(@base))
                        candidates.Add(Path.Combine(directory, "bin", "java.exe"));
                }
            }
            var available = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => File.Exists(p)).Select(p => new { Path = p, Version = Major(p) }).Where(j => j.Version >= minimum).OrderBy(j => j.Version).ToList();
            if (available.Count == 0)
                throw new InvalidOperationException("未发现可用的 Java " + minimum + " 或更高版本。请在下载设置中选择 java.exe。");
            return available[0].Path;
        }
        private static int Major(string java)
        {
            try
            {
                using (var process = new Process() { StartInfo = new ProcessStartInfo(java, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } })
                {
                    var clock = Stopwatch.StartNew();
                    process.Start();
                    using (var output = process.StandardOutput)
                    using (var errors = process.StandardError)
                    {
                        // Both pipes must drain before waiting; JVM option notices can fill either.
                        var stdout = ReadMajorAsync(output);
                        var stderr = ReadMajorAsync(errors);
                        if (!process.WaitForExit((int)Math.Max(0L, 5000L - clock.ElapsedMilliseconds)))
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                            return 0;
                        }
                        if (!Task.WhenAll(stdout, stderr).Wait((int)Math.Max(0L, 5000L - clock.ElapsedMilliseconds)))
                            return 0;
                        if (process.ExitCode != 0)
                            return 0;
                        return stderr.Result > 0 ? stderr.Result : stdout.Result;
                    }
                }
            }
            catch
            {
                return 0;
            }
        }
        private async static Task<int> ReadMajorAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            var line = new StringBuilder();
            var overflow = default(bool);
            var version = default(int);
            try
            {
                do
                {
                    int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    for (int index = 0, loopTo = count - 1; index <= loopTo; index++)
                    {
                        if (buffer[index] == ControlChars.Cr || buffer[index] == ControlChars.Lf)
                        {
                            if (version == 0 && !overflow)
                                version = ParseMajor(line.ToString());
                            line.Clear();
                            overflow = false;
                        }
                        else if (version == 0 && !overflow)
                        {
                            if (line.Length == 4096)
                            {
                                line.Clear();
                                overflow = true;
                            }
                            else
                            {
                                line.Append(buffer[index]);
                            }
                        }
                    }
                }
                while (true);
                if (version == 0 && !overflow)
                    version = ParseMajor(line.ToString());
                return version;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }
        private static int ParseMajor(string line)
        {
            // PCL also recognizes the unquoted OpenJDK banner. Ignore option echoes.
            var match = Regex.Match(line, @"\A\s*(?:(?:java|openjdk)(?: full)? version ""(?<version>[0-9][0-9A-Za-z._+\-]{0,127})""|openjdk (?<version>[0-9][0-9A-Za-z._+\-]{0,127})(?=\s|\z))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return 0;
            string value = match.Groups["version"].Value;
            if (value.StartsWith("1.", StringComparison.Ordinal))
                value = value.Substring(2);
            int result;
            return int.TryParse(value.Split('.', '_', '+', '-')[0], out result) && result > 0 ? result : 0;
        }
        public async static Task RunAsync(string java, IEnumerable<string> arguments, string work, Action<string> report, CancellationToken token)
        {
            var info = new ProcessStartInfo() { FileName = java, Arguments = string.Join(" ", arguments.Select(Quote)), WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var process = new Process() { StartInfo = info, EnableRaisingEvents = true })
            {
                process.Exited += (sender, e) => completion.TrySetResult(true);
                var lines = new Queue<string>();
                var outputEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var errorEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool accepting = true;
                // A full/removed log destination must not crash a stream callback.
                Action<string> consume = line => { lock (lines) { if (!accepting) return; lines.Enqueue(line); if (lines.Count > 12) lines.Dequeue(); try { report?.Invoke(line); } catch (IOException) { } } };
                DataReceivedEventHandler output = (sender, e) => { if (e.Data is null) outputEnd.TrySetResult(true); else consume(e.Data); };
                DataReceivedEventHandler errors = (sender, e) => { if (e.Data is null) errorEnd.TrySetResult(true); else consume(e.Data); };
                process.OutputDataReceived += output;
                process.ErrorDataReceived += errors;
                try
                {
                    token.ThrowIfCancellationRequested();
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    using (var registration = token.Register(() =>
                        {
                            cancelled.TrySetResult(true);
                            try
                            {
                                if (!process.HasExited)
                                    process.Kill();
                            }
                            catch (InvalidOperationException)
                            {
                            }
                        }))
                    {
                        await completion.Task.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        // A descendant can retain these pipes after the owned process exits.
                        // Bound EOF draining, and keep cancellation responsive during it.
                        var streams = Task.WhenAll(outputEnd.Task, errorEnd.Task);
                        var finished = await Task.WhenAny(streams, Task.Delay(5000), cancelled.Task).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        string tail;
                        lock (lines)
                            tail = string.Join(Constants.vbCrLf, lines);
                        if (!ReferenceEquals(finished, streams))
                            throw new IOException("Java 安装处理器已退出 (" + process.ExitCode + ")，但输出流未在 5 秒内结束，请重试。" + Constants.vbCrLf + tail);
                        if (process.ExitCode != 0)
                            throw new IOException("Java 安装处理器失败 (" + process.ExitCode + ")：" + tail);
                    }
                }
                finally
                {
                    lock (lines)
                        accepting = false;
                    process.OutputDataReceived -= output;
                    process.ErrorDataReceived -= errors;
                    try
                    {
                        process.CancelOutputRead();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    try
                    {
                        process.CancelErrorRead();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
        public static string Quote(string value)
        {
            if (value.Contains(Constants.vbCr) || value.Contains(Constants.vbLf))
                throw new ArgumentException("进程参数含换行。");
            // Windows CommandLineToArgvW escaping, including trailing backslashes.
            return "\"" + Regex.Replace(value, @"(\\*)""", m => new string('\\', m.Groups[1].Length * 2 + 1) + "\"") + (value.EndsWith(@"\", StringComparison.Ordinal) ? new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count()) : "") + "\"";
        }
    }
}
