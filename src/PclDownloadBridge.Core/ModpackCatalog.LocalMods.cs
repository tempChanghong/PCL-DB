using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public sealed partial class ModpackCatalogClient
    {
        // PCL 5ef81e4 LocalResourceLoaders: identify the actual installed bytes;
        // never use a display name or a guessed project ID to offer an update.
        public async Task<LocalModUpdateCheckResult> CheckLocalModsAsync(InstalledInstance instance, IEnumerable<LocalModFingerprint> files, CancellationToken token)
        {
            var entries = files.ToList();
            var matches = new List<LocalModUpdate>();
            var errors = new List<string>();
            Task<LocalModUpdateCheckResult>[] checks = new[] { CheckLocalPlatformAsync(ModpackPlatform.Modrinth, instance, entries, token), CheckLocalPlatformAsync(ModpackPlatform.CurseForge, instance, entries, token) };
            // Observe both platform tasks even when their shared caller cancels.
            LocalModUpdateCheckResult[] results = await Task.WhenAll(checks).ConfigureAwait(false);
            foreach (var result in results)
            {
                matches.AddRange(result.Matches);
                errors.AddRange(result.Errors);
            }
            token.ThrowIfCancellationRequested();
            return new LocalModUpdateCheckResult(matches.GroupBy(item => item.Entry.FilePath, StringComparer.OrdinalIgnoreCase).Select(@group => @group.OrderByDescending(item => item.HasUpdate).ThenByDescending(item => (item.Release ?? item.Current).Published).ThenByDescending(item => item.Project.Platform == ModpackPlatform.Modrinth).First()), errors);
        }

        private async Task<LocalModUpdateCheckResult> CheckLocalPlatformAsync(ModpackPlatform platform, InstalledInstance instance, List<LocalModFingerprint> entries, CancellationToken token)
        {
            var matches = new List<LocalModUpdate>();
            try
            {
                for (int offset = 0, loopTo = entries.Count - 1; offset <= loopTo; offset += 40)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = entries.Skip(offset).Take(40).ToList();
                    matches.AddRange(await (platform == ModpackPlatform.Modrinth ? CheckRinthModsAsync(instance, batch, token) : CheckCurseModsAsync(instance, batch, token)).ConfigureAwait(false));
                }
                return new LocalModUpdateCheckResult(matches);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LocalModUpdateCheckResult(matches, new[] { platform.ToString() + "：" + ex.Message });
            }
        }

        private async Task<List<LocalModUpdate>> CheckRinthModsAsync(InstalledInstance instance, List<LocalModFingerprint> batch, CancellationToken token)
        {
            JObject identified = await TokenAsync("https://api.modrinth.com/v2/version_files", token, new JObject() { { "hashes", new JArray(batch.Select(item => item.Sha1).Distinct()) }, { "algorithm", "sha1" } }).ConfigureAwait(false) as JObject;
            if (identified is null)
                throw new InvalidDataException("Modrinth 文件信息无效。");
            var known = new List<LocalModUpdate>();
            foreach (var file in batch)
            {
                JObject raw = identified[file.Sha1] as JObject;
                if (raw is null)
                    continue;
                var project = new ModpackProject() { Kind = CommunityResourceKind.Mod, Platform = ModpackPlatform.Modrinth, Id = Text(raw, "project_id"), Name = file.Entry.DisplayName };
                var current = ParseRelease(project, raw);
                if (!file.Matches(current))
                    continue;
                known.Add(new LocalModUpdate(file.Entry, project, current, null));
            }
            var results = new List<LocalModUpdate>();
            foreach (var group in known.GroupBy(item => string.Join(",", item.Current.Loaders.Select(value => value.ToLowerInvariant()).Distinct().OrderBy(value => value))))
            {
                var hashes = batch.Where(file => @group.Any(item => ReferenceEquals(item.Entry, file.Entry))).ToList();
                JObject updated = await TokenAsync("https://api.modrinth.com/v2/version_files/update", token, new JObject() { { "hashes", new JArray(hashes.Select(item => item.Sha1).Distinct()) }, { "algorithm", "sha1" }, { "loaders", new JArray(@group.First().Current.Loaders) }, { "game_versions", new JArray(instance.MinecraftVersion) } }).ConfigureAwait(false) as JObject;
                if (updated is null)
                    throw new InvalidDataException("Modrinth 更新信息无效。");
                foreach (var item in group)
                {
                    var file = hashes.First(value => ReferenceEquals(value.Entry, item.Entry));
                    JObject raw = updated[file.Sha1] as JObject;
                    ModpackRelease candidate = null;
                    if (raw is not null && (Text(raw, "project_id") ?? "") == (item.Project.Id ?? ""))
                        candidate = ParseRelease(item.Project, raw);
                    results.Add(new LocalModUpdate(item.Entry, item.Project, item.Current, LocalModUpdate.Compatible(instance, item.Current, candidate) ? candidate : null));
                }
            }
            return results;
        }

        private async Task<List<LocalModUpdate>> CheckCurseModsAsync(InstalledInstance instance, List<LocalModFingerprint> batch, CancellationToken token)
        {
            JObject root = await TokenAsync("https://api.curseforge.com/v1/fingerprints/432", token, new JObject() { { "fingerprints", new JArray(batch.Select(item => item.CurseForge).Distinct()) } }).ConfigureAwait(false) as JObject;
            if (root is null)
                throw new InvalidDataException("CurseForge 文件信息无效。");
            var known = new List<LocalModUpdate>();
            foreach (var match in ArrayValue(ObjectValue(root, "data"), "exactMatches").OfType<JObject>())
            {
                var raw = ObjectValue(match, "file");
                long fingerprint = Number(raw, "fileFingerprint");
                var project = new ModpackProject() { Kind = CommunityResourceKind.Mod, Platform = ModpackPlatform.CurseForge, Id = Text(match, "id") };
                var current = ParseRelease(project, raw);
                foreach (var file in batch.Where(item => item.CurseForge == fingerprint && item.Matches(current)))
                {
                    project.Name = file.Entry.DisplayName;
                    known.Add(new LocalModUpdate(file.Entry, project, current, null));
                }
            }
            if (known.Count == 0)
                return known;
            JObject projectsRoot = await TokenAsync("https://api.curseforge.com/v1/mods", token, new JObject() { { "modIds", new JArray(known.Select(item => long.Parse(item.Project.Id)).Distinct()) } }).ConfigureAwait(false) as JObject;
            if (projectsRoot is null)
                throw new InvalidDataException("CurseForge 项目信息无效。");
            var ids = new HashSet<long>();
            var projects = new Dictionary<string, ModpackProject>(StringComparer.Ordinal);
            foreach (var raw in ArrayValue(projectsRoot, "data").OfType<JObject>())
            {
                string id = Text(raw, "id");
                if (!known.Any(item => (item.Project.Id ?? "") == (id ?? "")) || !CommunityResources.Matches(ModpackPlatform.CurseForge, raw, CommunityResourceKind.Mod))
                    continue;
                projects[id] = ParseProject(ModpackPlatform.CurseForge, raw, false, CommunityResourceKind.Mod);
                foreach (var index in ArrayValue(raw, "latestFilesIndexes").OfType<JObject>())
                {
                    if ((Text(index, "gameVersion") ?? "") != (instance.MinecraftVersion ?? ""))
                        continue;
                    string loader = CurseLoader(Number(index, "modLoader"));
                    if (loader is not null && known.Any(item => (item.Project.Id ?? "") == (id ?? "") && item.Current.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)))
                        ids.Add(Number(index, "fileId"));
                }
            }
            var candidates = new List<ModpackRelease>();
            for (int offset = 0, loopTo = ids.Count - 1; offset <= loopTo; offset += 40)
            {
                JObject rawFiles = await TokenAsync("https://api.curseforge.com/v1/mods/files", token, new JObject() { { "fileIds", new JArray(ids.Skip(offset).Take(40)) } }).ConfigureAwait(false) as JObject;
                if (rawFiles is null)
                    throw new InvalidDataException("CurseForge 更新信息无效。");
                foreach (var raw in ArrayValue(rawFiles, "data").OfType<JObject>())
                {
                    ModpackProject project = null;
                    if (!ids.Contains(Number(raw, "id")) || !projects.TryGetValue(Text(raw, "modId"), out project))
                        continue;
                    var candidate = ParseRelease(project, raw);
                    if (candidate is not null)
                        candidates.Add(candidate);
                }
            }
            return known.Select(item => new LocalModUpdate(item.Entry, projects.ContainsKey(item.Project.Id) ? projects[item.Project.Id] : item.Project, item.Current, candidates.Where(candidate => LocalModUpdate.Compatible(instance, item.Current, candidate)).OrderByDescending(candidate => candidate.Published).FirstOrDefault())).ToList();
        }
        private static string CurseLoader(long value)
        {
            switch (value)
            {
                case 1L:
                    {
                        return "forge";
                    }

                case 3L:
                    {
                        return "liteloader";
                    }

                case 4L:
                    {
                        return "fabric";
                    }

                case 5L:
                    {
                        return "quilt";
                    }

                case 6L:
                    {
                        return "neoforge";
                    }

                default:
                    {
                        return null;
                    }
            }
        }
    }
}
