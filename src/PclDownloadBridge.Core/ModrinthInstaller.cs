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

    public sealed class ModrinthInstaller
    {
        private readonly SourcePolicy _policy;
        private readonly string _pclDirectory;
        private readonly LauncherInstallTarget _target;
        public ModrinthInstaller(SourcePolicy policy, string pclDirectory = null, LauncherInstallTarget launcherTarget = null)
        {
            _policy = policy;
            _pclDirectory = pclDirectory;
            _target = launcherTarget;
        }
        public async Task<InstallResult> InstallAsync(ModrinthPackage package, string root, string instanceName, IEnumerable<string> reuseRoots, IEnumerable<string> optionalPaths, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L, string javaPath = null)
        {
            token.ThrowIfCancellationRequested();
            if (package is null)
                throw new ArgumentNullException(nameof(package));
            if (reuseRoots is null)
                throw new ArgumentNullException(nameof(reuseRoots));
            _target?.ValidateDestination(root);
            InstallPreflight.ValidateDestination(root, instanceName, false);
            string[] reuse = reuseRoots.ToArray();
            string[] selected = (optionalPaths ?? Enumerable.Empty<string>()).ToArray();
            using (var plan = new ModrinthInstallPlan(package, selected, token))
            {
                return await ModpackInstallation.InstallAsync(plan, _policy, _pclDirectory, root, instanceName, reuse, progress, token, package.LoaderKind, package.LoaderVersion, connections, limit, javaPath, _target).ConfigureAwait(false);
            }
        }
    }

    internal sealed class ModrinthInstallPlan : ModpackInstallPlan
    {
        private readonly ModrinthPackage _package;
        internal override string Minecraft
        {
            get
            {
                return _package.Minecraft;
            }
        }
        protected override bool AllowLayeredOverrides
        {
            get
            {
                return true;
            }
        }
        protected override IEnumerable<KeyValuePair<string, ZipArchiveEntry>> OverrideEntries
        {
            get
            {
                return _package.OverrideSources.Select(pair => new KeyValuePair<string, ZipArchiveEntry>(pair.Key, Archive.GetEntry(pair.Value)));
            }
        }
        internal ModrinthInstallPlan(ModrinthPackage package, IEnumerable<string> selected, CancellationToken token) : base(package.SourcePath, package.Sha256, token)
        {
            _package = package;
            try
            {
                var choices = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                if (choices.Any(path => !package.References.Any(item => item.Client == "optional" && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("可选文件不属于当前整合包。");
                Files.AddRange(package.References.Where(item => item.Client == "required" || item.Client == "optional" && choices.Contains(item.Path)).Select(item => item.ToDownload()));
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        internal override void WriteLedger(string stage, CancellationToken token)
        {
            ValidatePayload(stage, token);
            var records = new JArray(Files.Select(item => new JObject() { { "path", item.RelativePath }, { "estimatedSize", item.EstimatedSize }, { "sha1", item.Sha1 }, { "sha512", item.Sha512 }, { "overridden", OverrideHashes.ContainsKey(item.RelativePath) } }));
            var overrideRecords = new JArray(_package.OverrideSources.Select(pair => new JObject() { { "path", pair.Key }, { "archiveEntry", pair.Value }, { "sha256", OverrideHashes[pair.Key] } }));
            string target = SafeFiles.UnderRoot(stage, "PCL/ModrinthInstall.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, new JObject() { { "formatVersion", 1 }, { "name", _package.Name }, { "version", _package.Version }, { "archiveSha256", _package.Sha256 }, { "minecraft", _package.Minecraft }, { "loader", _package.LoaderKind.ToString() }, { "loaderVersion", _package.LoaderVersion }, { "files", records }, { "overrides", overrideRecords } }.ToString(), new UTF8Encoding(false));
        }
    }
}