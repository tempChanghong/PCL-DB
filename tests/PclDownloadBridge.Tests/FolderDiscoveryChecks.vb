Imports System.IO
Imports PclDownloadBridge.Core

Friend Module FolderDiscoveryChecks
    Friend Function RunAsync(testRoot As String, Optional group As String = "folder-all") As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim root = Path.Combine(testRoot, "folder-discovery-" & Guid.NewGuid().ToString("N"))
        Dim game = Path.Combine(root, ".minecraft"), setup = Path.Combine(root, "PCL", "Setup.ini")
        Directory.CreateDirectory(Path.GetDirectoryName(setup))
        Directory.CreateDirectory(Path.Combine(game, "versions"))
        Dim reader As New PclFolders()
        If group = "folder-all" OrElse group = "folder-alias" Then
            Dim folders = reader.Discover(root, False), original = folders.Single(Function(f) f.Location = game)
            reader.AddCustomFolders(folders, "我的整合包>$.minecraft\", root, "自定义来源")
            Check(original.Name = "我的整合包" AndAlso original.Origin = "自定义来源", "PCL custom directory aliases rename an already scanned game directory", passed)
            reader.AddCustomFolders(folders, "最新名称>" & game.ToUpperInvariant() & "\.\", root, "新的来源")
            Check(folders.Where(Function(f) String.Equals(f.Location, game, StringComparison.OrdinalIgnoreCase)).Count() = 1 AndAlso original.Name = "最新名称" AndAlso original.Origin = "新的来源", "case and dot aliases update the same directory without adding a duplicate", passed)
            Check(Object.ReferenceEquals(original, folders.Single(Function(f) f.Location = game)), "renaming a discovered directory retains its folder object and location", passed)
        End If
        If group = "folder-all" OrElse group = "folder-root" Then
            Dim driveRoot = Path.GetPathRoot(root), folders As New List(Of GameFolder)
            reader.AddCustomFolders(folders, "盘符根目录>" & driveRoot, root, "自定义来源")
            Check(folders.Count = 1 AndAlso folders(0).Location = driveRoot, "a custom drive root retains its trailing separator instead of becoming drive-relative", passed)
            Check(Path.Combine(folders(0).Location, "versions", "fixture") = Path.Combine(driveRoot, "versions", "fixture"), "an imported drive root keeps installation paths rooted on that drive", passed)
            reader.AddCustomFolders(folders, "根目录新名称>" & driveRoot.ToLowerInvariant(), root, "新的来源")
            Check(folders.Count = 1 AndAlso folders(0).Name = "根目录新名称", "equivalent drive roots retain one fully qualified renamed entry", passed)
        End If
        If group = "folder-all" OrElse group = "folder-selection" Then
            File.WriteAllText(setup, "LaunchFolderSelect:?:\invalid" & vbCrLf & "Unrelated:keep")
            Dim before = File.ReadAllBytes(setup), folders = reader.Discover(root, False)
            Check(folders.Any(Function(f) f.Location = game) AndAlso Not folders.Any(Function(f) f.IsSelected), "an invalid PCL selected path cannot hide valid scanned game directories", passed)
            Check(reader.Warnings.Count > 0 AndAlso File.ReadAllBytes(setup).SequenceEqual(before), "invalid selected paths produce a warning without rewriting PCL Setup.ini", passed)
            File.WriteAllText(setup, "LaunchFolderSelect:$.minecraft\")
            folders = reader.Discover(root, False)
            Check(folders.First().Location = game AndAlso folders.First().IsSelected AndAlso reader.Warnings.Count = 0, "a later valid PCL selection is restored and clears warnings from the previous scan", passed)
            File.WriteAllText(setup, "LaunchFolderSelect:" & Path.Combine(root, "missing"))
            folders = reader.Discover(root, False)
            Check(folders.Any(Function(f) f.Location = game) AndAlso Not folders.Any(Function(f) f.IsSelected) AndAlso Not Directory.Exists(Path.Combine(root, "missing")), "a missing selected directory preserves available folders without creating a replacement", passed)
        End If
        If group = "folder-all" OrElse group = "folder-unreadable" Then
            File.WriteAllText(setup, "LaunchFolderSelect:$.minecraft\" & vbCrLf & "Unrelated:keep")
            Dim before = File.ReadAllBytes(setup)
            Using held As New FileStream(setup, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Dim folders = reader.Discover(root, False)
                Check(folders.Any(Function(f) f.Location = game) AndAlso reader.Warnings.Count > 0, "a locked PCL selection file reports a warning while preserving directory discovery", passed)
            End Using
            Check(File.ReadAllBytes(setup).SequenceEqual(before), "a failed PCL selection read leaves the locked configuration byte-identical", passed)
        End If
        If group = "folder-all" OrElse group = "folder-empty" Then
            Dim folders As New List(Of GameFolder)
            reader.AddCustomFolders(folders, Nothing, root, "自定义来源")
            Check(folders.Count = 0, "an absent custom-directory value behaves as an empty list", passed)
            reader.AddCustomFolders(folders, "无效>?:\invalid|有效>$.minecraft\", root, "自定义来源")
            Check(folders.Count = 1 AndAlso folders(0).Location = game AndAlso folders(0).Name = "有效" AndAlso reader.Warnings.Count > 0, "an invalid custom entry does not prevent a later valid directory from being imported", passed)
        End If
        If passed.Count = 0 Then Throw New ArgumentException("Unknown folder regression group: " & group)
        Return Task.FromResult(passed)
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
End Module
