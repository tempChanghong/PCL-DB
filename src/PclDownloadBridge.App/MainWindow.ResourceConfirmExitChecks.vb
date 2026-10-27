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
    Public Sub VerifyResourceConfirmExitUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckResourceConfirmExitAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Resource confirmation exit checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-confirm-exit.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceConfirmExitAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 1
                        _motionPreview = False
                        Dim folders = PrepareManagementFixture(directory)
                        FolderList.SelectedItem = folders(launcher)
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Shader) : settle()
                        Dim target = CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                        _managementProvider = Function(binding, token) Task.FromResult(New List(Of InstalledInstance) From {instance})
                        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Shader, .Id = "exit-shader", .Name = "Resource confirmation"}
                        Dim release As New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = "exit-file", .Name = "Confirmation fixture",
                            .FileName = "exit-shader.zip", .GameVersions = {"1.20.1"}, .Loaders = {"iris"}, .Size = 100, .Sha1 = New String("a"c, 40)}
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
                        Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        InstallCommunityResource(release, button)
                        Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading)
                        ResourceTargetInstance.SelectedIndex = 0
                        If _onlinePromptMotion IsNot Nothing Then
                            _onlinePromptMotion.Controller.SkipToFill()
                            Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                        End If
                        Dim exitTask = _onlinePromptClosed.Task, parent = _resourceTargetRequest, closing As ClockGroup = Nothing
                        Using registration = parent.Token.Register(Sub()
                                                                       closing = _onlinePromptMotion
                                                                       If closing IsNot Nothing Then PauseSelectionClock(closing)
                                                                   End Sub)
                            ConfirmOnlinePackName(Nothing, Nothing)
                            Await WaitPackUiAsync(Function() _onlinePrompt Is Nothing)
                            Await Task.Delay(20) : layout()
                            check(If(animated, closing IsNot Nothing AndAlso Not exitTask.IsCompleted AndAlso PackNameOverlay.IsVisible AndAlso PackNameDialog.Opacity > 0.99 AndAlso starts = 0 AndAlso
                                _taskGeneration = generation AndAlso _choosingOnline AndAlso Not _busy, exitTask.IsCompleted AndAlso Not PackNameOverlay.IsVisible AndAlso starts = 1),
                                "resource confirmation waits for its own animated exit while reduced motion remains immediate" & label)
                            If scenario = 0 Then
                                If closing IsNot Nothing Then
                                    closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
                                    If launcher = 0 AndAlso Not dark Then SaveControlVisual(host, Path.Combine(directory, "resource-confirm-exit-80ms.png"))
                                    closing.Controller.SkipToFill()
                                End If
                                Await WaitPackUiAsync(Function() starts = 1 AndAlso Not _choosingOnline AndAlso Not _busy) : settle()
                                check(accepted IsNot Nothing AndAlso accepted.InstanceName = instance.Name AndAlso SameManagementTarget(accepted.LauncherTarget, target) AndAlso
                                    accepted.Destination = Path.Combine(instance.RunDirectory, "shaderpacks", release.FileName) AndAlso _taskGeneration = generation + 1 AndAlso
                                    _taskOutcome = TaskOutcome.Succeeded AndAlso Not PackNameOverlay.IsVisible,
                                    "only the completed confirmation starts one task with the frozen native installation target" & label)
                            Else
                                Dim replacement = PromptOnlinePackAsync("后续确认", "已替代前一次确认退场。", Nothing, Nothing, True, CancellationToken.None), replacementOwner = _onlinePrompt, replacementClock = _onlinePromptMotion
                                If replacementClock IsNot Nothing Then PauseSelectionClock(replacementClock)
                                Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()
                                If closing IsNot Nothing Then closing.Controller.SkipToFill() : layout()
                                check(starts = If(animated, 0, 1) AndAlso _taskGeneration = generation + If(animated, 0, 1) AndAlso _onlinePrompt Is replacementOwner AndAlso
                                    Not replacement.IsCompleted AndAlso _onlinePromptMotion Is replacementClock AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso Not TopTabs.IsEnabled,
                                    "a replacement modal retires pending resource installation and keeps its own animation and input ownership" & label)
                                CompleteOnlinePrompt("no")
                                If _onlinePromptMotion IsNot Nothing Then _onlinePromptMotion.Controller.SkipToFill()
                                Await replacement : Await WaitPackUiAsync(Function() Not PackNameOverlay.IsVisible) : settle()
                            End If
                        End Using
                        If _downloadView = DownloadView.Progress Then ResumeInstallation(Nothing, Nothing) : settle()
                        Dim after = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        check(_packProject Is project AndAlso _packReleases Is releases AndAlso _packMode = PackBrowserMode.Detail AndAlso _downloadView = DownloadView.Modpacks AndAlso
                            PageSurface.IsEnabled AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso
                            files.Count = after.Count AndAlso files.All(Function(item) after.ContainsKey(item.Key) AndAlso after(item.Key).SequenceEqual(item.Value)),
                            "confirmation exit and task return preserve parent releases every native configuration byte and ordinary detail input" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
