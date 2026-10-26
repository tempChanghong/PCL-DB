Imports System.IO
Imports System.Threading

Partial Public NotInheritable Class LocalModActions
    Private NotInheritable Class Change
        Friend Entry As LocalModEntry, Replacement As LocalModEntry, Source As NativeLease, Staged As NativeLease
        Friend Original As String, Destination As String, Backup As String
    End Class

    Public Shared Function SetEnabledBatch(instance As InstalledInstance, entries As IEnumerable(Of LocalModEntry), enabled As Boolean, token As CancellationToken) As LocalModActionResult
        Return ChangeBatch(instance, entries, enabled, False, token)
    End Function
    Public Shared Function DeleteBatch(instance As InstalledInstance, entries As IEnumerable(Of LocalModEntry), token As CancellationToken) As LocalModActionResult
        Return ChangeBatch(instance, entries, Nothing, True, token)
    End Function
    Private Shared Function ChangeBatch(instance As InstalledInstance, entries As IEnumerable(Of LocalModEntry), enabled As Boolean?, delete As Boolean, token As CancellationToken) As LocalModActionResult
        Dim directory = LocalModCatalog.ModsDirectory(instance, token), changes As New List(Of Change), backup As String = Nothing
        Using gate As New OperationGate(instance, directory, token)
            Try
                Dim selected = entries.ToList()
                If selected.Count = 0 Then Return New LocalModActionResult(directory, 0, 0)
                If delete Then backup = CreateBackup(directory, gate)
                Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each entry In selected
                    Dim item = HoldOriginal(directory, entry, token)
                    changes.Add(item)
                    If Not seen.Add(item.Original) Then Throw New InvalidDataException("重复的 Mod 选择。")
                    If Not delete Then EnsureAliases(directory, entry.EnabledName, item.Original)
                    If delete Then
                        item.Destination = Child(backup, entry.FileName)
                    ElseIf entry.Enabled <> enabled.Value Then
                        item.Destination = Child(directory, If(enabled.Value, entry.EnabledName, entry.EnabledName & ".disabled"))
                    End If
                Next
                For Each item In changes.Where(Function(value) value.Destination IsNot Nothing)
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token) : token.ThrowIfCancellationRequested()
                    If Not delete Then EnsureAliases(directory, item.Entry.EnabledName, item.Original)
                    item.Source.Rename(item.Destination)
                Next
                InstalledInstanceCatalog.EnsureUnchanged(instance, token) : token.ThrowIfCancellationRequested()
                For Each item In changes
                    If Not delete Then EnsureAliases(directory, item.Entry.EnabledName, item.Source.CurrentPath)
                Next
                Return New LocalModActionResult(directory, changes.Where(Function(item) item.Destination IsNot Nothing).Count(), changes.Where(Function(item) item.Destination Is Nothing).Count(), backup:=backup)
            Catch ex As Exception
                Rollback(changes, ex)
                Throw
            Finally
                For Each item In changes : item.Source?.Dispose() : Next
                If backup IsNot Nothing Then gate.ReleaseModsDirectory(backup) : RemoveEmpty(backup)
            End Try
        End Using
    End Function

    ' Keep PCL's custom-name behaviour, while preserving the exact physical
    ' disabled suffix. Do not guess if the platform's original name is absent.
    Public Shared Function UpdateFileName(entry As LocalModEntry, current As ModpackRelease, release As ModpackRelease) As String
        Dim oldName = current.FileName, newName = release.FileName, local = entry.EnabledName
        If String.Equals(local, oldName, StringComparison.OrdinalIgnoreCase) Then
            local = newName
        Else
            ' PageInstanceMod.UpdateMods removes only common leading/trailing
            ' dash segments. Keep bracketed translations and the disabled suffix.
            Dim oldStem = IO.Path.GetFileNameWithoutExtension(oldName), newStem = IO.Path.GetFileNameWithoutExtension(newName)
            Dim before = oldStem.Split("-"c).ToList(), after = newStem.Split("-"c).ToList()
            While before.Count > 0 AndAlso after.Count > 0 AndAlso before(0) = after(0)
                before.RemoveAt(0) : after.RemoveAt(0)
            End While
            While before.Count > 0 AndAlso after.Count > 0 AndAlso before.Last() = after.Last()
                before.RemoveAt(before.Count - 1) : after.RemoveAt(after.Count - 1)
            End While
            Dim oldPart = If(before.Count > 0 AndAlso after.Count > 0, String.Join("-", before), oldStem)
            Dim newPart = If(before.Count > 0 AndAlso after.Count > 0, String.Join("-", after), newStem)
            If oldPart.Length = 0 OrElse local.IndexOf(oldPart, StringComparison.Ordinal) < 0 OrElse local.IndexOf(oldPart, StringComparison.Ordinal) <> local.LastIndexOf(oldPart, StringComparison.Ordinal) Then
                oldPart = oldStem : newPart = newStem
            End If
            If oldPart.Length > 0 AndAlso local.IndexOf(oldPart, StringComparison.Ordinal) >= 0 AndAlso local.IndexOf(oldPart, StringComparison.Ordinal) = local.LastIndexOf(oldPart, StringComparison.Ordinal) Then local = local.Replace(oldPart, newPart)
        End If
        CommunityResources.ValidateFileName(CommunityResourceKind.Mod, local)
        Return local & entry.FileName.Substring(entry.EnabledName.Length)
    End Function

    Public Shared Function ApplyUpdates(instance As InstalledInstance, updates As IEnumerable(Of LocalModUpdate), paths As IEnumerable(Of String), token As CancellationToken) As LocalModActionResult
        Return ApplyUpdatesCore(instance, updates.ToList(), paths.ToList(), token, Nothing)
    End Function
    Friend Shared Function ApplyUpdatesCore(instance As InstalledInstance, updates As List(Of LocalModUpdate), paths As List(Of String), token As CancellationToken, beforePublish As Action(Of Integer)) As LocalModActionResult
        If updates.Count = 0 OrElse updates.Count <> paths.Count Then Throw New ArgumentException("更新文件与所选 Mod 不符。")
        Dim directory = LocalModCatalog.ModsDirectory(instance, token), changes As New List(Of Change), downloads As New List(Of NativeLease)
        Dim stage As String = Nothing, backup As String = Nothing, committed = False
        Using gate As New OperationGate(instance, directory, token)
            Try
                Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For index = 0 To updates.Count - 1
                    Dim update = updates(index), release = update.Release
                    If Not update.HasUpdate OrElse Not LocalModUpdate.Compatible(instance, update.Current, release) Then Throw New InvalidDataException("更新与实例不兼容，请重新检查。")
                    Dim item = HoldOriginal(directory, update.Entry, token)
                    changes.Add(item)
                    Dim source = NativeLease.Open(paths(index), False) : downloads.Add(source)
                    CommunityResourceDownload.VerifySource(source.Stream, release, token)
                    Dim replacement = LocalModCatalog.Read(paths(index), token)
                    If Not replacement.IsArchiveValid OrElse replacement.Problem IsNot Nothing OrElse
                        (item.Entry.ModIds.Count > 0 AndAlso Not item.Entry.ModIds.Intersect(replacement.ModIds, StringComparer.Ordinal).Any()) Then Throw New InvalidDataException("更新文件与原 Mod 身份不符。")
                    item.Replacement = replacement
                    If changes.Any(Function(other) other IsNot item AndAlso other.Replacement IsNot Nothing AndAlso replacement.ModIds.Intersect(other.Replacement.ModIds, StringComparer.Ordinal).Any()) Then Throw New IOException("所选更新包含重复的 Mod ID，请分别检查。")
                    item.Destination = Child(directory, UpdateFileName(item.Entry, update.Current, release))
                    If Not names.Add(LocalModCatalog.EnabledFileName(item.Destination)) Then Throw Conflict(item.Destination)
                    EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Original)
                    For Each existing In IO.Directory.EnumerateFiles(directory).Where(Function(file) LocalModCatalog.IsSupportedFile(file) AndAlso Not Same(file, item.Original))
                        Dim other = LocalModCatalog.Read(existing, token)
                        If replacement.ModIds.Intersect(other.ModIds, StringComparer.Ordinal).Any() Then Throw New IOException("其他文件已包含更新的 Mod ID：" & other.FileName)
                    Next
                Next
                backup = CreateBackup(directory, gate)
                stage = Child(directory, ".pcl-db-mod-stage-" & Guid.NewGuid().ToString("N"))
                IO.Directory.CreateDirectory(stage) : gate.HoldModsDirectory(stage)
                For index = 0 To changes.Count - 1
                    Dim item = changes(index), replacement = item.Replacement
                    item.Backup = Child(backup, item.Entry.FileName)
                    item.Staged = NativeLease.Create(Child(stage, IO.Path.GetFileName(item.Destination)))
                    CopyChecked(downloads(index).Stream, item.Staged.Stream, replacement, token)
                Next
                For index = 0 To changes.Count - 1
                    Dim item = changes(index)
                    beforePublish?.Invoke(index)
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token) : token.ThrowIfCancellationRequested()
                    EnsureAliases(directory, item.Entry.EnabledName, item.Original)
                    EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Original)
                    item.Source.Rename(item.Backup) : item.Staged.Rename(item.Destination)
                    EnsureAliases(directory, LocalModCatalog.EnabledFileName(item.Destination), item.Destination)
                Next
                InstalledInstanceCatalog.EnsureUnchanged(instance, token) : token.ThrowIfCancellationRequested()
                For Each item In changes
                    For Each other In IO.Directory.EnumerateFiles(directory).Where(Function(file) LocalModCatalog.IsSupportedFile(file) AndAlso Not changes.Any(Function(own) Same(own.Destination, file)))
                        If item.Replacement.ModIds.Intersect(LocalModCatalog.Read(other, token).ModIds, StringComparer.Ordinal).Any() Then Throw Conflict(other)
                    Next
                Next
                committed = True
                Return New LocalModActionResult(directory, changes.Count, 0, backup:=backup)
            Catch ex As Exception
                Rollback(changes, ex)
                Throw
            Finally
                For Each item In changes
                    If Not committed AndAlso item.Staged IsNot Nothing Then
                        Try
                            item.Staged.DeleteOnClose()
                        Catch ex As IOException
                            ' The owned stage is retained if cleanup is blocked.
                        End Try
                    End If
                    item.Staged?.Dispose() : item.Source?.Dispose()
                Next
                For Each source In downloads : source.Dispose() : Next
                If stage IsNot Nothing Then gate.ReleaseModsDirectory(stage) : RemoveEmpty(stage)
                If backup IsNot Nothing Then gate.ReleaseModsDirectory(backup) : RemoveEmpty(backup)
            End Try
        End Using
    End Function
    Private Shared Function HoldOriginal(directory As String, entry As LocalModEntry, token As CancellationToken) As Change
        If entry Is Nothing OrElse Not entry.CanToggle OrElse Not Same(Child(directory, entry.FileName), entry.FilePath) Then Throw New InvalidDataException("Mod 不属于当前实例或无法读取，请刷新。")
        Dim item As New Change With {.Entry = entry, .Original = entry.FilePath, .Source = NativeLease.Open(entry.FilePath, True)}
        Try
            CheckDigest(item.Source.Stream, entry.Length, entry.Sha256, token)
            Return item
        Catch
            item.Source.Dispose() : Throw
        End Try
    End Function
    Private Shared Function CreateBackup(directory As String, gate As OperationGate) As String
        ' A sibling of Mods cannot be rediscovered as another active Mod by a
        ' recursive loader. Both directories remain on the same held volume.
        Dim parent = Child(IO.Path.GetDirectoryName(directory), ".pcl-db-mod-backups")
        ValidatePath(parent, True) : IO.Directory.CreateDirectory(parent) : gate.HoldModsDirectory(parent)
        Dim backup = Child(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") & "-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(backup) : gate.HoldModsDirectory(backup)
        Return backup
    End Function
    Private Shared Sub Rollback(changes As List(Of Change), cause As Exception)
        Dim failures As New List(Of Exception)
        For Each item In changes.AsEnumerable().Reverse()
            Try
                ' Discard only our held replacement before restoring the original.
                If item.Staged IsNot Nothing AndAlso Same(item.Staged.CurrentPath, item.Destination) Then item.Staged.DeleteOnClose() : item.Staged.Dispose() : item.Staged = Nothing
                If item.Source IsNot Nothing AndAlso Not Same(item.Source.CurrentPath, item.Original) Then item.Source.Rename(item.Original)
            Catch ex As Exception
                failures.Add(ex)
            End Try
        Next
        If failures.Count > 0 Then Throw New IOException("部分文件无法恢复，原文件已保留在运行目录的 .pcl-db-mod-backups 中，请检查后刷新。", New AggregateException({cause}.Concat(failures)))
    End Sub
End Class
