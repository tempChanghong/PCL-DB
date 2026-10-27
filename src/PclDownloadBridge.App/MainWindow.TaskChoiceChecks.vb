Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyTaskChoiceUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckTaskChoiceAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Task choice handoff did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "task-choice.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckTaskChoiceAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        Dim hidePrompt As Action = Sub()
                                       StopOnlinePromptMotion() : HideOnlinePrompt() : settle()
                                   End Sub
        Dim number = 0
        For Each resource In {False, True}
            For Each animated In {False, True}
                For Each dark In {False, True}
                    number += 1 : _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    Dim root = Path.Combine(directory, "choice-game-" & Guid.NewGuid().ToString("N"))
                    Dim profile = Path.Combine(root, "versions/Fixture/Fixture.json")
                    IO.Directory.CreateDirectory(Path.GetDirectoryName(profile))
                    File.WriteAllText(profile, New JObject From {{"id", "Fixture"}, {"clientVersion", "1.20.1"}, {"mainClass", "net.minecraft.client.main.Main"}, {"type", "release"}, {"libraries", New JArray()}}.ToString())
                    Dim originalProfile = File.ReadAllBytes(profile)
                    ShowDownload(Nothing, Nothing) : SelectManualFolder(root)
                    Dim target = CurrentInstallTarget(), instances = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                    _managementProvider = Function(binding, token) Task.FromResult(instances)
                    SelectResourceKind(If(resource, CommunityResourceKind.ResourcePack, CommunityResourceKind.Modpack))
                    Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = _resourceKind, .Id = "choice" & number, .Name = "Choice fixture"}
                    Dim release As New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = "release", .Name = "Release", .ReleaseType = "release",
                        .FileName = If(resource, "resource.zip", "archive.mrpack"), .GameVersions = {"1.20.1"}, .Loaders = If(resource, {"minecraft"}, {"fabric"}),
                        .Url = "https://cdn.modrinth.com/fixture/" & If(resource, "resource.zip", "archive.mrpack"), .Size = 100, .Sha1 = New String("a"c, 40)}
                    _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = New List(Of ModpackRelease) From {release}
                    _packTargetGame = "" : RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions() : settle()
                    Dim row = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children.OfType(Of PclListItem)().Single()
                    Dim actionOwner As FrameworkElement = If(resource, DirectCast(row.Children.OfType(Of PclIconButton)().Single(), FrameworkElement), row)
                    Dim begin As Action = Sub()
                                              If resource Then InstallCommunityResource(release, actionOwner) Else InstallOnlinePack(release, release.Name, actionOwner)
                                          End Sub
                    Dim gates As New List(Of TaskChoiceGate)
                    _onlineInstallRunner = Function(item, gameRoot, name, progress, token)
                                               Dim gate As New TaskChoiceGate(token, item, gameRoot, name)
                                               gates.Add(gate) : PauseChoicePrompt()
                                               Return gate.Completion.Task
                                           End Function
                    _resourceInstallRunner = Function(plan, cache, policy, progress, token)
                                                 Dim gate As New TaskChoiceGate(token, plan.Release, plan.LauncherTarget.Root, plan.InstanceName) With {.Plan = plan}
                                                 gates.Add(gate) : PauseChoicePrompt()
                                                 Return gate.Completion.Task
                                             End Function
                    Dim label = "/kind=" & If(resource, "instance-resource", "online-modpack") & "/motion=" & animated & "/dark=" & dark
                    Dim generation = _taskGeneration
                    begin()
                    Await WaitPackUiAsync(Function() _onlinePrompt IsNot Nothing AndAlso (Not resource OrElse Not _resourceTargetLoading))
                    check(_choosingOnline AndAlso Not _busy AndAlso Not PageSurface.IsEnabled AndAlso Not TopTabs.IsEnabled AndAlso Not NavigateTitleBack(),
                        "a pending install choice owns modal input and blocks return before any task starts" & label)
                    CancelOnlinePackName(Nothing, Nothing) : PauseChoicePrompt()
                    Await WaitPackUiAsync(Function() Not _choosingOnline)
                    check(_onlinePrompt Is Nothing AndAlso Not _busy AndAlso gates.Count = 0 AndAlso _taskGeneration = generation,
                        "cancelling the choice starts no task and releases its logical selection owner" & label)
                    Dim blockedExit = Not animated OrElse (PackNameOverlay.Visibility = Visibility.Visible AndAlso Not NavigateTitleBack())
                    check(blockedExit AndAlso (animated OrElse PackNameOverlay.Visibility = Visibility.Collapsed),
                        "the visual modal exit keeps title return gated until the overlay is actually hidden" & label)
                    hidePrompt()
                    ChangePackMode(PackBrowserMode.Detail) : settle()

                    begin()
                    Await WaitPackUiAsync(Function() _onlinePrompt IsNot Nothing AndAlso (Not resource OrElse Not _resourceTargetLoading))
                    If Not resource Then OnlinePackName.Text = "choice-task-" & number
                    ConfirmOnlinePackName(Nothing, Nothing)
                    Await WaitPackUiAsync(Function() _busy AndAlso gates.Count = 1)
                    Dim first = gates(0), taskGeneration = _taskGeneration, cancellation = _cancellation
                    begin()
                    check(Not _choosingOnline AndAlso gates.Count = 1 AndAlso _taskGeneration = taskGeneration AndAlso _onlinePrompt Is Nothing AndAlso
                        first.Release.Id = release.Id AndAlso first.Root = root AndAlso Not first.Token.IsCancellationRequested,
                        "the accepted choice hands ownership to the frozen running task rather than retaining a global selection lock" & label)
                    blockedExit = If(resource, PackNameOverlay.Visibility = Visibility.Collapsed AndAlso PageSurface.IsEnabled,
                        Not animated OrElse (PackNameOverlay.Visibility = Visibility.Visible AndAlso Not NavigateTitleBack()))
                    check(blockedExit, "task start respects its resource exit completion or online-pack modal input gate" & label)
                    hidePrompt()
                    Dim returned = NavigateTitleBack() : settle()
                    check(returned AndAlso _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail AndAlso _packProject Is project AndAlso
                        _busy AndAlso _cancellation Is cancellation AndAlso Not first.Token.IsCancellationRequested AndAlso _taskGeneration = taskGeneration,
                        "title return works during a running confirmed task without cancelling or replacing it" & label)
                    ShowManagement(Nothing, Nothing) : settle()
                    check(ManagementPage.IsVisible AndAlso _busy AndAlso Not ManagementFolders.IsEnabled AndAlso Not first.Token.IsCancellationRequested AndAlso SameManagementTarget(target, CurrentInstallTarget()),
                        "running task permits management inspection while keeping its install target frozen" & label)
                    ShowDownload(Nothing, Nothing) : settle()
                    CancelInstall(Nothing, Nothing)
                    Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline) : settle()
                    check(_taskOutcome = TaskOutcome.Cancelled AndAlso _cancellation Is Nothing AndAlso first.Token.IsCancellationRequested AndAlso ManagementFolders.IsEnabled,
                        "cancellation releases task and choice gates without leaving management controls disabled" & label)
                    Await ResumeResourceTaskForChecksAsync() : settle()
                    begin()
                    Await WaitPackUiAsync(Function() _onlinePrompt IsNot Nothing AndAlso (Not resource OrElse Not _resourceTargetLoading))
                    ConfirmOnlinePackName(Nothing, Nothing)
                    Await WaitPackUiAsync(Function() _busy AndAlso gates.Count = 2)
                    Dim retry = gates(1)
                    check(_taskGeneration > taskGeneration AndAlso Not retry.Token.IsCancellationRequested AndAlso retry.Release.Id = first.Release.Id AndAlso retry.Root = root AndAlso retry.Name = first.Name,
                        "retry uses a fresh task token with the same accepted release target and proposed name" & label)
                    hidePrompt()
                    retry.Completion.SetResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions", retry.Name), .FileCount = 1})
                    Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline) : settle()
                    check(_taskOutcome = TaskOutcome.Succeeded AndAlso _cancellation Is Nothing AndAlso _onlinePrompt Is Nothing AndAlso gates.Count = 2 AndAlso
                        File.ReadAllBytes(profile).SequenceEqual(originalProfile) AndAlso IO.Directory.GetDirectories(Path.Combine(root, "versions")).Length = 1 AndAlso
                        (Not resource OrElse Not File.Exists(retry.Plan.Destination)),
                        "controlled retry completion leaves no modal owner and preserves existing instance files and settings" & label)
                    For Each gate In gates : gate.Registration.Dispose() : Next
                Next
            Next
        Next
    End Function

    Private Sub PauseChoicePrompt()
        Dim clock = _onlinePromptMotion
        If clock IsNot Nothing Then PauseSelectionClock(clock)
    End Sub
    Private Class TaskChoiceGate
        Friend ReadOnly Completion As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Token As CancellationToken, Release As ModpackRelease, Root As String, Name As String
        Friend ReadOnly Registration As CancellationTokenRegistration
        Friend Plan As ResourceInstallationPlan
        Friend Sub New(token As CancellationToken, release As ModpackRelease, root As String, name As String)
            Me.Token = token : Me.Release = release.Copy() : Me.Root = root : Me.Name = name
            Registration = token.Register(Sub() Completion.TrySetCanceled())
        End Sub
    End Class
End Class
