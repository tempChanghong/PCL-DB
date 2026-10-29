using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public class CurseForgeInstaller
    {
        private readonly SourcePolicy _policy;
        private readonly string _pclDirectory;
        private readonly CurseForgeFileResolver _resolver;
        private readonly LauncherInstallTarget _target;
        public CurseForgeInstaller(SourcePolicy policy, string pclDirectory = null, CurseForgeFileResolver resolver = null, LauncherInstallTarget launcherTarget = null)
        {
            _policy = policy;
            _pclDirectory = pclDirectory;
            _resolver = resolver;
            _target = launcherTarget;
        }
        public async Task<InstallResult> InstallAsync(CurseForgePackage package, string root, string instanceName, IEnumerable<string> reuseRoots, IEnumerable<long> optionalFileIds, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L, string javaPath = null)
        {
            token.ThrowIfCancellationRequested();
            if (package is null)
                throw new ArgumentNullException(nameof(package));
            if (reuseRoots is null)
                throw new ArgumentNullException(nameof(reuseRoots));
            _target?.ValidateDestination(root);
            InstallPreflight.ValidateDestination(root, instanceName, false);
            var reuse = reuseRoots.ToList();
            long[] selected = (optionalFileIds ?? Enumerable.Empty<long>()).ToArray();
            using (var plan = new CurseForgeInstallPlan(package, token))
            {
                plan.ValidateTargets(instanceName);
                if (_resolver is null)
                {
                    using (var resolver = new CurseForgeFileResolver(_policy))
                    {
                        plan.SetFiles(await resolver.ResolveAsync(package, selected, token, progress).ConfigureAwait(false));
                    }
                }
                else
                {
                    plan.SetFiles(await _resolver.ResolveAsync(package, selected, token, progress).ConfigureAwait(false));
                }
                plan.ValidateTargets(instanceName);
                return await ModpackInstallation.InstallAsync(plan, _policy, _pclDirectory, root, instanceName, reuse, progress, token, package.LoaderKind, package.LoaderVersion, connections, limit, javaPath, _target).ConfigureAwait(false);
            }
        }
    }

    internal sealed class CurseForgeInstallPlan : ModpackInstallPlan
    {
        private readonly CurseForgePackage _package;
        private List<CurseForgeResolvedFile> _resolved = new List<CurseForgeResolvedFile>();
        internal override string Minecraft
        {
            get
            {
                return _package.Minecraft;
            }
        }
        protected override IEnumerable<KeyValuePair<string, ZipArchiveEntry>> OverrideEntries
        {
            get
            {
                string prefix = _package.BaseFolder + _package.OverridesFolder + "/";
                return Archive.Entries.Where(entry => !CurseForgePackage.IsDirectory(entry) && entry.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(entry => new KeyValuePair<string, ZipArchiveEntry>(entry.FullName.Replace('\\', '/').Substring(prefix.Length), entry));
            }
        }
        internal CurseForgeInstallPlan(CurseForgePackage package, CancellationToken token) : base(package.SourcePath, package.Sha256, token)
        {
            _package = package;
        }
        internal void SetFiles(List<CurseForgeResolvedFile> resolved)
        {
            _resolved = resolved;
            Files.AddRange(DownloadPlan.Merge(resolved.Select(item => item.Download)));
        }
        internal override void WriteLedger(string stage, CancellationToken token)
        {
            ValidatePayload(stage, token);
            var ledgerFiles = new JArray();
            foreach (var item in _resolved)
                ledgerFiles.Add(new JObject() { { "projectId", item.Reference.ProjectId }, { "fileId", item.Reference.FileId }, { "path", item.Download.RelativePath }, { "size", item.Download.Size }, { "sha1", item.Download.Sha1 }, { "md5", item.Download.Md5 }, { "s3SinglePartETag", item.Download.S3SinglePartETag }, { "informationSource", item.InformationSource }, { "checksumSource", item.ChecksumSource } });
            string target = SafeFiles.UnderRoot(stage, "PCL/CurseForgeInstall.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, new JObject() { { "formatVersion", 1 }, { "name", _package.Name }, { "version", _package.Version }, { "archiveSha256", _package.Sha256 }, { "minecraft", _package.Minecraft }, { "loader", _package.LoaderKind.ToString() }, { "loaderVersion", _package.LoaderVersion }, { "files", ledgerFiles } }.ToString(), new UTF8Encoding(false));
        }
    }
}