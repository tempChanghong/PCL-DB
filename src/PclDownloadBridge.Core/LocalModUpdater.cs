using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    public sealed class LocalModUpdater
    {
        private LocalModUpdater()
        {
        }
        public static async Task<LocalModActionResult> InstallAsync(InstalledInstance instance, IEnumerable<LocalModUpdate> updates, string cache, SourcePolicy policy, IProgress<TransferProgress> progress, CancellationToken token, int connections = 16, long limit = 0L)
        {
            var selected = updates.ToList();
            if (selected.Count == 0)
                throw new ArgumentException("请选择可更新的 Mod。");
            var paths = new string[selected.Count];
            int lanes = Math.Min(3, Math.Min(Math.Max(1, connections), selected.Count));
            // Download outside the native Mods transaction. All downloads must verify
            // before the first installed file moves; retries reuse the download cache.
            using (var stopAll = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var slots = new SemaphoreSlim(lanes))
            {
                Task[] tasks = selected.Select(async (update, index) =>
                    {
                        await slots.WaitAsync(stopAll.Token).ConfigureAwait(false);
                        try
                        {
                            if (!update.HasUpdate || !LocalModUpdate.Compatible(instance, update.Current, update.Release))
                                throw new System.IO.InvalidDataException("更新与实例不兼容。");
                            paths[index] = await ModpackArchiveInstaller.AcquireAsync(update.Release, cache, policy, progress, stopAll.Token, Math.Max(1, connections / lanes), limit == 0L ? 0L : Math.Max(1L, limit / lanes)).ConfigureAwait(false);
                        }
                        catch
                        {
                            stopAll.Cancel();
                            throw;
                        }
                        finally
                        {
                            slots.Release();
                        }
                    }).ToArray();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch
                {
                    token.ThrowIfCancellationRequested();
                    var failed = tasks.FirstOrDefault(task => task.IsFaulted && task.Exception.Flatten().InnerExceptions.Any(ex => !(ex is OperationCanceledException)));
                    if (failed is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed.Exception.Flatten().InnerExceptions.First(ex => !(ex is OperationCanceledException))).Throw();
                    throw;
                }
            }
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => LocalModActions.ApplyUpdates(instance, selected, paths, token), token).ConfigureAwait(false);
        }
    }
}