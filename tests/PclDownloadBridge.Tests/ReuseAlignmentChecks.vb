Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ReuseAlignmentChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "reuse-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "reuse-unknown" Then Await UnknownAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "reuse-client-name" Then Await ClientNamesAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "reuse-declarations" Then Await DeclarationsAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "reuse-corrupt" Then Await CorruptAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "reuse-install" Then Await InstallAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown reuse regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(bytes As Byte(), Optional md5 As Boolean = False) As String
        Using digest = If(md5, DirectCast(Security.Cryptography.MD5.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.SHA1.Create())
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Sub Write(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        File.WriteAllBytes(path, bytes)
    End Sub

    Private Function Snapshot(root As String) As Dictionary(Of String, String)
        Return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(path) path.Substring(root.Length), Function(path) Hash(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase)
    End Function

    Private Function Unchanged(root As String, before As Dictionary(Of String, String)) As Boolean
        Dim after = Snapshot(root)
        Return before.Count = after.Count AndAlso before.All(Function(entry) after.ContainsKey(entry.Key) AndAlso after(entry.Key) = entry.Value)
    End Function

    Private Async Function UnknownAsync(testRoot As String, passed As List(Of String)) As Task
        For Each blankHashes In {False, True}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim root = Path.Combine(testRoot, "reuse-unknown-" & blankHashes)
                Dim oldRoot = root & "-source"
                Dim bytes = Encoding.UTF8.GetBytes("fresh response from the selected source")
                server.Files("/payload") = bytes
                Dim file As New DownloadFile With {.RelativePath = "libraries/unknown.jar", .Url = server.Url & "/payload"}
                If blankHashes Then file.Sha1 = " " : file.Md5 = vbTab
                Write(SafeFiles.UnderRoot(oldRoot, file.RelativePath), Encoding.UTF8.GetBytes("unrelated old file"))
                Dim before = Snapshot(oldRoot)
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {oldRoot}, 2, root)
                    Await engine.DownloadAsync(root, {file}, timeout.Token)
                    Check(IO.File.ReadAllBytes(SafeFiles.UnderRoot(root, file.RelativePath)).SequenceEqual(bytes) AndAlso engine.Reused = 0 AndAlso
                          engine.NetworkBytes = bytes.Length AndAlso server.Requests.ContainsKey("/payload") AndAlso server.Requests("/payload") = 1,
                          "no size or checksum declaration means fresh download instead of cross-folder reuse; blank=" & blankHashes, passed)
                End Using
                Check(Unchanged(oldRoot, before), "unverified source files remain byte-for-byte untouched; blank=" & blankHashes, passed)
                Check(Not Directory.EnumerateFiles(root, "*.reuse-*", SearchOption.AllDirectories).Any(), "fresh fallback leaves no reuse temporary files; blank=" & blankHashes, passed)
            End Using
        Next
    End Function

    Private Async Function ClientNamesAsync(testRoot As String, passed As List(Of String)) As Task
        For Each hashOnly In {False, True}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim root = Path.Combine(testRoot, "reuse-client-name-" & hashOnly)
                Dim oldRoot = root & "-source"
                Dim bytes = Encoding.UTF8.GetBytes("fixture checked client")
                server.Files("/client") = bytes
                Dim file As New DownloadFile With {.RelativePath = "new-client.jar", .Url = server.Url & "/client", .Sha1 = Hash(bytes), .Size = If(hashOnly, -1, bytes.Length)}
                Dim folder = Path.Combine(oldRoot, "versions", "旧版本 (副本)")
                Write(Path.Combine(folder, "旧版本 (副本).jar"), New Byte(bytes.Length - 1) {})
                Write(Path.Combine(folder, "downloaded-client.JAR"), bytes)
                Write(Path.Combine(folder, "matching.json"), bytes)
                Dim before = Snapshot(oldRoot)
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {oldRoot}, 2, root)
                    Await engine.DownloadAsync(root, {file}, timeout.Token)
                    Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0 AndAlso Not server.Requests.ContainsKey("/client"),
                          "client reuse searches checked JARs beyond the version directory name; hashOnly=" & hashOnly, passed)
                    Check(SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file), "renamed client copy passes the full declared checksum; hashOnly=" & hashOnly, passed)
                End Using
                Check(Unchanged(oldRoot, before), "renamed client search preserves all old version files; hashOnly=" & hashOnly, passed)
            End Using
        Next
    End Function

    Private Async Function DeclarationsAsync(testRoot As String, passed As List(Of String)) As Task
        For Each declaration In {"size", "sha1", "md5", "empty-size", "empty-sha1"}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim root = Path.Combine(testRoot, "reuse-declaration-" & declaration)
                Dim oldRoot = root & "-source"
                Dim bytes = If(declaration.StartsWith("empty-", StringComparison.Ordinal), New Byte() {}, Encoding.UTF8.GetBytes("fixture declared reusable file"))
                server.Files("/payload") = bytes
                Dim file As New DownloadFile With {.RelativePath = "libraries/declared.jar", .Url = server.Url & "/payload"}
                Select Case declaration
                    Case "size", "empty-size" : file.Size = bytes.Length
                    Case "sha1", "empty-sha1" : file.Sha1 = Hash(bytes)
                    Case "md5" : file.Md5 = Hash(bytes, True)
                End Select
                Write(SafeFiles.UnderRoot(oldRoot, file.RelativePath), bytes)
                Dim before = Snapshot(oldRoot)
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {oldRoot}, 2, root)
                    Await engine.DownloadAsync(root, {file}, timeout.Token)
                    Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0 AndAlso Not server.Requests.ContainsKey("/payload") AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file),
                          "declared size or digest remains sufficient for checked cross-folder reuse; " & declaration, passed)
                End Using
                Check(Unchanged(oldRoot, before), "declared cross-folder reuse never writes to the source root; " & declaration, passed)
            End Using
        Next
    End Function

    Private Async Function CorruptAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            Dim root = Path.Combine(testRoot, "reuse-corrupt")
            Dim oldRoot = root & "-source"
            Dim bytes = Encoding.UTF8.GetBytes("fixture correct client")
            server.Files("/client") = bytes
            Dim file As New DownloadFile With {.RelativePath = "new.jar", .Url = server.Url & "/client", .Size = bytes.Length, .Sha1 = Hash(bytes)}
            Dim folder = Path.Combine(oldRoot, "versions", "existing")
            Write(Path.Combine(folder, "renamed.jar"), New Byte(bytes.Length - 1) {})
            Write(Path.Combine(folder, "matching.json"), bytes)
            Write(Path.Combine(folder, "nested", "matching.jar"), bytes)
            Dim before = Snapshot(oldRoot)
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {oldRoot}, 2, root)
                Await engine.DownloadAsync(root, {file}, timeout.Token)
                Check(engine.Reused = 0 AndAlso engine.NetworkBytes = bytes.Length AndAlso server.Requests("/client") = 1 AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file),
                      "corrupt renamed JARs fall back to downloading without treating JSON or nested files as clients", passed)
            End Using
            Check(Unchanged(oldRoot, before), "corrupt optional source and unrelated files stay untouched during fallback", passed)
        End Using
    End Function

    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "reuse-client-name"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim oldRoot = Path.Combine(testRoot, "reuse-install-source")
            Dim folder = Path.Combine(oldRoot, "versions", "已有实例")
            Write(Path.Combine(folder, "custom-client.jar"), fixture.Server.Files("/client"))
            Write(Path.Combine(folder, "已有实例.json"), Encoding.UTF8.GetBytes("fixture old metadata"))
            Write(Path.Combine(folder, "PCL", "Setup.ini"), Encoding.UTF8.GetBytes("fixture old settings"))
            Dim before = Snapshot(oldRoot)
            Dim installer As New MinecraftInstaller(New SourcePolicy With {.AllowLoopback = True})
            Dim result = Await installer.InstallAsync(fixture.Entry, fixture.Root, "新实例", {oldRoot}, Nothing, timeout.Token)
            Check(Not fixture.Server.Requests.ContainsKey("/client") AndAlso File.ReadAllBytes(Path.Combine(result.InstancePath, "新实例.jar")).SequenceEqual(fixture.Server.Files("/client")),
                  "real installer stages and publishes a renamed checked PCL client without another client request", passed)
            Dim json = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "新实例.json")))
            Check(CStr(json("id")) = "新实例" AndAlso CStr(json("clientVersion")) = fixture.Entry.Id AndAlso Unchanged(oldRoot, before),
                  "new instance keeps its own PCL-readable identity while preserving old JSON and PCL settings", passed)
            Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader", "staging")
            Check(Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(), "successful reuse installation leaves no private staged payload", passed)
        End Using
    End Function
End Module
