Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackRecoveryAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ChangePackMode(PackBrowserMode.Search) : layout()
        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "held"
        Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim entered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Async Function(uri, token)
                                                                                                                          entered.TrySetResult(True)
                                                                                                                          Await gate.Task
                                                                                                                          token.ThrowIfCancellationRequested()
                                                                                                                      End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await entered.Task
        Dim request = _packSearchRequest, session = _packSearchSession
        PackSearchGame.Text = New String("x"c, 101) : RunPackSearch(Nothing, Nothing)
        Dim preserved = _packSearchRequest Is request AndAlso Not request.IsCancellationRequested AndAlso _packSearchSession Is session
        gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(preserved, "invalid replacement criteria do not cancel the accepted in-flight search")
        check(PackProjectRows.Children.Count = 40 AndAlso TypeOf PackProjectRows.Children(0) Is PclResourceItem, "the accepted search still replaces its loading view after an invalid submission")
        check(PackSearchInputHint.IsVisible AndAlso Not PackSearchHint.IsVisible, "form validation survives the accepted result without overwriting its platform status")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-invalid-query.png"))

        Dim duplicateGate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim duplicateEntered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim requestCount = 0
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Async Function(uri, token)
                                                                                                                          Interlocked.Increment(requestCount)
                                                                                                                          duplicateEntered.TrySetResult(True)
                                                                                                                          Await duplicateGate.Task
                                                                                                                          token.ThrowIfCancellationRequested()
                                                                                                                      End Function}, "")
        PackSearchGame.Text = "" : PackSearchText.Text = "deduplicate" : RunPackSearch(Nothing, Nothing) : Await duplicateEntered.Task
        request = _packSearchRequest : session = _packSearchSession
        RunPackSearch(Nothing, Nothing)
        Dim sharedRequest = _packSearchRequest Is request AndAlso Not request.IsCancellationRequested
        duplicateGate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(sharedRequest AndAlso requestCount = 1 AndAlso _packSearchSession Is session, "repeated identical searches share one accepted request and one source fetch")
        check(Not PackSearchInputHint.IsVisible, "valid submission clears the form error without a new platform error")
        Dim cacheTime = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(20)) : _packLastSearch = cacheTime
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession Is session AndAlso _packLastSearch = cacheTime AndAlso requestCount = 1, "using a cached search does not extend its sixty-second freshness window")
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        _packLastSearch = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(61))
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession IsNot session, "expired search data is fetched into a new session")

        PackSearchGame.Text = "1.20.1|part" : PackSearchText.Text = "tail" : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing)
        session = _packSearchSession
        PackSearchGame.Text = "1.20.1" : PackSearchText.Text = "part|tail" : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession IsNot session AndAlso _packSearchSession.Query.Minecraft = "1.20.1" AndAlso _packSearchSession.Query.Text = "part|tail", "literal separators in different query fields cannot alias one cached search")

        ' An argument failure is reachable at the catalogue's bounded page limit.
        Await LoadPackSearchPageAsync(501) : layout()
        check(PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch AndAlso PackProjectRows.Children.OfType(Of TextBlock)().Any() AndAlso PackSearchHint.Theme = PclHintTheme.Red AndAlso PackSearchHint.IsVisible, "a failed page request restores a readable full-width error state instead of a blank centered loading card")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-request-error.png"))
        _packCatalogFactory = Function() FailingPackCatalog(New TaskCanceledException("fixture current search timeout"))
        Await LoadPackSearchPageAsync(0) : layout()
        check(_packSearchRequest Is Nothing AndAlso PackSearchHint.IsVisible AndAlso PackProjectRows.Children.OfType(Of PclLoadingView)().Count() = 0 AndAlso PackSearchHint.ToolTip.ToString().Contains("fixture current search timeout"), "an unexpected current search cancellation reaches an error state instead of leaving its loading animation")
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(PackProjectRows.Children.Count = 40 AndAlso Not PackSearchHint.IsVisible AndAlso PackNextPage.IsEnabled, "search retry recovers its results and paging from a terminal request error")

        Dim project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
        _packCatalogFactory = Function() FailingPackCatalog(New TaskCanceledException("fixture current detail timeout"))
        OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(PackDetailHint.IsVisible AndAlso PackDetailRetry.IsVisible AndAlso PackVersionCards.Children.Count = 0 AndAlso PackDetailHint.ToolTip.ToString().Contains("fixture current detail timeout"), "an unexpected current detail cancellation exposes a retry action and retires its loading view")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-detail-error.png"))
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(Not PackDetailHint.IsVisible AndAlso Not PackDetailRetry.IsVisible AndAlso PackDetailHint.ToolTip Is Nothing AndAlso _packReleases.Count = 2, "detail retry replaces the error with usable versions and clears its obsolete diagnostics")

        Dim card = DirectCast(PackVersionCards.Children(0), PclCard), rows = DirectCast(card.Body, StackPanel), row = DirectCast(rows.Children(0), PclListItem)
        Dim failedToken As CancellationToken, retryToken As CancellationToken, chosenName As String = Nothing, chosenRoot As String = Nothing, chosenRelease As String = Nothing
        Dim generation = _taskGeneration
        _onlineInstallRunner = Function(release, root, name, progress, token)
                                   failedToken = token
                                   Return Task.FromException(Of InstallResult)(New IOException("fixture archive transfer failed"))
                               End Function
        row.Activate() : OnlinePackName.Text = "Recovery-Instance" : ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _taskGeneration > generation AndAlso Not _busy AndAlso Not _choosingOnline) : layout()
        check(_taskOutcome = TaskOutcome.Failed AndAlso _cancellation Is Nothing AndAlso _onlinePrompt Is Nothing AndAlso StatusText.Text.Contains("fixture archive transfer failed"), "failed archive acquisition releases task ownership and reports failure rather than success or cancellation")
        ResumeInstallation(Nothing, Nothing) : layout()
        check(PackVersionCards.Children(0) Is card AndAlso card.Body Is rows AndAlso GameFolderCard.IsEnabled, "returning from a failed task retains its project, expanded versions and editable destination")
        _onlineInstallRunner = Function(release, root, name, progress, token)
                                   retryToken = token : chosenName = name : chosenRoot = root : chosenRelease = release.Id
                                   Return Task.FromResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions", name), .FileCount = 1})
                               End Function
        generation = _taskGeneration
        row.Activate() : layout()
        check(OnlinePackName.Text = "Recovery-Instance", "retry after acquisition failure retains the previous custom name")
        ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _taskGeneration > generation AndAlso Not _busy AndAlso Not _choosingOnline) : layout()
        check(_taskOutcome = TaskOutcome.Succeeded AndAlso Not retryToken.IsCancellationRequested AndAlso retryToken <> failedToken AndAlso chosenName = "Recovery-Instance" AndAlso chosenRoot = _selectedFolder.Location AndAlso chosenRelease = DirectCast(row.Tag, ModpackRelease).Id, "retry owns a fresh cancellation token while preserving the chosen release, name and destination")
        ResumeInstallation(Nothing, Nothing) : layout()

        ChangePackMode(PackBrowserMode.Search) : layout() : _motionPreview = True : _packResultMotion.PauseForChecks = True
        Await LoadPackSearchPageAsync(501) : layout()
        Dim obsoleteError = _packResultMotion.Transition
        check(obsoleteError IsNot Nothing AndAlso Not PackSearchResultsHost.IsHitTestVisible AndAlso PackSearchText.IsHitTestVisible, "request-error transitions retain PCL result animation ownership while keeping search inputs usable")
        _surfaceMotion.PauseForChecks = True
        ShowSettings(Nothing, Nothing) : layout()
        obsoleteError.Controller.SkipToFill() : layout()
        check(SettingsPage.IsVisible AndAlso PackSearchGhost.Children.Count = 0 AndAlso _packResultMotion.Transition Is Nothing, "an obsolete error transition cannot paint over settings or retain its input gate")
        _surfaceMotion.Finish() : ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        _packResultMotion.PauseForChecks = False : _surfaceMotion.PauseForChecks = False : _motionPreview = False
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(PackSearchSurface.IsVisible AndAlso PackSearchResultsHost.IsHitTestVisible AndAlso Not PackSearchHint.IsVisible, "returning from settings can recover an error without leftover animation gates")
    End Function
    Private Shared Function FailingPackCatalog(failure As Exception) As ModpackCatalogClient
        Throw failure
    End Function
End Class
