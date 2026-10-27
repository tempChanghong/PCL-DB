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
    Public Sub VerifyLocalModPromptUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        FocusManager.SetIsFocusScope(host, True)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModPromptAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Mod confirmation motion checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-prompt-motion.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckLocalModPromptAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(Path.Combine(directory, "prompt"), layout)
        Dim entry = _localMods.Single(Function(item) item.FileName = "first.jar"), row = DirectCast(_localModRowViews(entry), PclLocalModItem)
        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "prompt-mod"}
        Dim current As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = project.Platform, .ProjectId = project.Id, .Id = "old",
            .FileName = entry.FileName, .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {_localModInstance.MinecraftVersion}, .Loaders = {"fabric"},
            .Size = entry.Length, .Sha1 = New String("a"c, 40)}
        Dim candidate = current.Copy() : candidate.Id = "new" : candidate.Published = current.Published.AddDays(1) : candidate.Sha1 = New String("b"c, 40)
        _localModUpdates(entry.FilePath) = New LocalModUpdate(entry, project, current, candidate)
        row.Checked = True : layout()
        Dim protectedFiles = DirectoryFilesForModUpdateChecks(_localModInstance.Target.Root)
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout() : _motionPreview = True
            Dim pending = ConfirmLocalModBatchAsync("更新所选 Mod", "受控动画检查，不下载或修改 Mod。")
            Dim entrance = _onlinePromptMotion : PauseSelectionClock(entrance)
            check(entrance IsNot Nothing AndAlso PackNameDialog.Opacity = 0 AndAlso _onlinePromptOffset.Y = 40 AndAlso _onlinePromptRotation.Angle = -4,
                "Mod confirmation begins with PCL opacity, offset and pivot in dark mode " & dark)
            entrance.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(90), TimeSeekOrigin.BeginTime) : layout()
            SaveControlVisual(host, Path.Combine(directory, "mod-prompt-enter-" & dark & ".png"))
            check(Math.Abs(PackNameDialog.Opacity - 0.25) < 0.01 AndAlso Math.Abs(_onlinePromptRotation.Angle + 3.24) < 0.01 AndAlso
                Math.Abs(_onlinePromptOffset.Y - 40 * (1 - PclMotionCurves.Back(0.1, 2))) < 0.01,
                "Mod confirmation samples PCL delayed fade, Weak Back and rotation at ninety ms in dark mode " & dark)
            entrance.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
            File.WriteAllText(Path.Combine(directory, "mod-prompt-entry-" & dark & ".json"), New JObject From {
                {"opacity", PackNameDialog.Opacity}, {"y", _onlinePromptOffset.Y}, {"angle", _onlinePromptRotation.Angle}, {"shade", _onlinePromptShade.Color.A}}.ToString())
            check(PackNameDialog.Opacity = 1 AndAlso Math.Abs(_onlinePromptOffset.Y) < 0.01 AndAlso Math.Abs(_onlinePromptRotation.Angle) < 0.01,
                "Mod confirmation entry settles without residual transform clocks in dark mode " & dark)
            CancelOnlinePackName(Nothing, Nothing)
            Dim closing = _onlinePromptMotion : PauseSelectionClock(closing)
            CancelOnlinePackName(Nothing, Nothing)
            check(_onlinePromptMotion Is closing AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso Not TopTabs.IsEnabled,
                "repeated Mod cancellation preserves the closing animation and input gate in dark mode " & dark)
            If _onlinePromptMotion Is closing Then
                closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(20), TimeSeekOrigin.BeginTime) : layout()
                check(_onlinePromptShade.Color.A = 90 AndAlso Math.Abs(PackNameDialog.Opacity - 1) < 0.01,
                    "Mod dialog shade retains PCL thirty-ms close delay in dark mode " & dark)
                closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
                SaveControlVisual(host, Path.Combine(directory, "mod-prompt-exit-" & dark & ".png"))
                Dim expectedShade = 90 * Math.Pow(1 - 50 / 200.0, 2)
                check(Math.Abs(CDbl(_onlinePromptShade.Color.A) - expectedShade) <= 1 AndAlso Math.Abs(PackNameDialog.Opacity - 0.25) < 0.01 AndAlso
                    Math.Abs(_onlinePromptOffset.Y - 20 * PclMotionCurves.Fluent(80 / 150.0, 3)) < 0.01,
                    "Mod dialog samples PCL shade easing and moving exit at eighty ms in dark mode " & dark)
            Else
                check(False, "Mod dialog shade retains PCL thirty-ms close delay in dark mode " & dark)
                check(False, "Mod dialog samples PCL shade easing and moving exit at eighty ms in dark mode " & dark)
            End If
            closing.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
            check(Not Await pending AndAlso Not PackNameOverlay.IsVisible AndAlso LocalModInputAllowed() AndAlso _localModSelection.Contains(entry.FilePath),
                "cancelled Mod confirmation restores the unchanged selection after exit in dark mode " & dark)
            check(LocalModSelectionBar.IsHitTestVisible AndAlso LocalModUpdateSelected.IsEnabled AndAlso LocalModDeleteSelected.IsEnabled,
                "closing a Mod confirmation restores floating actions before returning focus in dark mode " & dark)

            pending = ConfirmLocalModBatchAsync("更新所选 Mod", "遮罩交接检查。")
            _onlinePromptMotion.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
            _onlinePromptShade.Color = System.Windows.Media.Color.FromArgb(90, 0, 0, 0)
            CompleteOnlinePrompt("yes") : closing = _onlinePromptMotion : PauseSelectionClock(closing)
            closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
            Dim shadeBefore = _onlinePromptShade.Color
            Dim replacement = PromptOnlinePackAsync("新的确认", "旧确认不能启动文件写入。", Nothing, Nothing, True, CancellationToken.None)
            Dim replacementClock = _onlinePromptMotion : PauseSelectionClock(replacementClock)
            check(_onlinePromptShade.Color.Equals(shadeBefore), "a replacement Mod dialog inherits its displayed shade without flashing in dark mode " & dark)
            Await Task.Delay(30)
            check(pending.IsCompleted AndAlso Not Await pending, "replacing a closing Mod confirmation rejects its old accepted result in dark mode " & dark)
            closing.Controller.SkipToFill() : layout()
            check(_onlinePromptMotion Is replacementClock AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled,
                "a retired Mod exit cannot release a replacement modal in dark mode " & dark)
            CompleteOnlinePrompt("no") : _onlinePromptMotion.Controller.SkipToFill()
            Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : Await replacement : layout()
        Next

        LocalModUpdateSelected.IsEnabled = True : FocusPromptTarget(LocalModUpdateSelected, LocalModsPage)
        Dim focusBefore = TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)
        Dim confirmed = ConfirmLocalModBatchAsync("更新所选 Mod", "暂停退场超过旧轮询期限。")
        _onlinePromptMotion.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
        CompleteOnlinePrompt("yes") : Dim delayed = _onlinePromptMotion : PauseSelectionClock(delayed)
        Await Task.Delay(1650)
        check(Not confirmed.IsCompleted AndAlso Not LocalModInputAllowed(), "an accepted Mod operation waits for its owned exit rather than a polling timeout")
        delayed.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
        check(Await confirmed, "an accepted Mod confirmation still succeeds after a delayed exit")
        File.WriteAllText(Path.Combine(directory, "mod-prompt-focus.json"), New JObject From {
            {"before", focusBefore?.Name}, {"after", TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)?.Name},
            {"enabled", LocalModUpdateSelected.IsEnabled}, {"visible", LocalModUpdateSelected.IsVisible}, {"hitTest", LocalModUpdateSelected.IsHitTestVisible}, {"focusable", LocalModUpdateSelected.Focusable},
            {"width", LocalModUpdateSelected.ActualWidth}, {"height", LocalModUpdateSelected.ActualHeight}, {"barOpacity", LocalModSelectionBar.Opacity}}.ToString())
        check(FocusManager.GetFocusedElement(host) Is LocalModUpdateSelected, "Mod confirmation restores its actual action instead of jumping to select-all")

        confirmed = ConfirmLocalModBatchAsync("更新所选 Mod", "取消实例归属检查。")
        CompleteOnlinePrompt("yes") : delayed = _onlinePromptMotion : PauseSelectionClock(delayed)
        CancelLocalModRequest()
        delayed.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
        check(Not Await confirmed, "an old Mod confirmation cannot approve writes after its native context retires")

        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = False
            Dim pending = PromptOnlinePackAsync("减少动画", "不创建弹窗时钟。", Nothing, Nothing, True, CancellationToken.None)
            check(_onlinePromptMotion Is Nothing AndAlso PackNameDialog.Opacity = 1 AndAlso _onlinePromptShade.Color.A = 90 AndAlso
                _onlinePromptOffset.Y = 0 AndAlso _onlinePromptRotation.Angle = 0,
                "reduced Mod dialog motion applies final shade and transform immediately in dark mode " & dark)
            CompleteOnlinePrompt("no") : Await pending
            check(Not PackNameOverlay.IsVisible AndAlso PageSurface.IsEnabled, "reduced Mod dialog exit leaves no input gate in dark mode " & dark)
        Next
        Dim finalFiles = DirectoryFilesForModUpdateChecks(_managedInstance.Target.Root)
        check(finalFiles.Count = protectedFiles.Count AndAlso protectedFiles.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
            "Mod prompt motion checks preserve all instance, Mod and configuration bytes")
        SaveControlVisual(host, Path.Combine(directory, "mod-prompt-settled.png"))
    End Function
End Class
