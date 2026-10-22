Imports System.IO
Imports System.Threading

' Remap aliases are staged after the shared hash-addressed objects. A new shared
' installation never overwrites existing resources, and rolls back only files it
' created if final instance publication fails. Repairs retain completed fixes.
Friend NotInheritable Class LegacyResourcePublication
    Implements IDisposable
    Private ReadOnly _root As String
    Private ReadOnly _stage As String
    Private ReadOnly _name As String
    Private ReadOnly _files As List(Of DownloadFile)
    Private ReadOnly _isolated As Boolean
    Private ReadOnly _repair As Boolean
    Private ReadOnly _description As String
    Private ReadOnly _holdTargets As Boolean
    Private ReadOnly _held As New List(Of FileStream)
    Private ReadOnly _created As New List(Of DownloadFile)
    Private _completed As Boolean
    Friend Sub New(root As String, stage As String, name As String, files As List(Of DownloadFile), isolated As Boolean, repair As Boolean, Optional description As String = "旧资源", Optional holdTargets As Boolean = False)
        _root = root : _stage = stage : _name = name : _files = files : _isolated = isolated : _repair = repair
        _description = description
        _holdTargets = holdTargets
    End Sub
    Friend Shared Sub Validate(root As String, files As IEnumerable(Of DownloadFile), repair As Boolean, Optional description As String = "旧资源")
        Dim targets As New HashSet(Of String)(files.Select(Function(item) SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase)
        For Each item In files
            Dim target = SafeFiles.UnderRoot(root, item.RelativePath)
            If Directory.Exists(target) Then Throw New IOException("目录占用了" & description & "文件：" & item.RelativePath)
            Dim parent = Path.GetDirectoryName(target)
            While parent IsNot Nothing
                If targets.Contains(parent) OrElse File.Exists(parent) Then Throw New InvalidDataException(description & "文件与目录路径冲突：" & item.RelativePath)
                parent = Path.GetDirectoryName(parent)
            End While
            If Not repair AndAlso File.Exists(target) AndAlso Not SafeFiles.Matches(target, item) Then Throw New IOException("已有" & description & "内容不同，不会覆盖共享文件：" & item.RelativePath)
        Next
    End Sub
    Friend Shared Sub ValidateLayout(root As String, files As IEnumerable(Of DownloadFile))
        Dim targets As New HashSet(Of String)(files.Select(Function(item) SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase)
        For Each target In targets
            Dim parent = Path.GetDirectoryName(target)
            While parent IsNot Nothing
                If targets.Contains(parent) Then Throw New InvalidDataException("下载文件与旧资源映射的目录路径冲突。")
                parent = Path.GetDirectoryName(parent)
            End While
        Next
    End Sub
    Friend Function StagedFiles() As List(Of DownloadFile)
        Return _files.Select(Function(item) New DownloadFile With {.RelativePath = If(_isolated, item.RelativePath.Substring(("versions/" & _name & "/").Length), item.RelativePath),
            .Url = item.Url, .Sha1 = item.Sha1, .Md5 = item.Md5, .Size = item.Size, .Category = "asset"}).ToList()
    End Function
    Friend Sub Publish(token As CancellationToken, Optional guard As PclIsolationPlan.RepairGuard = Nothing)
        If _isolated AndAlso Not _repair Then Return ' Directory.Move publishes the whole payload.
        Validate(_root, _files, _repair, _description)
        Dim staged = StagedFiles()
        For i = 0 To _files.Count - 1
            token.ThrowIfCancellationRequested() : guard?.Verify()
            Dim item = _files(i), target = SafeFiles.UnderRoot(_root, item.RelativePath)
            If _holdTargets AndAlso File.Exists(target) Then HoldTarget(target)
            If SafeFiles.Matches(target, item) Then
                token.ThrowIfCancellationRequested() : guard?.Verify()
                DeleteOwnedFile(SafeFiles.UnderRoot(_stage, staged(i).RelativePath))
                Continue For
            End If
            token.ThrowIfCancellationRequested() : guard?.Verify()
            If Not _repair AndAlso File.Exists(target) Then Throw New IOException(_description & "目标在安装期间发生变化：" & item.RelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            Dim source = SafeFiles.UnderRoot(_stage, staged(i).RelativePath)
            If Not SafeFiles.Matches(source, item) Then Throw New InvalidDataException("待发布的" & _description & "文件校验失败：" & item.RelativePath)
            token.ThrowIfCancellationRequested() : guard?.Verify()
            If _repair Then
                SafeFiles.CommitFile(source, target)
            Else
                File.Move(source, target)
                _created.Add(item)
                If _holdTargets Then
                    HoldTarget(target)
                    If Not SafeFiles.Matches(target, item) Then Throw New IOException(_description & "目标在发布期间发生变化：" & item.RelativePath)
                    token.ThrowIfCancellationRequested()
                End If
            End If
        Next
        guard?.Verify()
    End Sub
    Friend Sub Complete()
        _completed = True
    End Sub
    Private Sub HoldTarget(target As String)
        ' Shared Mods are few, unlike asset aliases. Hold existing or freshly
        ' moved files only through the short final commit, allowing game readers
        ' while preventing a later step from observing a changed earlier Mod.
        _held.Add(New FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        ' Release our leases before rolling back owned files, including when
        ' cancellation was raised by a final content check.
        For Each stream In _held
            stream.Dispose()
        Next
        _held.Clear()
        If _completed OrElse _repair Then Return
        For Each item In _created.AsEnumerable().Reverse()
            Try
                Dim target = SafeFiles.UnderRoot(_root, item.RelativePath)
                If SafeFiles.Matches(target, item) Then DeleteOwnedFile(target)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                InstallLog.Append(_root, "未完成实例的共享" & _description & "回收失败：" & item.RelativePath & " | " & ex.Message)
            End Try
        Next
    End Sub
    Private Shared Sub DeleteOwnedFile(path As String)
        If Not File.Exists(path) Then Return
        ' File.Copy retains read-only attributes. These are our private aliases or
        ' newly created, uncommitted shared files, never an existing user's file.
        Dim attributes = File.GetAttributes(path)
        If (attributes And FileAttributes.ReadOnly) <> 0 Then File.SetAttributes(path, attributes And Not FileAttributes.ReadOnly)
        File.Delete(path)
    End Sub
End Class
