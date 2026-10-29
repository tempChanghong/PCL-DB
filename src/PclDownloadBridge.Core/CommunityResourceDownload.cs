using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    // Downloading a resource never invokes a modpack installer or changes game options.
    // Publication uses the same read/rename leases as local Mods, without overwrite.
    public sealed class CommunityResourceDownload
    {
        private CommunityResourceDownload()
        {
        }
        public static async Task SaveAsync(ModpackRelease release, string destination, string cache, SourcePolicy policy, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L, InstalledInstance instance = null)
        {
            release = release.Copy();
            token.ThrowIfCancellationRequested();
            ValidateDestination(release, destination);
            var acquisition = new InlineProgress(value =>
                {
                    value.Phase = "archive";
                    value.Stage = "下载并校验文件";
                    value.StageFraction = value.Completed >= value.Total && value.Total > 0 ? 1d : Math.Min(0.99d, value.NetworkBytes / (double)release.Size);
                    progress?.Report(value);
                });
            string source = await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, acquisition, token, connections, limit).ConfigureAwait(false);
            progress?.Report(new TransferProgress() { Phase = "commit", Stage = "保存文件", StageFraction = 0d });
            await Task.Run(() => Publish(release, source, destination, token, instance), token).ConfigureAwait(false);
            progress?.Report(new TransferProgress() { Phase = "commit", Stage = "文件已保存", StageFraction = 1d });
        }
        private static void ValidateDestination(ModpackRelease release, string destination)
        {
            if (release.Kind == CommunityResourceKind.Modpack || release.Kind == CommunityResourceKind.Any || !CommunityResources.AcceptsFile(release.Kind, release.FileName) || !CommunityResources.AcceptsFile(release.Kind, Path.GetFileName(destination)))
                throw new InvalidDataException("保存文件的扩展名与资源类型不符。");
            CommunityResources.ValidateFileName(release.Kind, release.FileName);
            CommunityResources.ValidateFileName(release.Kind, Path.GetFileName(destination));
            if (release.Size <= 0L || release.Sha1 is null && release.Sha512 is null && release.Md5 is null)
                throw new InvalidDataException("资源缺少有效大小或校验摘要。");
            string full = Path.GetFullPath(destination);
            if ((Path.GetFileName(full) ?? "") != (Path.GetFileName(destination) ?? "") || !Directory.Exists(Path.GetDirectoryName(full)))
                throw new DirectoryNotFoundException("保存文件夹不存在。");
            if (File.Exists(full) || Directory.Exists(full))
                throw new IOException("目标已存在，请选择其他文件名；已有文件已保留。");
        }
        internal static void Publish(ModpackRelease release, string source, string destination, CancellationToken token, InstalledInstance instance = null, Action beforePublish = null, Action afterPublish = null)
        {
            token.ThrowIfCancellationRequested();
            ValidateDestination(release, destination);
            destination = Path.GetFullPath(destination);
            string directory = Path.GetDirectoryName(destination);
            var leases = new List<IDisposable>();
            LocalModActions.NativeLease stage = null;
            bool published = false;
            try
            {
                if (release.Kind == CommunityResourceKind.Mod && Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase))
                {
                    leases.Add(new LocalModActions.OperationGate(instance, directory, token));
                    LocalModActions.EnsureAliases(directory, Path.GetFileName(destination), null);
                }
                else
                {
                    string current = directory;
                    while (current is not null)
                    {
                        string identity = null;
                        leases.Add(LocalModActions.NativeLease.OpenDirectory(current, ref identity));
                        current = Path.GetDirectoryName(current);
                    }
                }
                using (var input = LocalModActions.NativeLease.Open(source, false))
                {
                    // Verify under the same source lease that supplies the published bytes.
                    VerifySource(input.Stream, release, token);
                    stage = LocalModActions.NativeLease.Create(Path.Combine(directory, ".pcldb-resource-" + Guid.NewGuid().ToString("N") + ".tmp"));
                    var buffer = new byte[65536];
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        int count = input.Stream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                            break;
                        stage.Stream.Write(buffer, 0, count);
                    }
                    while (true);
                    stage.Stream.Flush(true);
                    beforePublish?.Invoke();
                    if (instance is not null)
                        InstalledInstanceCatalog.EnsureUnchanged(instance, token);
                    token.ThrowIfCancellationRequested();
                    stage.Rename(destination);
                    afterPublish?.Invoke();
                    token.ThrowIfCancellationRequested();
                    published = true;
                }
            }
            finally
            {
                try
                {
                    if (stage is not null)
                    {
                        try
                        {
                            if (!published)
                                stage.DeleteOnClose();
                        }
                        finally
                        {
                            stage.Dispose();
                        }
                    }
                }
                finally
                {
                    foreach (var handle in leases.AsEnumerable().Reverse())
                        handle.Dispose();
                }
            }
        }
        internal static void VerifySource(Stream input, ModpackRelease release, CancellationToken token)
        {
            if (input.Length != release.Size)
                throw new InvalidDataException("下载缓存已改变，请重新下载。");
            var hashes = new List<Tuple<HashAlgorithm, string>>();
            try
            {
                if (release.Sha1 is not null)
                    hashes.Add(Tuple.Create<HashAlgorithm, string>(SHA1.Create(), release.Sha1));
                if (release.Sha512 is not null)
                    hashes.Add(Tuple.Create<HashAlgorithm, string>(SHA512.Create(), release.Sha512));
                if (release.Md5 is not null)
                    hashes.Add(Tuple.Create<HashAlgorithm, string>(MD5.Create(), release.Md5));
                var buffer = new byte[65536];
                do
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    foreach (var hash in hashes)
                        hash.Item1.TransformBlock(buffer, 0, count, buffer, 0);
                }
                while (true);
                foreach (var hash in hashes)
                {
                    hash.Item1.TransformFinalBlock(new byte[] { }, 0, 0);
                    if (!string.Equals(BitConverter.ToString(hash.Item1.Hash).Replace("-", ""), hash.Item2, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("下载缓存已改变，请重新下载。");
                }
                input.Position = 0L;
            }
            finally
            {
                foreach (var hash in hashes)
                    hash.Item1.Dispose();
            }
        }
    }
}