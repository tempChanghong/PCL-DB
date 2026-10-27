Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyDependencyResetUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckDependencyResetAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Dependency name reset did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "dependency-reset.json"), observations.ToString())
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

    Private Async Function CheckDependencyResetAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 2
                        _motionPreview = False : ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod) : settle()
                        Dim project As New ModpackProject With {.Platform = platform, .Kind = CommunityResourceKind.Mod, .Id = If(platform = ModpackPlatform.Modrinth, "parent", "100"), .Name = "Name reset parent"}
                        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                        ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project)
                        PackVersionCards.Children.Clear() : ResetResourceNames() : _resourceDependencyNames.Clear()
                        Dim button As New PclButton With {.Text = "查看前置", .Height = 28}, dependency As New ResourceDependency With {.ProjectId = "123"}
                        Dim rows As New StackPanel : rows.Children.Add(button)
                        PackVersionCards.Children.Add(New PclCard With {.Title = "前置资源", .Body = rows})
                        Dim state As New DependencyNameState With {.Kind = project.Kind, .Name = "Current dependency"}
                        _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNameHandler(state), "", 0)
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim retired = _resourceNamesRequest, token = retired.Token, newest As CancellationTokenSource = Nothing
                        Dim replacement As CancellationToken, retiredBeforeNotify = False, taskGeneration = _taskGeneration
                        Dim nested = scenario = 0, closing = scenario = 2
                        Dim label = "/platform=" & platform.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        Using registration = token.Register(Sub()
                                                                retiredBeforeNotify = _resourceNamesRequest Is Nothing
                                                                If closing Then Return
                                                                If nested Then ResetResourceNames()
                                                                newest = _resourceNamesRequest : replacement = newest.Token
                                                                LoadDependencyNameAsync(project, dependency, button, replacement)
                                                            End Sub)
                            If closing Then _closing = True
                            ResetResourceNames()
                            If closing Then
                                check(retiredBeforeNotify AndAlso _resourceNamesRequest Is Nothing AndAlso token.IsCancellationRequested AndAlso state.Calls = 0,
                                    "closing retires the name source before notifying cancellation without creating another reader" & label)
                            Else
                                check(_resourceNamesRequest Is newest AndAlso Not replacement.IsCancellationRequested AndAlso state.Calls = 1 AndAlso _resourceDependencyNameLoads.Count = 1,
                                    "a name read started during cancellation retains the current live source and one shared request" & label)
                            End If
                            state.Gate.TrySetResult(True)
                            Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 4 AndAlso _resourceDependencyNameLoads.Count = 0)
                            Await Task.Yield() : layout()
                            Dim key = platform.ToString() & ":123"
                            check(If(closing, _resourceDependencyNames.Count = 0 AndAlso button.Text = "查看前置",
                                button.Text = state.Name AndAlso _resourceDependencyNames.ContainsKey(key) AndAlso _resourceDependencyNames(key) = state.Name) AndAlso
                                _taskGeneration = taskGeneration AndAlso Not _busy AndAlso _resourceBack.Count = 0 AndAlso ActivePackProject() Is project AndAlso
                                PageSurface.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible,
                                "accepted dependency names or closing cleanup retain the page gates parent and download task identity" & label)
                        End Using
                        _closing = False
                    Next
                Next
            Next
        Next
    End Function
End Class
