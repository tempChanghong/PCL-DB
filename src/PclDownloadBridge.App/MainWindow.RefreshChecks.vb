Imports System.Windows
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports System.Threading
Imports System.Windows.Threading
Imports System.ComponentModel

Partial Public Class MainWindow
    Private Sub VerifyRefreshRebinding(check As Action(Of Boolean, String))
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        ClearComponent(ComponentKind.FabricApi)
        InstanceName.Text = "保留我的安装方案"
        Dim original = _componentSelection.Item(ComponentKind.Fabric)
        Dim refreshed As New ComponentEntry With {.Kind = original.Kind, .Minecraft = original.Minecraft, .Version = original.Version, .Url = "https://example.org/refreshed-metadata", .Data = New JObject()}
        AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {refreshed})
        check(_componentSelection.Item(ComponentKind.Fabric) Is refreshed, "refresh rebinds the chosen component version to newly validated catalogue metadata")
        check(InstanceName.Text = "保留我的安装方案" AndAlso Not _componentSelection.Has(ComponentKind.FabricApi), "catalogue refresh preserves a custom name and an explicitly cleared dependency")
        check(RefreshButton.Visibility = Visibility.Visible, "install options expose a refresh action for component catalogues")
    End Sub

    Private Sub VerifyRefreshWorkflow(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim previous = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
        Try
            Dim work = VerifyRefreshWorkflowAsync(check, artifactDirectory)
            If Not work.IsCompleted Then
                Dim frame As New DispatcherFrame()
                Dim timer As New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(10)}
                AddHandler timer.Tick, Sub() frame.Continue = False
                Dim completion = work.ContinueWith(Sub(finished)
                    Dispatcher.BeginInvoke(New Action(Sub() frame.Continue = False))
                End Sub, TaskScheduler.Default)
                timer.Start()
                Dispatcher.PushFrame(frame)
                timer.Stop()
            End If
            If Not work.IsCompleted Then Throw New TimeoutException("Refresh workflow fixture exceeded its deadline.")
            work.GetAwaiter().GetResult()
        Finally
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Shared Function ListGate(Of T)() As TaskCompletionSource(Of List(Of T))
        Return New TaskCompletionSource(Of List(Of T))(TaskCreationOptions.RunContinuationsAsynchronously)
    End Function

    Private Async Function VerifyRefreshWorkflowAsync(check As Action(Of Boolean, String), artifactDirectory As String) As Task
        Dim originalVersions = _versions
        Try
            SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            ClearComponent(ComponentKind.FabricApi)
            InstanceName.Text = "跨来源保留的名称"
            Dim selected = _componentSelection.Item(ComponentKind.Fabric)
            Dim seed = _componentEntries.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value.ToList())
            Dim oldGate = ListGate(Of ComponentEntry)(), newGate = ListGate(Of ComponentEntry)()
            Dim sources As New List(Of SourcePreference), tokens As New List(Of CancellationToken)
            _componentProvider = Function(sourcePolicy, kind, mc, token)
                If kind <> ComponentKind.Fabric Then Return Task.FromResult(seed(kind))
                sources.Add(sourcePolicy.EffectiveVersionPreference) : tokens.Add(token)
                Return If(sources.Count = 1, oldGate.Task, newGate.Task)
            End Function
            Dim originalLoad = RefreshComponentListsAsync()
            check(_componentPending.Contains(ComponentKind.Fabric) AndAlso Not _componentEntries.ContainsKey(ComponentKind.Fabric) AndAlso Not InstallButton.IsEnabled, "refresh hides stale component rows and waits for selected component metadata")
            VersionSourceBox.SelectedIndex = 0
            Dim newLoad = RefreshCurrentListsIfNeededAsync()
            check(tokens(0).IsCancellationRequested AndAlso sources.Count = 2 AndAlso sources(1) = SourcePreference.MirrorFirst, "switching list source cancels the old request and refreshes with the chosen policy")
            Dim replacement As New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = selected.Minecraft, .Version = selected.Version, .Url = "https://example.org/current", .Data = New JObject()}
            newGate.SetResult(New List(Of ComponentEntry) From {replacement})
            Await newLoad
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso InstanceName.Text = "跨来源保留的名称" AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso InstallButton.IsEnabled, "source refresh preserves the chosen version, custom name and cleared API while rebinding metadata")
            oldGate.SetResult(New List(Of ComponentEntry) From {selected}) ' Deliberately ignore cancellation.
            Await originalLoad
            check(_componentEntries(ComponentKind.Fabric).Single() Is replacement AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso _componentRequests.Count = 0, "a late response from the old source cannot overwrite or leave a live request")

            Dim neoGate = ListGate(Of ComponentEntry)(), fabricGate = ListGate(Of ComponentEntry)(), retryGate = ListGate(Of ComponentEntry)()
            Dim fabricCalls = 0
            Dim fabricToken As CancellationToken, neoToken As CancellationToken
            _componentProvider = Function(sourcePolicy, kind, mc, token)
                If kind = ComponentKind.NeoForge Then
                    neoToken = token : Return neoGate.Task
                End If
                fabricCalls += 1
                If fabricCalls = 1 Then fabricToken = token
                Return If(fabricCalls = 1, fabricGate.Task, retryGate.Task)
            End Function
            Dim neoLoad = RefreshComponentListsAsync(ComponentKind.NeoForge)
            check(InstallButton.IsEnabled, "an unselected component refresh does not block a valid installation plan")
            Dim fabricLoad = RefreshComponentListsAsync(ComponentKind.Fabric)
            Dim retryLoad = RefreshComponentListsAsync(ComponentKind.Fabric)
            check(fabricToken.IsCancellationRequested AndAlso Not neoToken.IsCancellationRequested, "single-component retry only cancels its own previous request")
            retryGate.SetResult(New List(Of ComponentEntry) From {replacement})
            Await retryLoad
            fabricGate.SetException(New IO.IOException("obsolete source failure"))
            Await fabricLoad
            check(Not _componentErrors.ContainsKey(ComponentKind.Fabric) AndAlso _componentPending.Contains(ComponentKind.NeoForge), "obsolete retry errors cannot replace success or stop a different component loader")
            neoGate.SetResult(seed(ComponentKind.NeoForge))
            Await neoLoad
            check(_componentRequests.Count = 0 AndAlso _componentPending.Count = 0, "all retried component requests finish and release their owned cancellation sources")

            _componentProvider = Function(sourcePolicy, kind, mc, token) Task.FromException(Of List(Of ComponentEntry))(New IO.IOException("current source failure"))
            Await RefreshComponentListsAsync(ComponentKind.Fabric)
            check(_componentErrors.ContainsKey(ComponentKind.Fabric) AndAlso Not _componentEntries.ContainsKey(ComponentKind.Fabric) AndAlso Not InstallButton.IsEnabled AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement, "failed selected component refresh retains the choice but cannot silently install stale metadata")
            Dim alternative As New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = selected.Minecraft, .Version = "0.17.0", .Data = New JObject()}
            _componentProvider = Function(sourcePolicy, kind, mc, token) Task.FromResult(New List(Of ComponentEntry) From {alternative})
            Await RefreshComponentListsAsync(ComponentKind.Fabric)
            check(_unresolvedComponents.Contains(ComponentKind.Fabric) AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso ComponentHint.Text.Contains("未提供") AndAlso Not InstallButton.IsEnabled, "a source missing the selected version reports it without silently upgrading")
            SelectComponent(alternative)
            check(Not _unresolvedComponents.Contains(ComponentKind.Fabric) AndAlso InstallButton.IsEnabled AndAlso InstanceName.Text = "跨来源保留的名称", "explicitly choosing an available replacement resolves the refresh warning and preserves the custom name")
            _suppressedDependencies.Remove(ComponentKind.FabricApi)
            _componentEntries(ComponentKind.FabricApi) = seed(ComponentKind.FabricApi)
            ApplyAutoDependencies()
            _unresolvedComponents.Add(ComponentKind.FabricApi)
            ClearComponent(ComponentKind.Fabric)
            SelectComponent(alternative)
            check(_componentSelection.Has(ComponentKind.FabricApi) AndAlso Not _unresolvedComponents.Contains(ComponentKind.FabricApi) AndAlso InstallButton.IsEnabled, "clearing and reselecting a loader cannot retain a stale dependency warning")
            _busy = True
            Try
                Await RefreshComponentListsAsync()
                check(_componentRequests.Count = 0 AndAlso _componentSelection.Item(ComponentKind.Fabric) Is alternative, "refresh during installation cannot change the frozen selection")
            Finally
                _busy = False
            End Try
            _versionsDirty = False
            Dim versionGeneration = _versionGeneration, componentGeneration = _componentGeneration
            SourceBox.SelectedIndex = If(SourceBox.SelectedIndex = 0, 2, 0)
            check(_versionGeneration = versionGeneration AndAlso _componentGeneration = componentGeneration AndAlso Not _versionsDirty AndAlso Not _componentsDirty, "changing file source leaves independent version catalogues intact")

            SetDownloadView(DownloadView.Versions)
            Dim oldVersions = ListGate(Of VersionEntry)(), newVersions = ListGate(Of VersionEntry)()
            Dim versionCalls = 0
            Dim firstToken As CancellationToken
            _versionProvider = Function(sourcePolicy, token)
                versionCalls += 1
                If versionCalls = 1 Then firstToken = token
                Return If(versionCalls = 1, oldVersions.Task, newVersions.Task)
            End Function
            Dim firstVersionLoad = LoadVersionsAsync(True)
            VersionSourceBox.SelectedIndex = 2
            Dim currentVersionLoad = RefreshCurrentListsIfNeededAsync()
            Dim freshVersions = originalVersions.ToList()
            freshVersions.Insert(0, New VersionEntry With {.Id = "fixture-new-source", .Kind = "release", .Released = DateTimeOffset.Now})
            newVersions.SetResult(freshVersions)
            Await currentVersionLoad
            oldVersions.SetResult(originalVersions)
            Await firstVersionLoad
            check(firstToken.IsCancellationRequested AndAlso _versions Is freshVersions AndAlso Not _loading AndAlso Not _versionsDirty AndAlso Await VerifySupplementalRefreshAsync(artifactDirectory), "new-source manifests and optional supplements preserve normal rows and reject stale busy or closing results")

            Dim olderManifest = ListGate(Of VersionEntry)()
            _versionProvider = Function(sourcePolicy, token) olderManifest.Task
            Dim olderLoad = LoadVersionsAsync(True)
            _versionProvider = Function(sourcePolicy, token) Task.FromException(Of List(Of VersionEntry))(New IO.IOException("manifest refresh failed"))
            Await LoadVersionsAsync(True)
            olderManifest.SetResult(originalVersions)
            Await olderLoad
            check(_versions Is freshVersions AndAlso _versionsDirty AndAlso EmptyVersions.Visibility = Visibility.Collapsed AndAlso StatusText.Text.Contains("上次"), "failed latest manifest refresh keeps the visible cache and rejects an older successful response")

            Dim busyManifest = ListGate(Of VersionEntry)()
            _versionProvider = Function(sourcePolicy, token) busyManifest.Task
            Dim busyLoad = LoadVersionsAsync(True)
            _busy = True : StatusText.Text = "正在安装测试实例"
            busyManifest.SetResult(originalVersions)
            Await busyLoad
            _busy = False
            check(_versions Is freshVersions AndAlso StatusText.Text = "正在安装测试实例" AndAlso _versionsDirty, "metadata finishing during installation cannot replace its progress status or cached version list")

            Dim closingGate = ListGate(Of VersionEntry)()
            Dim closingToken As CancellationToken
            _versionProvider = Function(sourcePolicy, token)
                closingToken = token : Return closingGate.Task
            End Function
            Dim closingLoad = LoadVersionsAsync(True)
            WindowClosing(Me, New CancelEventArgs())
            closingGate.SetResult(originalVersions)
            Await closingLoad
            check(closingToken.IsCancellationRequested AndAlso _versions Is freshVersions AndAlso _versionCancellation Is Nothing, "closing cancels metadata work and ignores a late manifest result")
        Finally
            _closing = False
            _componentProvider = Nothing : _versionProvider = Nothing
            CancelComponentRequests()
            _versions = originalVersions : _versionsDirty = False
            _restoringSettings = True
            RestoreSourcePreferences(New JObject())
            _restoringSettings = False
            SelectVersion(originalVersions(0))
            UpdateVersions()
        End Try
    End Function
End Class
