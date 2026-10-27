Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModUpdateFilterUi(directory As String, passed As List(Of String))
        Dim failures As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failures.Add(label)
                                                 End Sub
        For Each dark In {False, True}
            For Each moving In {False, True}
                Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
                Dim owner As New MainWindow(True), original = owner.Content
                Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
                owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
                FocusManager.SetIsFocusScope(host, True)
                Try
                    Using surface = PreviewSurface.Attach(host, 900, 700)
                        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                        Dim work = owner.CheckLocalModUpdateFilterAsync(Path.Combine(directory, "update-filter-" & dark & "-" & moving), dark, moving, layout, check)
                        If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Mod update filter checks did not settle.")
                        work.GetAwaiter().GetResult()
                    End Using
                Finally
                    owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
                    owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
                    owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
                    host.Child = Nothing : owner.Content = original
                    RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
                End Try
            Next
        Next
        If failures.Count > 0 Then Throw New InvalidOperationException("Mod update filter failures: " & failures.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failures))
    End Sub

    Private Async Function CheckLocalModUpdateFilterAsync(directory As String, dark As Boolean, moving As Boolean, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(directory, layout)
        RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
        Dim protectedFiles = DirectoryFilesForModUpdateChecks(_localModInstance.Target.Root)
        Dim first = _localMods.Single(Function(item) item.FileName = "first.jar"), second = _localMods.Single(Function(item) item.FileName = "second.jar.disabled")
        Dim makeUpdate As Func(Of LocalModEntry, String, LocalModUpdate) =
            Function(entry, name)
                Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "filter-" & entry.EnabledName}
                Dim current As New ModpackRelease With {.Kind = project.Kind, .Platform = project.Platform, .ProjectId = project.Id, .Id = "before01", .Name = "before", .FileName = entry.FileName,
                    .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {_localModInstance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = entry.Length, .Sha1 = New String("a"c, 40)}
                Dim release = current.Copy() : release.Id = "after001" : release.Name = name : release.Published = current.Published.AddDays(1)
                Return New LocalModUpdate(entry, project, current, release)
            End Function
        Dim update = makeUpdate(first, "old-metadata"), nextUpdate = makeUpdate(first, "new-metadata"), other = makeUpdate(second, "second-update")
        _localModUpdates(first.FilePath) = update : _localModFilter = "updates" : RenderLocalMods() : layout()
        Dim row = DirectCast(_localModRowViews(first), PclLocalModItem), marker = row.Children.OfType(Of Border)().Single(Function(item) item.Width = 5)
        row.MotionPolicy.EnabledForChecks = moving : row.MotionPolicy.PauseForChecks = moving
        FocusLocalModElementForChecks(row) : row.Checked = True : layout()
        Dim entering = LocalModSelectionClock(row)
        If entering IsNot Nothing Then PauseSelectionClock(entering) : entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
        Dim height = marker.Height, calls = 0
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, entries, token)
                                      calls += 1 : Return Task.FromResult(New LocalModUpdateCheckResult({nextUpdate}))
                                  End Function
        Await CheckLocalModUpdatesAsync() : layout()
        Dim label As Func(Of String, String) = Function(value) value & "; dark=" & dark & "; motion=" & moving
        check(LocalModRows.Children.Count = 1 AndAlso LocalModRows.Children(0) Is row AndAlso row.IsLoaded,
            label("unchanged update-filter membership retains the attached Mod row"))
        check(Keyboard.FocusedElement Is row, label("accepted metadata retains keyboard focus on the same update-filter row"))
        check(LocalModSelectionClock(row) Is entering AndAlso Math.Abs(marker.Height - height) < 0.001,
            label("accepted metadata preserves the sampled update-filter selection clock and marker"))
        check(row.HasUpdate AndAlso CStr(row.ToolTip).Contains("new-metadata") AndAlso Not CStr(row.ToolTip).Contains("old-metadata") AndAlso
            _localModSelection.SetEquals({first.FilePath}) AndAlso LocalModUpdatable.Text = "可更新 (1)",
            label("retained update-filter rows still receive new release metadata and retain selection"))
        If entering IsNot Nothing Then entering.Controller.Resume() : entering.Controller.SkipToFill() : PumpMotionUntil(Function() LocalModSelectionClock(row) Is Nothing)
        FocusLocalModElementForChecks(row)
        Await CheckLocalModUpdatesAsync() : layout()
        check(calls = 1 AndAlso Keyboard.FocusedElement Is row AndAlso LocalModSelectionClock(row) Is Nothing,
            label("a successful cached update-filter refresh neither refetches nor detaches the focused row"))
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, entries, token) Task.FromResult(New LocalModUpdateCheckResult({nextUpdate, other}))
        Await CheckLocalModUpdatesAsync() : layout()
        check(LocalModRows.Children.Count = 2 AndAlso LocalModRows.Children.OfType(Of PclLocalModItem)().Select(Function(item) DirectCast(item.Tag, LocalModEntry)).SequenceEqual(
            _localMods.Where(Function(item) item Is first OrElse item Is second)) AndAlso LocalModUpdatable.Text = "可更新 (2)",
            label("new update-filter membership rebuilds the correct ordered visible rows"))
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, entries, token) Task.FromResult(New LocalModUpdateCheckResult({other}))
        Await CheckLocalModUpdatesAsync() : layout()
        check(LocalModRows.Children.Count = 1 AndAlso DirectCast(LocalModRows.Children(0), PclLocalModItem).Tag Is second AndAlso _localModSelection.Count = 0,
            label("a withdrawn update removes its old row and prunes its file selection"))
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, entries, token) Task.FromResult(New LocalModUpdateCheckResult({}))
        Await CheckLocalModUpdatesAsync() : layout()
        check(LocalModRows.Children.Count = 0 AndAlso LocalModEmpty.IsVisible AndAlso LocalModUpdatable.IsVisible AndAlso LocalModUpdatable.Checked AndAlso LocalModUpdatable.Text = "可更新 (0)",
            label("empty update-filter results retain the selected zero-count tab and empty state"))
        Dim gate As New TaskCompletionSource(Of LocalModUpdateCheckResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModUpdateCacheKey = Nothing : _localModUpdateProvider = Function(instance, entries, token) gate.Task
        Dim pending = CheckLocalModUpdatesAsync()
        FocusLocalModElementForChecks(LocalModSearch) : LocalModSearch.Text = "no-current-match" : layout()
        gate.TrySetResult(New LocalModUpdateCheckResult({nextUpdate})) : Await pending : layout()
        check(Keyboard.FocusedElement Is LocalModSearch AndAlso LocalModRows.Children.Count = 0 AndAlso LocalModUpdatable.IsVisible AndAlso
            LocalModUpdatable.Checked AndAlso LocalModUpdatable.Text = "可更新 (0)",
            label("metadata arriving after a newer query respects the editor and its empty selected update filter"))
        gate = New TaskCompletionSource(Of LocalModUpdateCheckResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModUpdateCacheKey = Nothing : _localModUpdateProvider = Function(instance, entries, token) gate.Task
        pending = CheckLocalModUpdatesAsync()
        ShowSettings(Nothing, Nothing) : layout() : FocusLocalModElementForChecks(SpeedLimitBox)
        gate.TrySetResult(New LocalModUpdateCheckResult({nextUpdate, other})) : Await pending : layout()
        check(SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is SpeedLimitBox AndAlso _localModUpdates.Count = 0 AndAlso _localModSelection.Count = 0,
            label("retired update-filter results cannot repopulate the catalogue or steal settings focus"))
        check(protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            label("update-filter metadata and animation checks preserve all original instance and launcher bytes"))
    End Function
End Class
