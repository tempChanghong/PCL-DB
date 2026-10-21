Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports PlainCraftDownloader.Core

Friend Module RateLimitChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "rate-idle" Then Await IdleAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "rate-parallel" Then Await ParallelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "rate-cancel" Then Await CancelAsync(testRoot, passed)
        ' Opt-in wall-clock check crosses the production 45-second inactivity deadline.
        If onlyCase = "rate-slow" Then Await SlowAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown rate regression case: " & onlyCase)
        Return passed
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Item(server As TestServer, name As String, size As Integer) As DownloadFile
        Dim bytes(size - 1) As Byte
        Dim random As New Random(name.GetHashCode())
        random.NextBytes(bytes)
        server.Files("/" & name) = bytes
        Dim hash As String
        Using sha = Security.Cryptography.SHA1.Create()
            hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
        Return New DownloadFile With {.RelativePath = name & ".bin", .Url = server.Url & "/" & name, .Size = size, .Sha1 = hash}
    End Function

    Private Async Function IdleAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "rate-idle")
            Directory.CreateDirectory(root)
            Dim reused = Item(server, "already-present", 4096)
            File.WriteAllBytes(Path.Combine(root, reused.RelativePath), server.Files("/already-present"))
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 8, root) With {.BytesPerSecondLimit = 96 * 1024}
                Await engine.DownloadAsync(root, {reused}, timeout.Token)
                For batch = 1 To 2
                    ' Represents time spent reusing/checking files or between installer phases.
                    Await Task.Delay(1200, timeout.Token)
                    Dim file = Item(server, "after-idle-" & batch, 192 * 1024)
                    Dim clock = Stopwatch.StartNew()
                    Await engine.DownloadAsync(root, {file}, timeout.Token)
                    Console.WriteLine("RATE_IDLE batch=" & batch & " elapsedMs=" & clock.ElapsedMilliseconds)
                    Assert(clock.ElapsedMilliseconds >= 1650 AndAlso SafeFiles.Matches(Path.Combine(root, file.RelativePath), file), "idle/reuse time does not buy an unrestricted burst in batch " & batch, passed)
                Next
                Assert(engine.Reused = 1 AndAlso Not server.Requests.ContainsKey("/already-present"), "rate limiting preserves verified reuse without an HTTP request", passed)
            End Using
        End Using
    End Function

    Private Async Function ParallelAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "rate-parallel")
            Dim files = Enumerable.Range(0, 4).Select(Function(i) Item(server, "parallel-" & i, 128 * 1024)).ToList()
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 8, root) With {.BytesPerSecondLimit = 256 * 1024}
                Dim clock = Stopwatch.StartNew()
                Await engine.DownloadAsync(root, files, timeout.Token)
                Console.WriteLine("RATE_PARALLEL elapsedMs=" & clock.ElapsedMilliseconds & " bytes=" & engine.NetworkBytes)
                Assert(clock.ElapsedMilliseconds >= 1650, "parallel files share one aggregate bandwidth budget", passed)
                Assert(engine.NetworkBytes = 512 * 1024 AndAlso files.All(Function(f) SafeFiles.Matches(Path.Combine(root, f.RelativePath), f)), "rate-limited parallel files publish only complete matching hashes", passed)
            End Using
        End Using
    End Function

    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "rate-cancel")
            Dim large = Item(server, "cancel-range", 8 * 1024 * 1024)
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 4, root) With {.BytesPerSecondLimit = 1024}
                Dim cancelled = False
                Dim clock = Stopwatch.StartNew()
                Using cancel As New CancellationTokenSource(350)
                    Try
                        Await engine.DownloadAsync(root, {large}, cancel.Token)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                End Using
                Console.WriteLine("RATE_CANCEL elapsedMs=" & clock.ElapsedMilliseconds & " bytes=" & engine.NetworkBytes)
                Assert(cancelled AndAlso clock.ElapsedMilliseconds < 2000 AndAlso server.RangeRequests > 0 AndAlso engine.NetworkBytes > 0 AndAlso Not File.Exists(Path.Combine(root, large.RelativePath)), "cancellation interrupts throttled range workers and leaves no published partial target", passed)
                Dim small = Item(server, "after-cancel", 1024)
                clock.Restart()
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(5))
                    Await engine.DownloadAsync(root, {small}, timeout.Token)
                End Using
                Console.WriteLine("RATE_RESTART elapsedMs=" & clock.ElapsedMilliseconds)
                Assert(clock.ElapsedMilliseconds >= 750 AndAlso clock.ElapsedMilliseconds < 2500 AndAlso SafeFiles.Matches(Path.Combine(root, small.RelativePath), small), "a new batch on the same engine discards cancelled throttle debt and still obeys its limit", passed)
                engine.BytesPerSecondLimit = 0
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                    Await engine.DownloadAsync(root, {large}, timeout.Token)
                End Using
                Assert(SafeFiles.Matches(Path.Combine(root, large.RelativePath), large), "throttled range cancellation remains resumable with a matching final hash", passed)
            End Using
        End Using
    End Function

    Private Async Function SlowAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "rate-slow")
            Dim file = Item(server, "intentional-wait", 1024)
            Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(65)), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 1, root) With {.BytesPerSecondLimit = 20}
                Dim clock = Stopwatch.StartNew()
                Await engine.DownloadAsync(root, {file}, timeout.Token)
                Console.WriteLine("RATE_SLOW elapsedMs=" & clock.ElapsedMilliseconds & " retries=" & engine.Retries)
                Assert(clock.ElapsedMilliseconds >= 50000 AndAlso engine.Retries = 0 AndAlso SafeFiles.Matches(Path.Combine(root, file.RelativePath), file), "intentional throttle wait longer than 45 seconds does not trigger a network inactivity timeout", passed)
            End Using
        End Using
    End Function
End Module
