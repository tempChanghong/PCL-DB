Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module BugChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "cache-alias" Then Await CacheAliasAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "empty-file" Then Await EmptyFileAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "corrupt-journal" Then Await CorruptJournalAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown regression case: " & onlyCase)
        Return passed
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Sha1(bytes As Byte()) As String
        Using hash = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Async Function CacheAliasAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer With {.Slow = True}
            Dim bytes(2 * 1024 * 1024 - 1) As Byte
            Dim random As New Random(805)
            random.NextBytes(bytes)
            Dim hash = Sha1(bytes)
            server.Files("/alias") = bytes
            Dim root = Path.Combine(testRoot, "cache-alias")
            Dim paths = New List(Of String) From {"assets/objects/" & hash.Substring(0, 2) & "/" & hash}
            paths.AddRange(Enumerable.Range(0, 7).Select(Function(i) "assets/virtual/legacy/aliases/sound-" & i & ".ogg"))
            Dim files = paths.Select(Function(relative) New DownloadFile With {.RelativePath = relative, .Url = server.Url & "/alias", .Size = bytes.Length, .Sha1 = hash, .Category = "asset"}).ToList()
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {root}, 8, root)
                Await engine.DownloadAsync(root, files, timeout.Token)
                Assert(files.All(Function(item) SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item)), "aliased assets commit every verified destination without competing for the same cache", passed)
                Assert(server.Requests("/alias") = 1 AndAlso engine.Reused = 7, "aliased assets download once and reuse the verified object for legacy paths", passed)
            End Using
            Dim cancelledRoot = Path.Combine(testRoot, "cache-alias-cancel")
            Dim cancelled = False
            Using cancel As New CancellationTokenSource(120), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {cancelledRoot}, 8, cancelledRoot)
                Try
                    Await engine.DownloadAsync(cancelledRoot, files, cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Assert(cancelled AndAlso files.All(Function(item) Not File.Exists(SafeFiles.UnderRoot(cancelledRoot, item.RelativePath))), "cancelling aliased transfers releases waiting workers without publishing partial targets", passed)
            server.Slow = False
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {cancelledRoot}, 8, cancelledRoot)
                Await engine.DownloadAsync(cancelledRoot, files, timeout.Token)
            End Using
            Assert(files.All(Function(item) SafeFiles.Matches(SafeFiles.UnderRoot(cancelledRoot, item.RelativePath), item)), "retry after alias cancellation verifies and publishes every destination", passed)
        End Using
    End Function

    Private Async Function EmptyFileAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim bytes = New Byte() {}
            server.Files("/empty") = bytes
            Dim root = Path.Combine(testRoot, "empty-file")
            Dim item As New DownloadFile With {.RelativePath = "assets/empty", .Url = server.Url & "/empty", .Size = 0, .Sha1 = Sha1(bytes), .Category = "asset"}
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 4, root)
                Await engine.DownloadAsync(root, {item}, timeout.Token)
                Assert(SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "declared zero-byte asset passes its size and SHA-1 checks", passed)
                Await engine.DownloadAsync(root, {item}, timeout.Token)
                Assert(server.Requests("/empty") = 1 AndAlso engine.Reused = 1, "verified empty asset is reused without another HTTP request", passed)
            End Using
            Assert(Not SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), New DownloadFile With {.Size = 4}), "empty file still fails a nonzero expected size", passed)
            Assert(Not SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), New DownloadFile()), "unknown unchecked empty file is not accepted for reuse", passed)
        End Using
    End Function

    Private Async Function CorruptJournalAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer With {.Slow = True}
            Dim bytes(10 * 1024 * 1024 - 1) As Byte
            Dim random As New Random(806)
            random.NextBytes(bytes)
            server.Files("/journal") = bytes
            Dim root = Path.Combine(testRoot, "corrupt-journal")
            Dim item As New DownloadFile With {.RelativePath = "large.bin", .Url = server.Url & "/journal", .Size = bytes.Length, .Sha1 = Sha1(bytes)}
            Dim cancelled = False
            Using cancel As New CancellationTokenSource(120), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 4, root)
                Try
                    Await engine.DownloadAsync(root, {item}, cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Assert(cancelled AndAlso Not File.Exists(Path.Combine(root, item.RelativePath)), "cancelled range transfer leaves no published target", passed)
            Dim cache = Directory.EnumerateDirectories(Path.Combine(root, ".vanilla-downloader", "transfers")).Single()
            Dim journal = Path.Combine(cache, "segments.json")
            File.WriteAllText(journal, New JObject From {{"size", bytes.Length}}.ToString())
            Dim unrelated = Path.Combine(cache, "keep-user-note.txt")
            File.WriteAllText(unrelated, "keep")
            server.Slow = False
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 4, root)
                Await engine.DownloadAsync(root, {item}, timeout.Token)
            End Using
            Assert(SafeFiles.Matches(Path.Combine(root, item.RelativePath), item), "missing parts in a resume journal recover to a verified fresh download", passed)
            Assert(File.ReadAllText(unrelated) = "keep", "journal recovery leaves unrelated cache files intact", passed)
            Dim malformed = New JObject() {
                New JObject From {{"size", "invalid"}, {"parts", New JArray()}},
                New JObject From {{"size", bytes.Length}, {"parts", New JObject()}},
                New JObject From {{"size", bytes.Length}, {"parts", New JArray(JValue.CreateNull())}},
                New JObject From {{"size", bytes.Length}, {"parts", New JArray(New JObject From {{"first", "invalid"}, {"last", bytes.Length - 1}})}}}
            For Each invalid In malformed
                File.Delete(Path.Combine(root, item.RelativePath))
                File.WriteAllText(journal, invalid.ToString())
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 4, root)
                    Await engine.DownloadAsync(root, {item}, timeout.Token)
                End Using
                If Not SafeFiles.Matches(Path.Combine(root, item.RelativePath), item) Then Throw New InvalidOperationException("Invalid journal failed to recover: " & invalid.ToString())
            Next
            Assert(File.ReadAllText(unrelated) = "keep", "invalid journal types and missing range fields recover without removing unrelated files", passed)
        End Using
    End Function
End Module
