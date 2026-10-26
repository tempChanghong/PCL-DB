Imports System.Collections.Concurrent
Imports System.ComponentModel
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceArtworkUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceArtworkAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Resource artwork did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-artwork.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckResourceArtworkAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim png As Byte(), latePng As Byte()
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/Grass.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : png = bytes.ToArray()
        End Using
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/GoldBlock.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : latePng = bytes.ToArray()
        End Using
        Dim bitmap As New BitmapImage()
        Using bytes As New MemoryStream(png)
            bitmap.BeginInit() : bitmap.CacheOption = BitmapCacheOption.OnLoad : bitmap.StreamSource = bytes : bitmap.EndInit() : bitmap.Freeze()
        End Using
        Dim cachedProject = ArtworkProject("cached"), detached As New PclResourceItem(cachedProject) With {.Tag = cachedProject}
        Dim placeholder = detached.Icon.Source
        _packIcons(cachedProject.IconUrl) = bitmap
        Using cancelled As New CancellationTokenSource()
            cancelled.Cancel() : LoadPackIconAsync(detached, cachedProject, cancelled.Token)
            check(detached.Icon.Source Is placeholder, "a cancelled artwork owner cannot publish even an already cached image")
        End Using
        detached.Icon.Source = placeholder : detached.Tag = ArtworkProject("replacement")
        LoadPackIconAsync(detached, cachedProject, CancellationToken.None)
        check(detached.Icon.Source Is placeholder, "cached artwork cannot overwrite a row now owned by another project")
        detached.Icon.Source = placeholder : detached.Tag = cachedProject : _closing = True
        LoadPackIconAsync(detached, cachedProject, CancellationToken.None)
        check(detached.Icon.Source Is placeholder, "closing prevents cached artwork from updating controls")
        _closing = False

        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        Dim measurements As New JArray, number = 0
        For Each animated In {False, True}
            For Each dark In {False, True}
                number += 1 : _motionPreview = animated
                RestoreAppearance(New JObject From {{"darkMode", dark}})
                Dim state As New ArtworkFixtureState, project = ArtworkProject("category" & number)
                PrepareArtworkProject(project, state, settle)
                Dim initial = state.Calls.ToArray().LastOrDefault(), oldRow = ArtworkHeader(), label = "/motion=" & animated & "/dark=" & dark
                Dim pendingCalls = state.Calls.Count
                RenderPackProject(project) : settle()
                check(initial IsNot Nothing AndAlso initial.Address = project.IconUrl AndAlso Not initial.Token.IsCancellationRequested AndAlso state.Calls.Count = pendingCalls,
                    "detail artwork starts without waiting for entry clocks and reuses the same pending request" & label)
                _packArtworkRequest.Cancel() : FlushControlLayout()
                check(initial IsNot Nothing AndAlso Not initial.Token.IsCancellationRequested,
                    "retiring search-list artwork does not cancel the active detail header" & label)
                SelectResourceKind(CommunityResourceKind.Shader) : settle()
                check(initial IsNot Nothing AndAlso initial.Token.IsCancellationRequested,
                    "leaving the resource category retires its pending detail image" & label)
                Dim before = state.Calls.Count
                SelectResourceKind(CommunityResourceKind.Mod) : settle()
                Dim current = If(state.Calls.Count > before, state.Calls.ToArray().Last(), Nothing), row = ArtworkHeader()
                check(current IsNot Nothing AndAlso current IsNot initial AndAlso Not current.Token.IsCancellationRequested AndAlso row.Tag Is project,
                    "restoring a completed detail starts fresh artwork rather than reusing cancelled search state" & label)
                If current IsNot Nothing Then
                    current.Reply(png)
                    Await WaitPackUiAsync(Function() _packIcons.ContainsKey(project.IconUrl))
                End If
                Dim accepted As BitmapSource = Nothing
                _packIcons.TryGetValue(project.IconUrl, accepted)
                check(accepted IsNot Nothing AndAlso accepted.IsFrozen AndAlso accepted.PixelWidth = 100 AndAlso row.Icon.Source Is accepted,
                    "the restored header displays the decoded bounded image" & label)
                initial?.Reply(latePng)
                If initial IsNot Nothing Then Await WaitPackUiAsync(Function() initial.Returned)
                FlushControlLayout() : settle()
                check(accepted Is Nothing OrElse (_packIcons(project.IconUrl) Is accepted AndAlso row.Icon.Source Is accepted),
                    "a retired late image cannot replace the accepted cache or current header" & label)
                Dim calls = state.Calls.Count, generation = _taskGeneration
                RenderPackProject(project) : settle()
                check(state.Calls.Count = calls AndAlso row.Icon.Source Is accepted,
                    "rendering a completed cached header does not start redundant HTTP work" & label)
                check(_packProject Is project AndAlso _packDetailCompletedProject Is project AndAlso _packReleases.Single().Id = "release" AndAlso _resourceBack.Count = 0 AndAlso
                    generation = _taskGeneration AndAlso PageSurface.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible,
                    "image completion preserves releases navigation task identity and input handoff" & label)
                measurements.Add(New JObject From {{"Motion", animated}, {"Dark", dark}, {"Calls", state.Calls.Count}, {"Loaded", accepted IsNot Nothing}, {"OldCancelled", If(initial Is Nothing, False, initial.Token.IsCancellationRequested)}})
                state.CompleteAll(png) : FlushControlLayout()
            Next
        Next

        Dim retry As New ArtworkFixtureState, retryProject = ArtworkProject("retry")
        PrepareArtworkProject(retryProject, retry, settle)
        Dim failed = retry.Calls.ToArray().Last()
        failed.Reply(png, HttpStatusCode.ServiceUnavailable)
        Await WaitPackUiAsync(Function() failed.Returned) : FlushControlLayout() : settle()
        check(Not _packIcons.ContainsKey(retryProject.IconUrl) AndAlso _packDetailCompletedProject Is retryProject,
            "optional image failure leaves completed resource versions usable")
        Dim previousCalls = retry.Calls.Count
        RenderPackProject(retryProject) : settle()
        Dim retried = If(retry.Calls.Count > previousCalls, retry.Calls.ToArray().Last(), Nothing)
        check(retried IsNot Nothing AndAlso Not retried.Token.IsCancellationRequested,
            "rerendering the same project after image failure retries its existing header")
        retried?.Reply(png)
        If retried IsNot Nothing Then Await WaitPackUiAsync(Function() _packIcons.ContainsKey(retryProject.IconUrl))
        check(_packIcons.ContainsKey(retryProject.IconUrl) AndAlso ArtworkHeader().Icon.Source Is _packIcons(retryProject.IconUrl),
            "successful optional image retry replaces the fallback without changing project ownership")
        retry.CompleteAll(png) : FlushControlLayout()

        Dim surfaceState As New ArtworkFixtureState, surfaceProject = ArtworkProject("surface")
        PrepareArtworkProject(surfaceProject, surfaceState, settle)
        Dim hiddenImage = surfaceState.Calls.ToArray().Last(), selectedFolder = _selectedFolder
        ShowSettings(Nothing, Nothing) : settle()
        check(hiddenImage.Token.IsCancellationRequested AndAlso Not PackDetailSurface.IsVisible,
            "switching to settings retires the hidden header image without leaving a download task")
        _managementProvider = Function(target, token) Task.FromResult(New List(Of InstalledInstance)())
        ShowManagement(Nothing, Nothing) : settle()
        previousCalls = surfaceState.Calls.Count
        ShowDownload(Nothing, Nothing) : settle()
        Dim visibleAgain = If(surfaceState.Calls.Count > previousCalls, surfaceState.Calls.ToArray().Last(), Nothing)
        check(visibleAgain IsNot Nothing AndAlso Not visibleAgain.Token.IsCancellationRequested AndAlso _selectedFolder Is selectedFolder AndAlso
            _packProject Is surfaceProject AndAlso _packMode = PackBrowserMode.Detail,
            "settings and management round trips restore fresh artwork with the same folder and detail owner")
        visibleAgain?.Reply(png)
        If visibleAgain IsNot Nothing Then Await WaitPackUiAsync(Function() _packIcons.ContainsKey(surfaceProject.IconUrl))
        Dim surfaceImage As BitmapSource = Nothing
        _packIcons.TryGetValue(surfaceProject.IconUrl, surfaceImage)
        surfaceState.CompleteAll(latePng) : Await WaitPackUiAsync(Function() hiddenImage.Returned) : FlushControlLayout() : settle()
        check(surfaceImage IsNot Nothing AndAlso ArtworkHeader().Icon.Source Is surfaceImage AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible,
            "late images across main-page switches cannot replace the current decoded header or its input handoff")

        Dim taskState As New ArtworkFixtureState, taskProject = ArtworkProject("task")
        PrepareArtworkProject(taskProject, taskState, settle)
        Dim pendingImage = taskState.Calls.ToArray().Last(), release = _packReleases.Single()
        _resourceSaveRunner = Async Function(item, destination, progress, token)
                                  Await Task.Delay(Timeout.Infinite, token)
                              End Function
        Dim saving = RunResourceSaveAsync(release, Path.Combine(directory, "not-published.jar"))
        settle()
        check(_busy AndAlso _downloadView = DownloadView.Progress AndAlso pendingImage.Token.IsCancellationRequested,
            "starting a resource transfer retires only its hidden detail artwork")
        CancelInstall(Nothing, Nothing) : Await saving : settle()
        check(Not _busy AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso Not File.Exists(Path.Combine(directory, "not-published.jar")),
            "controlled resource cancellation reaches a terminal task without publishing a file")
        previousCalls = taskState.Calls.Count
        Await ResumeResourceTaskForChecksAsync() : settle()
        Dim resumed = If(taskState.Calls.Count > previousCalls, taskState.Calls.ToArray().Last(), Nothing)
        check(resumed IsNot Nothing AndAlso Not resumed.Token.IsCancellationRequested,
            "returning from a cancelled transfer restarts artwork on the retained detail row")
        resumed?.Reply(png)
        If resumed IsNot Nothing Then Await WaitPackUiAsync(Function() _packIcons.ContainsKey(taskProject.IconUrl))
        check(_packProject Is taskProject AndAlso _packReleases.Single() Is release AndAlso _packMode = PackBrowserMode.Detail AndAlso
            resumed IsNot Nothing AndAlso ArtworkHeader().Icon.Source Is _packIcons(taskProject.IconUrl),
            "task return retains exact installable release and restores its project image")
        taskState.CompleteAll(latePng) : FlushControlLayout() : settle()
        check(_taskOutcome = TaskOutcome.Cancelled AndAlso _packDetailCompletedProject Is taskProject AndAlso _resourceBack.Count = 0,
            "late retired task artwork cannot alter cancellation outcome or the detail return stack")

        Dim closingState As New ArtworkFixtureState, closingProject = ArtworkProject("closing")
        PrepareArtworkProject(closingProject, closingState, settle)
        Dim closingImage = closingState.Calls.ToArray().Last(), closingRow = ArtworkHeader(), closingFallback = closingRow.Icon.Source
        WindowClosing(Me, New CancelEventArgs())
        closingState.CompleteAll(png) : Await WaitPackUiAsync(Function() closingImage.Returned) : FlushControlLayout()
        check(_closing AndAlso closingImage.Token.IsCancellationRequested AndAlso closingRow.Icon.Source Is closingFallback AndAlso Not _packIcons.ContainsKey(closingProject.IconUrl),
            "window closing retires an in-flight image and ignores its late decoded result")
        File.WriteAllText(Path.Combine(directory, "resource-artwork-observations.json"), measurements.ToString())
    End Function

    Private Shared Function ArtworkProject(id As String) As ModpackProject
        Return New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = id, .Name = "Artwork " & id,
            .IconUrl = "https://cdn.modrinth.com/data/" & id & "/icon.png"}
    End Function
    Private Function ArtworkHeader() As PclResourceItem
        Return DirectCast(PackProjectIdentity.Children(0), PclResourceItem)
    End Function
    Private Sub PrepareArtworkProject(project As ModpackProject, state As ArtworkFixtureState, settle As Action)
        CancelResourceRequests()
        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod)
        ChangePackMode(PackBrowserMode.Search) : settle()
        _packIcons.Clear() : _resourceBack.Clear()
        _packArtworkClientFactory = Function() New HttpClient(New ArtworkFixtureHandler(state)) With {.Timeout = TimeSpan.FromSeconds(12)}
        _packArtworkRequest = New CancellationTokenSource()
        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
        _packTargetGame = "" : _packReleases = New List(Of ModpackRelease) From {New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id,
            .Id = "release", .Name = "Release", .FileName = "resource.jar", .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}}}
        RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions() : settle()
    End Sub
    Private Class ArtworkFixtureState
        Friend ReadOnly Calls As New ConcurrentQueue(Of ArtworkFixtureCall)
        Friend Sub CompleteAll(bytes As Byte())
            For Each callInfo In Calls.ToArray() : callInfo.Reply(bytes) : Next
        End Sub
    End Class
    Private Class ArtworkFixtureCall
        Friend Address As String, Token As CancellationToken, Returned As Boolean
        Friend Gate As New TaskCompletionSource(Of HttpResponseMessage)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Sub Reply(bytes As Byte(), Optional status As HttpStatusCode = HttpStatusCode.OK)
            Dim response As New HttpResponseMessage(status) With {.Content = New ByteArrayContent(bytes)}
            If Not Gate.TrySetResult(response) Then response.Dispose()
        End Sub
    End Class
    Private Class ArtworkFixtureHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As ArtworkFixtureState
        Friend Sub New(state As ArtworkFixtureState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim callInfo As New ArtworkFixtureCall With {.Address = request.RequestUri.AbsoluteUri, .Token = token}
            _state.Calls.Enqueue(callInfo)
            Dim response = Await callInfo.Gate.Task.ConfigureAwait(False) ' Intentionally late after cancellation.
            callInfo.Returned = True
            Return response
        End Function
    End Class
End Class
