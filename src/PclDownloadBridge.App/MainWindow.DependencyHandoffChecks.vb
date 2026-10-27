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
    Public Sub VerifyDependencyHandoffUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 960, 740)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckDependencyHandoffAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Dependency startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "dependency-handoff.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckDependencyHandoffAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 1
                        _motionPreview = False
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : settle()
                        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = kind, .Id = "parent", .Name = "Dependency handoff parent"}
                        Dim releases As New List(Of ModpackRelease) From {New ModpackRelease With {.Platform = project.Platform, .Kind = kind, .ProjectId = project.Id,
                            .Id = "parent-release", .Name = "Parent release", .FileName = If(kind = CommunityResourceKind.Mod, "resource.jar", "resource.zip"),
                            .GameVersions = {"1.20.1"}, .Loaders = {If(kind = CommunityResourceKind.Mod, "fabric", "iris")},
                            .Dependencies = {New ResourceDependency With {.ProjectId = "dep1"}, New ResourceDependency With {.ProjectId = "dep2"}, New ResourceDependency With {.ProjectId = "dep3"}}}}
                        _resourceBack.Clear()
                        For Each id In {"dep1", "dep2", "dep3"} : _resourceDependencyNames("Modrinth:" & id) = id : Next
                        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = releases
                        _packTargetGame = "" : _resourceTargetLoader = ""
                        ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project) : InitializePackVersions() : settle()
                        Dim links = DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().
                            SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).ToDictionary(Function(button) DirectCast(button.Tag, ResourceDependency).ProjectId)
                        Dim state As New DependencyHandoffState(kind)
                        _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyHandoffHandler(state), "", 0)
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim taskGeneration = _taskGeneration, label = "/kind=" & kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        OpenResourceDependency(project, DirectCast(links("dep1").Tag, ResourceDependency), links("dep1"))
                        Await WaitPackUiAsync(Function() state.Calls.ContainsKey("dep1"))
                        Dim retired = _dependencyRequest, newest As CancellationTokenSource = Nothing, returnDuringCancel = scenario = 1
                        Using registration = retired.Token.Register(Sub()
                                                                       If Not returnDuringCancel Then
                                                                           OpenResourceDependency(project, DirectCast(links("dep3").Tag, ResourceDependency), links("dep3"))
                                                                           newest = _dependencyRequest
                                                                       Else
                                                                           NavigateTitleBack() : settle()
                                                                       End If
                                                                   End Sub)
                            OpenResourceDependency(project, DirectCast(links("dep2").Tag, ResourceDependency), links("dep2"))
                            If scenario = 0 Then
                                check(_dependencyRequest Is newest AndAlso newest IsNot Nothing AndAlso Not newest.IsCancellationRequested AndAlso
                                    state.Calls.ContainsKey("dep3") AndAlso Not state.Calls.ContainsKey("dep2"), "a newer dependency click owns resolution before the replaced click can issue HTTP" & label)
                            Else
                                check(_packMode = PackBrowserMode.Search AndAlso _dependencyRequest Is Nothing AndAlso Not state.Calls.ContainsKey("dep2"),
                                    "title back during dependency cancellation retires the outgoing click before HTTP" & label)
                            End If
                            For Each gate In state.Gates.Values : gate.TrySetResult(True) : Next
                            Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing AndAlso _packDetailRequest Is Nothing)
                            settle()
                            If scenario = 0 Then
                                check(_packProject.Id = "dep3" AndAlso _packDetailCompletedProject Is _packProject AndAlso _resourceBack.Count = 1 AndAlso _resourceBack.Peek().Project Is project,
                                    "only the newest dependency enters its detail with one exact parent frame" & label)
                                NavigateTitleBack() : settle()
                            Else
                                check(_packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0 AndAlso _packProject Is project AndAlso
                                    Not PackProjectActionStatus.IsVisible, "late dependency completion cannot navigate or publish feedback after title back" & label)
                                ChangePackMode(PackBrowserMode.Detail) : settle()
                            End If
                            check(_packProject Is project AndAlso _packReleases Is releases AndAlso _resourceBack.Count = 0 AndAlso _taskGeneration = taskGeneration AndAlso Not _busy AndAlso
                                PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible,
                                "dependency cancellation and return preserve parent releases and retire all animation gates without a download task" & label)
                        End Using
                    Next
                Next
            Next
        Next
    End Function

    Private Class DependencyHandoffState
        Friend ReadOnly Kind As CommunityResourceKind
        Friend ReadOnly Gates As New Dictionary(Of String, TaskCompletionSource(Of Boolean)) From {
            {"dep1", New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)},
            {"dep2", New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)},
            {"dep3", New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)}}
        Friend ReadOnly Calls As New System.Collections.Concurrent.ConcurrentDictionary(Of String, Integer)
        Friend Sub New(kind As CommunityResourceKind)
            Me.Kind = kind
        End Sub
    End Class
    Private Class DependencyHandoffHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As DependencyHandoffState
        Friend Sub New(state As DependencyHandoffState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim path = request.RequestUri.AbsolutePath.Replace("/modrinth/", "/"), id = path.Split("/"c)(3), data As JToken
            If path.EndsWith("/version", StringComparison.Ordinal) Then
                data = New JArray(CommunityReleaseFixture(_state.Kind, id))
            Else
                _state.Calls.AddOrUpdate(id, 1, Function(key, count) count + 1)
                Await _state.Gates(id).Task ' Intentionally returns after cancellation.
                data = CommunityProjectFixture(_state.Kind, id)
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
