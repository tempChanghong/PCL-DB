Imports System.IO
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module NamePreflightChecks
    Friend Async Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim unused = Path.Combine(testRoot, "name-preflight-new", ".minecraft")
        Dim invalidNames = {"", " ", " leading", "trailing ", "tail.", "bad/name", "bad\name", "bad:name", "bad!name", "bad;name", "CON", "com0", "LPT0", "CLOCK$", "abc~1", New String("x"c, 101)}
        For i = 0 To invalidNames.Length - 1
            Dim rejected = False
            Try
                InstallPreflight.ValidateDestination(unused, invalidNames(i), False)
            Catch ex As ArgumentException
                rejected = True
            End Try
            Check(rejected, "PCL name preflight rejects invalid case " & i & " before writing", passed)
        Next
        Check(Not Directory.Exists(unused), "name validation creates no game-root or staging directory", passed)
        InstallPreflight.ValidateDestination(unused, "中文 Fabric 0.16.9", False)
        Check(Not Directory.Exists(unused), "valid Unicode names can be checked without creating their destination", passed)

        Dim root = Path.Combine(testRoot, "name-preflight-existing", ".minecraft")
        Dim instance = Path.Combine(root, "versions", "existing")
        Directory.CreateDirectory(instance)
        Dim sentinel = Path.Combine(instance, "keep-user-data.txt")
        Dim before = Encoding.UTF8.GetBytes("preserve existing instance")
        File.WriteAllBytes(sentinel, before)
        Dim collision = False
        Try : InstallPreflight.ValidateDestination(root, "existing", False) : Catch ex As IOException : collision = True : End Try
        Check(collision AndAlso File.ReadAllBytes(sentinel).SequenceEqual(before), "new installation preflight rejects an existing instance without changing its files", passed)
        Dim missingJson = False
        Try : InstallPreflight.ValidateDestination(root, "existing", True) : Catch ex As IOException : missingJson = True : End Try
        Check(missingJson, "repair preflight requires an existing version JSON", passed)
        Dim json = Path.Combine(instance, "existing.json")
        File.WriteAllText(json, "{}")
        InstallPreflight.ValidateDestination(root, "existing", True)
        Check(File.ReadAllText(json) = "{}" AndAlso File.ReadAllBytes(sentinel).SequenceEqual(before), "repair destination checks leave the existing JSON and user files byte-identical", passed)
        File.WriteAllText(Path.Combine(root, "versions", "blocked"), "existing file")
        Dim fileBlocked = False
        Try : InstallPreflight.ValidateDestination(root, "blocked", False) : Catch ex As IOException : fileBlocked = True : End Try
        Check(fileBlocked AndAlso File.ReadAllText(Path.Combine(root, "versions", "blocked")) = "existing file", "a file occupying the instance path is reported without replacement", passed)
        Dim entry As New VersionEntry With {.Id = "1.21.1", .Url = "not a network endpoint"}
        Dim rejectedInstall = False
        Try
            Await New MinecraftInstaller(New SourcePolicy()).InstallAsync(entry, root, "blocked", {}, Nothing, CancellationToken.None)
        Catch ex As IOException
            rejectedInstall = True
        End Try
        Check(rejectedInstall AndAlso Not Directory.Exists(Path.Combine(root, ".vanilla-downloader")), "the real installer rejects an occupied target before metadata or private-directory creation", passed)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
End Module
