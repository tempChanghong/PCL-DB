Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SegmentResponseChecks
    Private ReadOnly Payload As Byte() = MakePayload()
    Private Function MakePayload() As Byte()
        Dim bytes(8 * 1024 * 1024 - 1) As Byte
        Dim random As New Random(2210)
        random.NextBytes(bytes)
        Return bytes
    End Function
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Item(server As TestServer) As DownloadFile
        server.Files("/payload") = Payload
        Return New DownloadFile With {.RelativePath = "downloads/large.bin", .Url = server.Url & "/payload", .Size = Payload.Length, .Sha1 = Hash(Payload)}
    End Function
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub
    Private Class InlineProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "single-resume-segment-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        For Each mode In {"length", "length-long", "unit", "wildcard", "nototal", "missing", "start", "end", "total"}
            If onlyCase Is Nothing OrElse onlyCase = "single-resume-segment-" & mode Then Await InvalidAsync(root, mode, passed)
        Next
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-segment-valid" Then Await ValidAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-segment-cancel" Then Await CancelAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-segment-corrupt" Then Await CorruptAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-segment-install" Then Await InstallAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function InvalidAsync(root As String, mode As String, passed As List(Of String)) As Task
        root = Path.Combine(root, "segment-response-" & mode)
        Using server As New TestServer With {.InvalidRange = mode}, timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)),
              engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root)
            Dim file = Item(server)
            Await engine.DownloadAsync(root, {file}, timeout.Token)
            Check(server.RangeRequests = 1 AndAlso server.Requests("/payload") = 2 AndAlso engine.NetworkBytes = file.Size AndAlso engine.Retries = 0 AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file), "invalid segment headers fall back before body transfer and publish verified full content: " & mode, passed)
            Check(Not Directory.EnumerateFiles(Path.Combine(root, ".vanilla-downloader", "transfers"), "segment-*.part", SearchOption.AllDirectories).Any() AndAlso
                Not Directory.EnumerateFiles(Path.Combine(root, ".vanilla-downloader", "transfers"), "segments.json", SearchOption.AllDirectories).Any(), "segment fallback clears known fragments only after successful publication: " & mode, passed)
        End Using
    End Function
    Private Async Function ValidAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"normal", "unitcase", "nolength"}
            Dim target = Path.Combine(root, "segment-response-valid-" & mode)
            Using server As New TestServer With {.InvalidRange = mode}, timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)),
                  engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, target)
                Dim file = Item(server)
                If mode = "nolength" Then server.OmitLengths("/payload") = True
                Await engine.DownloadAsync(target, {file}, timeout.Token)
                Check(server.RangeRequests = 1 AndAlso server.Requests("/payload") = 1 AndAlso engine.NetworkBytes = file.Size AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(target, file.RelativePath), file), "valid segment headers retain range transfer and final checksum: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function CancelAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"length", "unit", "wildcard", "nototal"}
            Dim target = Path.Combine(root, "segment-response-cancel-" & mode)
            Using server As New TestServer With {.Slow = True}, safety As New CancellationTokenSource(TimeSpan.FromSeconds(12))
                Dim file = Item(server)
                Using cancel = CancellationTokenSource.CreateLinkedTokenSource(safety.Token)
                    Dim progress As New InlineProgress(Sub(value)
                        If value.NetworkBytes >= 65536 Then cancel.Cancel()
                    End Sub)
                    Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 1, target)
                        Try
                            Await engine.DownloadAsync(target, {file}, cancel.Token)
                            Throw New InvalidOperationException("Seed transfer was not cancelled.")
                        Catch ex As OperationCanceledException When Not safety.IsCancellationRequested
                        End Try
                    End Using
                End Using
                Dim part = Directory.EnumerateFiles(Path.Combine(target, ".vanilla-downloader", "transfers"), "segment-*.part", SearchOption.AllDirectories).Single()
                Dim prefix = IO.File.ReadAllBytes(part), requests = server.Requests("/payload")
                If prefix.Length = 0 OrElse prefix.Length >= Payload.Length Then Throw New InvalidOperationException("Seed transfer did not retain a partial segment.")
                server.Slow = False : server.InvalidRange = mode
                Using gate As New ManualResetEventSlim(), cancel = CancellationTokenSource.CreateLinkedTokenSource(safety.Token),
                      engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, target)
                    server.BodyGates("/payload") = gate
                    Dim work = engine.DownloadAsync(target, {file}, cancel.Token)
                    Try
                        While server.Requests("/payload") < requests + 2 AndAlso Not work.IsCompleted
                            Await Task.Delay(10, safety.Token)
                        End While
                        cancel.Cancel()
                        Try
                            Await work
                        Catch ex As OperationCanceledException When Not safety.IsCancellationRequested
                        End Try
                        Check(server.Requests("/payload") = requests + 2 AndAlso IO.File.ReadAllBytes(part).SequenceEqual(prefix) AndAlso Not IO.File.Exists(SafeFiles.UnderRoot(target, file.RelativePath)), "cancelled single fallback keeps the original segment bytes and publishes nothing: " & mode, passed)
                    Finally
                        cancel.Cancel() : gate.Set() : server.BodyGates.Clear()
                    End Try
                End Using
                server.InvalidRange = Nothing
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, target)
                    Await engine.DownloadAsync(target, {file}, safety.Token)
                    Check(engine.NetworkBytes = file.Size - prefix.Length AndAlso server.RangeStarts.ToArray().Last() = prefix.Length AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(target, file.RelativePath), file), "retry after cancelled fallback resumes the checked segment without redownloading its prefix: " & mode, passed)
                End Using
            End Using
        Next
    End Function
    Private Async Function CorruptAsync(root As String, passed As List(Of String)) As Task
        root = Path.Combine(root, "segment-response-corrupt")
        Using server As New TestServer With {.InvalidRange = "unit", .Corrupt = True}, timeout As New CancellationTokenSource(TimeSpan.FromSeconds(12))
            Dim file = Item(server), rejected = False
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root)
                Try
                    Await engine.DownloadAsync(root, {file}, timeout.Token)
                Catch ex As IOException When TypeOf ex.InnerException Is InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso Not IO.File.Exists(SafeFiles.UnderRoot(root, file.RelativePath)) AndAlso engine.Retries = 2, "single fallback still rejects bad complete-file hashes and never publishes them", passed)
            End Using
            server.Corrupt = False
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root)
                Await engine.DownloadAsync(root, {file}, timeout.Token)
                Check(engine.Reused = 0 AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file), "retry after bad fallback content does not reuse poisoned segments or complete cache", passed)
            End Using
        End Using
    End Function
    Private Async Function InstallAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "segment-response-install")
            fixture.Server.Files("/client") = Payload : fixture.Server.InvalidRange = "length"
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("downloads")("client")("size") = Payload.Length : metadata("downloads")("client")("sha1") = Hash(Payload)
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString()) : fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            Dim result = Await fixture.InstallAsync("large")
            Check(fixture.Server.RangeRequests = 1 AndAlso fixture.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(Path.Combine(result.InstancePath, "large.jar")).SequenceEqual(Payload), "actual installation falls back for a large client and publishes its checked JAR", passed)
            Check(File.Exists(Path.Combine(result.InstancePath, "large.json")), "segment fallback preserves final instance publication", passed)
        End Using
    End Function
End Module
