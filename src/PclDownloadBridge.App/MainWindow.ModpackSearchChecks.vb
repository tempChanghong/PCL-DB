Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyPackSearchUi(directory As String, passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), previous = SynchronizationContext.Current
        Dim content = DirectCast(owner.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.VerifyPackSearchAsync(directory, host, check)
                If Not work.IsCompleted AndAlso Not PumpMotionUntil(Function() work.IsCompleted) Then Throw New TimeoutException("Pack search UI exceeded its deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._packSearchRequest?.Cancel() : owner._packDetailRequest?.Cancel() : owner._packArtworkRequest?.Cancel()
            owner._packResultMotion.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            owner.CompleteOnlinePrompt(Nothing)
            host.Child = Nothing : owner.Content = content
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function VerifyPackSearchAsync(directory As String, host As Border, check As Action(Of Boolean, String)) As Task
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        _selectedFolder = New GameFolder With {.Location = Path.Combine(directory, "online-game-" & Guid.NewGuid().ToString("N")), .Name = "isolated UI fixture"}
        IO.Directory.CreateDirectory(_selectedFolder.Location)
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        SetDownloadView(DownloadView.Modpacks) : layout()
        PackSearchGame.ApplyTemplate()
        Dim editor = TryCast(PackSearchGame.Template.FindName("PART_EditableTextBox", PackSearchGame), TextBox)
        check(editor IsNot Nothing AndAlso editor.IsVisible AndAlso editor.Text.Contains("全部"), "editable PCL version selector displays its initial value through its native editor part")
        editor.Text = "1.20.1" : layout()
        check(PackSearchGameValue() = "1.20.1", "typed Minecraft filters flow from the editable selector into the search request")
        ResetPackSearch(Nothing, Nothing)
        Dim loader As New PclLoadingView("正在获取整合包列表")
        PackResultsCard.HorizontalAlignment = HorizontalAlignment.Center
        PackProjectRows.Children.Clear() : PackProjectRows.Children.Add(loader) : layout()
        check(Math.Abs(PclPickaxeAnimation.Sample(250) - 55) < 0.0001 AndAlso Math.Abs(PclPickaxeAnimation.Sample(600) + 20) < 0.0001 AndAlso Math.Abs(PclPickaxeAnimation.Sample(1500) - 55) < 0.0001, "PCL loading pickaxe preserves its pause, backswing and parallel-return endpoints")
        If SystemParameters.ClientAreaAnimation Then
            check(loader.Transition IsNot Nothing, "visible PCL loading view owns its repeating animation clock")
            loader.Transition.Controller.Pause()
            loader.Transition.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(600), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime)
        End If
        SaveControlVisual(host, Path.Combine(directory, "pack-search-loading.png"))
        PackProjectRows.Children.Clear() : layout()
        check(loader.Transition Is Nothing, "unloading the PCL loading view releases its repeating clocks")
        check(PackSearchSurface.IsVisible AndAlso Not PackLocalSurface.IsVisible AndAlso DownloadNav.Children.Count = 2, "modpack landing exposes PCL search while retaining two sidebar destinations")
        OpenLocalPacks(Nothing, Nothing) : layout()
        check(PackLocalSurface.IsVisible AndAlso ImportPackButton.IsVisible AndAlso ImportModrinthButton.IsVisible, "local CF and MR imports remain accessible from the search page")
        BackToPackSearch(Nothing, Nothing) : layout()
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.All})
        Await LoadPackSearchPageAsync(0) : layout()
        check(PackProjectRows.Children.Count = 40 AndAlso PackPagingCard.IsVisible AndAlso PackSearchHint.IsVisible, "partial source failure keeps a full MR result page and its warning")
        Dim first = DirectCast(PackProjectRows.Children(0), PclResourceItem), project = DirectCast(first.Tag, ModpackProject)
        check(first.Height = 64 AndAlso first.Icon.Width = 50 AndAlso first.Icon.Height = 50, "project rows use PCL's 64px layout and 50px artwork")
        check(PackNextPage.IsEnabled AndAlso Not PackPreviousPage.IsEnabled, "paging enables only available directions")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-results.png"))
        Dim firstIdentity = project.Key
        Await LoadPackSearchPageAsync(1) : layout()
        check(PackPageLabel.Text = "2" AndAlso PackPreviousPage.IsEnabled AndAlso Not PackNextPage.IsEnabled, "next page uses independent source pagination and ends correctly")
        Await LoadPackSearchPageAsync(0) : layout()
        check(DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject).Key = firstIdentity, "returning to a cached page preserves ordering")
        project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
        ModpackPage.ScrollToVerticalOffset(250) : layout()
        Dim remembered = ModpackPage.VerticalOffset
        PackSearchGame.Text = "1.18.2" ' An unsubmitted edit must not change the displayed result's query.
        OpenPackDetails(project)
        Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing)
        layout()
        check(_packTargetGame = "" AndAlso _packGameFilter = "", "project details inherit the committed search instead of an unsubmitted Minecraft edit")
        check(Not PackVersionFilterCard.IsVisible, "a single compatible Minecraft version needs no redundant filter card")
        check(PackDetailSurface.IsVisible AndAlso PackVersionCards.Children.Count = 1 AndAlso _packReleases.Count = 2, "project selection loads a separate native version detail page")
        Dim versions = DirectCast(PackVersionCards.Children(0), PclCard), body = DirectCast(versions.Body, StackPanel), row = DirectCast(body.Children(0), PclListItem)
        check(row.Title.EndsWith("2.3", StringComparison.Ordinal) AndAlso row.Height = 42, "release labels retain dotted display versions and PCL's 42px row")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-versions.png"))
        BackToPackSearch(Nothing, Nothing) : layout()
        check(Math.Abs(ModpackPage.VerticalOffset - remembered) < 1 AndAlso PackPageLabel.Text = "1", "back restores the search snapshot and its scroll offset")
        OpenPackDetails(project) : layout()
        check(PackVersionCards.Children(0) Is versions AndAlso versions.Body Is body, "reopening a project retains its expanded cards and lazy row instances")

        Dim installGate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim selected As ModpackRelease = Nothing, selectedName As String = Nothing, selectedRoot As String = Nothing
        _onlineInstallRunner = Function(release, root, name, progress, token)
                                   selected = release : selectedName = name : selectedRoot = root
                                   progress.Report(New TransferProgress With {.Phase = "archive", .Stage = "下载整合包归档", .StageFraction = 0.5, .NetworkBytes = 100, .Total = 1})
                                   Return installGate.Task
                               End Function
        row.Activate() : layout()
        check(_onlinePrompt IsNot Nothing AndAlso Not _busy AndAlso Not _hasTask AndAlso PackNameOverlay.IsVisible, "selecting a release asks for its instance name before downloading")
        OnlinePackName.Text = "CON" : layout()
        check(Not OnlinePackConfirm.IsEnabled AndAlso OnlinePackNameHint.IsVisible, "name confirmation rejects reserved Windows names")
        OnlinePackName.Text = "Search-Instance" : layout()
        SaveControlVisual(host, Path.Combine(directory, "pack-search-name.png"))
        ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _busy AndAlso selected IsNot Nothing) : layout()
        check(_downloadView = DownloadView.Progress AndAlso _taskRows.ContainsKey("archive") AndAlso selectedName = "Search-Instance" AndAlso selectedRoot = _selectedFolder.Location, "confirmation starts one archive-to-install task with frozen destination and release identity")
        check(selected IsNot row.Tag AndAlso selected.Id = DirectCast(row.Tag, ModpackRelease).Id, "online installation owns a copied release snapshot")
        check(Not PackDetailSurface.IsEnabled AndAlso Not GameFolderCard.IsEnabled AndAlso CancelButton.IsEnabled, "archive acquisition freezes install inputs while retaining cancellation")
        Dim progressBefore = _taskOverall
        RenderTaskProgress(New TransferProgress With {.Phase = "archive", .Stage = "归档已校验", .StageFraction = 1, .NetworkBytes = 200})
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载游戏与整合包文件", .StageFraction = 0.25, .NetworkBytes = 80,
            .PhaseFractions = New Dictionary(Of String, Double) From {{"metadata", 1}, {"files", 0.25}, {"components", 0}, {"processors", 0}, {"commit", 0}}})
        check(_taskOverall >= progressBefore AndAlso _lastNetworkBytes = 280 AndAlso _taskRows("archive").Complete, "archive handoff preserves monotone progress and accumulates transfer bytes")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-progress.png"))
        installGate.SetResult(New InstallResult With {.InstancePath = Path.Combine(selectedRoot, "versions", selectedName), .FileCount = 10})
        Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline)
        check(_taskOutcome = TaskOutcome.Succeeded AndAlso _onlinePrompt Is Nothing, "successful online task releases its prompt, cancellation and busy ownership")
        ResumeInstallation(Nothing, Nothing) : layout()
        check(_packMode = PackBrowserMode.Detail AndAlso PackVersionCards.Children(0) Is versions, "return from the task restores the same project and chosen version list")
        Dim generation = _taskGeneration
        row.Activate() : layout()
        check(OnlinePackName.Text = "Search-Instance", "retry proposes the previous custom instance name")
        CancelOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() Not _choosingOnline)
        check(_taskGeneration = generation AndAlso Not _busy, "cancelling the name prompt creates no new download task")
        _onlineInstallRunner = Async Function(release, root, name, progress, token)
                                   Await Task.Delay(Timeout.Infinite, token)
                                   Return New InstallResult()
                               End Function
        row.Activate() : ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _busy)
        CancelInstall(Nothing, Nothing)
        Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline)
        check(_taskOutcome = TaskOutcome.Cancelled AndAlso _cancellation Is Nothing AndAlso _onlinePrompt Is Nothing, "cancelling online acquisition joins the task and leaves no orphan continuation")
        ResumeInstallation(Nothing, Nothing) : layout()

        Dim optionalArchive = Path.Combine(_selectedFolder.Location, "optional-prompts.mrpack")
        Using zip = IO.Compression.ZipFile.Open(optionalArchive, IO.Compression.ZipArchiveMode.Create)
            Dim files As New JArray()
            For Each path In {"mods/one.jar", "mods/two.jar"}
                files.Add(New JObject From {{"path", path}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}}, {"downloads", New JArray("https://cdn.modrinth.com/optional.jar")}, {"fileSize", 1}, {"env", New JObject From {{"client", "optional"}, {"server", "required"}}}})
            Next
            Using writer As New StreamWriter(zip.CreateEntry("modrinth.index.json").Open())
                writer.Write(New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"name", "Optional fixture"}, {"versionId", "1"}, {"dependencies", New JObject From {{"minecraft", "1.20.1"}}}, {"files", files}}.ToString())
            End Using
        End Using
        Dim optionalPackage = ModrinthPackage.Read(optionalArchive)
        Dim choices = ChooseOnlineOptionalAsync(optionalPackage, CancellationToken.None)
        check(_onlineOptionalPrompt AndAlso PackNameDescription.Text.Contains("one.jar"), "online MR install asks about client-optional files after reading the archive")
        CancelOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _onlinePrompt IsNot Nothing)
        check(PackNameDescription.Text.Contains("two.jar"), "declining one optional file advances to the next file")
        ConfirmOnlinePackName(Nothing, Nothing)
        check((Await choices).SequenceEqual({"mods/two.jar"}), "optional prompt decisions are passed by exact manifest path")
        Using cancellation As New CancellationTokenSource()
            Dim cancelling = ChooseOnlineOptionalAsync(optionalPackage, cancellation.Token)
            cancellation.Cancel() : CompleteOnlinePrompt(Nothing)
            Dim replacement = PromptOnlinePackAsync("输入版本名称", "新请求", _selectedFolder.Location, "New-Prompt", False, CancellationToken.None)
            Dim cancelled = False
            Try
                Await cancelling
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Await Task.Yield()
            check(cancelled AndAlso _onlinePrompt IsNot Nothing AndAlso Not replacement.IsCompleted AndAlso OnlinePackName.Text = "New-Prompt", "an obsolete optional cancellation callback cannot close a newer name prompt")
            CompleteOnlinePrompt(Nothing)
        End Using

        Dim originalReleases = _packReleases
        Dim multi = originalReleases(0).Copy()
        multi.GameVersions = {"1.20.1", "1.20.2", "1.20.2"}
        _packReleases = New List(Of ModpackRelease) From {multi, originalReleases(1)}
        InitializePackVersions() : layout()
        check(PackVersionCards.Children.Count = 2, "an unfiltered multi-game release appears in each compatible Minecraft card")
        Dim secondFilter = PackVersionFilters.Children.OfType(Of PclButton)().Single(Function(button) button.Text = "1.20.2")
        SelectPackVersionFilter(secondFilter) : layout()
        check(PackVersionCards.Children.Count = 1 AndAlso DirectCast(PackVersionCards.Children(0), PclCard).Title.StartsWith("1.20.2", StringComparison.Ordinal), "filtering a multi-game release uses the matching Minecraft card rather than its first advertised version")
        Dim multiCard = DirectCast(PackVersionCards.Children(0), PclCard), multiRows = DirectCast(multiCard.Body, StackPanel)
        check(multiRows.Children.Count = 1 AndAlso DirectCast(multiRows.Children(0), PclListItem).Tag Is multi, "repeated compatibility declarations produce one install action per release and game card")
        SelectPackVersionFilter(DirectCast(PackVersionFilters.Children(0), PclButton)) : layout()
        check(PackVersionCards.Children.Contains(multiCard) AndAlso multiCard.Body Is multiRows AndAlso Not multiCard.Collapsed AndAlso PackVersionFilters.Children.Contains(secondFilter), "filter changes retain button identity, expansion state and already built release rows")
        multiCard.Collapsed = True : layout()
        Dim obsoleteFold = multiCard.HeightTransition
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclButton)().Single(Function(button) button.Text = "1.20.1")) : layout()
        Dim generationBeforeHiddenRow = _taskGeneration
        DirectCast(multiRows.Children(0), PclListItem).Activate() : layout()
        check(_onlinePrompt Is Nothing AndAlso Not _choosingOnline AndAlso _taskGeneration = generationBeforeHiddenRow, "a filtered-away cached row cannot start a name prompt or install task")
        SelectPackVersionFilter(secondFilter) : layout()
        obsoleteFold?.Controller.SkipToFill() : layout()
        Await WaitPackUiAsync(Function() multiCard.HeightTransition Is Nothing)
        check(PackVersionCards.Children(0) Is multiCard AndAlso multiCard.Body Is multiRows AndAlso Not multiCard.Collapsed AndAlso Double.IsNaN(multiCard.Height), "filtering during a fold cannot let an obsolete clock collapse the restored release card")

        Dim many As New List(Of ModpackRelease)
        For Each game In {"1.20", "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6", "1.21", "1.21.1", "1.21.2", "1.21.3"}
            Dim item = multi.Copy() : item.Id = "game-" & game : item.Name = "Fixture " & game : item.GameVersions = {game}
            many.Add(item)
        Next
        _packReleases = many : _packTargetGame = "1.20.4" : InitializePackVersions() : layout()
        check(PackVersionFilters.Children.OfType(Of PclButton)().Select(Function(button) button.Text).SequenceEqual({"全部", "1.21", "1.20"}) AndAlso _packGameFilter = "1.20", "nine or more exact version filters fold into PCL release families while retaining the requested family")
        Dim targetCard = DirectCast(PackVersionCards.Children(0), PclCard)
        check(CStr(targetCard.Tag) = "1.20.4（所选版本）" AndAlso Not targetCard.Collapsed AndAlso Not PackVersionCards.Children.OfType(Of PclCard)().Any(Function(card) CStr(card.Tag) = "1.20.4"), "the requested Minecraft version is expanded first without a duplicate ordinary card")
        check(PackVersionCards.Children.OfType(Of PclCard)().All(Function(card) card.Title.StartsWith("1.20", StringComparison.Ordinal)), "a grouped version filter excludes other families but retains exact patch cards")
        SelectPackVersionFilter(DirectCast(PackVersionFilters.Children(0), PclButton)) : layout()
        check(PackVersionCards.Children.Count = 11 AndAlso PackVersionCards.Children(0) Is targetCard, "the all filter restores every exact version while preserving the selected-version card")
        Await Task.Delay(220) : layout() ' Capture the settled filter colors, not their 200ms transition.
        SaveControlVisual(host, Path.Combine(directory, "pack-search-version-families.png"))
        _packTargetGame = "1.19.4" : InitializePackVersions() : layout()
        check(_packGameFilter = "" AndAlso PackVersionCards.Children.Count = 11, "a requested version with no downloadable file falls back to all instead of an empty phantom filter")
        SelectPackVersionFilter(secondFilter)
        check(_packGameFilter = "", "an obsolete filter button cannot change a refreshed project's version selection")
        Dim oldVersions As New List(Of ModpackRelease)
        For Each game In {"1.4.7", "1.5.2", "1.6.4", "1.7.2", "1.7.10", "1.8", "1.8.9", "1.9", "1.10.2", "1.11.2", "1.20.1", "1.20.2", "26.1", "26.2", "25w01a", "25w02a"}
            Dim item = multi.Copy() : item.Id = "old-" & game : item.GameVersions = {game}
            oldVersions.Add(item)
        Next
        _packReleases = oldVersions : _packTargetGame = "" : InitializePackVersions() : layout()
        Dim folded = PackVersionFilters.Children.OfType(Of PclButton)().Select(Function(button) button.Text).ToList()
        check(folded.SequenceEqual({"全部", "26.2", "26.1", "1.20.2", "1.20.1", "快照版", "远古版"}), "old releases fold only after minor grouping is insufficient, while new numbering and snapshot groups remain distinct")
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclButton)().Single(Function(button) button.Text = "远古版")) : layout()
        check(PackVersionCards.Children.Count = 10 AndAlso PackVersionCards.Children.OfType(Of PclCard)().All(Function(card) MinecraftVersionRules.ReleaseFamily(CStr(card.Tag)) < 120), "the old-version filter retains every exact old release card without newer or snapshot files")
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclButton)().Single(Function(button) button.Text = "快照版")) : layout()
        Dim snapshots = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel)
        check(PackVersionCards.Children.Count = 1 AndAlso snapshots.Children.Count = 2, "snapshot compatibility labels share one expanded card without losing either file")
        Dim unlabelled = multi.Copy() : unlabelled.GameVersions = {}
        _packReleases = New List(Of ModpackRelease) From {unlabelled} : InitializePackVersions() : layout()
        check(PackVersionCards.Children.Count = 1 AndAlso CStr(DirectCast(PackVersionCards.Children(0), PclCard).Tag) = "其他" AndAlso Not PackVersionFilterCard.IsVisible, "files with no declared game versions remain accessible in the other card")
        _packReleases = New List(Of ModpackRelease)
        PackDetailHint.Theme = PclHintTheme.Red : PackDetailHint.ToolTip = "obsolete retry error"
        _packTargetGame = "" : InitializePackVersions() : layout()
        check(PackVersionCards.Children.Count = 0 AndAlso Not PackVersionFilterCard.IsVisible AndAlso PackDetailHint.IsVisible AndAlso PackDetailHint.Theme = PclHintTheme.Blue AndAlso PackDetailHint.ToolTip Is Nothing, "an empty usable-file list shows a neutral hint without stale retry details or filters")
        _packReleases = originalReleases : InitializePackVersions()

        Dim olderProject As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "older", .Name = "older"}
        Dim currentProject As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "current", .Name = "current"}
        _packProject = olderProject
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.Delay = 60}, "")
        Dim olderDetails = LoadPackDetailsAsync(olderProject)
        _packProject = currentProject
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        Await LoadPackDetailsAsync(currentProject) : Await olderDetails : layout()
        check(_packProject Is currentProject AndAlso _packReleases.All(Function(release) release.ProjectId = "current") AndAlso PackVersionCards.Children.Count = 1, "a delayed older project's details cannot replace the current project's version cards")

        ' Query replacement must win even if an earlier handler ignores cancellation.
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.Delay = 60}, "")
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth, .Text = "old"})
        Dim old = LoadPackSearchPageAsync(0)
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth, .Text = "new"})
        Await LoadPackSearchPageAsync(0) : Await old
        check(DirectCast(PackProjectRows.Children(0), PclResourceItem).ToolTip.ToString().Contains("new"), "a late older query cannot overwrite the current search results")

        ChangePackMode(PackBrowserMode.Search) : layout() : _motionPreview = True : _categoryMotion.PauseForChecks = True
        ChangePackMode(PackBrowserMode.Detail) : layout()
        Dim entering = _categoryMotion.Transition
        check(entering IsNot Nothing AndAlso Not DownloadViewHost.IsHitTestVisible AndAlso DownloadGhost.Children.Count > 0, "search-to-detail owns an inert exit snapshot and gates input")
        ChangePackMode(PackBrowserMode.Search) : layout()
        Dim returning = _categoryMotion.Transition
        entering.Controller.SkipToFill() : layout()
        check(_packMode = PackBrowserMode.Search AndAlso _categoryMotion.Transition Is returning, "obsolete detail animation cannot replace a rapid return to search")
        _categoryMotion.Finish() : layout()
        check(DownloadViewHost.IsHitTestVisible AndAlso DownloadGhost.Children.Count = 0 AndAlso PackSearchSurface.Opacity = 1, "settled pack navigation restores input and clears its owned visual state")
        _packResultMotion.PauseForChecks = True
        ChangePackResults(Sub() PackResultsCard.Title = "动画验证") : layout()
        check(_packResultMotion.Transition IsNot Nothing AndAlso Not PackSearchResultsHost.IsHitTestVisible AndAlso PackSearchText.IsHitTestVisible, "result animation gates only result content and keeps the search form interactive")
        Dim obsoleteResult = _packResultMotion.Transition
        _surfaceMotion.PauseForChecks = True
        ShowSettings(Nothing, Nothing) : layout()
        check(_packResultMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition IsNot Nothing AndAlso SettingsPage.IsVisible, "settings navigation captures the composited result animation before releasing its clock")
        obsoleteResult.Controller.SkipToFill() : layout()
        check(SettingsPage.IsVisible AndAlso PackSearchGhost.Children.Count = 0, "an obsolete result clock cannot reveal content over the settings page")
        _surfaceMotion.Finish() : ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish()
        Dim animatedPrompt = PromptOnlinePackAsync("输入版本名称", "动画验证", _selectedFolder.Location, "Animated-Prompt", False, CancellationToken.None)
        Dim enteringPrompt = _onlinePromptMotion
        check(enteringPrompt IsNot Nothing AndAlso _onlinePromptRotation.Angle = -4 AndAlso _onlinePromptOffset.Y = 40 AndAlso PackNameDialog.RenderTransformOrigin.Equals(New Point(0, 0.5)), "PCL input dialog enters from a 40px offset and -4 degree rotation around its left center")
        enteringPrompt.Controller.Pause()
        enteringPrompt.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(90), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime) : layout()
        CompleteOnlinePrompt(Nothing)
        Dim exitingPrompt = _onlinePromptMotion
        enteringPrompt.Controller.SkipToFill() : layout()
        check(_onlinePromptMotion Is exitingPrompt AndAlso PackNameOverlay.IsVisible AndAlso animatedPrompt.IsCompleted, "obsolete input entrance cannot hide or detach its newer closing animation")
        Dim replacementPrompt = PromptOnlinePackAsync("输入版本名称", "新动画", _selectedFolder.Location, "Replacement-Prompt", False, CancellationToken.None)
        Dim replacementMotion = _onlinePromptMotion
        exitingPrompt.Controller.SkipToFill() : layout()
        check(_onlinePromptMotion Is replacementMotion AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso Not replacementPrompt.IsCompleted, "obsolete dialog exit cannot close a replacement prompt or enable its background")
        CompleteOnlinePrompt(Nothing) : _onlinePromptMotion.Controller.SkipToFill() : layout()
        Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
        check(_onlinePromptMotion Is Nothing AndAlso Not PackNameOverlay.IsVisible AndAlso PageSurface.IsEnabled, "input dialog close releases owned clocks, its shade and background input gate")
        _motionPreview = False
        host.Width = 810 : host.Height = 470 : layout()
        check(PackSearchSurface.ActualWidth > 500 AndAlso PackSearchButton.ActualWidth = 140 AndAlso ModpackPage.ScrollableHeight > 0, "search cards and paging stay usable at the minimum window size")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-minimum.png"))
    End Function
    Private Shared Async Function WaitPackUiAsync(condition As Func(Of Boolean)) As Task
        Dim clock = System.Diagnostics.Stopwatch.StartNew()
        While Not condition()
            If clock.Elapsed > TimeSpan.FromSeconds(5) Then Throw New TimeoutException("Pack UI continuation did not settle.")
            Await Task.Delay(1)
        End While
    End Function
    Private Class PackSearchFixtureHandler
        Inherits HttpMessageHandler
        Public Property Delay As Integer
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            If Delay > 0 Then Await Task.Delay(Delay)
            Dim address = request.RequestUri, result As JToken
            If address.Host <> "api.modrinth.com" Then Return New HttpResponseMessage(HttpStatusCode.ServiceUnavailable) With {.Content = New StringContent("{}")}
            If address.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) Then
                Dim hits As New JArray(), offset = If(address.Query.Contains("offset=40"), 40, 0)
                For i = offset To offset + 39
                    Dim item = ProjectJson("pack" & i)
                    item("title") = If(address.Query.Contains("query=new"), "new", "Fixture") & " 整合包 " & i
                    hits.Add(item)
                Next
                result = New JObject From {{"hits", hits}, {"total_hits", 80}}
            ElseIf address.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then
                Dim id = address.Segments(address.Segments.Length - 2).TrimEnd("/"c)
                Dim versions As New JArray()
                For Each number In {"2.3", "2.2"}
                    versions.Add(New JObject From {{"id", "release" & number.Replace(".", "")}, {"project_id", id}, {"name", "Fixture " & number}, {"version_number", number}, {"version_type", "release"}, {"date_published", "2026-10-01T00:00:00Z"}, {"game_versions", New JArray("1.20.1")}, {"loaders", New JArray("forge")}, {"files", New JArray(New JObject From {{"filename", "fixture.mrpack"}, {"primary", True}, {"url", "https://cdn.modrinth.com/fixture.mrpack"}, {"size", 100}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}}}})}})
                Next
                result = versions
            Else
                result = ProjectJson(address.Segments.Last())
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(result.ToString(), Encoding.UTF8, "application/json")}
        End Function
        Private Shared Function ProjectJson(id As String) As JObject
            Return New JObject From {{"id", id}, {"project_id", id}, {"project_type", "modpack"}, {"title", "Fixture 整合包"}, {"description", "用于离线检查搜索、项目详情、版本选择、命名和下载任务的示例数据。"}, {"slug", id}, {"icon_url", ""}, {"downloads", 123456}, {"date_modified", "2026-10-01T00:00:00Z"}, {"updated", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}, {"game_versions", New JArray("1.20.1")}}
        End Function
    End Class
End Class
