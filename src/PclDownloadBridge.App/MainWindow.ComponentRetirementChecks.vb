Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyComponentRetirementUi(directory As String, passed As List(Of String))
        Dim appearance = _appearance.ToJson(), owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), host As New Border With {.Width = 900, .Height = 700}, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckComponentRetirementAsync(ControlLayout(host), check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Component retirement checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "component-retirement.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._pageSwitch.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = original : RestoreAppearance(appearance)
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckComponentRetirementAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _pageSwitch.Finish() : _surfaceMotion.Finish() : _categoryMotion.Finish() : _titleNavigation.Finish() : layout()
                                   For Each view In _componentViews.Values : FinishDeferredCard(view.Card, layout) : Next
                                   layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For scenario = 0 To 4
                    _closing = False : _componentProvider = Nothing : _motionPreview = False
                    _restoringSettings = True : VersionSourceBox.SelectedIndex = 1 : _restoringSettings = False
                    SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1")) : settle()
                    SelectComponent(_componentEntries(ComponentKind.Fabric)(0)) : ClearComponent(ComponentKind.FabricApi) : InstanceName.Text = "原方案保留名称"
                    Dim entry = _selectedEntry, seed = _componentEntries.ToDictionary(Function(item) item.Key, Function(item) item.Value.ToList()), selected = _componentSelection.Item(ComponentKind.Fabric)
                    Dim targetB As New VersionEntry With {.Id = "1.21.1", .Kind = "release"}, targetC As New VersionEntry With {.Id = "1.21.4", .Kind = "release"}
                    Dim replacements As New Dictionary(Of String, ComponentEntry)
                    For Each mc In {entry.Id, targetB.Id, targetC.Id}
                        replacements(mc) = New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = mc, .Version = selected.Version, .Url = "https://example.invalid/current/" & mc, .Data = New JObject()}
                    Next
                    Dim phase = 0, calls As New List(Of String), oldGate = ListGate(Of ComponentEntry)(), neoGate = ListGate(Of ComponentEntry)(), latestGate = ListGate(Of ComponentEntry)()
                    Dim oldToken As CancellationToken, neoToken As CancellationToken, latestToken As CancellationToken
                    Dim currentCase = scenario
                    _componentProvider = Function(policy, kind, mc, token)
                                             If kind = ComponentKind.Fabric Then
                                                 calls.Add(mc)
                                                 If phase = 0 Then oldToken = token : Return oldGate.Task
                                                 If mc = entry.Id AndAlso currentCase <= 1 Then latestToken = token : Return latestGate.Task
                                                 Return Task.FromResult(New List(Of ComponentEntry) From {replacements(mc)})
                                             End If
                                             If phase = 0 AndAlso kind = ComponentKind.NeoForge Then neoToken = token : Return neoGate.Task
                                             Return Task.FromResult(New List(Of ComponentEntry)())
                                         End Function
                    Dim oldWork = RefreshComponentListsAsync(ComponentKind.Fabric), neoWork = RefreshComponentListsAsync(ComponentKind.NeoForge)
                    Dim generation = _taskGeneration, nested As Task = Task.CompletedTask, action As Task = Task.CompletedTask, failure As Exception = Nothing
                    Dim latestRequest As CancellationTokenSource = Nothing, latestCard As PclCard = Nothing
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle() : phase = 1
                    Dim label = "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                    Using registration = oldToken.Register(Sub()
                                                               If currentCase <= 1 Then
                                                                   nested = RefreshComponentListsAsync(ComponentKind.Fabric) : latestRequest = _componentRequests(ComponentKind.Fabric)
                                                               ElseIf currentCase = 2 Then
                                                                   SelectVersion(targetC) : SelectComponent(_componentEntries(ComponentKind.Fabric).Single())
                                                                   ClearComponent(ComponentKind.FabricApi) : InstanceName.Text = "后选页面保留名称" : latestCard = _componentViews(ComponentKind.Fabric).Card
                                                               ElseIf currentCase = 3 Then
                                                                   nested = RefreshComponentListsAsync()
                                                               Else
                                                                   Throw New IOException("Controlled batch cancellation observer failure")
                                                               End If
                                                           End Sub)
                        Try
                            Select Case scenario
                                Case 0 : CancelComponentRequests()
                                Case 1 : action = RefreshComponentListsAsync()
                                Case 2 : SelectVersion(targetB)
                                Case 3 : VersionSourceBox.SelectedIndex = 0
                                Case 4 : WindowClosing(Me, New CancelEventArgs())
                            End Select
                        Catch ex As Exception
                            failure = ex
                        End Try
                        Dim current As CancellationTokenSource = Nothing : _componentRequests.TryGetValue(ComponentKind.Fabric, current)
                        check(failure Is Nothing AndAlso Not action.IsFaulted AndAlso oldToken.IsCancellationRequested AndAlso neoToken.IsCancellationRequested AndAlso
                            If(scenario <= 1, latestRequest IsNot Nothing AndAlso current Is latestRequest AndAlso Not latestToken.IsCancellationRequested AndAlso _componentPending.Contains(ComponentKind.Fabric) AndAlso calls.Count = 2,
                                If(scenario = 2, _selectedEntry Is targetC AndAlso _componentViews(ComponentKind.Fabric).Card Is latestCard AndAlso InstanceName.Text = "后选页面保留名称",
                                    If(scenario = 3, Not _componentsDirty AndAlso InstallButton.IsEnabled, _closing AndAlso _componentRequests.Count = 0 AndAlso _componentPending.Count = 0))),
                            "batch retirement leaves newer ownership intact and notifies every old component even when an observer fails" & label)
                        oldGate.TrySetResult(seed(ComponentKind.Fabric)) : neoGate.TrySetResult(seed(ComponentKind.NeoForge)) : latestGate.TrySetResult(New List(Of ComponentEntry) From {replacements(entry.Id)})
                        Try
                            Await Task.WhenAll(oldWork, neoWork, action, nested)
                        Catch ex As Exception
                            failure = ex
                        End Try
                        settle()
                        Dim accepted As List(Of ComponentEntry) = Nothing : _componentEntries.TryGetValue(ComponentKind.Fabric, accepted)
                        check(failure Is Nothing AndAlso _componentRequests.Count = 0 AndAlso _componentPending.Count = 0 AndAlso If(scenario = 4, _closing AndAlso Not InstallButton.IsEnabled,
                            accepted IsNot Nothing AndAlso accepted.Single() Is replacements(If(scenario = 2, targetC.Id, entry.Id)) AndAlso
                            _componentSelection.Item(ComponentKind.Fabric) Is accepted.Single() AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso
                            InstanceName.Text = If(scenario = 2, "后选页面保留名称", "原方案保留名称") AndAlso InstallButton.IsEnabled),
                            "late retired results cannot erase the accepted selection name dependency choice or close state" & label)
                        check(_taskGeneration = generation AndAlso Not _busy AndAlso If(scenario = 4, failure Is Nothing AndAlso oldToken.IsCancellationRequested AndAlso neoToken.IsCancellationRequested,
                            PageSurface.IsHitTestVisible AndAlso _selectedEntry Is If(scenario = 2, targetC, entry) AndAlso
                            If(scenario = 2, calls.Where(Function(mc) mc = targetC.Id).Count() = 1, calls.Count = 2)),
                            "only the latest component work runs and page input returns without starting an installation" & label)
                    End Using
                Next
            Next
        Next
    End Function
End Class
