Imports System.Threading

Public NotInheritable Class LocalModUpdater
    Private Sub New()
    End Sub
    Public Shared Async Function InstallAsync(instance As InstalledInstance, updates As IEnumerable(Of LocalModUpdate), cache As String, policy As SourcePolicy,
        progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0) As Task(Of LocalModActionResult)
        Dim selected = updates.ToList()
        If selected.Count = 0 Then Throw New ArgumentException("请选择可更新的 Mod。")
        Dim paths(selected.Count - 1) As String, lanes = Math.Min(3, Math.Min(Math.Max(1, connections), selected.Count))
        ' Download outside the native Mods transaction. All downloads must verify
        ' before the first installed file moves; retries reuse the download cache.
        Using stopAll = CancellationTokenSource.CreateLinkedTokenSource(token), slots As New SemaphoreSlim(lanes)
            Dim tasks = selected.Select(Async Function(update, index)
                                            Await slots.WaitAsync(stopAll.Token).ConfigureAwait(False)
                                            Try
                                                If Not update.HasUpdate OrElse Not LocalModUpdate.Compatible(instance, update.Current, update.Release) Then Throw New IO.InvalidDataException("更新与实例不兼容。")
                                                paths(index) = Await ModpackArchiveInstaller.AcquireAsync(update.Release, cache, policy, progress, stopAll.Token,
                                                    Math.Max(1, connections \ lanes), If(limit = 0, 0, Math.Max(1, limit \ lanes))).ConfigureAwait(False)
                                            Catch
                                                stopAll.Cancel() : Throw
                                            Finally
                                                slots.Release()
                                            End Try
                                        End Function).ToArray()
            Try
                Await Task.WhenAll(tasks).ConfigureAwait(False)
            Catch
                token.ThrowIfCancellationRequested()
                Dim failed = tasks.FirstOrDefault(Function(task) task.IsFaulted AndAlso task.Exception.Flatten().InnerExceptions.Any(Function(ex) Not TypeOf ex Is OperationCanceledException))
                If failed IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed.Exception.Flatten().InnerExceptions.First(Function(ex) Not TypeOf ex Is OperationCanceledException)).Throw()
                Throw
            End Try
        End Using
        token.ThrowIfCancellationRequested()
        Return Await Task.Run(Function() LocalModActions.ApplyUpdates(instance, selected, paths, token), token).ConfigureAwait(False)
    End Function
End Class
