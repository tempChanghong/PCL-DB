Imports System.IO
Imports System.Text
Imports Newtonsoft.Json.Linq

' Read-only adaptation of pinned PCL McInstance.PathIndie. Existing preferences
' are never initialized or migrated by a repair. New remapped instances freeze
' their own resolved value only when their private payload is published.
Friend NotInheritable Class PclIsolationPlan
    Friend ReadOnly Isolated As Boolean
    Private ReadOnly _inputs As New List(Of Input)
    Private ReadOnly _root As String
    Private ReadOnly _name As String
    Private _folderSignal As Boolean?
    Private Class Input
        Friend Root As String
        Friend Relative As String
        Friend Bytes As Byte()
    End Class
    Private Sub New(root As String, name As String, entry As VersionEntry, metadata As JObject, pclDirectory As String, repair As Boolean, withComponents As Boolean)
        _root = root : _name = name
        If Not repair AndAlso withComponents Then
            ' Existing component installations explicitly use isolated mods.
            Isolated = True : Return
        End If
        If repair Then
            Dim settings = ReadInput(root, "versions/" & name & "/PCL/Setup.ini")
            Dim value = ReadValue(settings.Bytes, "VersionArgumentIndieV2"), enabled As Boolean
            If value IsNot Nothing Then
                If Not Boolean.TryParse(value, enabled) Then Throw New InvalidDataException("已有实例的版本隔离设置无效，请先在 PCL 中确认。")
                Isolated = enabled : Return
            End If
            value = ReadValue(settings.Bytes, "VersionArgumentIndie")
            If value IsNot Nothing Then
                Dim legacy As Integer
                If Not Integer.TryParse(value, legacy) Then Throw New InvalidDataException("已有实例的旧版隔离设置无效，请先在 PCL 中确认。")
                If legacy > 0 Then
                    Isolated = legacy = 1 : Return
                End If
            End If
            _folderSignal = HasUserData()
            If _folderSignal.Value Then
                Isolated = True : Return
            End If
        End If
        Dim strategy = 4 ' Pinned PCL LaunchArgumentIndieV2 default.
        If Not String.IsNullOrWhiteSpace(pclDirectory) Then
            Dim settings = ReadInput(Path.GetFullPath(pclDirectory), "PCL/Setup.ini")
            Dim value = ReadValue(settings.Bytes, "LaunchArgumentIndieV2")
            If value IsNot Nothing AndAlso Not Integer.TryParse(value, strategy) Then Throw New InvalidDataException("PCL 的默认版本隔离设置无效，请先在 PCL 中确认。")
        End If
        Dim kind = If(CStr(metadata("type")), entry.Kind)
        Dim nonRelease = entry.IsSupplemental OrElse Not String.Equals(kind, "release", StringComparison.OrdinalIgnoreCase)
        Select Case strategy
            Case 0 : Isolated = False
            Case 1 : Isolated = withComponents
            Case 2 : Isolated = nonRelease
            Case 3 : Isolated = nonRelease OrElse withComponents
            Case Else : Isolated = True
        End Select
    End Sub
    Friend Shared Function Resolve(root As String, name As String, entry As VersionEntry, metadata As JObject, pclDirectory As String, repair As Boolean, withComponents As Boolean) As PclIsolationPlan
        Return New PclIsolationPlan(root, name, entry, metadata, pclDirectory, repair, withComponents)
    End Function
    Friend Sub WriteNewSettings(stage As String)
        Dim target = SafeFiles.UnderRoot(stage, "PCL/Setup.ini")
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        File.WriteAllText(target, "VersionArgumentIndie:" & If(Isolated, "1", "2") & vbCrLf & "VersionArgumentIndieV2:" & Isolated.ToString() & vbCrLf, New UTF8Encoding(False))
    End Sub
    Private Function ReadInput(root As String, relative As String) As Input
        Dim path = SafeFiles.UnderRoot(root, relative)
        Dim prior = _inputs.FirstOrDefault(Function(item) String.Equals(SafeFiles.UnderRoot(item.Root, item.Relative), path, StringComparison.OrdinalIgnoreCase))
        If prior IsNot Nothing Then Return prior
        Dim result As New Input With {.Root = root, .Relative = relative}
        Using stream = OpenInput(result)
            result.Bytes = ReadBytes(stream)
        End Using
        _inputs.Add(result)
        Return result
    End Function
    Private Shared Function OpenInput(input As Input) As FileStream
        Dim path = SafeFiles.UnderRoot(input.Root, input.Relative)
        Dim parent = IO.Path.GetDirectoryName(path)
        While parent IsNot Nothing
            If File.Exists(parent) Then Throw New IOException("文件占用了 PCL 设置目录：" & parent)
            parent = IO.Path.GetDirectoryName(parent)
        End While
        Try
            Return New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        Catch ex As FileNotFoundException
            Return Nothing
        Catch ex As DirectoryNotFoundException
            Return Nothing
        End Try
    End Function
    Private Shared Function ReadBytes(stream As FileStream) As Byte()
        If stream Is Nothing Then Return Nothing
        Using buffer As New MemoryStream()
            stream.CopyTo(buffer)
            Return buffer.ToArray()
        End Using
    End Function
    Private Shared Function ReadValue(bytes As Byte(), key As String) As String
        If bytes Is Nothing Then Return Nothing
        Dim result As String = Nothing
        Using reader As New StreamReader(New MemoryStream(bytes), Encoding.UTF8, True)
            While Not reader.EndOfStream
                Dim line = reader.ReadLine(), split = line.IndexOf(":"c)
                ' Pinned IniGetContent accepts colon-delimited, case-sensitive keys.
                If split > 0 AndAlso String.Equals(line.Substring(0, split), key, StringComparison.Ordinal) Then result = line.Substring(split + 1)
            End While
        End Using
        Return result
    End Function
    Private Function HasUserData() As Boolean
        Dim mods = IO.Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" & _name & "/mods/entry"))
        Dim saves = IO.Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" & _name & "/saves/entry"))
        Return (Directory.Exists(mods) AndAlso Directory.EnumerateFiles(mods).Any()) OrElse (Directory.Exists(saves) AndAlso Directory.EnumerateDirectories(saves).Any())
    End Function
    Friend Function LockForRepair() As RepairGuard
        Return New RepairGuard(Me)
    End Function
    Friend NotInheritable Class RepairGuard
        Implements IDisposable
        Private ReadOnly _plan As PclIsolationPlan
        Private ReadOnly _held As New List(Of FileStream)
        Friend Sub New(plan As PclIsolationPlan)
            _plan = plan
            Try
                For Each input In plan._inputs
                    Dim stream = OpenInput(input)
                    If stream IsNot Nothing Then _held.Add(stream)
                    Dim current = ReadBytes(stream)
                    If (current Is Nothing) <> (input.Bytes Is Nothing) OrElse (current IsNot Nothing AndAlso Not current.SequenceEqual(input.Bytes)) Then Throw Changed()
                Next
                Verify()
            Catch
                Dispose() : Throw
            End Try
        End Sub
        Friend Sub Verify()
            ' Missing files cannot be locked without creating user preferences.
            ' Recheck their absence and directory-based inference at every commit.
            For Each input In _plan._inputs.Where(Function(item) item.Bytes Is Nothing)
                Using stream = OpenInput(input)
                    If stream IsNot Nothing Then Throw Changed()
                End Using
            Next
            If _plan._folderSignal.HasValue AndAlso _plan.HasUserData() <> _plan._folderSignal.Value Then Throw Changed()
        End Sub
        Private Shared Function Changed() As InvalidDataException
            Return New InvalidDataException("版本隔离配置在补全期间发生变化，请确认 PCL 设置后重试。")
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            For Each stream In _held
                stream.Dispose()
            Next
            _held.Clear()
        End Sub
    End Class
End Class
