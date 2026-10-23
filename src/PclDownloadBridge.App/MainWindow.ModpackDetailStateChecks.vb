Imports System.IO
Imports System.Threading
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Async Function VerifyPackDetailCompletionAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ChangePackMode(PackBrowserMode.Search) : layout()
        Dim gate = PackCheckGate(), entered = PackCheckGate(), projectsFetched = 0, versionsFetched = 0
        Dim cancellationGate = PackCheckGate(), cancellationEntered = PackCheckGate(), oldGate = PackCheckGate(), oldEntered = PackCheckGate()
        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "packempty", .Name = "空结果状态回归"}
        Dim generation = _taskGeneration, search = _packSearchSession, firstRow = PackProjectRows.Children(0), installCancellation = _cancellation
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {
            .VersionPage = Function(uri) New JArray(),
            .BeforeSendAsync = Async Function(uri, token)
                                   If uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then
                                       Interlocked.Increment(versionsFetched) : entered.TrySetResult(True)
                                       Await gate.Task : token.ThrowIfCancellationRequested()
                                   Else
                                       Interlocked.Increment(projectsFetched)
                                   End If
                               End Function}, "")
        Dim emptyFactory = _packCatalogFactory
        Try
            OpenPackDetails(project) : Await entered.Task : layout()
            Dim request = _packDetailRequest, loading = PackVersionCards.Children(0)
            BackToPackSearch(Nothing, Nothing) : layout() : OpenPackDetails(project) : layout()
            Dim retainedRequest = _packDetailRequest Is request AndAlso PackVersionCards.Children(0) Is loading AndAlso projectsFetched = 1 AndAlso versionsFetched = 1
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(retainedRequest, "returning before an empty detail response completes retains the accepted in-flight request and loading card")
            check(_packDetailCompletedProject Is project AndAlso _packReleases.Count = 0 AndAlso PackVersionCards.Children.Count = 0 AndAlso PackDetailHint.IsVisible AndAlso
                PackDetailHint.Theme = PclHintTheme.Blue AndAlso Not PackDetailRetry.IsVisible,
                "a successful empty version response is rendered as a completed neutral result rather than a failure")
            Dim intro = PackProjectIdentity.Children(0)
            BackToPackSearch(Nothing, Nothing) : layout() : OpenPackDetails(project)
            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            File.WriteAllText(Path.Combine(directory, "pack-empty-detail-state.json"), New JObject From {
                {"ProjectRequests", projectsFetched}, {"VersionRequests", versionsFetched}, {"TaskGeneration", _taskGeneration},
                {"VersionCount", _packReleases.Count}, {"Hint", PackDetailHint.Theme.ToString()}}.ToString())
            check(projectsFetched = 1 AndAlso versionsFetched = 1 AndAlso PackProjectIdentity.Children(0) Is intro AndAlso
                PackVersionCards.Children.Count = 0 AndAlso _packSearchSession Is search AndAlso PackProjectRows.Children(0) Is firstRow AndAlso _taskGeneration = generation,
                "reopening a successfully completed empty detail uses its cache without another request or loading flash")
            SaveControlVisual(host, Path.Combine(directory, "pack-empty-completed.png"))

            _packCatalogFactory = Function() FailingPackCatalog(New IOException("fixture detail refresh failed"))
            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(_packDetailCompletedProject Is Nothing AndAlso PackDetailHint.Theme = PclHintTheme.Red AndAlso PackDetailRetry.IsVisible,
                "a failed explicit refresh invalidates a previously completed empty result instead of caching the error")
            SaveControlVisual(host, Path.Combine(directory, "pack-empty-failed-refresh.png"))
            BackToPackSearch(Nothing, Nothing) : layout() : _packCatalogFactory = emptyFactory
            OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(projectsFetched = 2 AndAlso versionsFetched = 2 AndAlso _packDetailCompletedProject Is project AndAlso
                PackDetailHint.Theme = PclHintTheme.Blue AndAlso Not PackDetailRetry.IsVisible,
                "reopening a failed detail retries its metadata and versions and accepts an empty recovery")
            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(projectsFetched = 3 AndAlso versionsFetched = 3 AndAlso _packDetailCompletedProject Is project,
                "an explicit detail retry still fetches a completed empty result when requested")

            _packCatalogFactory = Function() HeldEmptyPackCatalog(cancellationEntered, cancellationGate)
            Dim cancelledLoad = LoadPackDetailsAsync(project) : Await cancellationEntered.Task : layout()
            check(_packDetailCompletedProject Is Nothing AndAlso _packDetailRequest IsNot Nothing AndAlso PackVersionCards.Children.Count = 1,
                "starting a detail refresh clears completion while retaining one loading surface")
            _packDetailRequest.Cancel() : cancellationGate.TrySetResult(True) : Await cancelledLoad : layout()
            check(_packDetailCompletedProject Is Nothing AndAlso _packDetailRequest Is Nothing AndAlso _taskGeneration = generation AndAlso _cancellation Is installCancellation,
                "a cancelled detail fetch cannot mark its empty output complete or cancel the installation task")
            BackToPackSearch(Nothing, Nothing) : layout() : _packCatalogFactory = emptyFactory
            OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(projectsFetched = 4 AndAlso versionsFetched = 4 AndAlso _packDetailCompletedProject Is project AndAlso PackVersionCards.Children.Count = 0,
                "reopening a cancelled empty detail starts a fresh request and replaces its abandoned loading surface")

            _packCatalogFactory = Function() HeldEmptyPackCatalog(oldEntered, oldGate)
            Dim olderLoad = LoadPackDetailsAsync(project) : Await oldEntered.Task
            Dim olderRequest = _packDetailRequest
            Dim nextProject As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "packemptytwo", .Name = "新项目空结果"}
            BackToPackSearch(Nothing, Nothing) : layout() : _packCatalogFactory = emptyFactory
            OpenPackDetails(nextProject) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(olderRequest.IsCancellationRequested AndAlso _packDetailCompletedProject Is nextProject AndAlso _packProject Is nextProject,
                "changing projects cancels the older detail owner and accepts the new project's empty completion")
            oldGate.TrySetResult(True) : Await olderLoad : layout()
            check(_packDetailCompletedProject Is nextProject AndAlso _packProject Is nextProject AndAlso
                DirectCast(DirectCast(PackProjectIdentity.Children(0), PclResourceItem).Tag, ModpackProject).Key = nextProject.Key AndAlso _taskGeneration = generation,
                "a late cancelled empty response cannot replace the newer completion owner, identity or task")
            Dim fetched = projectsFetched
            BackToPackSearch(Nothing, Nothing) : layout() : OpenPackDetails(nextProject)
            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(projectsFetched = fetched AndAlso _packDetailCompletedProject Is nextProject,
                "the newer empty result remains cached after the obsolete request finishes")
            Await LoadPackDetailsAsync(project) : layout()
            check(projectsFetched = fetched AndAlso _packDetailCompletedProject Is nextProject AndAlso _packDetailRequest Is Nothing,
                "a stale explicit detail load cannot clear or replace the currently accepted project's cache")

            Dim nonemptyFetches = 0
            Dim nonemptyFactory As Func(Of ModpackCatalogClient) = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {
                .BeforeSendAsync = Function(uri, token)
                                       If uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then Interlocked.Increment(nonemptyFetches)
                                       Return Task.CompletedTask
                                   End Function}, "")
            _packCatalogFactory = nonemptyFactory
            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            Dim loadedNonempty = _packReleases.Count = 2 AndAlso _packDetailCompletedProject Is nextProject
            _packCatalogFactory = Function() FailingPackCatalog(New IOException("fixture nonempty refresh failed"))
            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            Dim failedWithOldFiles = _packReleases.Count = 2 AndAlso _packDetailCompletedProject Is Nothing AndAlso PackDetailRetry.IsVisible
            BackToPackSearch(Nothing, Nothing) : layout() : _packCatalogFactory = nonemptyFactory
            OpenPackDetails(nextProject) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(loadedNonempty AndAlso failedWithOldFiles AndAlso nonemptyFetches = 2 AndAlso _packDetailCompletedProject Is nextProject AndAlso
                PackVersionCards.Children.Count = 1 AndAlso Not PackDetailRetry.IsVisible,
                "retained nonempty files cannot suppress recovery after an explicit detail refresh fails")

            BackToPackSearch(Nothing, Nothing) : layout()
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
            ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "detail-state-reset"
            RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packDetailCompletedProject Is Nothing AndAlso _packProject Is Nothing AndAlso _packVerifiedProject Is Nothing,
                "a new accepted search releases the completed detail owner along with its verified project")
        Finally
            gate.TrySetResult(True) : cancellationGate.TrySetResult(True) : oldGate.TrySetResult(True)
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        End Try
    End Function

    Private Shared Function HeldEmptyPackCatalog(entered As TaskCompletionSource(Of Boolean), gate As TaskCompletionSource(Of Boolean)) As ModpackCatalogClient
        Return New ModpackCatalogClient(New PackSearchFixtureHandler With {
            .VersionPage = Function(uri) New JArray(),
            .BeforeSendAsync = Async Function(uri, token)
                                   If Not uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then Return
                                   entered.TrySetResult(True) : Await gate.Task : token.ThrowIfCancellationRequested()
                               End Function}, "")
    End Function
End Class
