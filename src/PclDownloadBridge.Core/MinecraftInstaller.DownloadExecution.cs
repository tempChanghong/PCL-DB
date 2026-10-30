using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{
    public partial class MinecraftInstaller
    {
        // Borrow the transaction's existing snapshots; do not clone or enumerate
        // branch inputs here. Publication and final runtime leases stay outside.
        private sealed class DownloadExecutionInputs
        {
            internal string Root;
            internal string Stage;
            internal string InstanceName;
            internal string InstancePath;
            internal IEnumerable<string> ReuseRoots;
            internal IProgress<TransferProgress> Progress;
            internal int Connections;
            internal long Limit;
            internal VersionEntry Entry;
            internal JObject Metadata;
            internal ComponentSelection Components;
            internal IInstanceIsolationPlan Isolation;
            internal NativeInstanceIsolation Native;
            internal DownloadFile Client;
            internal List<DownloadFile> SupportFiles;
            internal List<DownloadFile> AssetFiles;
            internal List<DownloadFile> MappedFiles;
            internal List<DownloadFile> Plan;
            internal LegacyResourcePublication Publication;
            internal ModpackInstallPlan Pack;
        }

        private sealed class DownloadExecutionResult
        {
            internal readonly JObject Metadata;
            internal readonly ComponentInstaller Extension;
            internal readonly long NetworkBytes;
            internal readonly int Reused;
            internal readonly int DynamicSplits;

            internal DownloadExecutionResult(JObject metadata, ComponentInstaller extension, long networkBytes, int reused, int splits)
            {
                Metadata = metadata;
                Extension = extension;
                NetworkBytes = networkBytes;
                Reused = reused;
                DynamicSplits = splits;
            }
        }

        private async Task<DownloadExecutionResult> ExecuteDownloadsAsync(DownloadExecutionInputs input, CancellationToken token)
        {
            string root = input.Root;
            string stage = input.Stage;
            string instanceName = input.InstanceName;
            string instancePath = input.InstancePath;
            IEnumerable<string> reuseRoots = input.ReuseRoots;
            IProgress<TransferProgress> progress = input.Progress;
            int connections = input.Connections;
            long limit = input.Limit;
            VersionEntry entry = input.Entry;
            JObject metadata = input.Metadata;
            ComponentSelection components = input.Components;
            IInstanceIsolationPlan isolation = input.Isolation;
            NativeInstanceIsolation native = input.Native;
            DownloadFile client = input.Client;
            List<DownloadFile> supportFiles = input.SupportFiles;
            List<DownloadFile> assetFiles = input.AssetFiles;
            List<DownloadFile> mappedFiles = input.MappedFiles;
            List<DownloadFile> plan = input.Plan;
            LegacyResourcePublication publication = input.Publication;
            ModpackInstallPlan pack = input.Pack;
            ComponentInstaller extension = null;
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
                                        ReportFailure(() => reporting.Failed("components"));
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
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
            return new DownloadExecutionResult(metadata, extension, networkBytes, reused, splits);
        }
    }
}
