Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementPromptUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 600}, layout = ControlLayout(host), observations As New JObject, failuresState As New JArray
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     If Not ok Then
                                                         Dim focused = TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)
                                                         Dim viewport = owner.ManagementContent()
                                                         failuresState.Add(New JObject From {{"label", label}, {"focusedName", focused?.Name}, {"focusedType", focused?.GetType().Name},
                                                             {"viewport", viewport.Name}, {"viewportVisible", viewport.IsVisible}, {"viewportEnabled", viewport.IsEnabled}, {"viewportHitTest", viewport.IsHitTestVisible},
                                                             {"rootMotion", owner._surfaceMotion.Transition IsNot Nothing},
                                                             {"managementMotion", owner._managementMotion.Transition IsNot Nothing}, {"prompt", owner._onlinePrompt IsNot Nothing}, {"overlay", owner.PackNameOverlay.IsVisible}})
                                                     End If
                                                 End Sub
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementPromptAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Management prompt checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-prompt.json"), observations.ToString())
            File.WriteAllText(Path.Combine(directory, "management-prompt-failures.json"), failuresState.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner._cancellation?.Cancel() : owner.CompleteOnlinePrompt(Nothing)
            owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementPromptAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim archivePath = Path.Combine(directory, "optional-focus-" & Guid.NewGuid().ToString("N") & ".mrpack")
        Dim manifest As New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"versionId", "fixture"}, {"name", "Optional focus fixture"},
            {"dependencies", New JObject From {{"minecraft", "1.20.1"}}}, {"files", New JArray(New JObject From {
                {"path", "mods/optional.jar"}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}},
                {"fileSize", 1}, {"env", New JObject From {{"client", "optional"}, {"server", "required"}}},
                {"downloads", New JArray("https://cdn.modrinth.com/fixture/optional.jar")}})}}
        Using archive As ZipArchive = ZipFile.Open(archivePath, ZipArchiveMode.Create)
            Using writer As New StreamWriter(archive.CreateEntry("modrinth.index.json").Open()) : writer.Write(manifest.ToString()) : End Using
        End Using
        Dim package = ModrinthPackage.Read(archivePath)
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        Dim closePrompt As Action(Of Boolean) = Sub(yes)
                                                   If yes Then ConfirmOnlinePackName(Nothing, Nothing) Else CancelOnlinePackName(Nothing, Nothing)
                                                   _onlinePromptMotion?.Controller.SkipToFill()
                                               End Sub
        Dim number = 0
        For view = 0 To 2
            For Each animated In {False, True}
                For Each dark In {False, True}
                    number += 1 : _motionPreview = False
                    Dim currentView = view
                    Dim folders = PrepareManagementFixture(directory)
                    FolderList.SelectedItem = folders(view)
                    Dim target = CurrentInstallTarget(), root = target.Root
                    Dim originalFiles = IO.Directory.GetFiles(Path.GetDirectoryName(root), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                    Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Modpack, .Id = "optional", .Name = "Optional", .GameVersions = {"1.20.1"}}
                    Dim gate As TaskCompletionSource(Of InstallResult) = Nothing, tokenOwned As CancellationToken
                    Dim registration As CancellationTokenRegistration
                    _onlineInstallRunner = Function(item, gameRoot, name, progress, token)
                                               tokenOwned = token
                                               gate = New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                               registration = token.Register(Sub() gate.TrySetCanceled())
                                               Return gate.Task
                                           End Function
                    _localModProvider = Function(instance, token) Task.FromResult(New List(Of LocalModEntry))
                    Dim enterManagement As Func(Of Task) = Async Function()
                                                              ShowManagement(Nothing, Nothing) : settle()
                                                              Await WaitPackUiAsync(Function() Not _managementScanning)
                                                              If currentView > 0 AndAlso ManagementListPage.Visibility = Visibility.Visible Then SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例"))
                                                              settle()
                                                              If currentView = 2 AndAlso LocalModsPage.Visibility <> Visibility.Visible Then ShowLocalMods(Nothing, Nothing)
                                                              Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                                                          End Function
                    Dim running = RunOnlinePackInstallationAsync(release, root, "optional-task-" & number, 0, target:=target)
                    Await enterManagement()
                    Dim viewport = DirectCast(ManagementContent(), PclScrollViewer)
                    Dim origin As FrameworkElement = If(view = 0, DirectCast(ManagementRefresh, FrameworkElement), If(view = 1, DirectCast(ManagedMods, FrameworkElement), DirectCast(LocalModRefresh, FrameworkElement)))
                    _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    origin.BringIntoView() : layout()
                    FocusManager.SetFocusedElement(host, origin)
                    Dim offset = viewport.VerticalOffset, generation = _taskGeneration
                    Dim label = "/view=" & view & "/launcher=" & target.Kind.ToString() & "/motion=" & animated & "/dark=" & dark
                    Dim choice = ChooseOnlineOptionalAsync(package, tokenOwned)
                    FocusManager.SetFocusedElement(host, OnlinePackConfirm)
                    check(_busy AndAlso _onlinePrompt IsNot Nothing AndAlso Not PageSurface.IsEnabled AndAlso Not NavigateTitleBack(),
                        "optional confirmation owns modal input while the actual task is running" & label)
                    closePrompt(True)
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : settle()
                    Keyboard.ClearFocus() : FocusManagementViewport()
                    check((Await choice).SequenceEqual({"mods/optional.jar"}) AndAlso FocusManager.GetFocusedElement(host) Is origin AndAlso Math.Abs(viewport.VerticalOffset - offset) < 0.1 AndAlso _taskGeneration = generation,
                        "optional acceptance retains its management caller even without system keyboard focus and without scrolling or replacing its task" & label)

                    FocusManager.SetFocusedElement(host, origin)
                    choice = ChooseOnlineOptionalAsync(package, tokenOwned)
                    FocusManager.SetFocusedElement(host, OnlinePackConfirm) : origin.Visibility = Visibility.Collapsed : layout()
                    offset = viewport.VerticalOffset
                    closePrompt(False)
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : settle()
                    check(Not (Await choice).Any() AndAlso FocusManager.GetFocusedElement(host) Is viewport AndAlso Math.Abs(viewport.VerticalOffset - offset) < 0.1,
                        "an unavailable management caller falls back to its current viewport rather than a hidden download page" & label)
                    origin.Visibility = Visibility.Visible : layout()

                    ' A scan can already be in flight when the installer asks
                    ' about optional content. Its refresh must not take focus
                    ' from that modal, or consume the later focus return early.
                    Dim provider = _managementProvider
                    Dim scanGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                    _managementProvider = Function(binding, token) scanGate.Task
                    Dim scanned = _managementInstances.ToList()
                    _managementMotion.PauseForChecks = True
                    FocusManager.SetFocusedElement(host, origin)
                    Dim scan = ScanManagementAsync(True)
                    choice = ChooseOnlineOptionalAsync(package, tokenOwned) : FocusManager.SetFocusedElement(host, OnlinePackConfirm)
                    scanGate.SetResult(If(view = 0, scanned, scanned.Where(Function(item) item.Name <> "Fabric 实例").ToList()))
                    Await scan : layout()
                    check(FocusManager.GetFocusedElement(host) Is OnlinePackConfirm AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled,
                        "a management refresh completing under an optional dialog leaves focus owned by that dialog" & label)
                    closePrompt(False)
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                    _managementMotion.Finish() : layout()
                    Dim expectedFocus As FrameworkElement = If(view = 0, origin, ManagementListPage)
                    check(Not (Await choice).Any() AndAlso FocusManager.GetFocusedElement(host) Is expectedFocus AndAlso _busy AndAlso Not tokenOwned.IsCancellationRequested,
                        "dialog focus waits for management entry and follows a removed instance to the current list" & label)
                    _managementProvider = provider : _managementMotion.PauseForChecks = False
                    _managementDirty = True
                    Await enterManagement()

                    origin.BringIntoView() : layout()
                    FocusManager.SetFocusedElement(host, origin)
                    choice = ChooseOnlineOptionalAsync(package, tokenOwned)
                    FocusManager.SetFocusedElement(host, OnlinePackConfirm)
                    Dim escape As New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(OnlinePackConfirm), Environment.TickCount, Key.Escape) With {.RoutedEvent = Keyboard.KeyDownEvent}
                    ShellKeyDown(OnlinePackConfirm, escape)
                    Try : Await choice : Catch ex As OperationCanceledException : End Try
                    Await running
                    ' PCL completes the result before removing MyMsgText.
                    ' Task completion must not truncate that owned exit.
                    _onlinePromptMotion?.Controller.SkipToFill()
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : settle()
                    check(escape.Handled AndAlso tokenOwned.IsCancellationRequested AndAlso Not _busy AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso _onlinePrompt Is Nothing AndAlso
                        PackNameOverlay.Visibility = Visibility.Collapsed AndAlso PageSurface.IsEnabled AndAlso FocusManager.GetFocusedElement(host) Is origin,
                        "Escape cancellation settles the task and returns focus to the still-active management page" & label)
                    registration.Dispose()

                    Dim cancelledToken = tokenOwned
                    _motionPreview = False
                    running = RunOnlinePackInstallationAsync(release, root, "optional-task-" & number, 0, target:=target)
                    Await enterManagement()
                    _motionPreview = animated
                    origin.BringIntoView() : layout() : FocusManager.SetFocusedElement(host, origin)
                    choice = ChooseOnlineOptionalAsync(package, tokenOwned) : FocusManager.SetFocusedElement(host, OnlinePackConfirm)
                    closePrompt(True)
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : settle()
                    check((Await choice).Count() = 1 AndAlso tokenOwned <> cancelledToken AndAlso Not tokenOwned.IsCancellationRequested AndAlso _busy AndAlso
                        FocusManager.GetFocusedElement(host) Is origin AndAlso SameManagementTarget(target, CurrentInstallTarget()),
                        "retry uses a fresh token and restores its own management caller with the frozen launcher binding" & label)
                    gate.SetResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions/controlled-result"), .FileCount = 0})
                    Await running : settle() : Await WaitPackUiAsync(Function() Not _managementScanning)
                    check(Not _busy AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso TopTabs.IsEnabled AndAlso
                        originalFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso Not IO.Directory.Exists(Path.Combine(root, "versions/controlled-result")),
                        "controlled completion preserves management navigation and every existing fixture configuration without publishing files" & label)
                    registration.Dispose()
                Next
            Next
        Next
        For Each animated In {False, True}
            For Each dark In {False, True}
                _motionPreview = False
                ShowManagement(Nothing, Nothing) : settle()
                Await WaitPackUiAsync(Function() Not _managementScanning AndAlso Not _localModScanning) : settle()
                Dim viewport = DirectCast(ManagementContent(), PclScrollViewer)
                _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                _managementMotion.PauseForChecks = True
                ChangeManagementContent(Sub() RenderManagementRows()) : layout()
                Dim pending = PromptOnlinePackAsync("下载可选文件", "切页检查", Nothing, Nothing, True, CancellationToken.None, viewport)
                FocusManager.SetFocusedElement(host, OnlinePackConfirm)
                closePrompt(False)
                Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                Dim entering = _managementMotion.Transition IsNot Nothing
                ShowSettings(Nothing, Nothing) : settle()
                FocusManager.SetFocusedElement(host, SpeedLimitBox)
                _managementMotion.Finish() : layout()
                check((Await pending) = "no" AndAlso entering = animated AndAlso _managementMotion.Transition Is Nothing AndAlso
                    ActiveSurface() Is SettingsPage AndAlso FocusManager.GetFocusedElement(host) Is SpeedLimitBox,
                    "switching surfaces retires a queued management dialog return without stealing newer settings focus/motion=" & animated & "/dark=" & dark)
                _managementMotion.PauseForChecks = False
            Next
        Next
    End Function
End Class
