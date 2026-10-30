using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // Adapted from PCL McInstallLoadersGet / McDownloadForgelikeLoader /
    // McDownloadLiteLoaderLoader / MergeJson. Uses the shared PCL-style downloader
    // for installer dependencies, then runs the official install_profile processors.
    public partial class ComponentInstaller
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
                    if (!SafeFiles.Matches(local, libraryInfo, token))
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
                    if (!SafeFiles.Matches(SafeFiles.UnderRoot(work, libraryInfo.RelativePath), libraryInfo, token))
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
    }
}
