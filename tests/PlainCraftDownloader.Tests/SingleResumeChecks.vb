Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module SingleResumeChecks
    ' Explicit opt-in public-network check; never runs in the default fixture suite.
    Friend Async Function LiveAsync(url As String, sha1 As String, size As Long, root As String) As Task
        If Not Path.IsPathRooted(root) OrElse Directory.Exists(root) Then Throw New ArgumentException("Live resume requires a new absolute output directory.")
        If size < 256 * 1024 OrElse size >= 8 * 1024 * 1024 Then Throw New ArgumentException("Choose a sized single-connection asset between 256 KiB and 8 MiB.")
        Dim item As New DownloadFile With {.RelativePath = "downloaded.bin", .Url = url, .Sha1 = sha1, .Size = size, .Category = "asset"}
        Dim policy As New SourcePolicy With {.Preference = SourcePreference.OfficialFirst}
        Directory.CreateDirectory(root)
        Dim firstBytes As Long, cancelled As Boolean, cancelElapsed As Long
        Using cancel As New CancellationTokenSource(), safety As New CancellationTokenSource(TimeSpan.FromSeconds(55))
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, safety.Token)
                Dim progress As New CallbackProgress(Sub(value)
                                                         If value.NetworkBytes >= 65536 Then cancel.Cancel()
                                                     End Sub)
                Using engine As New DownloadEngine(policy, progress, {}, 1, root) With {.BytesPerSecondLimit = 256 * 1024}
                    Dim watch = Stopwatch.StartNew()
                    Try
                        Await engine.DownloadAsync(root, {item}, linked.Token)
                    Catch ex As OperationCanceledException
                        cancelled = cancel.IsCancellationRequested AndAlso Not safety.IsCancellationRequested
                    End Try
                    firstBytes = engine.NetworkBytes : cancelElapsed = watch.ElapsedMilliseconds
                End Using
            End Using
        End Using
        If Not cancelled OrElse File.Exists(SafeFiles.UnderRoot(root, item.RelativePath)) Then Throw New InvalidOperationException("Live first download did not stop before publication.")
        Dim prefix = New FileInfo(PartialPath(root)).Length
        Dim resumed As Boolean, retryBytes As Long
        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(55))
            Dim progress As New CallbackProgress(Sub(value)
                                                     If value.Detail IsNot Nothing AndAlso value.Detail.StartsWith("正在续传文件", StringComparison.Ordinal) Then resumed = True
                                                 End Sub)
            Using engine As New DownloadEngine(policy, progress, {}, 1, root)
                Await engine.DownloadAsync(root, {item}, timeout.Token)
                retryBytes = engine.NetworkBytes
            End Using
        End Using
        If Not SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item) Then Throw New InvalidDataException("Live resumed file failed its declared hash.")
        If resumed AndAlso retryBytes <> size - prefix Then Throw New InvalidOperationException("Live resume received unexpected body bytes.")
        Dim report As New JObject From {{"Url", url}, {"ExpectedSha1", sha1}, {"Size", size}, {"CancelledBeforePublication", cancelled}, {"FirstNetworkBytes", firstBytes}, {"RetainedPrefixBytes", prefix}, {"CancellationElapsedMs", cancelElapsed}, {"ResumedRange", resumed}, {"RetryNetworkBytes", retryBytes}, {"FinalHashPassed", True}, {"Root", root}}
        File.WriteAllText(Path.Combine(root, "result.json"), report.ToString(), New UTF8Encoding(False))
        Console.WriteLine(report.ToString())
    End Function

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-cancel" Then Await CancelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-ignored" Then Await IgnoredAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-range" Then Await RangeAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-corrupt" Then Await CorruptAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-unverified" Then Await UnverifiedAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-disconnect" Then Await DisconnectAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-install" Then Await InstallAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "single-resume-complete" Then Await CompleteAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown single resume case: " & onlyCase)
        Return passed
    End Function
    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte(), Optional md5 As Boolean = False) As String
        Using digest = If(md5, DirectCast(Security.Cryptography.MD5.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.SHA1.Create())
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Item(server As TestServer) As DownloadFile
        Dim bytes(2 * 1024 * 1024 - 1) As Byte
        Dim random As New Random(1414)
        random.NextBytes(bytes)
        server.Files("/single") = bytes
        Return New DownloadFile With {.RelativePath = "downloads/single.bin", .Url = server.Url & "/single", .Size = bytes.Length, .Sha1 = Hash(bytes)}
    End Function
    Private Function PartialPath(root As String) As String
        Return Directory.EnumerateFiles(Path.Combine(root, ".vanilla-downloader", "transfers"), "complete.tmp", SearchOption.AllDirectories).Single(Function(path) New FileInfo(path).Length > 65536)
    End Function
    Private Async Function SeedAsync(server As TestServer, root As String, item As DownloadFile) As Task(Of String)
        server.Slow = True
        Using cancel As New CancellationTokenSource(), safety As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, safety.Token)
                Dim progress As New CallbackProgress(Sub(value)
                                                         If value.NetworkBytes >= 65536 Then cancel.Cancel()
                                                     End Sub)
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 1, root)
                    Dim cancelled = False
                    Dim watch = Stopwatch.StartNew()
                    Try
                        Await engine.DownloadAsync(root, {item}, linked.Token)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                    Dim received = engine.NetworkBytes
                    Await Task.Delay(120)
                    If Not cancelled OrElse Not cancel.IsCancellationRequested OrElse safety.IsCancellationRequested OrElse watch.ElapsedMilliseconds > 2500 OrElse engine.NetworkBytes <> received OrElse File.Exists(SafeFiles.UnderRoot(root, item.RelativePath)) Then Throw New InvalidOperationException("Cancellation did not stop and join the single transfer.")
                End Using
            End Using
        End Using
        server.Slow = False
        Dim partialFile = PartialPath(root)
        If New FileInfo(partialFile).Length >= server.Files("/single").Length Then Throw New InvalidOperationException("Fixture did not leave a partial single transfer.")
        Return partialFile
    End Function
    Private Async Function DownloadAsync(server As TestServer, root As String, item As DownloadFile, Optional destination As String = Nothing) As Task(Of Long)
        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root)
            Await engine.DownloadAsync(If(destination, root), {item}, timeout.Token)
            Return engine.NetworkBytes
        End Using
    End Function
    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim item = SingleResumeChecks.Item(server)
            Dim root = Path.Combine(testRoot, "single-resume-cancel")
            Dim partialFile = Await SeedAsync(server, root, item)
            Dim offset = New FileInfo(partialFile).Length
            Assert(offset > 0 AndAlso offset < item.Size, "cancelled single transfer joins its workers and retains an unpublished partial file", passed)
            Dim destination = Path.Combine(testRoot, "single-resume-new-staging")
            Dim received = Await DownloadAsync(server, root, item, destination)
            Console.WriteLine("SINGLE_RESUME offset=" & offset & " secondBytes=" & received & " total=" & item.Size)
            Assert(received = item.Size - offset AndAlso server.RangeStarts.ToArray().LastOrDefault() = offset, "a new engine and staging directory request only the missing single-file suffix", passed)
            Assert(SafeFiles.Matches(SafeFiles.UnderRoot(destination, item.RelativePath), item) AndAlso Not File.Exists(partialFile), "resumed single content passes full hash verification before publication and clears its temporary file", passed)
        End Using
    End Function
    Private Async Function IgnoredAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim item = SingleResumeChecks.Item(server)
            Dim root = Path.Combine(testRoot, "single-resume-ignored")
            Await SeedAsync(server, root, item)
            server.IgnoreRanges = True
            Dim requests = server.Requests("/single")
            Dim received = Await DownloadAsync(server, root, item)
            Assert(server.RangeRequests = 1 AndAlso server.Requests("/single") = requests + 1 AndAlso received = item.Size, "an ignored Range returning HTTP 200 overwrites the partial using that full response", passed)
            Assert(SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "HTTP 200 fallback never appends a complete body to a partial file", passed)
        End Using
    End Function
    Private Async Function RangeAsync(testRoot As String, passed As List(Of String)) As Task
        For Each mode In {"start", "end", "total", "length", "missing", "wildcard", "nototal", "unit", "416"}
            Using server As New TestServer()
                Dim item = SingleResumeChecks.Item(server)
                Dim root = Path.Combine(testRoot, "single-resume-range-" & mode)
                Await SeedAsync(server, root, item)
                server.InvalidRange = mode : server.RejectRanges = mode = "416"
                Dim requests = server.Requests("/single")
                Dim received = Await DownloadAsync(server, root, item)
                Assert(server.RangeRequests = 1 AndAlso server.Requests("/single") = requests + 2 AndAlso received = item.Size AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), mode & " resume response is rejected before append and falls back to a checked full download", passed)
            End Using
        Next
    End Function
    Private Async Function CorruptAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim item = SingleResumeChecks.Item(server)
            Dim root = Path.Combine(testRoot, "single-resume-corrupt")
            Dim partialFile = Await SeedAsync(server, root, item)
            Dim offset = New FileInfo(partialFile).Length
            Using stream As New FileStream(partialFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Dim first = stream.ReadByte()
                stream.Position = 0 : stream.WriteByte(CByte(first Xor 1))
            End Using
            Dim received = Await DownloadAsync(server, root, item)
            Assert(received = item.Size - offset + item.Size AndAlso server.RangeRequests = 1, "a corrupt retained prefix fails the final hash and triggers a fresh full retry", passed)
            Assert(SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "unverified partial content never becomes a published target", passed)
        End Using
    End Function
    Private Async Function UnverifiedAsync(testRoot As String, passed As List(Of String)) As Task
        For Each mode In {"nohash", "unknownsize", "md5"}
            Using server As New TestServer()
                Dim item = SingleResumeChecks.Item(server)
                If mode = "nohash" Then item.Sha1 = Nothing
                If mode = "unknownsize" Then item.Size = -1
                If mode = "md5" Then
                    item.Sha1 = Nothing : item.Md5 = Hash(server.Files("/single"), True)
                End If
                Dim root = Path.Combine(testRoot, "single-resume-" & mode)
                Dim partialFile = Await SeedAsync(server, root, item)
                Dim offset = New FileInfo(partialFile).Length
                Dim received = Await DownloadAsync(server, root, item)
                If mode = "md5" Then
                    Assert(server.RangeRequests = 1 AndAlso received = item.Size - offset AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "declared MD5 plus known size supports checked single-file resume", passed)
                Else
                    Assert(server.RangeRequests = 0 AndAlso received = server.Files("/single").Length AndAlso File.ReadAllBytes(SafeFiles.UnderRoot(root, item.RelativePath)).SequenceEqual(server.Files("/single")), mode & " downloads restart completely instead of trusting retained partial bytes", passed)
                End If
            End Using
        Next
    End Function
    Private Async Function DisconnectAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer With {.TruncateFirstResponseAt = 128 * 1024}
            Dim item = SingleResumeChecks.Item(server)
            Dim root = Path.Combine(testRoot, "single-resume-disconnect")
            Dim received = Await DownloadAsync(server, root, item)
            Console.WriteLine("SINGLE_RECONNECT bytes=" & received & " size=" & item.Size & " requests=" & server.Requests("/single"))
            Assert(server.Requests("/single") = 2 AndAlso server.RangeStarts.ToArray().SingleOrDefault() = 128 * 1024 AndAlso received = item.Size, "automatic retry after an interrupted single response fetches only the missing suffix", passed)
            Assert(SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "automatic single-file reconnect publishes only the complete checked result", passed)
        End Using
    End Function
    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "single-resume-install")
            Dim item = SingleResumeChecks.Item(fixture.Server)
            fixture.Server.Files("/client") = fixture.Server.Files("/single")
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("downloads")("client")("size") = item.Size
            metadata("downloads")("client")("sha1") = item.Sha1
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            fixture.Server.Slow = True
            Using cancel As New CancellationTokenSource(), safety As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, safety.Token)
                    Dim progress As New CallbackProgress(Sub(value)
                                                             If value.CurrentFile = "interrupted.jar" AndAlso value.NetworkBytes >= 65536 Then cancel.Cancel()
                                                         End Sub)
                    Dim cancelled = False
                    Try
                        Await fixture.InstallAsync("interrupted", progress:=progress, token:=linked.Token)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                    Assert(cancelled AndAlso cancel.IsCancellationRequested AndAlso Not Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/interrupted")), "cancelled client installation removes its private payload and leaves no completed instance", passed)
                End Using
            End Using
            Dim partialFile = PartialPath(fixture.Root)
            Dim offset = New FileInfo(partialFile).Length
            fixture.Server.Slow = False
            Dim result = Await fixture.InstallAsync("interrupted")
            Dim client = Path.Combine(result.InstancePath, "interrupted.jar")
            Assert(result.NetworkBytes = item.Size - offset AndAlso fixture.Server.RangeStarts.ToArray().LastOrDefault() = offset AndAlso SafeFiles.Matches(client, item), "retrying an actual installation resumes the staged client across payload cleanup", passed)
        End Using
    End Function
    Private Async Function CompleteAsync(testRoot As String, passed As List(Of String)) As Task
        For Each valid In {True, False}
            Using server As New TestServer()
                Dim item = SingleResumeChecks.Item(server)
                Dim root = Path.Combine(testRoot, "single-resume-complete-" & valid.ToString())
                Dim partialFile = Await SeedAsync(server, root, item)
                Dim bytes = DirectCast(server.Files("/single").Clone(), Byte())
                If Not valid Then bytes(0) = bytes(0) Xor CByte(1)
                File.WriteAllBytes(partialFile, bytes)
                Dim requests = server.Requests("/single")
                Dim received = Await DownloadAsync(server, root, item)
                If valid Then
                    Assert(received = 0 AndAlso server.Requests("/single") = requests AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "a complete retained transfer is hash-checked and published without another request", passed)
                Else
                    Assert(received = item.Size AndAlso server.RangeRequests = 0 AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), "a full-sized corrupt transfer is replaced by a fresh complete response", passed)
                End If
            End Using
        Next
    End Function
    Private Class CallbackProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _callback As Action(Of TransferProgress)
        Friend Sub New(callback As Action(Of TransferProgress))
            _callback = callback
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _callback(value)
        End Sub
    End Class
End Module
