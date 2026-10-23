Imports System.IO
Imports System.ComponentModel
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackLoadingAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim previousFactory = _packCatalogFactory, generation = _taskGeneration, installation = _cancellation
        Dim gates As New List(Of TaskCompletionSource(Of Boolean)), now As Double = 0
        Dim settle As Action = Sub()
                                   FinishPackContentMotion() : _categoryMotion.Finish() : _surfaceMotion.Finish() : layout()
                               End Sub
        Dim gate = PackCheckGate(), entered = PackCheckGate()
        Try
            _motionPreview = False : ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : ChangePackMode(PackBrowserMode.Search) : settle()
            _packLoadingTimingForChecks = True : _motionPreview = True
            _surfaceMotion.PauseForChecks = True : _categoryMotion.PauseForChecks = True
            _packResultMotion.PauseForChecks = True : _packDetailMotion.PauseForChecks = True
            _packSearchLoading.NowForChecks = Function() now : _packDetailLoading.NowForChecks = Function() now
            _packCatalogFactory = Function() HeldPackCatalog("/search", entered, gate)
            gates.Add(gate) : ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "loader-fast"
            RunPackSearch(Nothing, Nothing) : Await entered.Task : layout()
            check(_packSearchLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso PackProjectRows.Children.Count = 0 AndAlso PackResultsCard.Visibility = Visibility.Collapsed,
                "real search integration clears stale rows while withholding the loading card during LoaderWait")
            now = 399 : _packSearchLoading.Pulse() : layout()
            check(PackProjectRows.Children.Count = 0, "a search completing before the 400 ms threshold has never created a loading pickaxe")
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso PackProjectRows.Children.Count = 40 AndAlso PackResultsCard.IsVisible,
                "fast search results enter their existing content animation without a spinner flash")
            settle()
            Dim project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate)
            _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
            OpenPackDetails(project) : Await entered.Task : layout()
            check(_packDetailLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso PackVersionCards.Children.Count = 0 AndAlso PackProjectIdentity.Children.Count = 1,
                "version LoaderWait leaves the accepted project introduction visible without a premature loading card")
            now += 400 : _packDetailLoading.Pulse() : layout()
            Dim entry = PackEntryMotion(PackBrowserMode.Detail), loader = PackVersionCards.Children(0), request = _packDetailRequest
            check(_packDetailLoading.Phase = PclLoadingPresentation.Phases.Entering AndAlso entry Is _categoryMotion AndAlso _packDetailMotion.Transition Is Nothing,
                "a deferred version loader retargets the current category entry instead of starting a second animation owner")
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailLoading.CompletionPending)
            now += 2000 : _packDetailLoading.Pulse()
            check(_packDetailRequest Is request AndAlso PackVersionCards.Children(0) Is loader AndAlso _packReleases.Count = 0,
                "ready version data cannot replace the loader before its accepted parent entry finishes")
            entry.Finish() : now += 399 : _packDetailLoading.Pulse() : layout()
            check(_packDetailRequest Is request AndAlso _packDetailLoading.Phase = PclLoadingPresentation.Phases.MinimumStay AndAlso PackVersionCards.Children(0) Is loader,
                "version data stays pending through 399 ms of post-entry minimum stay")
            _titleNavigation.Finish() : layout()
            SaveControlVisual(host, Path.Combine(directory, "loader-minimum-stay.png"))
            now += 1 : _packDetailLoading.Pulse() : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(_packDetailCompletedProject Is project AndAlso _packReleases.Count = 2 AndAlso _packDetailLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso
                Not PackDetailResultsHost.IsHitTestVisible AndAlso PackProjectWebsite.IsHitTestVisible,
                "after minimum stay, accepted versions enter under the existing local input gate while project actions remain usable")
            settle()

            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate) : _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
            Dim hiddenLoad = LoadPackDetailsAsync(project) : Await entered.Task
            now += 400 : _packDetailLoading.Pulse() : PackEntryMotion(PackBrowserMode.Detail).Finish()
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailLoading.CompletionPending)
            ShowSettings(Nothing, Nothing) : layout()
            Dim settingsEntry = _surfaceMotion.Transition
            Await hiddenLoad : layout() : now += 10000 : _packDetailLoading.Pulse()
            check(SettingsPage.IsVisible AndAlso _surfaceMotion.Transition Is settingsEntry AndAlso _packDetailCompletedProject Is project AndAlso
                _packDetailLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not _packDetailLoading.TimerArmed,
                "leaving during minimum stay caches ready versions immediately without retargeting settings or reviving a delayed loader")
            ShowDownload(Nothing, Nothing) : settle() : BackToPackSearch(Nothing, Nothing) : settle()
            project = DirectCast(DirectCast(PackProjectRows.Children(1), PclResourceItem).Tag, ModpackProject)
            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate) : _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
            OpenPackDetails(project) : Await entered.Task : request = _packDetailRequest
            ShowSettings(Nothing, Nothing) : ShowDownload(Nothing, Nothing) : layout()
            check(_packDetailRequest Is request AndAlso Not request.IsCancellationRequested AndAlso _packDetailLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso _packDetailMotion.Transition Is Nothing,
                "quick return preserves the in-flight version request and resumes its wait after the parent visibility transaction")
            now += 399 : _packDetailLoading.Pulse()
            check(PackVersionCards.Children.Count = 0, "returning to an unfinished detail gives it a fresh initial loader wait")
            now += 1 : _packDetailLoading.Pulse() : layout()
            check(PackEntryMotion(PackBrowserMode.Detail) Is _surfaceMotion AndAlso _packDetailMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing,
                "loader arrival during root entry keeps one root animation owner after a rapid return")
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailLoading.CompletionPending)
            _surfaceMotion.Finish() : now += 400 : _packDetailLoading.Pulse()
            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()

            BackToPackSearch(Nothing, Nothing) : settle()
            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate) : _packCatalogFactory = Function() HeldPackCatalog("/search", entered, gate)
            PackSearchText.Text = "loader-old-owner" : RunPackSearch(Nothing, Nothing) : Await entered.Task
            now += 400 : _packSearchLoading.Pulse() : PackEntryMotion(PackBrowserMode.Search).Finish()
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchLoading.CompletionPending)
            Dim retiredRequest = _packSearchRequest
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
            PackSearchText.Text = "new-loader-owner" : RunPackSearch(Nothing, Nothing)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout() : now += 10000 : _packSearchLoading.Pulse()
            check(retiredRequest.IsCancellationRequested AndAlso _packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso
                PackProjectRows.Children.OfType(Of PclResourceItem)().All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("new", StringComparison.Ordinal)),
                "replacing a completed-but-held search cancels the old owner without publishing its results or late wait callback")
            settle()

            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate) : _packCatalogFactory = Function() HeldPackCatalog("/search", entered, gate)
            PackSearchText.Text = "loader-cancel-retry" : RunPackSearch(Nothing, Nothing) : Await entered.Task
            Dim canceledToken = _packSearchRequest.Token
            _packSearchRequest.Cancel() : gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(canceledToken.IsCancellationRequested AndAlso _packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not _packSearchLoading.TimerArmed,
                "canceling the request during LoaderWait removes its timer and leaves installation cancellation untouched")
            Dim retryToken As CancellationToken
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Function(uri, token)
                                                                                                                              retryToken = token : Return Task.CompletedTask
                                                                                                                          End Function}, "")
            RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
            check(Not retryToken.IsCancellationRequested AndAlso retryToken <> canceledToken AndAlso PackProjectRows.Children.Count = 40 AndAlso _packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle,
                "retry after waiting cancellation owns a fresh token and renders current results without a stranded loading state")

            project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
            gate = PackCheckGate() : entered = PackCheckGate() : gates.Add(gate)
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Async Function(uri, token)
                                                                                                                              If Not uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then Return
                                                                                                                              entered.TrySetResult(True) : Await gate.Task
                                                                                                                              Throw New IOException("fixture timed detail failure")
                                                                                                                          End Function}, "")
            OpenPackDetails(project) : Await entered.Task : now += 400 : _packDetailLoading.Pulse()
            Dim retiredEntry = PackEntryMotion(PackBrowserMode.Detail).Transition
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            check(PackDetailRetry.IsVisible AndAlso PackDetailHint.Theme = PclHintTheme.Red AndAlso _packDetailLoading.Phase = PclLoadingPresentation.Phases.Idle,
                "a version error immediately replaces timed loading with the existing readable retry state")
            retiredEntry.Controller.SkipToFill() : now += 10000 : _packDetailLoading.Pulse() : layout()
            check(PackDetailRetry.IsVisible AndAlso _packDetailCompletedProject Is Nothing, "retired entry and wait callbacks cannot erase a current version failure")
            settle() : _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
            check(Not PackDetailRetry.IsVisible AndAlso _packDetailCompletedProject Is project AndAlso _packReleases.Count = 2 AndAlso _packDetailLoading.Phase = PclLoadingPresentation.Phases.Idle,
                "retry after a timed loading failure accepts the new version result and clears its error")
            check(_taskGeneration = generation AndAlso _cancellation Is installation AndAlso Not _packSearchLoading.TimerArmed AndAlso Not _packDetailLoading.TimerArmed,
                "loading presentation timing does not alter installation ownership and leaves no active timer after completion")
        Finally
            _packSearchRequest?.Cancel() : _packDetailRequest?.Cancel()
            For Each pendingGate In gates : pendingGate.TrySetResult(True) : Next
            _packSearchLoading.Reset() : _packDetailLoading.Reset()
            _packSearchLoading.NowForChecks = Nothing : _packDetailLoading.NowForChecks = Nothing
            _packLoadingTimingForChecks = False : _motionPreview = False
            settle() : _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False
            _packResultMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False
            _packCatalogFactory = previousFactory : ChangePackMode(PackBrowserMode.Search) : layout()
        End Try
    End Function

    Private Async Function VerifyLoadingCloseAsync(check As Action(Of Boolean, String)) As Task
        Dim owner As New MainWindow(True), original = DirectCast(owner.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        Dim gate = PackCheckGate(), entered = PackCheckGate()
        owner.Content = Nothing : host.Child = original
        Try
            Using source = PreviewSurface.Attach(host, 850, 600)
                Dim layout = ControlLayout(host)
                owner.SetDownloadView(DownloadView.Modpacks) : layout()
                owner._packLoadingTimingForChecks = True : owner._motionPreview = True
                owner._packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
                owner._packCatalogFactory = Function() HeldPackCatalog("/search", entered, gate)
                Dim pending = owner.LoadPackSearchPageAsync(0) : Await entered.Task
                check(owner._packSearchLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso owner._packSearchLoading.TimerArmed, "window-close regression owns a real in-flight LoaderWait before closing")
                owner.WindowClosing(owner, New CancelEventArgs()) : gate.TrySetResult(True)
                Await pending : layout() : owner._packSearchLoading.Pulse()
                check(owner._closing AndAlso owner._packSearchRequest Is Nothing AndAlso owner._packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso
                    owner._packDetailLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not owner._packSearchLoading.TimerArmed AndAlso owner.PackProjectRows.Children.Count = 0,
                    "window closing cancels requests and clears both presentation controllers without publishing a late response")
            End Using
        Finally
            owner._packSearchRequest?.Cancel() : gate.TrySetResult(True)
            owner._packSearchLoading.Reset() : owner._packDetailLoading.Reset()
            owner.FinishPackContentMotion() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Function
End Class
