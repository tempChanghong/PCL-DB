Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module CleanupChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-component-success" Then Await SuccessAsync(testRoot, passed, False)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-component-log-blocked" Then Await SuccessAsync(testRoot, passed, True)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-component-failure" Then Await FailureAsync(testRoot, passed, False)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-stage-failure" Then Await FailureAsync(testRoot, passed, True)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-stage-cancel" Then Await CancelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "cleanup-publication-failure" Then Await PublicationAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown cleanup regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hold(root As String, stage As Boolean, ByRef heldPath As String) As FileStream
        Dim privateDirectory = Path.Combine(root, ".vanilla-downloader", If(stage, "staging", "components"))
        heldPath = Directory.EnumerateFiles(privateDirectory, If(stage, "pending.jar", "launcher_profiles.json"), SearchOption.AllDirectories).Single()
        Return New FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.Read)
    End Function

    Private Function Logged(root As String, heldPath As String) As Boolean
        Dim log = Path.Combine(root, ".vanilla-downloader", "installer.log")
        If Not File.Exists(log) Then Return False
        Dim text = File.ReadAllText(log)
        Dim relative = Path.GetDirectoryName(heldPath).Substring(root.Length + 1).Replace("\"c, "/"c)
        Return text.Contains("安装临时文件清理未完成") AndAlso text.Contains(relative)
    End Function

    Private Async Function SuccessAsync(testRoot As String, passed As List(Of String), blockedLog As Boolean) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "cleanup-success-" & blockedLog)
            Dim selection = fixture.Fabric()
            Dim logSentinel = Path.Combine(fixture.Root, ".vanilla-downloader", "installer.log", "keep.txt")
            If blockedLog Then
                Directory.CreateDirectory(Path.GetDirectoryName(logSentinel))
                File.WriteAllText(logSentinel, "preserve log blocker")
            End If
            Dim heldPath As String = Nothing, occupied As FileStream = Nothing
            Dim progress As New InlineProgress(Sub(value)
                                                   If value.Stage = "校验安装结果" AndAlso occupied Is Nothing Then occupied = Hold(fixture.Root, False, heldPath)
                                               End Sub)
            Try
                Dim result As InstallResult = Nothing, failure As Exception = Nothing
                Try
                    result = Await fixture.InstallAsync("pending", selection, progress:=progress)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(occupied IsNot Nothing, "successful component fixture really holds a private file; log-blocked=" & blockedLog, passed)
                Check(failure Is Nothing AndAlso result IsNot Nothing, "private cleanup cannot fail an otherwise complete component install; log-blocked=" & blockedLog, passed)
                Check(File.Exists(Path.Combine(result.InstancePath, "pending.json")) AndAlso File.Exists(Path.Combine(result.InstancePath, "pending.jar")) AndAlso
                      File.ReadAllBytes(fixture.SharedPath()).SequenceEqual(fixture.Server.Files("/shared")),
                      "cleanup warning still publishes the verified component instance; log-blocked=" & blockedLog, passed)
                If blockedLog Then
                    Check(File.ReadAllText(logSentinel) = "preserve log blocker" AndAlso File.Exists(heldPath), "failed cleanup logging preserves its blocking directory and the occupied private file", passed)
                Else
                    Check(Logged(fixture.Root, heldPath) AndAlso File.Exists(heldPath), "blocked component cleanup records its private path and retains the occupied file", passed)
                End If
            Finally
                occupied?.Dispose()
            End Try
        End Using
    End Function

    Private Async Function FailureAsync(testRoot As String, passed As List(Of String), stage As Boolean) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "cleanup-failure-" & stage)
            Dim selection = fixture.Fabric()
            Dim route = "/v2/versions/loader/" & fixture.Entry.Id & "/fixture/profile/json"
            Dim profile = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files(route)))
            profile("inheritsFrom") = "wrong-minecraft-version"
            fixture.Server.Files(route) = Encoding.UTF8.GetBytes(profile.ToString())
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.SharedPath()))
            Dim sentinel = Encoding.UTF8.GetBytes("existing shared user file")
            File.WriteAllBytes(fixture.SharedPath(), sentinel)
            Dim heldPath As String = Nothing, occupied As FileStream = Nothing
            Dim progress As New InlineProgress(Sub(value)
                                                   If value.Stage = "下载 Fabric" AndAlso occupied Is Nothing Then occupied = Hold(fixture.Root, stage, heldPath)
                                               End Sub)
            Try
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync("pending", selection, progress:=progress)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(occupied IsNot Nothing, "failed install fixture really holds its private file; staging=" & stage, passed)
                Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("Fabric 配置指向其他游戏版本"),
                      "cleanup cannot replace the original incompatible-profile error; staging=" & stage, passed)
                Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pending")) AndAlso File.ReadAllBytes(fixture.SharedPath()).SequenceEqual(sentinel),
                      "failed installation preserves existing shared bytes and publishes no instance; staging=" & stage, passed)
                Check(Logged(fixture.Root, heldPath) AndAlso File.Exists(heldPath), "failed-install cleanup logs the retained private path; staging=" & stage, passed)
            Finally
                occupied?.Dispose()
            End Try
        End Using
    End Function

    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "cleanup-cancel"), cancel As New CancellationTokenSource(TimeSpan.FromSeconds(20))
            Dim selection = fixture.Fabric()
            Dim heldPath As String = Nothing, occupied As FileStream = Nothing
            Dim progress As New InlineProgress(Sub(value)
                                                   If value.Stage <> "下载 Fabric" OrElse occupied IsNot Nothing Then Return
                                                   occupied = Hold(fixture.Root, True, heldPath)
                                                   cancel.Cancel()
                                               End Sub)
            Try
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync("pending", selection, progress:=progress, token:=cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(occupied IsNot Nothing, "cancelled install fixture really locks the staged client", passed)
                Check(TypeOf failure Is OperationCanceledException AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pending")),
                      "blocked staging cleanup preserves cancellation and publishes no component instance", passed)
                Check(File.ReadAllBytes(heldPath).SequenceEqual(fixture.Server.Files("/client")) AndAlso Logged(fixture.Root, heldPath),
                      "cancelled cleanup retains locked payload bytes and records their private path", passed)
                Using released As New FileStream(Path.Combine(fixture.Root, ".vanilla-downloader", "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    Check(True, "blocked cleanup still releases the game-root writer lock", passed)
                End Using
                occupied.Dispose() : occupied = Nothing
                Dim result = Await fixture.InstallAsync("pending", selection)
                Check(File.Exists(Path.Combine(result.InstancePath, "pending.json")) AndAlso fixture.Server.Requests("/client") = 1,
                      "retry after blocked cancellation publishes the instance without fetching the verified client again", passed)
            Finally
                occupied?.Dispose()
            End Try
        End Using
    End Function

    Private Async Function PublicationAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "cleanup-publication")
            Dim instance = Path.Combine(fixture.Root, "versions", "pending")
            Dim sentinel = Path.Combine(instance, "keep.txt")
            Dim heldPath As String = Nothing, occupied As FileStream = Nothing
            Dim progress As New InlineProgress(Sub(value)
                                                   If value.Phase <> "commit" OrElse occupied IsNot Nothing Then Return
                                                   occupied = Hold(fixture.Root, True, heldPath)
                                                   Directory.CreateDirectory(instance)
                                                   File.WriteAllText(sentinel, "preserve collision")
                                               End Sub)
            Try
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync("pending", progress:=progress)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(occupied IsNot Nothing AndAlso TypeOf failure Is IOException AndAlso File.ReadAllText(sentinel) = "preserve collision" AndAlso
                      Not File.Exists(Path.Combine(instance, "pending.json")), "nonfatal cleanup does not hide a real publication failure or overwrite a colliding instance", passed)
                Check(File.Exists(heldPath) AndAlso Logged(fixture.Root, heldPath), "publication failure logs its blocked staging payload", passed)
                occupied.Dispose() : occupied = Nothing
                File.Delete(sentinel) : Directory.Delete(instance)
                Dim result = Await fixture.InstallAsync("pending")
                Check(File.Exists(Path.Combine(result.InstancePath, "pending.json")) AndAlso fixture.Server.Requests("/client") = 1,
                      "retry after removing a fixture publication collision reuses the checked client", passed)
            Finally
                occupied?.Dispose()
            End Try
        End Using
    End Function
End Module
