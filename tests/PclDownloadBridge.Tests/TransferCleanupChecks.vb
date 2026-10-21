Imports System.IO
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module TransferCleanupChecks
    Private Class ProgressCapture
        Implements IProgress(Of TransferProgress)
        Public Latest As TransferProgress
        Public CompletionReported As Boolean
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            Latest = value
            If value.Completed > 0 Then CompletionReported = True
        End Sub
    End Class

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "transfer-cleanup-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-single" Then Await SuccessAsync(testRoot, False, False, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-segment" Then Await SuccessAsync(testRoot, True, False, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-log" Then Await SuccessAsync(testRoot, False, True, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-corrupt" Then Await FailureAsync(testRoot, False, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-publish" Then Await FailureAsync(testRoot, True, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown transfer cleanup regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Item(server As TestServer, large As Boolean) As DownloadFile
        Dim bytes(If(large, 8 * 1024 * 1024, 128 * 1024) - 1) As Byte
        Dim random As New Random(3501)
        random.NextBytes(bytes)
        server.Files("/payload") = bytes
        Using hash = Security.Cryptography.SHA1.Create()
            Return New DownloadFile With {.RelativePath = "downloads/payload.bin", .Url = server.Url & "/payload", .Size = bytes.Length,
                .Sha1 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()}
        End Using
    End Function

    ' Gate the actual HTTP request: the engine has initialized its transfer cache,
    ' but no response body has arrived. No timing-dependent file watcher is needed.
    Private Async Function WaitForRequestAsync(server As TestServer, token As CancellationToken) As Task
        While Not server.Requests.ContainsKey("/payload")
            Await Task.Delay(10, token)
        End While
    End Function

    Private Async Function SuccessAsync(testRoot As String, large As Boolean, blockLog As Boolean, passed As List(Of String)) As Task
        Dim label = If(large, "segment", If(blockLog, "log", "single"))
        Dim root = Path.Combine(testRoot, "transfer-cleanup-" & label)
        Dim progress As New ProgressCapture()
        Using server As New TestServer(), gate As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)),
              engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 1, root)
            Dim file = Item(server, large)
            server.Gates("/payload") = gate
            Dim download = engine.DownloadAsync(root, {file}, timeout.Token)
            Await WaitForRequestAsync(server, timeout.Token)
            Dim cache = Directory.EnumerateDirectories(Path.Combine(root, ".vanilla-downloader", "transfers")).Single()
            Dim locked = Path.Combine(cache, If(large, "segment-0.part", "0.part"))
            IO.File.WriteAllBytes(locked, New Byte() {})
            Dim removable = {"segment-999999999.part", "segments.tmp", "1.part", "2.part", "3.part"}
            For Each name In removable
                IO.File.WriteAllText(Path.Combine(cache, name), "stale")
            Next
            If Not large Then IO.File.WriteAllText(Path.Combine(cache, "segments.json"), "stale")
            Dim unrelated = Path.Combine(cache, "keep-user-note.txt")
            IO.File.WriteAllText(unrelated, "keep")
            Dim unrecognized = Path.Combine(cache, "segment-user.part")
            IO.File.WriteAllText(unrecognized, "keep")
            Dim other = Path.Combine(Path.GetDirectoryName(cache), New String("a"c, 64), "0.part")
            Directory.CreateDirectory(Path.GetDirectoryName(other))
            IO.File.WriteAllText(other, "other transfer")
            Dim log = Path.Combine(root, ".vanilla-downloader", "download.log")
            If blockLog Then Directory.CreateDirectory(log)
            Dim failure As Exception = Nothing
            Using hold As New FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                gate.Set()
                Try
                    Await download
                Catch ex As Exception
                    failure = ex
                End Try
                Check(failure Is Nothing, "verified transfer succeeds while cache cleanup is blocked; " & label & "; failure=" & If(failure?.Message, "none"), passed)
                Check(SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file) AndAlso progress.Latest.Completed = 1 AndAlso
                      engine.Retries = 0 AndAlso engine.NetworkBytes = file.Size AndAlso server.Requests("/payload") = 1,
                      "cleanup failure causes no redownload or false retry statistics; " & label, passed)
                Check(IO.File.Exists(locked) AndAlso removable.All(Function(name) Not IO.File.Exists(Path.Combine(cache, name))) AndAlso
                      Not IO.File.Exists(Path.Combine(cache, "segments.json")), "cleanup continues past occupied files and removes other known fragments; " & label, passed)
                Check(IO.File.ReadAllText(unrelated) = "keep" AndAlso IO.File.ReadAllText(unrecognized) = "keep" AndAlso IO.File.ReadAllText(other) = "other transfer",
                      "transfer cleanup preserves unrelated files and other transfer caches; " & label, passed)
                Check(If(blockLog, Directory.Exists(log), IO.File.Exists(log) AndAlso IO.File.ReadAllText(log).Contains(Path.GetFileName(cache) & "/" & Path.GetFileName(locked))),
                      "cleanup records the retained relative path or tolerates an unavailable log; " & label, passed)
            End Using
            Using retry As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root)
                Await retry.DownloadAsync(root, {file}, timeout.Token)
                Check(retry.Reused = 1 AndAlso retry.NetworkBytes = 0 AndAlso server.Requests("/payload") = 1,
                      "next batch reuses the published verified target after cleanup failure; " & label, passed)
            End Using
        End Using
    End Function

    Private Async Function FailureAsync(testRoot As String, publishConflict As Boolean, passed As List(Of String)) As Task
        Dim label = If(publishConflict, "publish", "corrupt")
        Dim root = Path.Combine(testRoot, "transfer-cleanup-" & label)
        Dim progress As New ProgressCapture()
        Using server As New TestServer With {.Corrupt = Not publishConflict}, gate As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)),
              engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 1, root)
            Dim file = Item(server, False)
            server.Gates("/payload") = gate
            Dim download = engine.DownloadAsync(root, {file}, timeout.Token)
            Await WaitForRequestAsync(server, timeout.Token)
            Dim target = SafeFiles.UnderRoot(root, file.RelativePath)
            Dim marker As String = Nothing
            If publishConflict Then
                Directory.CreateDirectory(target)
                marker = Path.Combine(target, "keep.txt")
                IO.File.WriteAllText(marker, "existing directory")
            End If
            gate.Set()
            Dim failure As Exception = Nothing
            Try
                Await download
            Catch ex As Exception
                failure = ex
            End Try
            Check((TypeOf failure Is IOException OrElse TypeOf failure Is UnauthorizedAccessException) AndAlso Not IO.File.Exists(target) AndAlso Not progress.CompletionReported,
                  "real hash or target publication failure still fails the transfer; " & label, passed)
            Check(If(publishConflict, IO.File.ReadAllText(marker) = "existing directory", engine.Retries = 2 AndAlso server.Requests("/payload") = 2),
                  "real failure preserves target conflicts and normal hash retry behavior; " & label, passed)
            If publishConflict Then
                IO.File.Delete(marker)
                Directory.Delete(target)
            End If
            server.Corrupt = False
            Using retry As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 1, root)
                Await retry.DownloadAsync(root, {file}, timeout.Token)
                Check(SafeFiles.Matches(target, file) AndAlso progress.Latest.Completed = 1, "retry succeeds after fixing the actual failure; " & label, passed)
            End Using
        End Using
    End Function
End Module
