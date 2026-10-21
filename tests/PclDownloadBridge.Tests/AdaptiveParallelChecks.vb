Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module AdaptiveParallelChecks
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "adaptive-source" Then Await SourcesAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "parallel-stages" Then Await StagesAsync(root, passed, False)
        If onlyCase Is Nothing OrElse onlyCase = "parallel-cancel" Then Await StagesAsync(root, passed, True)
        If onlyCase Is Nothing OrElse onlyCase = "parallel-budget" Then Await BudgetAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "parallel-failure" Then Await FailureAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Manifest() As Byte()
        Dim entries As New JArray()
        For i = 1 To 200
            entries.Add(New JObject From {{"id", "fixture-" & i}, {"type", "release"}, {"url", "https://example.org/version.json"}, {"sha1", New String("a"c, 40)}, {"releaseTime", "2026-01-01T00:00:00Z"}})
        Next
        Return Encoding.UTF8.GetBytes(New JObject From {{"versions", entries}}.ToString())
    End Function
    Private Async Function SourcesAsync(passed As List(Of String)) As Task
        Const fileUrl As String = "https://resources.download.minecraft.net/ab/hash"
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(25))
            server.Files("/official") = Manifest() : server.Files("/mirror") = Manifest()
            Dim state As New SourceSelectionState()
            Dim policy As New ManifestPolicy(server.Url, state)
            Check(Not policy.PreferOfficial AndAlso New Uri(policy.Candidates(fileUrl, "asset")(0)).Host.Contains("bmclapi"), "automatic files start mirror-first before the official manifest probe", passed)
            Dim versions = Await New MinecraftInstaller(policy).VersionsAsync(timeout.Token)
            Check(versions.Count = 200 AndAlso state.PreferOfficial AndAlso server.Requests("/official") = 1 AndAlso Not server.Requests.ContainsKey("/mirror"), "fast validated official manifest wins before delayed mirror and enables official files", passed)
            Dim nextPolicy As New SourcePolicy(state)
            Check(nextPolicy.Candidates(fileUrl, "asset")(0) = fileUrl, "new policies share the same session decision across list and install", passed)
            nextPolicy.Preference = SourcePreference.MirrorFirst
            Check(New Uri(nextPolicy.Candidates(fileUrl, "asset")(0)).Host.Contains("bmclapi"), "explicit mirror setting overrides a successful automatic probe", passed)
            policy.Preference = SourcePreference.MirrorFirst
            Await policy.GetVersionManifestAsync(timeout.Token)
            Check(server.Requests("/mirror") = 1 AndAlso server.Requests("/official") = 1, "mirror-first manifest does not issue the official fallback after a successful mirror", passed)
            server.Files("/official") = Encoding.UTF8.GetBytes("{""versions"":[]}")
            Dim invalidState As New SourceSelectionState()
            policy = New ManifestPolicy(server.Url, invalidState)
            Await policy.GetVersionManifestAsync(timeout.Token)
            Check(Not invalidState.PreferOfficial AndAlso server.Requests("/mirror") = 2, "invalid official manifest starts fallback immediately and cannot teach official preference", passed)
            server.Files("/official") = Manifest()
            server.PathDelayMs("/official") = 4200
            Dim slowState As New SourceSelectionState()
            Await New ManifestPolicy(server.Url, slowState).GetVersionManifestAsync(timeout.Token)
            Check(Not slowState.PreferOfficial AndAlso server.Requests("/mirror") = 2, "valid official response slower than four seconds keeps automatic files mirror-first", passed)
            server.PathDelayMs("/official") = 6500
            Dim clock = Stopwatch.StartNew()
            Dim fallbackState As New SourceSelectionState()
            Await New ManifestPolicy(server.Url, fallbackState).GetVersionManifestAsync(timeout.Token)
            Check(clock.ElapsedMilliseconds >= 4800 AndAlso clock.ElapsedMilliseconds < 6400 AndAlso server.Requests("/mirror") = 3 AndAlso Not fallbackState.PreferOfficial, "automatic manifest starts mirror after the PCL five second grace and cancels the losing official request", passed)
            server.PathDelayMs("/official") = 2000
            Dim cancelledState As New SourceSelectionState()
            Dim cancelled = False
            Using cancel As New CancellationTokenSource(100)
                Try
                    Await New ManifestPolicy(server.Url, cancelledState).GetVersionManifestAsync(cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(cancelled AndAlso Not cancelledState.PreferOfficial, "cancelled manifest cannot change the session's automatic source decision", passed)
            Check(New SourcePolicy With {.Preference = SourcePreference.OfficialFirst}.PreferOfficial AndAlso Not New SourcePolicy().PreferOfficial, "explicit official preference and independent sessions remain isolated", passed)
        End Using
    End Function
    Private Class ManifestPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String, state As SourceSelectionState)
            MyBase.New(state)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & If(New Uri(source).Host.Contains("bmclapi"), "/mirror", "/official")
        End Function
    End Class
    Private Async Function StagesAsync(root As String, passed As List(Of String), cancelInstall As Boolean) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "parallel-" & cancelInstall), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim asset = Encoding.UTF8.GetBytes("parallel asset with a gated response")
            Dim hashValue = Hash(asset)
            Dim assetPath = "/" & hashValue.Substring(0, 2) & "/" & hashValue
            fixture.Server.Files(assetPath) = asset : fixture.Server.Gates(assetPath) = gate
            Dim index = Encoding.UTF8.GetBytes(New JObject From {{"objects", New JObject From {{"fixture.dat", New JObject From {{"hash", hashValue}, {"size", asset.Length}}}}}}.ToString())
            fixture.Server.Files("/index") = index
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("assetIndex")("size") = index.Length : metadata("assetIndex")("sha1") = Hash(index)
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            Dim ready As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim snapshots As New List(Of TransferProgress)
            Dim progress As New InlineProgress(Sub(value)
                SyncLock snapshots
                    snapshots.Add(value)
                End SyncLock
                If value.Stage = "组件安装完成" Then ready.TrySetResult(True)
            End Sub)
            Dim install = fixture.InstallAsync("parallel", fixture.Fabric(), progress:=progress, token:=cancel.Token)
            Try
                Dim winner = Await Task.WhenAny(ready.Task, install, Task.Delay(10000))
                Check(winner Is ready.Task AndAlso Not install.IsCompleted, "components finish while the independent resource branch is still blocked (cancel=" & cancelInstall & ")", passed)
                Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "parallel")), "parallel branches cannot publish an incomplete instance (cancel=" & cancelInstall & ")", passed)
                Dim sample = snapshots.Last(Function(v) v.Stage = "组件安装完成")
                Check(sample.PhaseFractions("files") < 1 AndAlso sample.PhaseFractions("components") = 1 AndAlso sample.PhaseFractions("processors") = 1, "parallel progress preserves unfinished file phase after completed components (cancel=" & cancelInstall & ")", passed)
                If cancelInstall Then
                    Dim lockPath = Path.Combine(fixture.Root, ".vanilla-downloader", "install.lock")
                    Dim lockHeld = False
                    Try
                        Using competingWriter As New FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        End Using
                    Catch ex As IOException
                        lockHeld = True
                    End Try
                    Check(lockHeld, "an unfinished parallel installation keeps its game-root write lock", passed)
                    cancel.Cancel()
                    Dim cancelled = False
                    Try
                        Await install
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                    Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "parallel")), "cancellation joins parallel branches and leaves no published version", passed)
                    Using releasedLock As New FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader", "staging")
                        Check(Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(), "completed cancellation releases the root lock and removes private installation payloads", passed)
                    End Using
                    gate.Set()
                    Dim retried = Await fixture.InstallAsync("parallel", fixture.Fabric())
                    Check(File.Exists(Path.Combine(retried.InstancePath, "parallel.json")) AndAlso File.Exists(Path.Combine(retried.InstancePath, "parallel.jar")) AndAlso
                        File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "assets/objects" & assetPath)).SequenceEqual(asset) AndAlso
                        File.ReadAllBytes(fixture.SharedPath()).SequenceEqual(fixture.Server.Files("/shared")) AndAlso retried.Reused > 0,
                        "retrying the same cancelled instance publishes verified resources and libraries with completed files reused", passed)
                Else
                    gate.Set()
                    Dim result = Await install
                    Check(File.Exists(Path.Combine(result.InstancePath, "parallel.json")) AndAlso File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "assets/objects" & assetPath)).SequenceEqual(asset), "instance publishes only after all independent branches validate successfully", passed)
                    Dim lastFiles = snapshots.Last(Function(v) v.PhaseFractions IsNot Nothing)
                    Check(lastFiles.NetworkBytes = result.NetworkBytes AndAlso lastFiles.Reused = result.Reused AndAlso lastFiles.ActiveConnections = 0, "parallel snapshots aggregate all bytes and reuse counts once and drain active connections", passed)
                End If
            Finally
                gate.Set()
            End Try
        End Using
    End Function
    Private Async Function BudgetAsync(root As String, passed As List(Of String)) As Task
        Using server As New TestServer(), budget As New DownloadBudget(2, 256 * 1024), cancel As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim bytes(256 * 1024 - 1) As Byte
            Dim random As New Random(16)
            random.NextBytes(bytes)
            Dim active As Integer
            Dim progress As New InlineProgress(Sub(value)
                SyncLock passed
                    active = Math.Max(active, value.ActiveConnections)
                End SyncLock
            End Sub)
            Dim engines As New List(Of DownloadEngine)
            Dim tasks As New List(Of Task)
            Dim clock = Stopwatch.StartNew()
            Try
                For i = 1 To 3
                    Dim route = "/budget-" & i
                    server.Files(route) = bytes
                    Dim engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 16, budget:=budget)
                    engines.Add(engine)
                    tasks.Add(engine.DownloadAsync(Path.Combine(root, "parallel-budget-" & i), {New DownloadFile With {.RelativePath = "file.bin", .Url = server.Url & route, .Sha1 = Hash(bytes), .Size = bytes.Length}}, cancel.Token))
                Next
                Await Task.WhenAll(tasks)
                Check(clock.Elapsed.TotalSeconds >= 2.7 AndAlso clock.Elapsed.TotalSeconds < 10, "three concurrent engines share one aggregate 256 KiB/s budget", passed)
                Check(active = 2 AndAlso engines.Sum(Function(e) e.NetworkBytes) = bytes.Length * 3L, "parallel engines respect the shared two-connection cap and count every byte once", passed)
                For i = 0 To engines.Count - 1
                    Check(SafeFiles.Matches(Path.Combine(root, "parallel-budget-" & (i + 1), "file.bin"), New DownloadFile With {.Size = bytes.Length, .Sha1 = Hash(bytes)}), "shared-budget branch " & i & " retains full hash validation", passed)
                Next
                server.Files("/alias") = bytes.Take(1024).ToArray()
                Dim aliasFile As New DownloadFile With {.RelativePath = "same.bin", .Url = server.Url & "/alias", .Size = 1024, .Sha1 = Hash(server.Files("/alias"))}
                Dim cacheRoot = Path.Combine(root, "parallel-alias-cache")
                Dim left = Path.Combine(root, "parallel-alias-left"), right = Path.Combine(root, "parallel-alias-right")
                Using first As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, cacheRoot:=cacheRoot, useComponentCache:=True, budget:=budget),
                      second As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, cacheRoot:=cacheRoot, useComponentCache:=True, budget:=budget)
                    Await Task.WhenAll(first.DownloadAsync(left, {aliasFile}, cancel.Token), second.DownloadAsync(right, {aliasFile}, cancel.Token))
                    Check(server.Requests("/alias") = 1 AndAlso first.NetworkBytes + second.NetworkBytes = 1024 AndAlso first.Reused + second.Reused = 1, "different parallel engines serialize one transfer-cache owner and reuse the checked result", passed)
                    Check(SafeFiles.Matches(Path.Combine(left, "same.bin"), aliasFile) AndAlso SafeFiles.Matches(Path.Combine(right, "same.bin"), aliasFile), "parallel cache aliases publish both independently checked destinations", passed)
                End Using
            Finally
                For Each engine In engines : engine.Dispose() : Next
            End Try
        End Using
    End Function
    Private Async Function FailureAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "parallel-failure"), gate As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(20))
            Dim selection = fixture.Fabric()
            Dim correct = fixture.Server.Files("/shared")
            fixture.Server.Files("/shared") = Encoding.UTF8.GetBytes("bad runtime")
            Dim asset = Encoding.UTF8.GetBytes("failure peer asset")
            Dim hashValue = Hash(asset)
            Dim route = "/" & hashValue.Substring(0, 2) & "/" & hashValue
            fixture.Server.Files(route) = asset : fixture.Server.Gates(route) = gate
            Dim index = Encoding.UTF8.GetBytes(New JObject From {{"objects", New JObject From {{"peer", New JObject From {{"hash", hashValue}, {"size", asset.Length}}}}}}.ToString())
            fixture.Server.Files("/index") = index
            Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            meta("assetIndex")("size") = index.Length : meta("assetIndex")("sha1") = Hash(index)
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            Dim lastPhase As String = Nothing
            Dim progress As New InlineProgress(Sub(value) lastPhase = value.Phase)
            Dim install = fixture.InstallAsync("failure", selection, progress:=progress, token:=timeout.Token)
            Dim failed = False
            Try
                Await install
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                failed = True
            End Try
            Check(failed AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "failure")), "failed parallel install preserves the original error and never publishes an instance", passed)
            Using released As New FileStream(SafeFiles.UnderRoot(fixture.Root, ".vanilla-downloader/install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(True, "failed parallel install joins its workers before releasing the root lock", passed)
            End Using
            Check(lastPhase = "components" AndAlso fixture.Server.Requests.ContainsKey(route), "component failure identifies its phase while cancelling an in-flight resource peer", passed)
            gate.Set()
            fixture.Server.Files("/shared") = correct
            Dim result = Await fixture.InstallAsync("failure", selection, token:=timeout.Token)
            Check(File.Exists(Path.Combine(result.InstancePath, "failure.json")) AndAlso File.ReadAllBytes(fixture.SharedPath()).SequenceEqual(correct), "parallel failure can retry the same name and publish only checked runtime files", passed)
        End Using
    End Function
End Module
