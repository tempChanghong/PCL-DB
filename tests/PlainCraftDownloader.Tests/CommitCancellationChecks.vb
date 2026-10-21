Imports System.IO
Imports System.Text
Imports System.Threading
Imports PlainCraftDownloader.Core

Friend Module CommitCancellationChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "commit-cancel-new" Then Await NewInstallAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "commit-cancel-repair" Then Await RepairAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "commit-cancel-entry" Then Await EntryAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "commit-cancel-batch" Then Await BatchAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown commit cancellation regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function PrivatePayloadsAbsent(root As String) As Boolean
        Return {"staging", "components"}.All(Function(branch)
                                                Dim directory = Path.Combine(root, ".vanilla-downloader", branch)
                                                Return Not IO.Directory.Exists(directory) OrElse Not IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any()
                                            End Function)
    End Function

    Private Async Function NewInstallAsync(testRoot As String, passed As List(Of String)) As Task
        For Each withComponents In {False, True}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "commit-new-" & withComponents), cancel As New CancellationTokenSource()
                Dim selection = If(withComponents, fixture.Fabric(), Nothing)
                Dim reached = False
                Dim progress As New InlineProgress(Sub(value)
                                                       If value.Phase <> "commit" Then Return
                                                       reached = True
                                                       cancel.Cancel()
                                                   End Sub)
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync("pending", selection, progress:=progress, token:=cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(reached AndAlso TypeOf failure Is OperationCanceledException, "cancellation from final commit progress remains cancellation; components=" & withComponents, passed)
                Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pending")) AndAlso PrivatePayloadsAbsent(fixture.Root),
                      "cancelled final commit publishes no instance and cleans its private payload; components=" & withComponents, passed)
                Dim kept = File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "assets/log_configs/client.xml")).SequenceEqual(fixture.Server.Files("/log"))
                If withComponents Then kept = kept AndAlso File.ReadAllBytes(fixture.SharedPath()).SequenceEqual(fixture.Server.Files("/shared"))
                Check(kept, "final commit cancellation preserves verified shared assets and component libraries; components=" & withComponents, passed)
                Dim clientRequests = fixture.Server.Requests("/client")
                Dim result = Await fixture.InstallAsync("pending", selection)
                Check(result.NetworkBytes = 0 AndAlso fixture.Server.Requests("/client") = clientRequests AndAlso
                      File.ReadAllBytes(Path.Combine(result.InstancePath, "pending.jar")).SequenceEqual(fixture.Server.Files("/client")) AndAlso File.Exists(Path.Combine(result.InstancePath, "pending.json")),
                      "retry after final cancellation reuses checked content and publishes a complete instance; components=" & withComponents, passed)
            End Using
        Next
    End Function

    Private Async Function RepairAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "commit-repair"), cancel As New CancellationTokenSource()
            Dim result = Await fixture.InstallAsync("existing")
            Dim jsonPath = Path.Combine(result.InstancePath, "existing.json")
            Dim json = File.ReadAllBytes(jsonPath)
            Dim preferences = Path.Combine(result.InstancePath, "PCL", "Setup.ini")
            Directory.CreateDirectory(Path.GetDirectoryName(preferences))
            Dim preferenceBytes = Encoding.UTF8.GetBytes("fixture existing PCL preferences" & vbCrLf)
            File.WriteAllBytes(preferences, preferenceBytes)
            Dim jar = Path.Combine(result.InstancePath, "existing.jar")
            Dim corrupt(fixture.Server.Files("/client").Length - 1) As Byte
            File.WriteAllBytes(jar, corrupt)
            Dim reached = False
            Dim progress As New InlineProgress(Sub(value)
                                                   If value.Phase <> "commit" Then Return
                                                   reached = True
                                                   cancel.Cancel()
                                               End Sub)
            Dim failure As Exception = Nothing
            Try
                Await fixture.InstallAsync("existing", repair:=True, progress:=progress, token:=cancel.Token)
            Catch ex As Exception
                failure = ex
            End Try
            Check(reached AndAlso TypeOf failure Is OperationCanceledException AndAlso fixture.Server.Requests("/client") = 2,
                  "repair can cancel after its replacement client is completely downloaded", passed)
            Check(File.ReadAllBytes(jar).SequenceEqual(corrupt) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(json) AndAlso File.ReadAllBytes(preferences).SequenceEqual(preferenceBytes),
                  "cancelled repair preserves the previous client, JSON and PCL preferences byte-for-byte", passed)
            Check(PrivatePayloadsAbsent(fixture.Root), "cancelled final repair cleans only its private payload", passed)
            Dim retried = Await fixture.InstallAsync("existing", repair:=True)
            Check(retried.NetworkBytes = 0 AndAlso fixture.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(jar).SequenceEqual(fixture.Server.Files("/client")),
                  "repair retry publishes the retained checked replacement without another client request", passed)
            Check(File.ReadAllBytes(jsonPath).SequenceEqual(json) AndAlso File.ReadAllBytes(preferences).SequenceEqual(preferenceBytes),
                  "successful retry still preserves existing metadata and PCL preferences", passed)
        End Using
    End Function

    Private Async Function EntryAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "commit-entry"), cancel As New CancellationTokenSource()
            cancel.Cancel()
            Dim failure As Exception = Nothing
            Try
                Await fixture.InstallAsync("pending", token:=cancel.Token)
            Catch ex As Exception
                failure = ex
            End Try
            Check(TypeOf failure Is OperationCanceledException, "an already cancelled installation returns cancellation", passed)
            Check(fixture.Server.Requests.Count = 0 AndAlso Not Directory.Exists(fixture.Root), "an already cancelled installation creates no game root or writer lock", passed)
        End Using
    End Function

    Private Async Function BatchAsync(testRoot As String, passed As List(Of String)) As Task
        For Each mode In {"empty", "nonempty", "during-plan", "completed"}
            Using server As New TestServer(), cancel As New CancellationTokenSource()
                Dim root = Path.Combine(testRoot, "commit-batch-" & mode)
                Dim completedReports = 0
                Dim progress As New InlineProgress(Sub(value)
                                                       If value.Detail <> "文件下载完成" Then Return
                                                       completedReports += 1
                                                       If mode = "completed" Then cancel.Cancel()
                                                   End Sub)
                Dim files As IEnumerable(Of DownloadFile) = {}
                If mode = "empty" OrElse mode = "nonempty" Then cancel.Cancel()
                If mode = "nonempty" Then files = {New DownloadFile With {.RelativePath = "file.bin", .Url = server.Url & "/file", .Size = 4}}
                If mode = "during-plan" Then
                    files = {New DownloadFile With {.RelativePath = "file.bin", .Url = server.Url & "/file"}}.Where(Function(item)
                                                                                                                    cancel.Cancel()
                                                                                                                    Return False
                                                                                                                End Function)
                End If
                Dim failure As Exception = Nothing
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 2)
                    Try
                        Await engine.DownloadAsync(root, files, cancel.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                End Using
                If mode = "completed" Then
                    Check(failure Is Nothing AndAlso completedReports = 1 AndAlso cancel.IsCancellationRequested AndAlso Not Directory.Exists(root),
                          "cancellation from the completed-batch notification does not retroactively fail a finished empty batch", passed)
                Else
                    Check(TypeOf failure Is OperationCanceledException AndAlso completedReports = 0 AndAlso server.Requests.Count = 0 AndAlso Not Directory.Exists(root),
                          "cancelled download batch makes no requests, files or completion notification; mode=" & mode, passed)
                End If
            End Using
        Next
    End Function
End Module
