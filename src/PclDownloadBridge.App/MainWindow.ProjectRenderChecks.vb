Imports System.IO
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
    Public Sub VerifyProjectRenderUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckProjectRenderAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Project rendering checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "project-render.json"), observations.ToString())
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

    Private Async Function CheckProjectRenderAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim png As Byte(), latePng As Byte()
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/Grass.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : png = bytes.ToArray()
        End Using
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/GoldBlock.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : latePng = bytes.ToArray()
        End Using
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For scenario = 0 To 1
                    _motionPreview = False
                    Dim state As New ArtworkFixtureState, project = ArtworkProject("original")
                    PrepareArtworkProject(project, state, settle)
                    Dim first = state.Calls.ToArray().Single(), retired = _packDetailArtworkRequest
                    Dim verified = ArtworkProject(project.Id) : verified.Name = "Verified original"
                    Dim other = ArtworkProject("newer") : other.Name = "Newer project"
                    _packVerifiedProject = verified
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim latestRow As PclResourceItem = Nothing, latest As ArtworkFixtureCall = Nothing, newest As CancellationTokenSource = Nothing
                    Dim expected = If(scenario = 0, verified, other), owner = If(scenario = 0, project, other), changeProject = scenario = 1
                    Dim generation = _taskGeneration, label = "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                    _packCatalogFactory = Function() FailingPackCatalog(New IOException("Controlled newer project metadata failure"))
                    Using registration = retired.Token.Register(Sub()
                                                                   If changeProject Then OpenPackDetails(other) Else RenderPackProject(verified)
                                                                   latestRow = ArtworkHeader() : latest = state.Calls.ToArray().Last() : newest = _packDetailArtworkRequest
                                                               End Sub)
                        RenderPackProject(verified) : settle()
                        check(ArtworkHeader() Is latestRow AndAlso latestRow.Tag Is expected AndAlso _packDetailArtworkRow Is latestRow AndAlso _packDetailArtworkRequest Is newest AndAlso
                            newest IsNot Nothing AndAlso Not latest.Token.IsCancellationRequested AndAlso state.Calls.Count = 2,
                            "a later same-project render or project change retains its header and one live image request" & label)
                        latest.Reply(png) : first.Reply(latePng)
                        Await WaitPackUiAsync(Function() latest.Returned AndAlso first.Returned)
                        Await WaitPackUiAsync(Function() latest.Token.IsCancellationRequested OrElse _packIcons.ContainsKey(expected.IconUrl))
                        If changeProject Then Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing)
                        FlushControlLayout() : settle()
                        Dim accepted As BitmapSource = Nothing : _packIcons.TryGetValue(expected.IconUrl, accepted)
                        check(ArtworkHeader() Is latestRow AndAlso ArtworkHeader().Tag Is expected AndAlso accepted IsNot Nothing AndAlso accepted.IsFrozen AndAlso
                            ArtworkHeader().Icon.Source Is accepted AndAlso PackProjectTitle.Text = expected.Name,
                            "only the current project header displays its accepted image after retired image and metadata responses" & label)
                        state.CompleteAll(png)
                        Await WaitPackUiAsync(Function() _packIconSlots.CurrentCount = 4) : FlushControlLayout() : settle()
                        check(_packProject Is owner AndAlso ActivePackProject() Is expected AndAlso state.Calls.Count = 2 AndAlso _resourceBack.Count = 0 AndAlso
                            _taskGeneration = generation AndAlso Not _busy AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible AndAlso
                            If(changeProject, PackDetailRetry.IsVisible AndAlso _packReleases.Count = 0, _packDetailCompletedProject Is project AndAlso _packReleases.Count = 1),
                            "header rendering preserves the accepted detail or retry state and retires page gates without duplicate work" & label)
                    End Using
                Next
            Next
        Next
    End Function
End Class
