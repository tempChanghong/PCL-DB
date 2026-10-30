Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceInstallHandoffUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host), observations As New JObject
        Dim focus = LocalModCheckGetNativeFocus()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceInstallHandoffAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Resource installation handoff did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-install-handoff.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
            LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceInstallHandoffAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 2
                        _motionPreview = False
                        Dim folders = PrepareManagementFixture(Path.Combine(directory, "handoff-" & launcher & "-" & dark & "-" & animated & "-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Shader) : settle()
                        Dim target = CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                        _managementProvider = Function(binding, token) Task.FromResult(New List(Of InstalledInstance) From {instance})
                        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Shader, .Id = "handoff-shader", .Name = "Installation handoff"}
                        Dim release As New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = "handoff-file", .Name = "Handoff fixture",
                            .FileName = "handoff-shader.zip", .GameVersions = {"1.20.1"}, .Loaders = {"iris"}, .Size = 100, .Sha1 = New String("a"c, 40)}
                        Dim releases As New List(Of ModpackRelease) From {release}
                        _resourceBack.Clear() : _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = releases
                        _packTargetGame = "" : _resourceTargetLoader = ""
                        ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project) : InitializePackVersions() : settle()
                        Dim row = DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of PclListItem)().Single(), button = row.Children.OfType(Of PclIconButton)().Single()
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), generation = _taskGeneration, starts = 0, accepted As ResourceInstallationPlan = Nothing
                        _resourceInstallRunner = Function(plan, cache, policy, progress, token)
                                                     starts += 1 : accepted = plan : Return Task.CompletedTask
                                                 End Function
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        InstallCommunityResource(release, button)
                        Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading)
                        ResourceTargetInstance.SelectedIndex = 0
                        If _onlinePromptMotion IsNot Nothing Then
                            _onlinePromptMotion.Controller.SkipToFill()
                            Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                        End If
                        Dim parent = _resourceTargetRequest, closing As ClockGroup = Nothing, expected As FrameworkElement = DownloadPage, expectedClock As ClockGroup = Nothing
                        Dim chosen = scenario
                        Using registration = parent.Token.Register(Sub()
                                                                       closing = _onlinePromptMotion
                                                                       If closing IsNot Nothing Then PauseSelectionClock(closing)
                                                                       If chosen > 0 Then ShowSettings(Nothing, Nothing)
                                                                       If chosen = 2 Then
                                                                           settle() : ShowDownload(Nothing, Nothing) : settle()
                                                                       End If
                                                                       expected = ActiveSurface() : expectedClock = _surfaceMotion.Transition
                                                                   End Sub)
                            ConfirmOnlinePackName(Nothing, Nothing)
                            Await WaitPackUiAsync(Function() _onlinePrompt Is Nothing)
                            If closing IsNot Nothing Then closing.Controller.SkipToFill()
                            Await WaitPackUiAsync(Function() Not _choosingOnline AndAlso Not _busy) : layout()
                        End Using
                        Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        check(If(scenario = 0, starts = 1 AndAlso accepted IsNot Nothing AndAlso SameManagementTarget(accepted.LauncherTarget, target) AndAlso
                            accepted.InstanceName = instance.Name AndAlso accepted.Destination = Path.Combine(instance.RunDirectory, "shaderpacks", release.FileName) AndAlso
                            _taskGeneration = generation + 1 AndAlso _taskOutcome = TaskOutcome.Succeeded,
                            starts = 0 AndAlso accepted Is Nothing AndAlso _taskGeneration = generation AndAlso PackProjectActionStatus.Text = "" AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed),
                            "only the original resource confirmation visit may start its frozen installation plan" & label)
                        Dim retained = scenario = 0 OrElse (ActiveSurface() Is expected AndAlso _surfaceMotion.Transition Is expectedClock)
                        settle()
                        check(retained AndAlso ActiveSurface() Is expected AndAlso _downloadView = If(scenario = 0, DownloadView.Progress, DownloadView.Modpacks) AndAlso
                            Not PackNameOverlay.IsVisible AndAlso _onlinePrompt Is Nothing AndAlso _resourceTargetRequest Is Nothing AndAlso _resourceTargetChoice Is Nothing AndAlso
                            PageSurface.IsEnabled AndAlso PageSurface.IsHitTestVisible AndAlso TopTabs.IsEnabled AndAlso Not _choosingOnline AndAlso
                            _packProject Is project AndAlso _packReleases Is releases,
                            "confirmation handoff preserves the accepted page and animation and releases modal input for retry" & label)
                        Dim after = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        check(after.Count = files.Count AndAlso files.All(Function(item) after.ContainsKey(item.Key) AndAlso after(item.Key).SequenceEqual(item.Value)),
                            "owned and retired installation handoffs preserve every native configuration and game file" & label)
                        If _downloadView = DownloadView.Progress Then ResumeInstallation(Nothing, Nothing) : settle()
                    Next
                Next
            Next
        Next
    End Function
End Class
