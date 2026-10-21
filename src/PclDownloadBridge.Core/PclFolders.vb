Imports System.IO
Imports Microsoft.Win32

' Behavioral adaptation of PCL 5ef81e4, ModMinecraft.McFolderListLoadSub / SetMcFolder
' and Pages/PageSetup/Settings.vb. See SOURCE_REFERENCES.md for exact provenance.
Public Class PclFolders
    Public ReadOnly Property Warnings As New List(Of String)

    Public Function Discover(pclDirectory As String, Optional readRegistry As Boolean = True) As List(Of GameFolder)
        Warnings.Clear()
        Dim result As New List(Of GameFolder)
        Dim basePath = Path.GetFullPath(pclDirectory)
        Dim selected As String = Nothing
        Try
            Dim stored = ReadIniValue(Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect")
            If Not String.IsNullOrWhiteSpace(stored) Then selected = NormalizeFolderPath(ExpandPath(stored, basePath))
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is NotSupportedException
            Warnings.Add("无法读取 PCL 当前目录设置：" & ex.Message)
        End Try
        AddFolder(result, basePath, "当前游戏目录", "PCL 所在目录", False)
        If Directory.Exists(basePath) Then
            Try
                For Each child In Directory.EnumerateDirectories(basePath)
                    AddFolder(result, child, If(Path.GetFileName(child) = ".minecraft", "PCL 游戏目录", Path.GetFileName(child)), "PCL 旁的目录", Path.GetFileName(child) = ".minecraft")
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Warnings.Add("部分目录无法读取：" & ex.Message)
            End Try
        End If
        If readRegistry Then
            ' PCLDebug is the published source build; PCL is the regular distribution.
            For Each view In {RegistryView.Registry64, RegistryView.Registry32}
                For Each keyName In {"PCL", "PCLDebug"}
                    Try
                        Using hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view), key = hive.OpenSubKey("Software\" & keyName, False)
                            If key Is Nothing Then Continue For
                            AddCustomFolders(result, CStr(key.GetValue("LaunchFolders", "")), basePath, "PCL 自定义目录")
                        End Using
                    Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Security.SecurityException
                        Warnings.Add("无法读取 PCL 目录设置：" & ex.Message)
                    End Try
                Next
            Next
        End If
        If Not String.IsNullOrWhiteSpace(selected) Then
            AddFolder(result, selected, "PCL 当前选择", "PCL\Setup.ini", True)
            For Each folder In result
                folder.IsSelected = SamePath(folder.Location, selected)
            Next
        End If
        Dim official = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
        AddFolder(result, official, "官方启动器目录", "AppData", False)
        Return result.OrderByDescending(Function(f) f.IsSelected).ToList()
    End Function

    Public Sub AddCustomFolders(result As List(Of GameFolder), raw As String, basePath As String, origin As String)
        If String.IsNullOrEmpty(raw) Then Return
        For Each entry In raw.Split("|"c)
            Dim split = entry.IndexOf(">"c)
            If split < 1 Then Continue For
            Dim location = ExpandPath(entry.Substring(split + 1), basePath)
            AddFolder(result, location, entry.Substring(0, split), origin, True, True)
        Next
    End Sub

    Private Sub AddFolder(result As List(Of GameFolder), location As String, name As String, origin As String, allowEmpty As Boolean, Optional customName As Boolean = False)
        Try
            Dim full = NormalizeFolderPath(location)
            If Not Directory.Exists(full) Then Return
            If Not allowEmpty AndAlso Not Directory.Exists(Path.Combine(full, "versions")) Then Return
            Dim existing = result.FirstOrDefault(Function(f) SamePath(f.Location, full))
            If existing Is Nothing Then
                result.Add(New GameFolder With {.Name = name, .Location = full, .Origin = origin})
            ElseIf customName Then
                ' PCL renames an already scanned directory instead of dropping its custom alias.
                existing.Name = name
                existing.Origin = origin
            End If
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is NotSupportedException
            Warnings.Add("忽略无效目录：" & location)
        End Try
    End Sub

    Public Shared Function ExpandPath(value As String, basePath As String) As String
        Return Environment.ExpandEnvironmentVariables(value.Trim().Replace("$", Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar))
    End Function

    Private Shared Function SamePath(a As String, b As String) As Boolean
        Return String.Equals(NormalizeFolderPath(a), NormalizeFolderPath(b), StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function NormalizeFolderPath(value As String) As String
        Dim full = Path.GetFullPath(value), root = Path.GetPathRoot(full)
        Dim trimmed = full.TrimEnd("\"c, "/"c)
        ' C:\ is fully qualified; C: instead resolves against that drive's working directory.
        Return If(trimmed.Length < root.Length, root, trimmed)
    End Function

    Public Shared Function ReadIniValue(path As String, name As String) As String
        If Not File.Exists(path) Then Return Nothing
        Dim value As String = Nothing
        For Each line In File.ReadLines(path)
            Dim index = line.IndexOf(":"c)
            Dim equals = line.IndexOf("="c)
            If equals >= 0 AndAlso (index < 0 OrElse equals < index) Then index = equals
            If index < 0 Then Continue For
            ' PCL IniGetContent overwrites repeated keys: the last occurrence wins.
            If line.Substring(0, index).Trim() = name Then value = line.Substring(index + 1).Trim()
        Next
        Return value
    End Function
End Class
