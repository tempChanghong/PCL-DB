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
        Dim selected = ReadIniValue(Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect")
        If selected IsNot Nothing Then selected = ExpandPath(selected, basePath)
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
        For Each entry In raw.Split("|"c)
            Dim split = entry.IndexOf(">"c)
            If split < 1 Then Continue For
            Dim location = ExpandPath(entry.Substring(split + 1), basePath)
            AddFolder(result, location, entry.Substring(0, split), origin, True)
        Next
    End Sub

    Private Sub AddFolder(result As List(Of GameFolder), location As String, name As String, origin As String, allowEmpty As Boolean)
        Try
            Dim full = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar)
            If Not Directory.Exists(full) Then Return
            If Not allowEmpty AndAlso Not Directory.Exists(Path.Combine(full, "versions")) Then Return
            If Not result.Any(Function(f) SamePath(f.Location, full)) Then result.Add(New GameFolder With {.Name = name, .Location = full, .Origin = origin})
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is NotSupportedException
            Warnings.Add("忽略无效目录：" & location)
        End Try
    End Sub

    Public Shared Function ExpandPath(value As String, basePath As String) As String
        Return Environment.ExpandEnvironmentVariables(value.Trim().Replace("$", Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar))
    End Function

    Private Shared Function SamePath(a As String, b As String) As Boolean
        Return String.Equals(Path.GetFullPath(a).TrimEnd("\"c, "/"c), Path.GetFullPath(b).TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase)
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
