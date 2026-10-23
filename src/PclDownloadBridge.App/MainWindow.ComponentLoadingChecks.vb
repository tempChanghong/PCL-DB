Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyComponentLoading(check As Action(Of Boolean, String), directory As String)
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, previous = SynchronizationContext.Current
        Dim appearance = _appearance.ToJson()
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
            owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
            Using source = PreviewSurface.Attach(host, 850, 700)
                Dim work = owner.VerifyComponentLoadingAsync(check, directory, host, ControlLayout(host))
                If Not PumpMotionUntil(Function() work.IsCompleted, 20000) Then Throw New TimeoutException("Component loading checks exceeded their deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._pageSwitch.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(appearance)
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function VerifyComponentLoadingAsync(check As Action(Of Boolean, String), directory As String, host As Border, layout As Action) As Task
        Dim gates As New List(Of TaskCompletionSource(Of List(Of ComponentEntry)))
        Dim createGate As Func(Of TaskCompletionSource(Of List(Of ComponentEntry))) = Function()
                                                                                       Dim value = ListGate(Of ComponentEntry)()
                                                                                       gates.Add(value) : Return value
                                                                                   End Function
        Dim settle As Action = Sub()
                                   _pageSwitch.Finish() : _categoryMotion.Finish() : _surfaceMotion.Finish() : _titleNavigation.Finish() : layout()
                                   For Each view In _componentViews.Values : FinishDeferredCard(view.Card, layout) : Next
                                   layout()
                               End Sub
        Dim press As Action(Of UIElement) = Sub(target)
                                               target.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host), Environment.TickCount, Key.Enter) With {.RoutedEvent = Keyboard.KeyDownEvent})
                                           End Sub
        Try
            SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1")) : settle()
            Dim entry = _selectedEntry, generation = _componentGeneration
            Dim releases = ComponentListSample(ComponentKind.Fabric, entry.Id, 3)
            AcceptComponentList(ComponentKind.Fabric, entry, generation, releases)
            SelectComponent(releases(1)) : ClearComponent(ComponentKind.FabricApi)
            InstanceName.Text = "组件刷新保留名称"
            Dim card = _componentViews(ComponentKind.Fabric).Card, header = card.Children.OfType(Of Grid)().Single()
            card.Collapsed = False : settle()
            Dim oldRows = MaterializeComponentRows(DirectCast(card.Body, StackPanel)), oldRow = oldRows(0)
            Dim gate = createGate(), calls = 0
            _componentProvider = Function(policy, kind, mc, token)
                                     calls += 1 : Return gate.Task
                                 End Function
            Dim work = RefreshComponentListsAsync(ComponentKind.Fabric), folding = card.HeightTransition
            folding?.Controller.Pause() : layout()
            Dim pendingFolded = card.Collapsed AndAlso Not card.Collapsible
            Dim oldBodyRetained = card.HasDeferredBodyRefresh AndAlso oldRow.IsLoaded AndAlso Not oldRow.IsEnabled
            press(header) : layout()
            Dim headerBlocked = card.Collapsed
            Dim replacement = releases.Select(Function(item) New ComponentEntry With {.Kind = item.Kind, .Minecraft = item.Minecraft, .Version = item.Version, .Url = item.Url & "/new"}).ToList()
            gate.SetResult(replacement) : Await work : settle()
            SelectComponent(replacement(1)) : card.Collapsed = False : settle()
            oldRow.Activate()
            Dim staleRowRejected = _componentSelection.Item(ComponentKind.Fabric) Is replacement(1)
            SelectComponent(replacement(1))

            _componentProvider = Function(policy, kind, mc, token) Task.FromException(Of List(Of ComponentEntry))(New IOException("component fixture failure"))
            Await RefreshComponentListsAsync(ComponentKind.Fabric)
            card.Collapsed = False : settle()
            Dim oldRetry = DirectCast(card.Body, StackPanel).Children.OfType(Of PclButton)().Single()
            _componentProvider = Function(policy, kind, mc, token)
                                     calls += 1 : Return Task.FromException(Of List(Of ComponentEntry))(New IOException("component fixture failure"))
                                 End Function
            Await RefreshComponentListsAsync(ComponentKind.Fabric) : settle()
            card.Collapsed = False : settle()
            Dim before = calls
            press(oldRetry) : layout()
            Dim staleRetryRejected = calls = before
            File.WriteAllText(Path.Combine(directory, "component-loading-probes.json"), New JObject From {
                {"pendingFolded", pendingFolded}, {"oldBodyRetainedAndDisabled", oldBodyRetained}, {"headerBlocked", headerBlocked},
                {"staleRowRejected", staleRowRejected}, {"staleRetryRejected", staleRetryRejected}}.ToString())
            check(pendingFolded AndAlso headerBlocked, "pending component catalogues remain folded and reject header activation like PCL PreviewSwap")
            check(oldBodyRetained, "refreshing an expanded component list retains disabled old rows only for its closing animation")
            check(staleRowRejected, "an obsolete component row cannot replace current metadata even while the new catalogue is open")
            check(staleRetryRejected, "a detached retry from an older component error cannot restart the accepted error state")

            Dim retry = DirectCast(card.Body, StackPanel).Children.OfType(Of PclButton)().Single()
            Dim neoGate = createGate(), fabricGate = createGate(), neoToken As CancellationToken
            _componentProvider = Function(policy, kind, mc, token)
                                     calls += 1
                                     If kind = ComponentKind.NeoForge Then neoToken = token : Return neoGate.Task
                                     Return fabricGate.Task
                                 End Function
            Dim neoWork = RefreshComponentListsAsync(ComponentKind.NeoForge)
            before = calls : press(retry) : layout()
            check(calls = before + 1 AndAlso _componentPending.Contains(ComponentKind.Fabric) AndAlso card.Collapsed AndAlso Not card.Collapsible,
                "the current component retry starts exactly one request and closes its error card while waiting")
            check(Not neoToken.IsCancellationRequested AndAlso _componentPending.Contains(ComponentKind.NeoForge), "retrying Fabric leaves a parallel NeoForge request and token untouched")
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(1) AndAlso InstanceName.Text = "组件刷新保留名称" AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso Not InstallButton.IsEnabled,
                "pending selected component retry preserves the chosen version, edited name and explicitly cleared API while blocking installation")
            settle() : before = calls : press(retry) : layout()
            check(calls = before, "a retired retry cannot replace the already running request after its fold finishes")
            SaveControlVisual(host, Path.Combine(directory, "component-loading-light.png"))
            fabricGate.SetResult(replacement)
            Await WaitPackUiAsync(Function() Not _componentRequests.ContainsKey(ComponentKind.Fabric)) : settle()
            check(card.Collapsible AndAlso card.Collapsed AndAlso card.Body Is Nothing AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement(1),
                "successful component retry re-enables expansion without opening or building its accepted rows automatically")
            check(InstallButton.IsEnabled AndAlso _componentPending.Contains(ComponentKind.NeoForge) AndAlso InstanceName.Text = "组件刷新保留名称", "an unrelated pending component does not block a verified selected installation plan")
            card.Collapsed = False : settle()
            Dim currentRow = MaterializeComponentRows(DirectCast(card.Body, StackPanel))(0)
            currentRow.Activate() : settle()
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso card.Collapsed AndAlso Not _componentSelection.Has(ComponentKind.FabricApi),
                "a current component row still applies normal selection and collapse without restoring a suppressed dependency")
            neoGate.SetResult(New List(Of ComponentEntry)()) : Await neoWork : settle()
            check(Not _componentViews(ComponentKind.NeoForge).Card.Collapsible AndAlso _componentViews(ComponentKind.NeoForge).Card.Collapsed AndAlso _componentRequests.Count = 0,
                "an empty component result stays folded and all independent requests release their owners")

            card.Collapsed = False : settle() : currentRow = MaterializeComponentRows(DirectCast(card.Body, StackPanel))(1)
            ShowSettings(Nothing, Nothing) : settle() : currentRow.Activate()
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso SettingsPage.IsVisible, "component callbacks arriving on hidden settings cannot change the installation choice")
            ShowDownload(Nothing, Nothing) : settle()
            VersionSourceBox.SelectedIndex = If(VersionSourceBox.SelectedIndex = 2, 0, 2)
            currentRow.Activate()
            check(_componentsDirty AndAlso Not card.Collapsible AndAlso card.Collapsed AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement(0),
                "invalidating the list source folds stale component catalogues and blocks their old row actions")

            _componentProvider = Function(policy, kind, mc, token) Task.FromResult(If(kind = ComponentKind.Fabric, replacement, New List(Of ComponentEntry)()))
            Await RefreshComponentListsAsync() : settle()
            card.Collapsed = False : settle()
            Dim acceptedRows = MaterializeComponentRows(DirectCast(card.Body, StackPanel)), oldClear = _componentViews(ComponentKind.Fabric).Clear
            SelectVersion(_versions.Single(Function(item) item.Id = "1.20.6")) : settle()
            acceptedRows(1).Activate()
            check(_selectedEntry.Id = "1.20.6" AndAlso Not _componentSelection.Has(ComponentKind.Fabric), "rows belonging to a previous Minecraft selection cannot inject a component into its replacement page")
            SelectVersion(entry) : settle()
            _componentProvider = Function(policy, kind, mc, token) Task.FromResult(If(kind = ComponentKind.Fabric, replacement, New List(Of ComponentEntry)()))
            Await RefreshComponentListsAsync() : SelectComponent(replacement(0)) : settle()
            card = _componentViews(ComponentKind.Fabric).Card : card.Collapsed = False : settle()
            acceptedRows = MaterializeComponentRows(DirectCast(card.Body, StackPanel))
            press(oldClear)
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0), "a clear button from an older card cannot clear a replacement card for the same Minecraft entry")
            _busy = True : acceptedRows(1).Activate()
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0), "component row callbacks cannot change a frozen selection during installation")
            _busy = False : _closing = True : acceptedRows(1).Activate()
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0), "component row callbacks cannot change selection after window closing begins")
            _closing = False
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
            gate = createGate() : _componentProvider = Function(policy, kind, mc, token) gate.Task
            work = RefreshComponentListsAsync(ComponentKind.Fabric) : settle()
            check(_appearance.DarkMode AndAlso Not card.Collapsible AndAlso card.Collapsed AndAlso _componentViews(ComponentKind.Fabric).Summary.Text.Contains("刷新中"),
                "dark-mode pending component headers preserve the selected version and readable refresh status without an empty expanded body")
            SaveControlVisual(host, Path.Combine(directory, "component-loading-dark.png"))
            gate.SetException(New IOException("final component fixture failure")) : Await work : settle()
            check(card.Collapsible AndAlso card.Collapsed AndAlso Not InstallButton.IsEnabled AndAlso _componentSelection.Has(ComponentKind.Fabric),
                "a selected component failure remains folded, preserves its choice and offers explicit retry without installing stale metadata")
            card.Collapsed = False : settle()
            gate = createGate() : press(DirectCast(card.Body, StackPanel).Children.OfType(Of PclButton)().Single()) : settle()
            Dim canceled = _componentRequests(ComponentKind.Fabric).Token
            _componentRequests(ComponentKind.Fabric).Cancel() : gate.SetResult(replacement)
            Await WaitPackUiAsync(Function() Not _componentRequests.ContainsKey(ComponentKind.Fabric)) : settle()
            check(canceled.IsCancellationRequested AndAlso _componentErrors(ComponentKind.Fabric).Contains("超时") AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso card.Collapsed,
                "canceling a selected component request rejects its late data, preserves selection and restores the retry error state")
            card.Collapsed = False : settle()
            gate = createGate() : press(DirectCast(card.Body, StackPanel).Children.OfType(Of PclButton)().Single()) : settle()
            InstanceName.Text = "组件取消后保留名称"
            press(_componentViews(ComponentKind.Fabric).Clear)
            check(Not _componentSelection.Has(ComponentKind.Fabric) AndAlso _componentPending.Contains(ComponentKind.Fabric) AndAlso card.Collapsed AndAlso Not card.Collapsible,
                "the current header clear action remains available while its selected component catalogue is pending")
            gate.SetResult(replacement)
            Await WaitPackUiAsync(Function() Not _componentRequests.ContainsKey(ComponentKind.Fabric)) : settle()
            check(Not _componentSelection.Has(ComponentKind.Fabric) AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso InstallButton.IsEnabled AndAlso InstanceName.Text = "组件取消后保留名称",
                "successful retry after clearing a pending choice cannot restore that component or its suppressed dependency")
        Finally
            _busy = False : _closing = False : _componentProvider = Nothing : CancelComponentRequests()
            For Each gate In gates : gate.TrySetResult(New List(Of ComponentEntry)()) : Next
        End Try
    End Function
End Class
