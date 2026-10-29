Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPromptFocusAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim motion = _motionPreview, focusScope = FocusManager.GetIsFocusScope(host), savedAppearance = _appearance.ToJson()
        Dim surfacePaused = _surfaceMotion.PauseForChecks, categoryPaused = _categoryMotion.PauseForChecks
        Dim installRunner = _onlineInstallRunner
        Dim installation As TaskCompletionSource(Of InstallResult) = Nothing
        Try
            _motionPreview = False : host.Width = 850 : host.Height = 600
            FocusManager.SetIsFocusScope(host, True)
            _packProject = New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "prompt-focus", .Name = "弹窗返回检查"}
            _packVerifiedProject = Nothing : _packTargetGame = ""
            _packReleases = Enumerable.Range(1, 60).Select(Function(index) New ModpackRelease With {
                .Platform = ModpackPlatform.Modrinth, .Id = "focus-" & index, .ProjectId = _packProject.Id, .Name = "版本 " & index, .Version = "v" & index,
                .FileName = "focus.mrpack", .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}, .Published = DateTimeOffset.UtcNow.AddMinutes(-index)}).ToList()
            ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(_packProject) : InitializePackVersions() : layout()
            ModpackPage.ScrollToVerticalOffset(420) : layout()
            Dim rows = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel)
            Dim origin = DirectCast(rows.Children(12), PclListItem), offset = ModpackPage.VerticalOffset
            check(PclExitSnapshot.VisibleBounds(origin, ModpackPage).Height >= origin.ActualHeight - 0.5 AndAlso PclExitSnapshot.VisibleBounds(PackProjectWebsite, ModpackPage).IsEmpty,
                "prompt focus fixture has a visible version row and an offscreen default website target")
            FocusManager.SetFocusedElement(host, origin)
            _motionPreview = True
            Dim pending = PromptOnlinePackAsync("输入版本名称", "焦点返回检查", _selectedFolder.Location, "Focus-Return", False, CancellationToken.None)
            _onlinePromptMotion.Controller.Pause()
            FocusManager.SetFocusedElement(host, OnlinePackName)
            CancelOnlinePackName(Nothing, Nothing)
            _onlinePromptMotion.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
            Dim focused = TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)
            IO.File.WriteAllText(IO.Path.Combine(directory, "prompt-return-state.json"), New JObject From {
                {"restoredOrigin", focused Is origin}, {"focusedType", focused?.GetType().FullName}, {"focusedName", focused?.Name},
                {"offsetBefore", offset}, {"offsetAfter", ModpackPage.VerticalOffset}, {"overlayVisible", PackNameOverlay.IsVisible}}.ToString())
            check((Await pending) Is Nothing AndAlso focused Is origin AndAlso Math.Abs(ModpackPage.VerticalOffset - offset) < 0.1,
                "cancelling the animated name prompt restores its originating version row without scrolling to the offscreen header")
            Dim show As Func(Of FrameworkElement, Boolean, Task(Of String)) =
                Function(target, optionalFile)
                    Dim result = PromptOnlinePackAsync("输入版本名称", "焦点所有权检查", If(optionalFile, Nothing, _selectedFolder.Location), "Focus-Return", optionalFile, CancellationToken.None, target)
                    _onlinePromptMotion?.Controller.Pause()
                    FocusManager.SetFocusedElement(host, If(optionalFile, DirectCast(OnlinePackConfirm, FrameworkElement), OnlinePackName))
                    Return result
                End Function
            Dim close As Func(Of Task) = Async Function()
                                             CancelOnlinePackName(Nothing, Nothing)
                                             _onlinePromptMotion?.Controller.SkipToFill()
                                             Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing AndAlso Not _choosingOnline)
                                             layout()
                                         End Function
            Dim settle As Action = Sub()
                                       _surfaceMotion.Finish() : _categoryMotion.Finish() : _titleNavigation.Finish() : layout()
                                   End Sub
            Dim generation = _taskGeneration, session = _packSearchSession, project = _packProject, release = origin.Tag
            For Each animate In {False, True}
                _motionPreview = animate : RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", animate}}) : layout()
                ModpackPage.ScrollToVerticalOffset(420) : layout()
                FocusManager.SetFocusedElement(host, PackProjectWebsite)
                ' Verify entry ownership before layout can pump natural completion.
                origin.Activate() : _onlinePromptMotion?.Controller.Pause() : layout()
                check(_onlinePrompt IsNot Nothing AndAlso _choosingOnline AndAlso Not PageSurface.IsEnabled AndAlso
                    If(animate, _onlinePromptMotion IsNot Nothing, _onlinePromptMotion Is Nothing),
                    "the real version-row action opens naming with its caller and the requested motion mode: " & animate)
                _onlinePromptMotion?.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime)
                FocusManager.SetFocusedElement(host, OnlinePackName)
                SaveControlVisual(host, IO.Path.Combine(directory, "prompt-focus-" & If(animate, "dark", "light") & ".png"))
                Await close()
                check(FocusManager.GetFocusedElement(host) Is origin AndAlso Math.Abs(ModpackPage.VerticalOffset - 420) < 0.1,
                    "mouse or automation activation returns to its explicit version row despite stale header focus: " & animate)
                check(_taskGeneration = generation AndAlso origin.Tag Is release AndAlso _packSearchSession Is session AndAlso Not _busy,
                    "cancelled row naming preserves release identity, accepted search and task state: " & animate)
            Next

            _motionPreview = True
            pending = show(origin, False)
            rows.Children.Remove(origin) : layout()
            offset = ModpackPage.VerticalOffset : Await close()
            check((Await pending) Is Nothing AndAlso FocusManager.GetFocusedElement(host) Is ModpackPage AndAlso Math.Abs(ModpackPage.VerticalOffset - offset) < 0.1,
                "a removed caller falls back to the detail viewport without restoring a detached control")
            rows.Children.Insert(12, origin) : layout()
            pending = show(origin, False)
            ModpackPage.ScrollToVerticalOffset(900) : layout() : Await close()
            check(FocusManager.GetFocusedElement(host) Is ModpackPage AndAlso Math.Abs(ModpackPage.VerticalOffset - 900) < 0.1,
                "an offscreen caller preserves the current viewport instead of resurrecting a historical offset")
            ModpackPage.ScrollToVerticalOffset(420) : layout()

            pending = show(origin, False)
            CancelOnlinePackName(Nothing, Nothing)
            Dim retiredExit = _onlinePromptMotion : retiredExit.Controller.Pause()
            Dim replacement = show(Nothing, False), newEntry = _onlinePromptMotion
            retiredExit.Controller.SkipToFill() : layout()
            check(_onlinePromptMotion Is newEntry AndAlso PackNameOverlay.IsVisible AndAlso FocusManager.GetFocusedElement(host) Is OnlinePackName,
                "an old closing animation cannot focus the page under a replacement dialog")
            Await close()
            check((Await pending) Is Nothing AndAlso (Await replacement) Is Nothing AndAlso FocusManager.GetFocusedElement(host) Is origin,
                "a replacement dialog inherits the original page caller rather than the old dialog's editor")

            _surfaceMotion.PauseForChecks = True : _categoryMotion.PauseForChecks = True
            Dim enterDownload As Action = Sub()
                                              ShowSettings(Nothing, Nothing) : settle()
                                              ShowDownload(Nothing, Nothing) : layout()
                                              Dim entering = _surfaceMotion.Transition
                                              PauseSelectionClock(entering)
                                              entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime) : layout()
                                          End Sub
            enterDownload()
            pending = show(origin, False) : Await close()
            check(_pendingSurfaceFocus IsNot Nothing, "dialog return waits for an existing root-page entry instead of focusing moving content")
            Dim newer = DirectCast(rows.Children(13), PclListItem)
            FocusManager.SetFocusedElement(host, newer) : _surfaceMotion.Finish() : layout()
            check(FocusManager.GetFocusedElement(host) Is newer AndAlso _pendingSurfaceFocus Is Nothing AndAlso Not _restorePackViewportFocus,
                "root completion preserves a newer row focus and consumes the old viewport request")

            enterDownload()
            pending = show(origin, False) : Await close()
            replacement = show(newer, False)
            _surfaceMotion.Finish() : layout()
            check(PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso FocusManager.GetFocusedElement(host) Is OnlinePackName,
                "a queued old focus return cannot escape a newly opened dialog when root entry completes")
            Await close()
            check(FocusManager.GetFocusedElement(host) Is newer AndAlso (Await replacement) Is Nothing,
                "the newer dialog restores its own explicit caller after the old request is retired")

            pending = show(origin, False) : CancelOnlinePackName(Nothing, Nothing)
            retiredExit = _onlinePromptMotion : retiredExit.Controller.Pause()
            ChangePackMode(PackBrowserMode.Search) : settle() : ChangePackMode(PackBrowserMode.Detail) : settle()
            FocusManager.SetFocusedElement(host, newer) : retiredExit.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
            check(FocusManager.GetFocusedElement(host) Is newer AndAlso _packProject Is project AndAlso _packMode = PackBrowserMode.Detail,
                "a same-project navigation round trip retires the old dialog's focus ownership")

            ShowSettings(Nothing, Nothing) : settle()
            SpeedLimitBox.BringIntoView() : layout()
            offset = SettingsPage.VerticalOffset
            pending = show(SpeedLimitBox, True) : Await close()
            check((Await pending) = "no" AndAlso SettingsPage.IsVisible AndAlso FocusManager.GetFocusedElement(host) Is SpeedLimitBox AndAlso Math.Abs(SettingsPage.VerticalOffset - offset) < 0.1,
                "an optional-file dialog restores the active settings editor rather than navigating focus to download")
            ShowDownload(Nothing, Nothing) : settle()

            ModpackPage.ScrollToVerticalOffset(420) : layout()
            pending = show(origin, False)
            Dim escapeArgs As New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(OnlinePackName), Environment.TickCount, Key.Escape) With {.RoutedEvent = Keyboard.KeyDownEvent}
            ShellKeyDown(OnlinePackName, escapeArgs)
            _onlinePromptMotion?.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
            check(escapeArgs.Handled AndAlso (Await pending) Is Nothing AndAlso _packMode = PackBrowserMode.Detail AndAlso FocusManager.GetFocusedElement(host) Is origin,
                "Escape closes naming and restores its row without also navigating back from details")
            pending = show(origin, False) : CancelOnlinePackName(Nothing, Nothing)
            retiredExit = _onlinePromptMotion : retiredExit.Controller.Pause()
            _closing = True
            retiredExit.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : layout()
            check(FocusManager.GetFocusedElement(host) Is OnlinePackName AndAlso Not PackNameOverlay.IsVisible,
                "window closing suppresses late focus restoration from an exiting dialog")
            _closing = False

            installation = New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
            _onlineInstallRunner = Function(selected, root, name, progress, token) installation.Task
            origin.Activate() : layout() : _onlinePromptMotion?.Controller.Pause()
            OnlinePackName.Text = "Focus-Task" : ConfirmOnlinePackName(Nothing, Nothing)
            retiredExit = _onlinePromptMotion : retiredExit.Controller.Pause()
            Await WaitPackUiAsync(Function() _busy AndAlso _downloadView = DownloadView.Progress)
            FocusManager.SetFocusedElement(host, CancelButton)
            retiredExit.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : settle()
            check(_taskGeneration <> generation AndAlso _busy AndAlso FocusManager.GetFocusedElement(host) Is CancelButton AndAlso _downloadView = DownloadView.Progress,
                "accepted naming lets task management take ownership without restoring the old project row")
            CancelInstall(Nothing, Nothing) : installation.TrySetCanceled()
            Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline)
        Finally
            installation?.TrySetCanceled()
            _closing = False
            _motionPreview = False : CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            _surfaceMotion.Finish() : _categoryMotion.Finish()
            _onlineInstallRunner = installRunner : _surfaceMotion.PauseForChecks = surfacePaused : _categoryMotion.PauseForChecks = categoryPaused
            _motionPreview = motion : FocusManager.SetIsFocusScope(host, focusScope) : RestoreAppearance(savedAppearance)
        End Try
    End Function
End Class
