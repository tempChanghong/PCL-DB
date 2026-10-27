Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyComponentRegressionUi(directory As String, passed As List(Of String))
        Dim appearance = _appearance.ToJson(), owner As New MainWindow(True), previous = SynchronizationContext.Current, focus = LocalModCheckGetNativeFocus()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
            ' Match VerifyUi's logical root; the loading fixture attaches its own
            ' visual surface and controls its own folding clocks.
            owner.VerifyRefreshRebinding(check)
            owner.VerifyCompatibilityWorkflow(check)
            owner.VerifyComponentLoading(check, directory)
            owner.VerifyRefreshWorkflow(check, directory)
        Finally
            owner._closing = True : owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._pageSwitch.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._titleNavigation.Finish()
            RestoreAppearance(appearance)
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Public Sub VerifyComponentRetryUi(directory As String, passed As List(Of String))
        Dim appearance = _appearance.ToJson(), focus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckComponentRetryAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Component retry checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "component-retry.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._pageSwitch.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
            RestoreAppearance(appearance) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckComponentRetryAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _pageSwitch.Finish() : _categoryMotion.Finish() : _surfaceMotion.Finish() : _titleNavigation.Finish() : layout()
                                   For Each view In _componentViews.Values : FinishDeferredCard(view.Card, layout) : Next
                                   layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For scenario = 0 To 2
                    _componentProvider = Nothing : _motionPreview = False
                    SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1")) : settle()
                    SelectComponent(_componentEntries(ComponentKind.Fabric)(0)) : ClearComponent(ComponentKind.FabricApi)
                    InstanceName.Text = "保留重试方案"
                    Dim entry = _selectedEntry, selected = _componentSelection.Item(ComponentKind.Fabric), seed = _componentEntries.ToDictionary(Function(item) item.Key, Function(item) item.Value.ToList())
                    Dim replacement As New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = entry.Id, .Version = selected.Version, .Url = "https://example.invalid/new-fabric", .Data = New JObject()}
                    Dim other As New VersionEntry With {.Id = "1.21.1", .Kind = "release", .Released = DateTimeOffset.Parse("2024-08-08T00:00:00Z")}
                    Dim newVersion As New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = other.Id, .Version = selected.Version, .Data = New JObject()}
                    Dim oldGate = ListGate(Of ComponentEntry)(), neoGate = ListGate(Of ComponentEntry)(), calls As New List(Of String), neoToken As CancellationToken
                    _componentProvider = Function(policy, kind, mc, token)
                                             If kind = ComponentKind.Fabric Then
                                                 calls.Add(mc)
                                                 If calls.Count = 1 Then Return oldGate.Task
                                                 Return Task.FromResult(New List(Of ComponentEntry) From {If(mc = other.Id, newVersion, replacement)})
                                             End If
                                             If kind = ComponentKind.NeoForge AndAlso mc = entry.Id Then neoToken = token : Return neoGate.Task
                                             Return Task.FromResult(New List(Of ComponentEntry)())
                                         End Function
                    Dim oldWork = RefreshComponentListsAsync(ComponentKind.Fabric), neoWork = RefreshComponentListsAsync(ComponentKind.NeoForge)
                    Dim retired = _componentRequests(ComponentKind.Fabric), nested As Task = Nothing, failure As Exception = Nothing, generation = _taskGeneration
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim label = "/dark=" & dark & "/motion=" & animated & "/case=" & scenario, changeCase = scenario
                    Using registration = retired.Token.Register(Sub()
                                                                   If changeCase = 0 Then
                                                                       nested = RefreshComponentListsAsync(ComponentKind.Fabric)
                                                                   ElseIf changeCase = 1 Then
                                                                       SelectVersion(other)
                                                                   Else
                                                                       Throw New IOException("Controlled component cancellation observer failure")
                                                                   End If
                                                               End Sub)
                        Try
                            Await RefreshComponentListsAsync(ComponentKind.Fabric)
                        Catch ex As Exception
                            failure = ex
                        End Try
                        If nested IsNot Nothing Then Await nested
                        check(failure Is Nothing AndAlso If(scenario = 1,
                            _selectedEntry Is other AndAlso calls.Where(Function(value) value = entry.Id).Count() = 1 AndAlso calls.Where(Function(value) value = other.Id).Count() = 1 AndAlso Not _componentPending.Contains(ComponentKind.Fabric),
                            Not neoToken.IsCancellationRequested AndAlso _componentPending.Contains(ComponentKind.NeoForge) AndAlso
                            If(scenario = 0, calls.Count = 2 AndAlso Not _componentErrors.ContainsKey(ComponentKind.Fabric), _componentErrors.ContainsKey(ComponentKind.Fabric) AndAlso calls.Count = 1)),
                            "component retry reserves its own request before notification and cannot replace newer work or leak observer errors" & label)
                        oldGate.TrySetResult(seed(ComponentKind.Fabric)) : neoGate.TrySetResult(seed(ComponentKind.NeoForge))
                        Await Task.WhenAll(oldWork, neoWork) : settle()
                        Dim accepted As List(Of ComponentEntry) = Nothing : _componentEntries.TryGetValue(ComponentKind.Fabric, accepted)
                        check(_componentRequests.Count = 0 AndAlso _componentPending.Count = 0 AndAlso If(scenario = 1,
                            accepted IsNot Nothing AndAlso accepted.Single() Is newVersion AndAlso _componentSelection.Item(ComponentKind.Fabric) Is Nothing AndAlso SelectedVersionText.Text = other.Id,
                            InstanceName.Text = "保留重试方案" AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso
                            If(scenario = 0, accepted IsNot Nothing AndAlso accepted.Single() Is replacement AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso InstallButton.IsEnabled,
                                accepted Is Nothing AndAlso _componentErrors.ContainsKey(ComponentKind.Fabric) AndAlso Not InstallButton.IsEnabled)),
                            "late component responses preserve current metadata or retry feedback without stranding pending state" & label)
                    End Using
                    If scenario = 2 Then Await RefreshComponentListsAsync(ComponentKind.Fabric)
                    settle()
                    check(_componentRequests.Count = 0 AndAlso _componentPending.Count = 0 AndAlso Not _componentErrors.ContainsKey(ComponentKind.Fabric) AndAlso InstallButton.IsEnabled AndAlso
                        PageSurface.IsHitTestVisible AndAlso _taskGeneration = generation AndAlso Not _busy AndAlso
                        If(scenario = 1, _selectedEntry Is other AndAlso neoToken.IsCancellationRequested, _componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso Not neoToken.IsCancellationRequested),
                        "successful retry or version handoff restores installation and input while respecting parallel component ownership" & label)
                Next
            Next
        Next
    End Function
End Class
