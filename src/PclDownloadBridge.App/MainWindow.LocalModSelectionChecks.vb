Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModSelectionUi(directory As String, passed As List(Of String))
        VerifyLocalModSelectionMotionUi(directory, passed)
        VerifyLocalModMotionFollowupUi(directory, passed)
        VerifyLocalModMetadataMotionUi(directory, passed)
        VerifyLocalModShortcutUi(directory, passed)
        VerifyLocalModToolbarFocusUi(directory, passed)
        VerifyLocalModBatchFocusUi(directory, passed)
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModSelectionAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Mod selection checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckLocalModSelectionAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(Path.Combine(directory, "selection"), layout)
        Dim entry = _localMods.Single(Function(item) item.FileName = "first.jar"), row = DirectCast(_localModRowViews(entry), PclLocalModItem)
        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource1", .Name = "fixture"}
        Dim current As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = project.Platform, .ProjectId = project.Id, .Id = "before01", .Name = "before", .FileName = entry.FileName,
            .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {_localModInstance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = entry.Length, .Sha1 = New String("a"c, 40)}
        Dim release = current.Copy() : release.Id = "after001" : release.Name = "new version" : release.Published = current.Published.AddDays(1) : release.Sha1 = New String("b"c, 40)
        Dim result As New LocalModUpdateCheckResult({New LocalModUpdate(entry, project, current, release)})
        _localModUpdateProvider = Function(instance, files, token) Task.FromResult(result)
        Await CheckLocalModUpdatesAsync() : layout()
        check(LocalModUpdatable.IsVisible AndAlso LocalModUpdatable.Text = "可更新 (1)" AndAlso row.HasUpdate, "installed Mod update counts and row state derive from accepted file identities")
        check(LocalModDirectory.Visibility = Visibility.Collapsed AndAlso LocalModSummary.Visibility = Visibility.Collapsed AndAlso LocalModDownload.Width = 110 AndAlso LocalModSelectAll.Width = 110,
            "PCL Mod toolbar exposes download and select-all without duplicate directory and count rows")
        _localModBarPolicy.EnabledForChecks = True : _localModBarPolicy.PauseForChecks = True
        row.Checked = True : layout()
        check(_localModSelection.SetEquals({entry.FilePath}) AndAlso LocalModSelectionBar.IsVisible AndAlso LocalModSelectionCount.Text.Contains("1"), "checking a visible Mod opens the floating selected-file toolbar")
        check(LocalModUpdateSelected.IsEnabled AndAlso Not LocalModEnableSelected.IsEnabled AndAlso LocalModDisableSelected.IsEnabled AndAlso LocalModDeleteSelected.IsEnabled,
            "bulk actions reflect update availability and physical enabled state")
        check(_localModBarOpacity.Clock IsNot Nothing AndAlso _localModBarOffset.Clock IsNot Nothing AndAlso _localModBarOffset.Clock.Timeline.Duration.TimeSpan.TotalMilliseconds = 360,
            "selection toolbar owns a sixty-ms fade and PCL three-step 360-ms entry")
        Dim old = _localModBarOffset.Clock
        _localModBarOffset.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(120), Media.Animation.TimeSeekOrigin.BeginTime)
        check(Math.Abs(LocalModSelectionOffset.Y + 27) < 0.01, "selection toolbar reaches the original minus-27 overshoot at 120 ms")
        Dim second = DirectCast(LocalModRows.Children.OfType(Of PclLocalModItem)().First(Function(item) Not DirectCast(item.Tag, LocalModEntry).Enabled), PclLocalModItem)
        second.Checked = True : check(_localModBarOffset.Clock IsNot old, "increasing selection replaces the previous entry clock")
        old = _localModBarOffset.Clock
        second.Checked = False : check(_localModBarOffset.Clock Is old, "decreasing a nonempty selection does not restart its bounce")
        ClearLocalModSelection(Nothing, Nothing)
        check(_localModSelection.Count = 0 AndAlso Not LocalModSelectionBar.IsHitTestVisible AndAlso _localModBarOpacity.Clock.Timeline.Duration.TimeSpan.TotalMilliseconds = 90,
            "selection exit immediately gates input and fades over the original ninety ms")
        _localModBarPolicy.EnabledForChecks = False : layout()
        check(LocalModSelectionBar.Visibility = Visibility.Collapsed AndAlso _localModBarOffset.Clock Is Nothing AndAlso _localModBarOpacity.Clock Is Nothing,
            "reduced motion settles and removes selection clocks")
        LocalModSearch.Text = "disabled_fixture" : layout() : SelectAllLocalMods(Nothing, Nothing)
        check(_localModSelection.Count = 1 AndAlso _localModSelection.Contains(DirectCast(second.Tag, LocalModEntry).FilePath), "select-all targets only the current filtered rows")
        LocalModSearch.Text = "enabled_fixture" : layout()
        check(_localModSelection.Count = 0 AndAlso Not LocalModSelectionBar.IsVisible, "search changes prune selected files which are no longer visible")
        FilterLocalMods(LocalModUpdatable, Nothing) : LocalModSearch.Text = "" : layout()
        check(LocalModRows.Children.Count = 1 AndAlso LocalModRows.Children(0) Is row AndAlso LocalModUpdatable.Checked, "the update filter retains the original row and only displays eligible updates")
        row.Checked = True
        PageSurface.IsHitTestVisible = False : row.Checked = False
        check(row.Checked AndAlso _localModSelection.Count = 1 AndAlso Not LocalModSelectionBar.IsHitTestVisible, "an outgoing gated row cannot change selection or activate floating actions")
        PageSurface.IsHitTestVisible = True : layout()
        check(LocalModSelectionBar.IsHitTestVisible, "page handoff restores selection toolbar input")
        RestoreAppearance(New JObject From {{"darkMode", True}}) : layout()
        check(DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color = DirectCast(FindResource("ColorBrush2"), SolidColorBrush).Color AndAlso row.Checked, "dark palette changes preserve selected-row accent and identity")
        host.Width = 810 : host.Height = 540 : layout()
        Dim bounds = LocalModSelectionBar.TransformToAncestor(host).TransformBounds(New Rect(LocalModSelectionBar.RenderSize))
        check(bounds.Left >= 0 AndAlso bounds.Right <= host.Width AndAlso bounds.Bottom <= host.Height AndAlso LocalModContent.Margin.Bottom = 95,
            "the compact selection toolbar fits minimum-size layout and reserves list clearance")
        SaveControlVisual(host, Path.Combine(directory, "local-mod-selection-dark.png"))
        RestoreAppearance(New JObject From {{"darkMode", False}}) : host.Width = 900 : host.Height = 700 : layout()
        Dim declined = ConfirmLocalModBatchAsync("删除确认测试", "受控确认，不删除文件。")
        check(Not LocalModInputAllowed() AndAlso Not LocalModSelectionBar.IsHitTestVisible AndAlso _localModSelection.Count = 1,
            "a bulk confirmation gates the underlying rows and floating toolbar without clearing selection")
        CompleteOnlinePrompt("no")
        check(Not Await declined AndAlso _localModSelection.Count = 1, "declining a bulk operation preserves selected files")
        _motionPreview = True
        Dim confirmed = ConfirmLocalModBatchAsync("更新确认测试", "受控确认，不下载文件。")
        CompleteOnlinePrompt("yes")
        check(Not confirmed.IsCompleted AndAlso Not PageSurface.IsEnabled, "a confirmed bulk action waits for the animated modal to leave before writing")
        check(Await confirmed AndAlso LocalModInputAllowed(), "bulk confirmation releases only after page input is restored")
        _motionPreview = False
        Dim entered As New TaskCompletionSource(Of Boolean), late As New TaskCompletionSource(Of LocalModUpdateCheckResult)
        _localModUpdateCacheKey = Nothing
        _localModUpdateProvider = Function(instance, files, token)
                                      entered.TrySetResult(True) : Return late.Task
                                  End Function
        Dim pending = CheckLocalModUpdatesAsync()
        Await entered.Task : BackFromLocalMods() : layout() : late.TrySetResult(result) : Await pending
        check(Not LocalModsPage.IsVisible AndAlso _localModUpdates.Count = 0 AndAlso _localModSelection.Count = 0 AndAlso _localModBarOpacity.Clock Is Nothing,
            "back navigation retires selection clocks and rejects a late update response")
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim calls = 0
        _localModUpdateProvider = Function(instance, files, token)
                                      calls += 1 : Return Task.FromResult(result)
                                  End Function
        _localModUpdateCacheKey = Nothing : _localModAutoCheckForChecks = True : _motionPreview = True
        Await LoadLocalModsAsync(True)
        Dim wait = System.Diagnostics.Stopwatch.StartNew()
        While _localModUpdates.Count = 0 AndAlso wait.ElapsedMilliseconds < 5000 : Await Task.Delay(10) : End While
        check(_localModUpdates.Count = 1 AndAlso calls = 1, "automatic update lookup starts after an animated local catalogue handoff accepts its rows")
        _localModAutoCheckForChecks = False : _motionPreview = False : _managementMotion.Finish() : layout()
        Await CheckLocalModUpdatesAsync()
        check(calls = 1 AndAlso _localModUpdates.Count = 1, "unchanged successful file identities reuse the six-hour lookup cache")
        DownloadNewLocalMod(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
        check(DownloadPage.IsVisible AndAlso _resourceKind = CommunityResourceKind.Mod AndAlso _packMode = PackBrowserMode.Search AndAlso
            PackSearchGame.Text = _managedInstance.MinecraftVersion AndAlso CStr(DirectCast(ResourceLoader.SelectedItem, FrameworkElement).Tag) = "fabric",
            "download-new-Mod opens native-version and loader constrained search through normal navigation")
        Dim loaderBefore = _managedInstance.LoaderSummary
        _managedInstance.LoaderSummary = "NeoForge 21.1.1" : ConfigureLocalModSearch(_managedInstance)
        check(CStr(DirectCast(ResourceLoader.SelectedItem, FrameworkElement).Tag) = "neoforge", "new-Mod navigation never mistakes the Forge substring in NeoForge for the native loader")
        _managedInstance.LoaderSummary = loaderBefore
    End Function
End Class
