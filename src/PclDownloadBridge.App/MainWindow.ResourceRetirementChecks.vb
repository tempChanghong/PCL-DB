Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceRetirementUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "resource-retirement.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceRetirementAsync(directory, layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Resource retirement checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceRetirementAsync(directory As String, layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For operation = 0 To 2
            For launcher = 0 To If(operation = 2, 2, 0)
                For Each origin In {0, 4}
                    For scenario = 0 To 4
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packResultMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False
                        CancelResourceRequests() : _resourceBrowsers.Clear() : ShowDownload(Nothing, Nothing)
                        Dim initialKind = If(operation = 1, CommunityResourceKind.ResourcePack, CommunityResourceKind.Mod)
                        SelectResourceKind(initialKind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        PackSearchText.Text = "original" : PackSearchSource.SelectedIndex = 2 : PackSearchCategory.SelectedIndex = 0
                        PackSearchGame.Text = "全部 (也可自行输入)" : ResourceLoader.SelectedIndex = 0
                        _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packLastQuery = PackQueryKey(CurrentPackQuery()) : _packLastSearch = DateTime.UtcNow
                        Dim old As New SearchStartState(initialKind, "Original", 40)
                        old.Gate.SetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(old), "", 0)
                        Await LoadPackSearchPageAsync(0)
                        Dim project As New ModpackProject With {.Kind = initialKind, .Platform = ModpackPlatform.Modrinth, .Id = "retired", .Name = "Retired detail"}
                        PrepareResourceBackDetail(project) : _resourceBack.Clear() : settle()
                        Dim files As New Dictionary(Of String, Byte()), instance As InstalledInstance = Nothing
                        If operation = 2 Then
                            Dim folders = PrepareManagementFixture(Path.Combine(directory, "native-" & launcher & "-" & origin & "-" & scenario))
                            FolderList.SelectedItem = folders(launcher)
                            _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                            ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                            SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
                            ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning AndAlso _localModReadSucceeded) : settle()
                            instance = _localModInstance : files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(instance.Target.Root))
                        End If
                        CancelResourceRequests() : _resourceBrowsers.Clear() : settle()
                        Dim sources = Enumerable.Range(0, 6).Select(Function(index) New CancellationTokenSource()).ToArray(), tokens = sources.Select(Function(source) source.Token).ToArray()
                        _resourceNamesRequest?.Dispose()
                        _dependencyRequest = sources(0) : _resourceNamesRequest = sources(1) : _packSearchRequest = sources(2) : _packDetailRequest = sources(3)
                        _packArtworkRequest = sources(4) : _packDetailArtworkRequest = sources(5)
                        _packDetailArtworkRow = PackProjectIdentity.Children.OfType(Of PclResourceItem)().FirstOrDefault()
                        RestoreAppearance(New JObject From {{"darkMode", scenario Mod 2 = 0}})
                        Dim animated = (scenario \ 2 + launcher + operation) Mod 2 = 0, chosenCase = scenario, selectedOperation = operation, taskGeneration = _taskGeneration
                        _motionPreview = animated : _surfaceMotion.PauseForChecks = animated : _categoryMotion.PauseForChecks = animated : _packResultMotion.PauseForChecks = animated : _packDetailMotion.PauseForChecks = animated
                        If animated AndAlso DownloadPage.IsVisible Then ChangePackContent(PackBrowserMode.Detail, Sub() RenderPackVersions()) : layout()
                        Dim retiredCompletions = 0
                        Dim completed As EventHandler = Sub() retiredCompletions += 1
                        AddHandler _packDetailMotion.Settled, completed
                        Dim latest As SearchStartState = Nothing, detail As DetailStartState = Nothing, other As ModpackProject = Nothing
                        Dim live As CancellationTokenSource = Nothing, liveToken As CancellationToken, artworkToken As CancellationToken
                        Dim expectedKind = If(operation = 1, CommunityResourceKind.Shader, initialKind), expectedMode = If(operation = 0, PackBrowserMode.Detail, PackBrowserMode.Search)
                        Dim expectedProject = If(operation = 1, Nothing, project), expectedSession = If(operation = 0, _packSearchSession, Nothing)
                        Dim expectedSurface As FrameworkElement = DownloadPage, expectedNames As CancellationTokenSource = Nothing
                        Dim categoryClock As ClockGroup = Nothing, contentClock As ClockGroup = Nothing, surfaceClock As ClockGroup = Nothing
                        Dim label = "/operation=" & operation & "/launcher=" & launcher & "/origin=" & origin & "/case=" & scenario
                        Dim escaped = False
                        failures.Clear()
                        Try
                            Using registration = tokens(origin).Register(Sub()
                                                                             If chosenCase = 4 Then Throw New IOException("Controlled retirement notification failure")
                                                                             If chosenCase < 2 Then settle()
                                                                             Select Case chosenCase
                                                                                 Case 0
                                                                                     latest = New SearchStartState(_resourceKind, "Latest", 40)
                                                                                     _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latest), "", 0)
                                                                                     PackSearchText.Text = "latest query" : RunPackSearch(Nothing, Nothing)
                                                                                     live = _packSearchRequest
                                                                                     If live IsNot Nothing Then liveToken = live.Token : artworkToken = _packArtworkRequest.Token
                                                                                 Case 1
                                                                                     detail = New DetailStartState(_resourceKind, "Latest detail", "latest01")
                                                                                     other = New ModpackProject With {.Kind = _resourceKind, .Platform = ModpackPlatform.Modrinth, .Id = "latest", .Name = "Latest detail"}
                                                                                     _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(detail), "", 0)
                                                                                     OpenPackDetails(other) : live = _packDetailRequest
                                                                                     If live IsNot Nothing Then liveToken = live.Token
                                                                                 Case 2 : ShowSettings(Nothing, Nothing)
                                                                                 Case 3 : SelectResourceKind(CommunityResourceKind.Modpack)
                                                                             End Select
                                                                             expectedKind = _resourceKind : expectedMode = _packMode : expectedProject = _packProject : expectedSession = _packSearchSession
                                                                             expectedSurface = If(selectedOperation = 2 AndAlso chosenCase < 2, DirectCast(DownloadPage, FrameworkElement), ActiveSurface())
                                                                             expectedNames = _resourceNamesRequest
                                                                             categoryClock = _categoryMotion.Transition : contentClock = If(_packMode = PackBrowserMode.Search, _packResultMotion.Transition, _packDetailMotion.Transition) : surfaceClock = _surfaceMotion.Transition
                                                                         End Sub)
                                Try
                                    Select Case operation
                                        Case 0 : CancelResourceRequests()
                                        Case 1 : SelectResourceKind(CommunityResourceKind.Shader)
                                        Case 2 : DownloadNewLocalMod(Nothing, Nothing)
                                    End Select
                                Catch
                                    escaped = True
                                End Try
                                layout()
                                Dim feedback = If(_packMode = PackBrowserMode.Detail, PackProjectActionStatus.Text, PackSearchInputHint.Text)
                                check(escaped = (chosenCase = 4 AndAlso operation = 0) AndAlso failures.Count = 0 AndAlso retiredCompletions = 0 AndAlso _resourceKind = expectedKind AndAlso _packMode = expectedMode AndAlso
                                    _packProject Is expectedProject AndAlso _packSearchSession Is expectedSession AndAlso ActiveSurface() Is expectedSurface AndAlso
                                    If(chosenCase = 4, operation = 0 OrElse feedback.Contains("重试"),
                                       If(operation = 2 AndAlso chosenCase < 2, True, _categoryMotion.Transition Is categoryClock AndAlso _surfaceMotion.Transition Is surfaceClock AndAlso
                                          If(_packMode = PackBrowserMode.Search, _packResultMotion.Transition, _packDetailMotion.Transition) Is contentClock) AndAlso
                                       If(chosenCase < 2, live IsNot Nothing AndAlso Not liveToken.IsCancellationRequested AndAlso (chosenCase <> 0 OrElse Not artworkToken.IsCancellationRequested), _resourceNamesRequest Is expectedNames)),
                                    "retiring shared requests preserves newer readers, navigation and clocks; UI failures remain retryable" & label)
                                RemoveHandler _packDetailMotion.Settled, completed
                                latest?.Gate.TrySetResult(True) : detail?.Gate.TrySetResult(True)
                                If live IsNot Nothing Then Await WaitPackUiAsync(Function() liveToken.IsCancellationRequested OrElse If(chosenCase = 0, _packSearchRequest IsNot live, _packDetailRequest IsNot live))
                                Await Task.Yield() : settle()
                                Dim published = If(chosenCase = 0, PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
                                    PackProjectRows.Children.OfType(Of PclResourceItem)().All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("Latest", StringComparison.Ordinal)),
                                    chosenCase <> 1 OrElse (_packDetailCompletedProject Is other AndAlso _packReleases.Count = 1 AndAlso _packReleases(0).Id = detail.VersionId))
                                check(tokens.All(Function(token) token.IsCancellationRequested) AndAlso failures.Count = 0 AndAlso _resourceKind = expectedKind AndAlso _packMode = expectedMode AndAlso
                                    _packProject Is expectedProject AndAlso _packSearchSession Is expectedSession AndAlso ActiveSurface() Is expectedSurface AndAlso published,
                                    "every captured old source is notified even after a fault, and late work cannot erase accepted results or context" & label)
                            End Using
                        Finally
                            RemoveHandler _packDetailMotion.Settled, completed
                            If _dependencyRequest Is sources(0) Then _dependencyRequest = Nothing
                            If _resourceNamesRequest Is sources(1) Then _resourceNamesRequest = Nothing
                            If _packSearchRequest Is sources(2) Then _packSearchRequest = Nothing
                            If _packDetailRequest Is sources(3) Then _packDetailRequest = Nothing
                            If _packArtworkRequest Is sources(4) Then _packArtworkRequest = Nothing
                            If _packDetailArtworkRequest Is sources(5) Then _packDetailArtworkRequest = Nothing : _packDetailArtworkRow = Nothing
                            For Each source In sources : CancelSubmittedSearchRequest(source) : source.Dispose() : Next
                        End Try
                        _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packResultMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False
                        ShowDownload(Nothing, Nothing) : ChangePackMode(PackBrowserMode.Search) : settle()
                        Dim retryKind = _resourceKind, recovered As New SearchStartState(_resourceKind, "Recovered", 40)
                        recovered.Gate.SetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(recovered), "", 0)
                        PackSearchText.Text = "retry " & label : RunPackSearch(Nothing, Nothing)
                        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
                        Dim saved = _packSearchSession
                        SelectResourceKind(If(retryKind = CommunityResourceKind.Shader, CommunityResourceKind.Modpack, CommunityResourceKind.Shader)) : settle()
                        SelectResourceKind(retryKind) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
                        check(_resourceKind = retryKind AndAlso _packSearchSession Is saved AndAlso _packMode = PackBrowserMode.Search AndAlso PackSearchText.Text = "retry " & label AndAlso
                            PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
                            PackSearchResultsHost.IsHitTestVisible AndAlso _taskGeneration = taskGeneration AndAlso files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "retry and category round trips retain cached results, native configuration bytes and settled input without starting a download" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
