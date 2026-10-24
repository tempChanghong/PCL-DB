Imports System.IO
Imports System.Text
Imports System.Threading

Friend Interface IInstanceConfigurationGuard
    Inherits IDisposable
    Sub Verify()
End Interface

Friend Interface IInstanceIsolationPlan
    ReadOnly Property Isolated As Boolean
    Sub WriteNewSettings(stage As String)
    Function LockForRepair() As IInstanceConfigurationGuard
End Interface

' Snapshot external preferences read-only, then hold their exact bytes (including
' absence) through the short filesystem commit. No migration or initialization.
Friend NotInheritable Class InstanceConfigurationInputs
    Private ReadOnly _files As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)
    Friend Function Read(filePath As String) As Byte()
        Dim full = Path.GetFullPath(filePath)
        If _files.ContainsKey(full) Then Return _files(full)
        Dim bytes As Byte()
        Using stream = Open(full)
            bytes = ReadBytes(stream)
        End Using
        _files(full) = bytes
        Return bytes
    End Function
    Friend Shared Function Text(bytes As Byte()) As String
        If bytes Is Nothing Then Return Nothing
        Using reader As New StreamReader(New MemoryStream(bytes), Encoding.UTF8, True)
            Return reader.ReadToEnd()
        End Using
    End Function
    Private Shared Function Open(full As String) As FileStream
        SafeFiles.UnderRoot(Path.GetDirectoryName(full), Path.GetFileName(full))
        Dim parent = Path.GetDirectoryName(full)
        While parent IsNot Nothing
            If File.Exists(parent) Then Throw New IOException("文件占用了实例配置的父目录：" & parent)
            If Directory.Exists(parent) AndAlso (File.GetAttributes(parent) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("实例配置的父目录包含链接：" & parent)
            parent = Path.GetDirectoryName(parent)
        End While
        If Directory.Exists(full) Then Throw New IOException("文件夹占用了实例配置文件：" & full)
        Try
            Return New FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)
        Catch ex As FileNotFoundException
            Return Nothing
        Catch ex As DirectoryNotFoundException
            Return Nothing
        End Try
    End Function
    Private Shared Function ReadBytes(stream As FileStream) As Byte()
        If stream Is Nothing Then Return Nothing
        If stream.Length > 2 * 1024 * 1024 Then Throw New InvalidDataException("实例配置超过大小限制。")
        Using buffer As New MemoryStream()
            stream.CopyTo(buffer)
            If buffer.Length > 2 * 1024 * 1024 Then Throw New InvalidDataException("实例配置超过大小限制。")
            Return buffer.ToArray()
        End Using
    End Function
    Friend Function Hold(Optional extraCheck As Action = Nothing) As IInstanceConfigurationGuard
        Return New Guard(Me, extraCheck)
    End Function
    Private NotInheritable Class Guard
        Implements IInstanceConfigurationGuard
        Private ReadOnly _owner As InstanceConfigurationInputs
        Private ReadOnly _streams As New List(Of FileStream)
        Private ReadOnly _extra As Action
        Friend Sub New(owner As InstanceConfigurationInputs, extra As Action)
            _owner = owner : _extra = extra
            Try
                For Each item In owner._files
                    Dim stream = Open(item.Key)
                    If stream IsNot Nothing Then _streams.Add(stream)
                    Dim current = ReadBytes(stream)
                    If (current Is Nothing) <> (item.Value Is Nothing) OrElse (current IsNot Nothing AndAlso Not current.SequenceEqual(item.Value)) Then Throw Changed()
                Next
                Verify()
            Catch
                Dispose() : Throw
            End Try
        End Sub
        Public Sub Verify() Implements IInstanceConfigurationGuard.Verify
            For Each item In _owner._files.Where(Function(pair) pair.Value Is Nothing)
                Using stream = Open(item.Key)
                    If stream IsNot Nothing Then Throw Changed()
                End Using
            Next
            _extra?.Invoke()
        End Sub
        Private Shared Function Changed() As InvalidDataException
            Return New InvalidDataException("启动器或实例配置在安装期间发生变化，请确认设置后重试。")
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            For Each stream In _streams
                stream.Dispose()
            Next
            _streams.Clear()
        End Sub
    End Class
End Class
