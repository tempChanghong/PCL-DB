Imports System.IO
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module FailureDiagnosticsChecks
    Private ReadOnly Good As Byte() = Encoding.UTF8.GetBytes("good fixture content")

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-diagnostics-engine" Then Await EngineAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-diagnostics-install" Then Await InstallAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-diagnostics-blocked" Then Await BlockedAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-diagnostics-cancel" Then Await CancelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "transfer-cleanup-diagnostics-parallel" Then Await ParallelAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Async Function EngineAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim root = NewRoot(testRoot), item = Download("payload"), log = LogPath(root)
            SetFailures(server, "payload")
            Using engine As New DownloadEngine(New FixturePolicy(server.Url), Nothing, {}, 2, root)
                Dim failure = Await FailedAsync(Function() engine.DownloadAsync(root, {item}, timeout.Token))
                Check(TypeOf failure Is IOException AndAlso failure.Message.Contains("/payload/first") AndAlso engine.Retries = 4 AndAlso Not File.Exists(Path.Combine(root, item.RelativePath)), "source failure keeps its existing first-exception contract and publishes no content", passed)
                Console.WriteLine("DOWNLOAD_FAILURE_DIAGNOSTICS exists=" & File.Exists(log).ToString())
                Dim text = If(File.Exists(log), File.ReadAllText(log), "")
                Check(text.Contains("/payload/first") AndAlso text.Contains("503") AndAlso text.Contains("/payload/second") AndAlso text.Contains("哈希校验失败") AndAlso text.Contains("下载失败：payload.jar"), "the game log retains both source causes and the target file before flattening the download failure", passed)
                Check(server.Requests("/payload/first") = 2 AndAlso server.Requests("/payload/second") = 2, "diagnostic recording adds no source requests or retry attempts", passed)
                server.StatusCodes.Clear() : server.Files("/payload/first") = Good
                Await engine.DownloadAsync(root, {item}, timeout.Token)
                Check(File.ReadAllBytes(Path.Combine(root, item.RelativePath)).SequenceEqual(Good) AndAlso File.ReadAllText(log) = text, "successful retry publishes verified bytes and preserves the original failure record", passed)
            End Using
        End Using
    End Function

    Private Async Function InstallAsync(passed As List(Of String)) As Task
        Using fixture As New OptiFinePayloadChecks.Fixture(SourcePreference.MirrorFirst), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            fixture.Server.Files(fixture.First) = OptiFinePayloadChecks.Jar(64 * 1024 - 1)
            fixture.Server.Files(fixture.Second) = OptiFinePayloadChecks.Jar(64 * 1024, False)
            Dim failure = Await FailedAsync(Function() fixture.InstallAsync(timeout.Token))
            Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("64 KiB") AndAlso Not Directory.Exists(fixture.InstancePath), "component validation keeps the existing leaf exception and prevents instance publication", passed)
            Dim log = LogPath(fixture.Root), text = If(File.Exists(log), File.ReadAllText(log), "")
            Console.WriteLine("INSTALL_FAILURE_DIAGNOSTICS exists=" & File.Exists(log).ToString())
            Check(text.Contains("OptiFine 官方源与 BMCLAPI 均下载失败") AndAlso text.Contains("64 KiB") AndAlso text.Contains("下载文件不是有效的 OptiFine 安装器"), "actual installation records both component-source validation failures in the task log", passed)
            Check(File.ReadAllText(fixture.Sentinel) = "keep user data" AndAlso fixture.Server.Requests.ContainsKey(fixture.Second), "full diagnostic capture preserves user data and the existing fallback flow", passed)
            fixture.Server.Files(fixture.First) = fixture.Good
            Dim result = Await fixture.InstallAsync(timeout.Token)
            Check(File.Exists(Path.Combine(result.InstancePath, "PCL", "Setup.ini")) AndAlso File.ReadAllText(log) = text, "a successful same-name installation retry keeps its previous source diagnostics available", passed)
        End Using
    End Function

    Private Async Function BlockedAsync(testRoot As String, passed As List(Of String)) As Task
        For Each blocked In {"readonly", "directory", "locked"}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                Dim root = NewRoot(testRoot), log = LogPath(root), original = Encoding.UTF8.GetBytes("keep old log bytes")
                Directory.CreateDirectory(Path.GetDirectoryName(log))
                Dim held As FileStream = Nothing
                If blocked = "directory" Then
                    Directory.CreateDirectory(log) : File.WriteAllBytes(Path.Combine(log, "keep.txt"), original)
                Else
                    File.WriteAllBytes(log, original)
                    If blocked = "readonly" Then File.SetAttributes(log, FileAttributes.ReadOnly)
                    If blocked = "locked" Then held = New FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None)
                End If
                Try
                    SetFailures(server, "blocked")
                    Using engine As New DownloadEngine(New FixturePolicy(server.Url), Nothing, {}, 2, root)
                        Dim failure = Await FailedAsync(Function() engine.DownloadAsync(root, {Download("blocked")}, timeout.Token))
                        Check(TypeOf failure Is IOException AndAlso failure.Message.Contains("/blocked/first") AndAlso engine.Retries = 4, "an unavailable optional failure log cannot replace the original source failure: " & blocked, passed)
                    End Using
                Finally
                    held?.Dispose()
                    If blocked = "readonly" Then File.SetAttributes(log, FileAttributes.Normal)
                End Try
                Check(File.ReadAllBytes(If(blocked = "directory", Path.Combine(log, "keep.txt"), log)).SequenceEqual(original) AndAlso Not File.Exists(Path.Combine(root, "blocked.jar")), "diagnostic write failure preserves the occupied path and cannot publish invalid content: " & blocked, passed)
            End Using
        Next
    End Function

    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer(), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(12))
            Dim root = NewRoot(testRoot), item = Download("cancel")
            server.Files("/cancel/first") = Good : server.BodyGates("/cancel/first") = gate
            Using engine As New DownloadEngine(New FixturePolicy(server.Url), Nothing, {}, 2, root)
                Dim task = engine.DownloadAsync(root, {item}, cancel.Token)
                While Not server.Requests.ContainsKey("/cancel/first") AndAlso Not task.IsCompleted
                    Await Task.Delay(10, timeout.Token)
                End While
                Check(server.Requests.ContainsKey("/cancel/first"), "diagnostic cancellation sample reaches a pending real HTTP body", passed)
                cancel.Cancel()
                Dim failure As Exception
                Try
                    failure = Await FailedAsync(Function() task)
                Finally
                    gate.Set()
                End Try
                Check(TypeOf failure Is OperationCanceledException AndAlso Not File.Exists(LogPath(root)) AndAlso Not server.Requests.ContainsKey("/cancel/second"), "user cancellation remains cancellation without a failure log or alternate-source attempt", passed)
                Await engine.DownloadAsync(root, {item}, timeout.Token)
                Check(File.ReadAllBytes(Path.Combine(root, item.RelativePath)).SequenceEqual(Good) AndAlso Not File.Exists(LogPath(root)), "successful retry after cancellation does not invent failure diagnostics", passed)
            End Using
        End Using
    End Function

    Private Async Function ParallelAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim root = NewRoot(testRoot), log = LogPath(root)
            Directory.CreateDirectory(Path.GetDirectoryName(log)) : File.WriteAllText(log, "existing processor record" & Environment.NewLine)
            SetFailures(server, "one") : SetFailures(server, "two")
            Using one As New DownloadEngine(New FixturePolicy(server.Url), Nothing, {}, 2, root), two As New DownloadEngine(New FixturePolicy(server.Url), Nothing, {}, 2, root)
                Dim failures = Await Task.WhenAll(FailedAsync(Function() one.DownloadAsync(root, {Download("one")}, timeout.Token)), FailedAsync(Function() two.DownloadAsync(root, {Download("two")}, timeout.Token)))
                Check(failures.All(Function(failure) TypeOf failure Is IOException), "concurrent failing batches both return their actual source failure", passed)
                Dim text = File.ReadAllText(log)
                Check(text.StartsWith("existing processor record", StringComparison.Ordinal) AndAlso {"/one/first", "/one/second", "/two/first", "/two/second"}.All(Function(source) text.Contains(source)), "parallel batches append all source diagnostics without losing the previous processor record", passed)
            End Using
        End Using
    End Function

    Private Sub SetFailures(server As TestServer, name As String)
        server.Files("/" & name & "/first") = Good : server.StatusCodes("/" & name & "/first") = 503
        server.Files("/" & name & "/second") = Encoding.UTF8.GetBytes("evil fixture content")
    End Sub
    Private Function Download(name As String) As DownloadFile
        Dim hash As String
        Using sha1 = Security.Cryptography.SHA1.Create()
            hash = BitConverter.ToString(sha1.ComputeHash(Good)).Replace("-", "").ToLowerInvariant()
        End Using
        Return New DownloadFile With {.RelativePath = name & ".jar", .Url = "https://libraries.minecraft.net/" & name, .Size = Good.Length, .Sha1 = hash}
    End Function
    Private Function NewRoot(testRoot As String) As String
        Return Path.Combine(testRoot, "failure-diagnostics-" & Guid.NewGuid().ToString("N"))
    End Function
    Private Function LogPath(root As String) As String
        Return SafeFiles.UnderRoot(root, ".vanilla-downloader/installer.log")
    End Function
    Private Async Function FailedAsync(operation As Func(Of Task)) As Task(Of Exception)
        Try
            Await operation()
        Catch ex As Exception
            Return ex
        End Try
        Throw New InvalidOperationException("Expected the isolated operation to fail.")
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Dim name = New Uri(url).AbsolutePath
            Return New List(Of String) From {_server & name & "/first", _server & name & "/second"}
        End Function
    End Class
End Module
