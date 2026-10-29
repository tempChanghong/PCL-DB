using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // Main behavioral reference: PCL 5ef81e4 McDownloadClientLoader, McLibListGetWithJson,
    // McJsonRuleCheck and McAssetsListGet. Keeps the selected PCL root and shared stores.
    public class MinecraftInstaller
    {
        private readonly SourcePolicy _policy;
        private readonly string _pclDirectory;
        private readonly int? _newInstanceIsolationStrategy;
        private readonly bool _directoryOnly;
        private readonly LauncherInstallTarget _launcherTarget;
        public MinecraftInstaller(SourcePolicy policy, string pclDirectory = null, int? newInstanceIsolationStrategy = default, bool directoryOnly = false, LauncherInstallTarget launcherTarget = null)
        {
            if (newInstanceIsolationStrategy.HasValue && (newInstanceIsolationStrategy.Value < 0 || newInstanceIsolationStrategy.Value > 4))
                throw new ArgumentOutOfRangeException(nameof(newInstanceIsolationStrategy));
            _policy = policy;
            _pclDirectory = pclDirectory;
            _newInstanceIsolationStrategy = newInstanceIsolationStrategy;
            _directoryOnly = directoryOnly;
            _launcherTarget = launcherTarget;
        }

        public async Task<List<VersionEntry>> VersionsAsync(CancellationToken token)
        {
            var manifest = await _policy.GetVersionManifestAsync(token).ConfigureAwait(false);
            return ((JArray)manifest["versions"]).OfType<JObject>().Select(v => new VersionEntry()
            {
                Id = (string)v["id"],
                Kind = (string)v["type"],
                Url = (string)v["url"],
                Sha1 = (string)v["sha1"],
                Released = DateTimeOffset.Parse((string)v["releaseTime"], System.Globalization.CultureInfo.InvariantCulture)
            }).ToList();
        }

        public async Task<InstallResult> InstallAsync(VersionEntry entry, string root, string instanceName, IEnumerable<string> reuseRoots, IProgress<TransferProgress> progress, CancellationToken token, bool repairExisting = false, int connections = 16, long limit = 0L, ComponentSelection components = null, InstanceIsolationMode isolationMode = InstanceIsolationMode.FollowPcl)
        {
            return await InstallCoreAsync(entry, root, instanceName, reuseRoots, progress, token, repairExisting, connections, limit, components, isolationMode, null).ConfigureAwait(false);
        }
        internal Task<InstallResult> InstallPackAsync(VersionEntry entry, string root, string instanceName, IEnumerable<string> reuseRoots, IProgress<TransferProgress> progress, CancellationToken token, ComponentSelection components, int connections, long limit, ModpackInstallPlan pack)
        {
            return InstallCoreAsync(entry, root, instanceName, reuseRoots, progress, token, false, connections, limit, components, InstanceIsolationMode.Enabled, pack);
        }
        private async Task<InstallResult> InstallCoreAsync(VersionEntry entry, string root, string instanceName, IEnumerable<string> reuseRoots, IProgress<TransferProgress> progress, CancellationToken token, bool repairExisting, int connections, long limit, ComponentSelection components, InstanceIsolationMode isolationMode, ModpackInstallPlan pack)
        {
            token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(typeof(InstanceIsolationMode), isolationMode))
                throw new ArgumentOutOfRangeException(nameof(isolationMode));
            if (entry is null)
                throw new ArgumentNullException(nameof(entry));
            if (_directoryOnly && (repairExisting || entry.IsSupplemental || pack is not null || components is not null && components.Entries.Count > 0 || isolationMode != InstanceIsolationMode.FollowPcl || _newInstanceIsolationStrategy.HasValue || _pclDirectory is not null))
            {
                throw new NotSupportedException("外部启动器目录模式仅支持标准原版新安装。");
            }
            if (reuseRoots is null)
                throw new ArgumentNullException(nameof(reuseRoots));
            entry = entry.Copy();
            _launcherTarget?.ValidateDestination(root);
            if (_launcherTarget is not null && _launcherTarget.Kind == ExternalLauncherKind.Hmcl && new[] { "modpack", "minecraftinstance", "manifest" }.Contains(instanceName, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("HMCL 保留了此实例名称，请使用其他名称。");
            bool nativeTarget = _launcherTarget is not null && _launcherTarget.Kind != ExternalLauncherKind.Pcl;
            InstallPreflight.ValidateDestination(root, instanceName, repairExisting);
            if (pack is not null)
            {
                if ((entry.Id ?? "") != (pack.Minecraft ?? "") || repairExisting || isolationMode != InstanceIsolationMode.Enabled)
                    throw new InvalidDataException("整合包必须使用指定原版和新的隔离实例。");
                pack.ValidateTargets(instanceName);
            }
            if (components is not null)
            {
                components = components.Copy();
                if (entry.IsSupplemental && components.Entries.Count > 0)
                    throw new InvalidOperationException("补充版本目前仅支持原版安装，请取消加载器选择。");
                components.Validate(entry.Id);
                if (repairExisting && components.Entries.Count > 0)
                    throw new InvalidOperationException("组件安装请使用新实例名称；原版校验不会修改加载器实例。");
            }
            // Snapshot caller-owned choices once, before any progress callback or
            // await can expose this task to catalogue/list changes. Content reuse is
            // still validated later; this captures directories, never trusted bytes.
            reuseRoots = reuseRoots.ToList();
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            string cache = SafeFiles.UnderRoot(root, ".vanilla-downloader/install.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            string instancePath = SafeFiles.UnderRoot(root, "versions/" + instanceName);
            string jsonTarget = SafeFiles.UnderRoot(root, "versions/" + instanceName + "/" + instanceName + ".json");
            // One writer per selected game root protects shared libraries/assets across processes.
            using (var rootLock = new FileStream(cache, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Report(progress, "准备安装", "读取并校验版本元数据");
                var metadata = await _policy.GetJsonAsync(entry.Url, token, entry.Sha1).ConfigureAwait(false);
                SupplementalVersions.ValidateMetadata(entry, metadata);
                if (entry.IsSupplemental)
                    metadata = await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, _policy, token).ConfigureAwait(false);
                metadata = PreparePclLibraryPaths(metadata);
                Report(progress, "准备安装", "检查组件安装所需的 Java 环境");
                InstallPreflight.VerifyJava(components, metadata);
                token.ThrowIfCancellationRequested();
                JObject existing = null;
                byte[] existingBytes = null;
                if (repairExisting)
                {
                    existingBytes = File.ReadAllBytes(jsonTarget);
                    using (var reader = new StreamReader(new MemoryStream(existingBytes), Encoding.UTF8, true))
                    {
                        existing = JObject.Parse(reader.ReadToEnd());
                    }
                    if (nativeTarget && _launcherTarget.Kind == ExternalLauncherKind.Hmcl)
                        existing = HmclVanillaMetadata(existing, entry.Id);
                    if (entry.IsSupplemental)
                        SupplementalLaunchRecipe.ValidateExisting(entry.Id, existing);
                    if (entry.IsSupplemental && (existing.Property("assetIndex") is null || existing["libraries"] is JArray && ((JArray)existing["libraries"]).OfType<JObject>().Any(library => library.Property("downloads") is null)))
                    {
                        throw new InvalidDataException("现有历史实例缺少完整的支持库或资源索引声明，请使用新名称安装；不会迁移或改写已有配置。");
                    }
                }
                bool legacySupplementalIndex = entry.IsSupplemental && PrepareSupplementalAssetIndex(metadata, existing);
                if (repairExisting)
                {
                    if (existing["inheritsFrom"] is not null || ((string)existing["mainClass"] ?? "") != ((string)metadata["mainClass"] ?? "") || ((string)existing.SelectToken("downloads.client.sha1") ?? "") != ((string)metadata.SelectToken("downloads.client.sha1") ?? "") || string.IsNullOrEmpty((string)metadata.SelectToken("downloads.client.sha1")))
                        throw new InvalidDataException("只支持校验同版本的原版实例；不会修改加载器或继承版本。");
                    if (!JToken.DeepEquals(existing["libraries"], metadata["libraries"]))
                        throw new InvalidDataException("现有实例支持库经过修改，请使用新名称安装原版。");
                    if (existing["clientVersion"] is not null && ((string)existing["clientVersion"] ?? "") != (entry.Id ?? ""))
                        throw new InvalidDataException("现有实例的 PCL 游戏版本与所选版本不一致，请使用新名称安装。");
                    if (!JToken.DeepEquals(existing["assetIndex"], metadata["assetIndex"]) || !JToken.DeepEquals(existing["assets"], metadata["assets"]))
                        throw new InvalidDataException("现有实例的资源索引经过修改，请使用新名称安装原版。");
                    if (!JToken.DeepEquals(existing.SelectToken("logging.client.file"), metadata.SelectToken("logging.client.file")))
                        throw new InvalidDataException("现有实例的日志文件声明经过修改，请使用新名称安装原版。");
                }
                var plan = BuildBasePlan(metadata, instanceName);
                if (entry.IsSupplemental)
                    SupplementalVersions.ValidatePlan(plan);
                JObject index = metadata["assetIndex"] as JObject;
                if (index is null)
                    throw new NotSupportedException("这个早期版本没有资源索引，首版暂不支持安装。");
                string indexId = (string)index["id"];
                if (entry.IsSupplemental)
                    SupplementalVersions.ValidatePlan(new[] { ToFile(index, "assets/indexes/index.json", "meta") });
                SafeFiles.ValidateInstanceName(indexId);
                string indexRelative = "assets/indexes/" + indexId + ".json";
                if (legacySupplementalIndex)
                {
                    string indexTarget = SafeFiles.UnderRoot(root, indexRelative);
                    if (File.Exists(indexTarget) && !SafeFiles.Matches(indexTarget, ToFile(index, indexRelative, "meta")))
                        throw new InvalidDataException("旧补充实例的共享资源索引与当前文件不一致。请使用新名称重新安装，以免覆盖其他版本的索引。");
                }
                Report(progress, "准备安装", "解析资源索引与支持库");
                var assets = await _policy.GetJsonAsync((string)index["url"], token, (string)index["sha1"]).ConfigureAwait(false);
                plan.Add(new DownloadFile() { RelativePath = indexRelative, Url = (string)index["url"], Size = index["size"] is null ? -1 : (long)index["size"], Sha1 = (string)index["sha1"] });
                var resourceFiles = BuildAssetsPlan(assets);
                var mappedFiles = resourceFiles.Where(item => item.RelativePath.StartsWith("resources/", StringComparison.Ordinal)).ToList();
                if (_directoryOnly && mappedFiles.Count > 0)
                    throw new NotSupportedException("此原版需要旧资源路径适配，当前外部启动器目录模式暂不支持；请选择 PCL 目录。");
                bool withComponents = components is not null && components.Entries.Count > 0;
                bool modable = ComponentInstaller.IsModable(components);
                var componentMods = ComponentInstaller.ModFiles(components, entry.Id);
                IInstanceIsolationPlan isolation = null;
                NativeInstanceIsolation native = null;
                if (nativeTarget)
                {
                    native = NativeInstanceIsolation.Resolve(_launcherTarget, instanceName, entry, metadata, repairExisting, withComponents, isolationMode, modable, _newInstanceIsolationStrategy);
                    native.ValidateDataLocation(mappedFiles.Count > 0 || componentMods.Count > 0 || modable);
                    isolation = native;
                }
                else if (mappedFiles.Count > 0 || !repairExisting && (withComponents || isolationMode != InstanceIsolationMode.FollowPcl || _newInstanceIsolationStrategy.HasValue))
                {
                    isolation = PclIsolationPlan.Resolve(root, instanceName, entry, metadata, _pclDirectory, repairExisting, withComponents, isolationMode, modable, _newInstanceIsolationStrategy);
                }
                if (isolation is not null)
                {
                    if (isolation.Isolated)
                    {
                        foreach (var item in mappedFiles)
                            item.RelativePath = "versions/" + instanceName + "/" + item.RelativePath;
                    }
                }
                var mappedTargets = new HashSet<string>(mappedFiles.Select(item => SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase);
                if (plan.Concat(resourceFiles.Except(mappedFiles)).Any(item => mappedTargets.Contains(SafeFiles.UnderRoot(root, item.RelativePath))))
                    throw new InvalidDataException("旧资源映射与其他下载目标冲突。");
                plan.AddRange(resourceFiles);
                if (entry.IsSupplemental)
                    SupplementalVersions.ValidatePlan(plan);
                // Merge and validate every destination across all branches before content
                // downloads start, so duplicate checks cannot disappear between stages.
                plan = DownloadPlan.Merge(plan, root);
                mappedFiles = plan.Where(item => mappedTargets.Contains(SafeFiles.UnderRoot(root, item.RelativePath))).ToList();
                if (mappedFiles.Count > 0)
                {
                    LegacyResourcePublication.ValidateLayout(root, plan);
                    LegacyResourcePublication.Validate(root, mappedFiles, repairExisting);
                }
                bool sharedComponents = withComponents && !isolation.Isolated;
                if (sharedComponents)
                {
                    SharedModsPublication.Preflight(root, componentMods, modable);
                    LegacyResourcePublication.ValidateLayout(root, plan.Concat(componentMods));
                }
                var sharedMods = new List<DownloadFile>();
                string stageRelative = ".vanilla-downloader/staging/" + Guid.NewGuid().ToString("N") + "/payload";
                string stage = SafeFiles.UnderRoot(root, stageRelative);
                Directory.CreateDirectory(stage);
                ComponentInstaller extension = null;
                try
                {
                    using (var publication = new LegacyResourcePublication(root, stage, instanceName, mappedFiles, isolation is not null && isolation.Isolated, repairExisting))
                    using (var modsPublication = new LegacyResourcePublication(root, stage, instanceName, sharedMods, false, false, "Mods", holdTargets: true))
                    {
                        bool newSettings = isolation is not null && !repairExisting;
                        if (newSettings && native is null)
                            isolation.WriteNewSettings(stage);
                        // Client JAR stays outside versions/ until the entire install succeeds.
                        var client = plan.Single(f => (f.RelativePath ?? "") == ("versions/" + instanceName + "/" + instanceName + ".jar" ?? ""));
                        var supportFiles = plan.Where(f => !ReferenceEquals(f, client) && !mappedFiles.Contains(f) && !f.RelativePath.StartsWith("assets/objects/", StringComparison.Ordinal) && !f.RelativePath.StartsWith("assets/virtual/", StringComparison.Ordinal) && !f.RelativePath.StartsWith("resources/", StringComparison.Ordinal) && (f.RelativePath ?? "") != (indexRelative ?? "")).ToList();
                        var assetFiles = plan.Where(f => !ReferenceEquals(f, client) && !supportFiles.Contains(f) && !mappedFiles.Contains(f)).ToList();
                        long networkBytes;
                        int reused;
                        int splits;
                        using (var budget = new DownloadBudget(connections, limit))
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            var reporting = new InstallationProgress(progress, budget, supportFiles.Count + 1 + (pack is null ? 0 : pack.Files.Count), assetFiles.Count + mappedFiles.Count);
                            var stagedClient = new DownloadFile() { RelativePath = instanceName + ".jar", Url = client.Url, Size = client.Size, Sha1 = client.Sha1, Category = client.Category };
                            using (var clientEngine = new DownloadEngine(_policy, reporting.Branch("client"), reuseRoots, connections, root, true, budget))
                            using (var supportEngine = new DownloadEngine(_policy, reporting.Branch("libraries"), reuseRoots, connections, root, budget: budget))
                            using (var assetsEngine = new DownloadEngine(_policy, reporting.Branch("assets"), reuseRoots, connections, root, budget: budget))
                            using (var mappedEngine = new DownloadEngine(_policy, reporting.Branch("mapped"), reuseRoots.Concat(new[] { isolation is not null && isolation.Isolated ? instancePath : root }), connections, root, budget: budget))
                            using (var packEngine = new DownloadEngine(_policy, reporting.Branch("pack"), reuseRoots, connections, root, true, budget))
                            {
                                // PCL's nonblocking library/resource LoaderCombos share ModNet.
                                // Components need the verified client/support branch, not all assets.
                                Func<Task> supportWork = async () =>
                                        {
                                            var clients = CancelPeersAsync(() => clientEngine.DownloadAsync(stage, new[] { stagedClient }, linked.Token), linked, () => reporting.Failed("files"));
                                            var libraries = CancelPeersAsync(() => supportEngine.DownloadAsync(root, supportFiles, linked.Token), linked, () => reporting.Failed("files"));
                                            await Task.WhenAll(clients, libraries).ConfigureAwait(false);
                                            if (components is not null && components.Entries.Count > 0)
                                            {
                                                extension = new ComponentInstaller(_policy, components, root, reuseRoots, reporting.Branch("components"), connections, limit, budget);
                                                try
                                                {
                                                    metadata = await extension.ApplyAsync(metadata, entry.Id, stage, instanceName, linked.Token, isolation.Isolated, writePclSettings: native is null).ConfigureAwait(false);
                                                }
                                                catch (Exception ex) when (!(ex is OperationCanceledException))
                                                {
                                                    reporting.Failed("components");
                                                    throw;
                                                }
                                                reporting.ComponentsComplete();
                                            }
                                        };
                                Func<Task> assetWork = async () =>
                                        {
                                            await assetsEngine.DownloadAsync(root, assetFiles, linked.Token).ConfigureAwait(false);
                                            if (mappedFiles.Count > 0)
                                                await mappedEngine.DownloadAsync(stage, publication.StagedFiles(), linked.Token).ConfigureAwait(false);
                                        };
                                Func<Task> packWork = async () => { if (pack is not null) await packEngine.DownloadAsync(stage, pack.Files, linked.Token).ConfigureAwait(false); };
                                Task[] branches = new[] { CancelPeersAsync(supportWork, linked), CancelPeersAsync(assetWork, linked, () => reporting.Failed("files")), CancelPeersAsync(packWork, linked, () => reporting.Failed("files")) };
                                try
                                {
                                    await Task.WhenAll(branches).ConfigureAwait(false);
                                }
                                catch
                                {
                                    token.ThrowIfCancellationRequested();
                                    InstallLog.Failure(root, "安装失败", branches.Select(task => (Exception)task.Exception));
                                    var failure = branches.Where(t => t.Exception is not null).SelectMany(t => t.Exception.Flatten().InnerExceptions).FirstOrDefault(e => !(e is OperationCanceledException));
                                    if (failure is not null)
                                        throw failure;
                                    throw;
                                }
                                networkBytes = clientEngine.NetworkBytes + supportEngine.NetworkBytes + assetsEngine.NetworkBytes + mappedEngine.NetworkBytes + packEngine.NetworkBytes;
                                reused = clientEngine.Reused + supportEngine.Reused + assetsEngine.Reused + mappedEngine.Reused + packEngine.Reused;
                                splits = clientEngine.DynamicSplits + supportEngine.DynamicSplits + assetsEngine.DynamicSplits + mappedEngine.DynamicSplits + packEngine.DynamicSplits;
                                if (extension is not null)
                                {
                                    networkBytes += extension.NetworkBytes;
                                    reused += extension.Reused;
                                    splits += extension.DynamicSplits;
                                    plan.AddRange(Enumerable.Repeat(new DownloadFile(), extension.FileCount));
                                }
                            }
                        }
                        if (pack is not null)
                            pack.CopyOverrides(stage, token);
                        if (newSettings && native is not null)
                        {
                            native.WriteNewSettings(stage);
                            if (pack is not null)
                            {
                                pack.NativeConfiguration = native;
                                pack.RefreshNativeHashes(stage);
                            }
                        }
                        if (sharedComponents)
                            sharedMods.AddRange(SharedModsPublication.CheckedFiles(stage, componentMods, token));
                        token.ThrowIfCancellationRequested();
                        Report(progress, "完成安装", "写入版本信息");
                        token.ThrowIfCancellationRequested();
                        if (repairExisting)
                        {
                            // Recheck the exact metadata accepted above, then keep readers allowed
                            // while blocking external edits through the final client replacement.
                            using (var jsonLock = new FileStream(SafeFiles.UnderRoot(root, "versions/" + instanceName + "/" + instanceName + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                using (var current = new MemoryStream())
                                {
                                    if (jsonLock.Length != existingBytes.LongLength)
                                        throw new InvalidDataException("待补全实例的版本 JSON 在校验期间发生变化，请确认后重试。");
                                    jsonLock.CopyTo(current);
                                    if (!existingBytes.SequenceEqual(current.ToArray()))
                                        throw new InvalidDataException("待补全实例的版本 JSON 在校验期间发生变化，请确认后重试。");
                                }
                                using (var isolationGuard = isolation is null ? null : isolation.LockForRepair())
                                {
                                    publication.Publish(token, isolationGuard);
                                    string clientTarget = SafeFiles.UnderRoot(root, client.RelativePath);
                                    bool needsReplacement = !SafeFiles.Matches(clientTarget, client);
                                    token.ThrowIfCancellationRequested();
                                    isolationGuard?.Verify();
                                    if (needsReplacement)
                                        SafeFiles.CommitFile(Path.Combine(stage, instanceName + ".jar"), clientTarget);
                                }
                            }
                        }
                        else
                        {
                            metadata["id"] = instanceName;
                            metadata["clientVersion"] = entry.Id; // Same PCL convention as McDownloadClientLoader.
                            File.WriteAllText(Path.Combine(stage, instanceName + ".json"), metadata.ToString(), new UTF8Encoding(false));
                            // Same-volume rename publishes the entire payload at once. If another
                            // application created this instance, failure leaves stage owned by our
                            // Finally cleanup instead of stranding it in an intermediate folder.
                            token.ThrowIfCancellationRequested();
                            Directory.CreateDirectory(Path.GetDirectoryName(instancePath));
                            token.ThrowIfCancellationRequested();
                            // Component publication has already removed its private work.
                            // Recheck the frozen hashless content and hold readers through
                            // this short instance commit, after all download branches join.
                            using (var runtimeGuard = extension?.LockPublishedRuntimes(token))
                            using (var configurationGuard = native?.LockForRepair())
                            {
                                publication.Publish(token, configurationGuard);
                                modsPublication.Publish(token, configurationGuard);
                                if (sharedComponents)
                                    SharedModsPublication.FinishFolders(root, stage, modable);
                                pack?.WriteLedger(stage, token);
                                token.ThrowIfCancellationRequested();
                                runtimeGuard?.Verify(token);
                                configurationGuard?.Verify();
                                native?.ValidateStaged(stage, token);
                                Directory.Move(stage, instancePath);
                            }
                        }
                        publication.Complete();
                        modsPublication.Complete();
                        // After the filesystem commit, return its real result even if cancellation
                        // arrives during optional cache cleanup; never undo a published instance.
                        VerifiedComponentCache.ReleasePublishedClient(root, client);
                        return new InstallResult() { InstancePath = instancePath, FileCount = plan.Count + 1 + (newSettings ? native is null ? 1 : native.WrittenPaths.Count() : 0) + (pack is null ? 0 : pack.Files.Count + pack.OverrideCount + 1), NetworkBytes = networkBytes, Reused = reused, DynamicSplits = splits };
                    }
                }
                finally
                {
                    // Preserve transfer caches and existing versions. A locked private payload
                    // is logged separately and cannot replace cancellation or the real error.
                    InstallCleanup.DeleteOwnedDirectory(root, stageRelative);
                }
            }
        }

        // HMCL stores vanilla metadata as its game patch. Compare that patch without
        // flattening or rewriting the user's manifest; loaders/inheritance stay refused.
        private static JObject HmclVanillaMetadata(JObject existing, string version)
        {
            if (existing["patches"] is null)
                return existing;
            JArray patches = existing["patches"] as JArray;
            if (patches is null || patches.Count != 1 || !(patches[0] is JObject) || existing["inheritsFrom"] is not null)
                throw new InvalidDataException("只支持补全 HMCL 的独立原版实例。");
            JObject game = (JObject)patches[0];
            if ((string)game["id"] != "game" || ((string)game["version"] ?? "") != (version ?? "") || game["inheritsFrom"] is not null)
                throw new InvalidDataException("HMCL 实例版本与所选原版不一致。");
            return (JObject)game.DeepClone();
        }

        private static async Task CancelPeersAsync(Func<Task> operation, CancellationTokenSource linked, Action failed = null)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                linked.Cancel();
                if (!(ex is OperationCanceledException))
                    failed?.Invoke();
                throw;
            }
        }

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

        // PCL McAssetsGetIndexName and ${assets_index_name} prefer assetIndex.id.
        // Supplemental releases can reuse one upstream ID for different index contents;
        // keep their content-addressed names separate from the ordinary shared index.
        private static bool PrepareSupplementalAssetIndex(JObject metadata, JObject existing)
        {
            JObject index = (JObject)metadata["assetIndex"];
            SafeFiles.ValidateInstanceName((string)index["id"]);
            string isolated = "pcldb-" + ((string)index["sha1"]).ToLowerInvariant();
            // Repair preserves existing JSON bytes. Keep legacy names, but the caller
            // refuses to overwrite an existing index that no longer matches this version.
            if (existing is not null && ((string)existing.SelectToken("assetIndex.id") ?? "") != (isolated ?? ""))
                return true;
            index["id"] = isolated;
            return false;
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

        private static DownloadFile ToFile(JObject data, string relative, string category)
        {
            return new DownloadFile() { RelativePath = relative, Url = (string)data["url"], Size = data["size"] is null || data["size"].Type == JTokenType.Null ? -1 : (long)data["size"], Sha1 = (string)data["sha1"], Category = category };
        }

        private static void Report(IProgress<TransferProgress> progress, string stage, string detail)
        {
            progress?.Report(new TransferProgress() { Phase = stage == "完成安装" ? "commit" : "metadata", Stage = stage, Detail = detail });
        }
    }
}