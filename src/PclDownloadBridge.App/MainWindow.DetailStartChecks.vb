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
    Public Sub VerifyDetailStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 680}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 680)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckDetailStartAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Detail startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "detail-start.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckDetailStartAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 2
                        _motionPreview = False : CancelResourceRequests()
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = kind, .Id = "first", .Name = "Original detail"}
                        Dim other As New ModpackProject With {.Platform = project.Platform, .Kind = kind, .Id = "second", .Name = "Newer detail"}
                        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                        _packTargetGame = "" : _resourceTargetLoader = "" : _resourceBack.Clear()
                        _packReleases = New List(Of ModpackRelease)
                        ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project) : InitializePackVersions() : settle()
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim latest As New DetailStartState(kind, "Latest verified project", "latest01")
                        Dim stale As New DetailStartState(kind, "Obsolete outer query", "stale001") With {.Fail = True}
                        stale.Gate.SetResult(True)
                        Dim newest As CancellationTokenSource = Nothing, nested As Task = Nothing, expected = project, taskGeneration = _taskGeneration
                        Dim label = "/kind=" & kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        If scenario = 1 Then
                            Dim names = _resourceNamesRequest
                            Using registration = names.Token.Register(Sub()
                                                                         _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(latest), "", 0)
                                                                         OpenPackDetails(other) : newest = _packDetailRequest
                                                                         _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(stale), "", 0)
                                                                     End Sub)
                                Await LoadPackDetailsAsync(project) : settle() : expected = other
                                check(_packProject Is other AndAlso _packDetailRequest Is newest AndAlso newest IsNot Nothing AndAlso Not newest.IsCancellationRequested AndAlso
                                    latest.Calls = 1 AndAlso stale.Calls = 0,
                                    "name cancellation cannot redirect an obsolete refresh into the newly opened project or cancel its reader" & label)
                                latest.Gate.SetResult(True)
                                Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
                            End Using
                        Else
                            Dim old As New DetailStartState(kind, "Retired project response", "old00001")
                            _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(old), "", 0)
                            Dim oldRead = LoadPackDetailsAsync(project), retired = _packDetailRequest, fault = scenario = 2, escaped = False
                            Using registration = retired.Token.Register(Sub()
                                                                           If fault Then Throw New IOException("Controlled detail cancellation failure")
                                                                           _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(latest), "", 0)
                                                                           nested = LoadPackDetailsAsync(project) : newest = _packDetailRequest
                                                                           _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(stale), "", 0)
                                                                       End Sub)
                                Try
                                    Await LoadPackDetailsAsync(project)
                                Catch
                                    escaped = True
                                End Try
                                old.Gate.SetResult(True) : Await oldRead : settle()
                                If fault Then
                                    check(Not escaped AndAlso _packDetailRequest Is Nothing AndAlso PackDetailRetry.IsVisible AndAlso PackDetailHint.Theme = PclHintTheme.Red,
                                        "a detail cancellation notification failure stays inside refresh feedback and owned cleanup" & label)
                                    latest.Gate.SetResult(True)
                                    _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(latest), "", 0)
                                    Await LoadPackDetailsAsync(project) : settle()
                                Else
                                    check(Not escaped AndAlso _packDetailRequest Is newest AndAlso newest IsNot Nothing AndAlso Not newest.IsCancellationRequested AndAlso latest.Calls = 1 AndAlso
                                        stale.Calls = 0 AndAlso Not PackDetailHint.IsVisible,
                                        "a newer same-project refresh owns loading before an older click can start another query" & label)
                                    latest.Gate.SetResult(True) : Await nested : settle()
                                End If
                            End Using
                        End If
                        check(_packProject Is expected AndAlso _packDetailCompletedProject Is expected AndAlso ActivePackProject().Name = latest.Name AndAlso
                            _packReleases.Count = 1 AndAlso _packReleases(0).Id = latest.VersionId AndAlso Not PackDetailRetry.IsVisible AndAlso Not PackDetailHint.IsVisible AndAlso
                            _resourceBack.Count = 0 AndAlso _taskGeneration = taskGeneration AndAlso PageSurface.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible,
                            "the accepted detail or retry retains current releases and page input without another download or return frame" & label)
                    Next
                Next
            Next
        Next
    End Function

    Private Class DetailStartState
        Friend ReadOnly Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Kind As CommunityResourceKind, Name As String, VersionId As String
        Friend Calls As Integer, Fail As Boolean
        Friend Sub New(kind As CommunityResourceKind, name As String, version As String)
            Me.Kind = kind : Me.Name = name : VersionId = version
        End Sub
    End Class
    Private Class DetailStartHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As DetailStartState
        Friend Sub New(state As DetailStartState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim path = request.RequestUri.AbsolutePath.Replace("/modrinth/", "/"), id = path.Split("/"c)(3), data As JToken
            If path.EndsWith("/version", StringComparison.Ordinal) Then
                data = New JArray(CommunityReleaseFixture(_state.Kind, id, _state.VersionId))
            Else
                Interlocked.Increment(_state.Calls) : Await _state.Gate.Task.ConfigureAwait(False)
                If _state.Fail Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("Controlled obsolete detail failure")}
                Dim project = CommunityProjectFixture(_state.Kind, id) : project("title") = _state.Name : data = project
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
