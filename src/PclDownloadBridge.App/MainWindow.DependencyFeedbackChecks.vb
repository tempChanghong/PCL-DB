Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyDependencyFeedbackUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 600}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckDependencyFeedbackAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Dependency feedback did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "dependency-feedback.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckDependencyFeedbackAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    Dim selectedPlatform = platform
                    _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : settle()
                    Dim project As New ModpackProject With {.Platform = platform, .Kind = CommunityResourceKind.Mod, .Id = "100", .Name = "前置反馈检查"}
                    Dim releases As New List(Of ModpackRelease)
                    For Each game In {"1.20.1", "1.21.1"}
                        releases.Add(New ModpackRelease With {.Platform = platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = game,
                            .Name = "测试版本", .FileName = "fixture.jar", .GameVersions = {game}, .Loaders = {"fabric"},
                            .Dependencies = {New ResourceDependency With {.ProjectId = "123"}, New ResourceDependency With {.ProjectId = "124"}}})
                    Next
                    Dim prepare As Action = Sub()
                                                CancelResourceRequests() : _resourceBack.Clear()
                                                _resourceKind = project.Kind : _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = releases
                                                _packTargetGame = "" : _resourceTargetLoader = "" : ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project)
                                                For Each id In {"123", "124"} : _resourceDependencyNames(selectedPlatform.ToString() & ":" & id) = "前置 " & id : Next
                                                InitializePackVersions() : settle()
                                                For Each card In PackVersionCards.Children.OfType(Of PclCard)() : card.Collapsed = False : Next
                                                SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "1.20.1")) : settle()
                                            End Sub
                    Dim link As Func(Of String, PclButton) = Function(id)
                                                               Return DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().
                                                                   SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).Single(Function(button) DirectCast(button.Tag, ResourceDependency).ProjectId = id)
                                                           End Function
                    Dim state As DependencyFeedbackState = Nothing
                    Dim begin As Func(Of PclButton) = Function()
                                                         state = New DependencyFeedbackState()
                                                         Dim accepted = state
                                                         _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyFeedbackHandler(accepted), "fixture-key", 1)
                                                         Dim button = link("123")
                                                         OpenResourceDependency(project, DirectCast(button.Tag, ResourceDependency), button)
                                                         Return button
                                                     End Function
                    Dim label = "/platform=" & platform.ToString() & "/dark=" & dark & "/motion=" & animated, taskGeneration = _taskGeneration

                    prepare()
                    Dim oldLink = begin()
                    Await WaitPackUiAsync(Function() state.Entered)
                    SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "1.21.1")) : settle()
                    Dim retainedRows = PackVersionCards.Children.Cast(Of UIElement)().ToArray()
                    state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : layout()
                    check(Not oldLink.IsVisible AndAlso PackProjectActionStatus.Visibility <> Visibility.Visible AndAlso _packGameFilter = "1.21.1" AndAlso
                        _packProject Is project AndAlso _packReleases Is releases AndAlso _resourceBack.Count = 0 AndAlso PackVersionCards.Children.Cast(Of UIElement)().SequenceEqual(retainedRows),
                        "a failed dependency from a filtered-out link cannot replace the accepted detail feedback rows or filter" & label)

                    prepare() : oldLink = begin()
                    Await WaitPackUiAsync(Function() state.Entered)
                    _packVersionCache("1.20.1").Collapsed = True
                    Await WaitPackUiAsync(Function() Not oldLink.IsVisible) : layout()
                    state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : layout()
                    check(PackProjectActionStatus.Visibility <> Visibility.Visible AndAlso _packVersionCache("1.20.1").Collapsed AndAlso _packProject Is project AndAlso _resourceBack.Count = 0,
                        "a failed dependency from a folded version card cannot publish feedback or reopen its group" & label)

                    prepare() : oldLink = begin()
                    Await WaitPackUiAsync(Function() state.Entered)
                    Dim retired = _dependencyRequest
                    NavigateTitleBack() : settle() : state.Gate.SetResult(True)
                    Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : layout()
                    check(retired.IsCancellationRequested AndAlso _packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0 AndAlso PackProjectActionStatus.Visibility <> Visibility.Visible,
                        "return cancels the dependency and discards its late failure without reviving detail feedback" & label)

                    prepare() : oldLink = begin()
                    Await WaitPackUiAsync(Function() state.Entered)
                    Dim nextLink = link("124"), older = state
                    OpenResourceDependency(project, DirectCast(nextLink.Tag, ResourceDependency), nextLink)
                    Await WaitPackUiAsync(Function() _packProject.Id = "124" AndAlso _packDetailCompletedProject Is _packProject) : settle()
                    older.Gate.SetResult(True) : Await WaitPackUiAsync(Function() older.Exited) : Await Task.Yield() : layout()
                    check(_packProject.Id = "124" AndAlso _resourceBack.Count = 1 AndAlso _resourceBack.Peek().Project Is project AndAlso PackProjectActionStatus.Visibility <> Visibility.Visible,
                        "a replaced dependency failure cannot overwrite the newer project or duplicate its return frame" & label)

                    prepare() : oldLink = begin()
                    Await WaitPackUiAsync(Function() state.Entered)
                    _packDetailMotion.PauseForChecks = animated
                    If animated Then ChangePackContent(PackBrowserMode.Detail, Sub() PackDetailHint.Visibility = Visibility.Collapsed) Else PackDetailResultsHost.IsHitTestVisible = False
                    Dim clock = _packDetailMotion.Transition
                    state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : layout()
                    check(oldLink.IsVisible AndAlso Not PackDetailResultsHost.IsHitTestVisible AndAlso PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.Text.Contains("无法获取前置") AndAlso
                        PackProjectActionStatus.ToolTip IsNot Nothing AndAlso _packDetailMotion.Transition Is clock AndAlso (Not animated OrElse clock IsNot Nothing) AndAlso _resourceBack.Count = 0,
                        "a current visible dependency still reports its failure during a temporary input gate without restarting entry motion" & label)
                    SaveControlVisual(host, Path.Combine(directory, "dependency-feedback-" & platform.ToString() & "-dark-" & dark & "-motion-" & animated & ".png"))
                    _packDetailMotion.Finish() : _packDetailMotion.PauseForChecks = False : PackDetailResultsHost.IsHitTestVisible = True : layout()
                    state.Fail = False : state.Delay = False
                    OpenResourceDependency(project, DirectCast(oldLink.Tag, ResourceDependency), oldLink)
                    Await WaitPackUiAsync(Function() _packProject.Id = "123" AndAlso _packDetailCompletedProject Is _packProject) : settle()
                    NavigateTitleBack() : settle()
                    check(_packProject Is project AndAlso _packReleases Is releases AndAlso _packGameFilter = "1.20.1" AndAlso _resourceBack.Count = 0 AndAlso
                        PackProjectActionStatus.Visibility <> Visibility.Visible AndAlso _taskGeneration = taskGeneration AndAlso Not _busy,
                        "retry after a current dependency failure returns once to the exact accepted parent without creating a download task" & label)
                Next
            Next
        Next
    End Function

    Private Class DependencyFeedbackState
        Friend Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Entered As Boolean, Exited As Boolean, Delay As Boolean = True, Fail As Boolean = True
    End Class
    Private Class DependencyFeedbackHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As DependencyFeedbackState
        Friend Sub New(state As DependencyFeedbackState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim path = request.RequestUri.AbsolutePath, id = path.Split("/"c).Last(), data As JToken
            If id = "123" Then
                If _state.Delay Then
                    _state.Entered = True
                    Await _state.Gate.Task.ConfigureAwait(False) ' Deliberately survives caller cancellation.
                    _state.Exited = True
                End If
                If _state.Fail Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("controlled dependency lookup failure")}
            End If
            If path.Contains("/project/") Then
                data = If(id = "version", DirectCast(New JArray(), JToken), CommunityProjectFixture(CommunityResourceKind.Mod, id))
            ElseIf id = "files" Then
                data = New JObject From {{"data", New JArray()}, {"pagination", New JObject From {{"totalCount", 0}}}}
            Else
                data = New JObject From {{"data", New JObject From {{"id", id}, {"classId", 6}, {"name", "前置 " & id}, {"slug", "fixture"}, {"summary", "dependency fixture"}, {"downloadCount", 1}, {"dateModified", "2026-10-01T00:00:00Z"}}}}
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
