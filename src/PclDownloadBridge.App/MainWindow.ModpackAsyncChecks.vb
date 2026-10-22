Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackAsyncEntryAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ChangePackMode(PackBrowserMode.Search) : layout()
        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "animated-details"
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        Dim project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
        Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously), entered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Async Function(uri, token)
                                                                                                                          If Not uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then Return
                                                                                                                          entered.TrySetResult(True) : Await gate.Task
                                                                                                                          token.ThrowIfCancellationRequested()
                                                                                                                      End Function}, "")
        OpenPackDetails(project) : Await entered.Task : layout()
        Dim retired = DirectCast(PackVersionCards.Children(0), PclCard)
        _packDetailMotion.PauseForChecks = True : _motionPreview = True : gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(TypeOf DirectCast(PackVersionCards.Children(0), PclCard).RenderTransform Is TransformGroup, "accepted detail versions enter PCL's own content animation instead of appearing abruptly after loading")
        Dim details = _packDetailMotion.Transition
        check(details IsNot Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso Not PackDetailResultsHost.IsHitTestVisible AndAlso PackProjectWebsite.IsHitTestVisible, "detail result entry gates only changing versions while keeping project navigation usable")
        check(Not retired.HasAnimatedProperties AndAlso Not TypeOf retired.RenderTransform Is TransformGroup, "detail completion releases every transform and animation from its detached loading card")
        Dim row = DirectCast(DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children(0), PclListItem)
        Dim generation = _taskGeneration
        row.Activate() : InstallOnlinePack(DirectCast(row.Tag, ModpackRelease))
        check(_onlinePrompt Is Nothing AndAlso _taskGeneration = generation, "gated detail rows and direct installation cannot open a name dialog during the exit phase")
        details.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(160), TimeSeekOrigin.BeginTime) : layout()
        check(PackDetailResultsHost.IsHitTestVisible AndAlso _packDetailMotion.Transition Is details, "PCL's content reveal restores input while its remaining entry movement continues")
        SaveControlVisual(host, Path.Combine(directory, "pack-detail-entry-160ms.png"))

        Dim installGate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _onlineInstallRunner = Function(release, root, name, progress, token) installGate.Task
        _categoryMotion.PauseForChecks = True
        row.Activate() : OnlinePackName.Text = "Animation-Instance" : ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _busy AndAlso _downloadView = DownloadView.Progress) : layout()
        Dim progressEntry = _categoryMotion.Transition
        check(progressEntry IsNot Nothing AndAlso DownloadGhost.Children.Count = 1 AndAlso _packDetailMotion.Transition Is Nothing AndAlso PackDetailGhost.Children.Count = 0, "starting installation captures the composited detail animation before releasing its clocks and shade")
        details.Controller.SkipToFill() : layout()
        check(_categoryMotion.Transition Is progressEntry AndAlso _downloadView = DownloadView.Progress AndAlso Not PackDetailSurface.IsVisible, "a retired detail clock cannot reveal versions or finish the progress page's entry")
        _categoryMotion.Finish() : _categoryMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False : _motionPreview = False
        installGate.SetResult(New InstallResult With {.InstancePath = Path.Combine(_selectedFolder.Location, "versions", "Animation-Instance"), .FileCount = 1})
        Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline)
        ResumeInstallation(Nothing, Nothing) : layout()

        ' Completion during a category entry must retarget that same controller.
        BackToPackSearch(Nothing, Nothing) : layout()
        project = DirectCast(DirectCast(PackProjectRows.Children(1), PclResourceItem).Tag, ModpackProject)
        gate = PackCheckGate() : entered = PackCheckGate()
        _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
        _motionPreview = True : _categoryMotion.PauseForChecks = True
        OpenPackDetails(project) : Await entered.Task : layout()
        Dim entering = _categoryMotion.Transition, oldCard = DirectCast(PackVersionCards.Children(0), PclCard)
        gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        Dim replacement = _categoryMotion.Transition
        check(entering IsNot Nothing AndAlso replacement IsNot Nothing AndAlso entering IsNot replacement AndAlso _packDetailMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing, "detail completion during category entry retargets its current controller without a second parent or child animation")
        check(Not oldCard.HasAnimatedProperties AndAlso Not TypeOf oldCard.RenderTransform Is TransformGroup AndAlso DownloadGhost.Children.Count = 1, "category retargeting releases the removed loading card and retains one inert composited exit frame")
        entering.Controller.SkipToFill() : layout()
        check(_categoryMotion.Transition Is replacement AndAlso _packReleases.All(Function(release) release.ProjectId = project.Id), "an obsolete category entry cannot finish or replace accepted detail versions")
        _categoryMotion.Finish() : _categoryMotion.PauseForChecks = False : _motionPreview = False

        ' A visible root entry owns completion; a hidden response preserves settings.
        BackToPackSearch(Nothing, Nothing) : layout()
        project = DirectCast(DirectCast(PackProjectRows.Children(2), PclResourceItem).Tag, ModpackProject)
        gate = PackCheckGate() : entered = PackCheckGate() : _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
        OpenPackDetails(project) : Await entered.Task
        ShowSettings(Nothing, Nothing) : _motionPreview = True : _surfaceMotion.PauseForChecks = True
        ShowDownload(Nothing, Nothing) : layout()
        Dim rootEntry = _surfaceMotion.Transition
        gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        Dim rootReplacement = _surfaceMotion.Transition
        check(rootEntry IsNot Nothing AndAlso rootReplacement IsNot Nothing AndAlso rootReplacement IsNot rootEntry AndAlso PageGhost.Children.Count = 1 AndAlso _packDetailMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing, "detail completion during main-page entry renews the existing root animation and cleans detached children")
        rootEntry.Controller.SkipToFill() : layout()
        check(_surfaceMotion.Transition Is rootReplacement AndAlso DownloadPage.IsVisible, "retired root entry callbacks cannot finish the newly accepted detail content")
        _surfaceMotion.Finish() : _surfaceMotion.PauseForChecks = False : _motionPreview = False

        BackToPackSearch(Nothing, Nothing) : layout()
        project = DirectCast(DirectCast(PackProjectRows.Children(3), PclResourceItem).Tag, ModpackProject)
        gate = PackCheckGate() : entered = PackCheckGate() : _packCatalogFactory = Function() HeldPackCatalog("/version", entered, gate)
        OpenPackDetails(project) : Await entered.Task
        _motionPreview = True : _surfaceMotion.PauseForChecks = True : ShowSettings(Nothing, Nothing) : layout()
        Dim settingsEntry = _surfaceMotion.Transition
        gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(SettingsPage.IsVisible AndAlso _surfaceMotion.Transition Is settingsEntry AndAlso _packDetailMotion.Transition Is Nothing AndAlso _packReleases.All(Function(release) release.ProjectId = project.Id), "hidden detail completion caches its own versions without restarting settings or revealing download content")
        _surfaceMotion.Finish() : _surfaceMotion.PauseForChecks = False : _motionPreview = False
        ShowDownload(Nothing, Nothing) : BackToPackSearch(Nothing, Nothing) : layout()

        For Each categoryEntry In {False, True}
            gate = PackCheckGate() : entered = PackCheckGate() : _packCatalogFactory = Function() HeldPackCatalog("/search", entered, gate)
            ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = If(categoryEntry, "category-arrival", "root-arrival")
            RunPackSearch(Nothing, Nothing) : Await entered.Task
            If categoryEntry Then
                ShowVanillaDownloads(Nothing, Nothing)
                _motionPreview = True : _categoryMotion.PauseForChecks = True : ShowModpackDownloads(Nothing, Nothing)
            Else
                ShowSettings(Nothing, Nothing)
                _motionPreview = True : _surfaceMotion.PauseForChecks = True : ShowDownload(Nothing, Nothing)
            End If
            Dim owner = If(categoryEntry, _categoryMotion, _surfaceMotion), previous = owner.Transition
            gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            Dim current = owner.Transition
            check(previous IsNot Nothing AndAlso current IsNot Nothing AndAlso current IsNot previous AndAlso _packResultMotion.Transition Is Nothing AndAlso PackProjectRows.Children.Count = 40, "search completion retargets its existing " & If(categoryEntry, "category", "main-page") & " entry without detached loading transforms")
            previous.Controller.SkipToFill() : layout()
            check(owner.Transition Is current AndAlso PackSearchSurface.IsVisible, "obsolete " & If(categoryEntry, "category", "main-page") & " callbacks cannot finish accepted search results")
            owner.Finish() : _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _motionPreview = False
        Next
        check(PackSearchResultsHost.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso DownloadGhost.Children.Count = 0 AndAlso PageGhost.Children.Count = 0 AndAlso PackDetailGhost.Children.Count = 0, "search and detail animation handoffs settle with no stale input gates or exit images")
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
    End Function

    Private Shared Function PackCheckGate() As TaskCompletionSource(Of Boolean)
        Return New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
    End Function
    Private Shared Function HeldPackCatalog(suffix As String, entered As TaskCompletionSource(Of Boolean), gate As TaskCompletionSource(Of Boolean)) As ModpackCatalogClient
        Return New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Async Function(uri, token)
                                                                                                If Not uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) Then Return
                                                                                                entered.TrySetResult(True) : Await gate.Task
                                                                                                token.ThrowIfCancellationRequested()
                                                                                            End Function}, "")
    End Function
End Class
