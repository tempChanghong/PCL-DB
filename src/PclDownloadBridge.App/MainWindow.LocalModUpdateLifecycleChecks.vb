Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModUpdateLifecycleUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModUpdateLifecycleAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Mod update lifecycle checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-update-lifecycle.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub
    Private Async Function CheckLocalModUpdateLifecycleAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(Path.Combine(directory, "update-lifecycle"), layout)
        Dim entry = _localMods.Single(Function(item) item.FileName = "first.jar"), row = DirectCast(_localModRowViews(entry), PclLocalModItem)
        Dim protectedFiles = DirectoryFilesForModUpdateChecks(_localModInstance.Target.Root)
        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource1"}
        Dim current As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = project.Platform, .ProjectId = project.Id, .Id = "before01", .FileName = entry.FileName,
            .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {_localModInstance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = entry.Length, .Sha1 = New String("a"c, 40)}
        Dim candidate = current.Copy() : candidate.Id = "after001" : candidate.Name = "lifecycle-update" : candidate.Published = current.Published.AddDays(1) : candidate.Sha1 = New String("b"c, 40)
        Dim update As New LocalModUpdate(entry, project, current, candidate), empty As New LocalModUpdateCheckResult({})
        entry.Warnings.Add("lifecycle-diagnostic")
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
            _localModUpdates(entry.FilePath) = update : ConfigureLocalModSelection(row, entry)
            check(CStr(row.ToolTip).Contains("lifecycle-update") AndAlso CStr(row.ToolTip).Contains("lifecycle-diagnostic") AndAlso CStr(row.ToolTip).Contains(entry.Description),
                "update tooltip retains local diagnostics and description in dark mode " & dark)
            _localModUpdates.Clear() : ConfigureLocalModSelection(row, entry)
            check(Not row.HasUpdate AndAlso Not CStr(row.ToolTip).Contains("lifecycle-update") AndAlso CStr(row.ToolTip).Contains("lifecycle-diagnostic"),
                "a withdrawn update restores the local tooltip in dark mode " & dark)
            row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True
            FocusLocalModElementForChecks(LocalModSearch) : FocusLocalModButtonForChecks(entry) : layout()
            Dim opacity = row.ActionOpacityTransition, inset = row.ActionInsetTransition, hover = row.HoverTransition
            ConfigureLocalModSelection(row, entry)
            check(opacity IsNot Nothing AndAlso row.ActionOpacityTransition Is opacity AndAlso row.ActionInsetTransition Is inset AndAlso row.HoverTransition Is hover,
                "unchanged update metadata preserves active row clocks in dark mode " & dark)
            row.MotionPolicy.EnabledForChecks = False : FocusLocalModElementForChecks(LocalModSearch)
        Next
        Dim calls = 0
        _localModUpdateProvider = Function(instance, files, token)
                                      calls += 1 : Return Task.FromResult(empty)
                                  End Function
        _localModUpdateCacheKey = Nothing : Await CheckLocalModUpdatesAsync()
        Dim before = calls
        _localModUpdateCacheTime = DateTime.UtcNow.AddDays(1) : Await CheckLocalModUpdatesAsync()
        check(calls = before + 1, "a future-dated update cache is rechecked after clock rollback")
        before = calls : _localModUpdateCacheTime = DateTime.UtcNow.AddHours(-7) : Await CheckLocalModUpdatesAsync()
        check(calls = before + 1, "an expired update cache queries again")
        before = calls : _localModUpdateCacheTime = DateTime.UtcNow.AddMinutes(-1) : Await CheckLocalModUpdatesAsync()
        check(calls = before, "a recent successful update cache remains reusable")

        Dim queriedAt = DateTime.UtcNow.AddHours(-5)
        _localModUpdateCacheTime = queriedAt : Await CheckLocalModUpdatesAsync()
        check(calls = before AndAlso _localModUpdateCacheTime = queriedAt,
            "revisiting Mod management does not extend the original update query lifetime")
        _localModUpdateProvider = Function(instance, files, token)
                                      calls += 1 : Return Task.FromResult(New LocalModUpdateCheckResult({update}))
                                  End Function
        _localModUpdateCacheTime = DateTime.UtcNow.AddHours(-7) : Await CheckLocalModUpdatesAsync() : layout()
        check(calls = before + 1 AndAlso row.HasUpdate AndAlso LocalModUpdatable.IsVisible AndAlso LocalModUpdateStatus.Text = "发现 1 个可更新的 Mod。",
            "an expired cached no-update result discovers and visibly announces a newly published update")
        queriedAt = _localModUpdateCacheTime : before = calls
        _localModUpdates.Clear() : LocalModUpdateStatus.Text = ""
        Await CheckLocalModUpdatesAsync() : layout()
        check(calls = before AndAlso _localModUpdateCacheTime = queriedAt AndAlso row.HasUpdate AndAlso LocalModUpdateStatus.Text = "发现 1 个可更新的 Mod。",
            "cached updates restore row and completion feedback without renewing their query time")
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, files, token) Task.FromResult(New LocalModUpdateCheckResult({New LocalModUpdate(entry, project, current, Nothing)}))
        Await CheckLocalModUpdatesAsync() : layout()
        check(Not row.HasUpdate AndAlso LocalModUpdateStatus.Text = "已检查 1 个 Mod，暂无可用更新。",
            "an identified current Mod reports a completed check without advertising an update")
        _localModUpdateCacheKey = Nothing : _localModUpdateProvider = Function(instance, files, token) Task.FromResult(empty)
        Await CheckLocalModUpdatesAsync()
        check(LocalModUpdateStatus.Text = "未匹配到 Mod 更新信息。" AndAlso CStr(LocalModUpdateStatus.ToolTip).Contains("已识别 0 /"),
            "unidentified files remain distinct from an identified no-update result")
        Dim partialCalls = 0
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, files, token)
                                      partialCalls += 1 : Return Task.FromResult(New LocalModUpdateCheckResult({update}, {"fixture-platform-unavailable"}))
                                  End Function
        Await CheckLocalModUpdatesAsync() : layout()
        check(row.HasUpdate AndAlso LocalModUpdatable.IsVisible AndAlso LocalModUpdateStatus.Text.Contains("发现 1 个") AndAlso
            LocalModUpdateStatus.Text.Contains("部分来源暂不可用") AndAlso CStr(LocalModUpdateStatus.ToolTip).Contains("fixture-platform-unavailable"),
            "partial platform failures retain discovered updates and visible failure feedback")
        Await CheckLocalModUpdatesAsync()
        check(partialCalls = 2 AndAlso _localModUpdateCacheKey Is Nothing, "partial update results are queried again instead of becoming successful cache entries")
        Dim acceptedFeedback = LocalModUpdateStatus.Text
        LocalModSearch.Text = "no-matching-local-mod" : layout()
        check(Not LocalModUpdatable.IsVisible AndAlso LocalModUpdateStatus.Text = acceptedFeedback AndAlso LocalModUpdateStatus.Text.Contains("发现 1 个"),
            "search filtering cannot hide the catalogue-wide update completion feedback")
        LocalModSearch.Text = "" : layout()

        Dim gate As New TaskCompletionSource(Of LocalModUpdateCheckResult), child As Task = Nothing, childCalls = 0
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, files, token)
                                      childCalls += 1 : Return gate.Task
                                  End Function
        Dim pending = CheckLocalModUpdatesAsync(), request = _localModUpdatesRequest, generation = _localModGeneration
        row.Checked = True : layout()
        Dim detached = False, readRetired = False, selectionRetired = False, generationRetired = False
        Using registration = request.Token.Register(Sub()
                                                         detached = _localModUpdatesRequest IsNot request
                                                         readRetired = Not _localModReadSucceeded
                                                         generationRetired = _localModGeneration <> generation
                                                         selectionRetired = _localModSelection.Count = 0 AndAlso _localModBarOpacity.Clock Is Nothing
                                                         child = CheckLocalModUpdatesAsync()
                                                     End Sub)
            CancelLocalModRequest()
        End Using
        check(detached, "update request ownership retires before cancellation callbacks run")
        check(readRetired AndAlso generationRetired, "the parent read and generation retire before update cancellation notification")
        check(selectionRetired, "selection and its clocks retire before update cancellation notification")
        check(childCalls = 1, "a cancellation callback cannot restart updates for a retired local catalogue")
        gate.TrySetResult(empty) : Await pending
        If child IsNot Nothing Then Await child
        check(_localModUpdatesRequest Is Nothing AndAlso LocalModUpdateStatus.Text = "" AndAlso _localModUpdates.Count = 0,
            "cancelled and reentrant update reads leave no stale owner or feedback")

        Await LoadLocalModsAsync(True, refreshInstance:=True) : layout()
        Dim oldGate As New TaskCompletionSource(Of LocalModUpdateCheckResult), nextGate As New TaskCompletionSource(Of LocalModUpdateCheckResult)
        Dim nested As Task = Nothing, newest As CancellationTokenSource = Nothing, notifications = 0, newCalls = 0, replacedBeforeNotify = False
        _localModUpdateCacheKey = Nothing : _localModUpdateProvider = Function(instance, files, token) oldGate.Task
        Dim oldRead = CheckLocalModUpdatesAsync(), oldRequest = _localModUpdatesRequest
        _localModUpdateProvider = Function(instance, files, token)
                                      newCalls += 1 : Return nextGate.Task
                                  End Function
        Using registration = oldRequest.Token.Register(Sub()
                                                            notifications += 1
                                                            replacedBeforeNotify = _localModUpdatesRequest IsNot oldRequest
                                                            If notifications <> 1 Then Return
                                                            nested = CheckLocalModUpdatesAsync() : newest = _localModUpdatesRequest
                                                        End Sub)
            Dim replacement = CheckLocalModUpdatesAsync()
            check(replacedBeforeNotify, "a replacement owns the query slot before cancelling the previous request")
            check(newest IsNot Nothing AndAlso _localModUpdatesRequest Is newest AndAlso newCalls = 1,
                "a reentrant newer query is not overwritten or duplicated by the outer replacement")
            nextGate.TrySetResult(empty) : Await replacement
            If nested IsNot Nothing Then Await nested
        End Using
        acceptedFeedback = LocalModUpdateStatus.Text
        oldGate.TrySetResult(empty) : Await oldRead
        check(_localModUpdatesRequest Is Nothing AndAlso LocalModUpdateStatus.Text = acceptedFeedback AndAlso acceptedFeedback <> "", "old query completion cannot replace the latest successful feedback")
        check(protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            "update UI lifecycle checks preserve every native configuration and Mod byte")
    End Function
    Private Shared Function DirectoryFilesForModUpdateChecks(root As String) As Dictionary(Of String, Byte())
        Return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
    End Function
End Class
