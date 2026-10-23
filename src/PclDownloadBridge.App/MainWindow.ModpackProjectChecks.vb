Imports System.IO
Imports System.Threading
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackProjectCacheAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ChangePackMode(PackBrowserMode.Search) : layout()
        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "project-cache" : PackSearchGame.Text = "1.20.1"
        Dim gate = PackCheckGate(), entered = PackCheckGate(), projectsFetched = 0, versionsFetched = 0
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {
            .ProjectPage = Function(uri)
                               Dim item = PackSearchFixtureHandler.ProjectJson(uri.Segments.Last())
                               item("title") = "已核对的整合包" : item("description") = "详情接口返回的新简介。"
                               item("downloads") = 777888 : item("game_versions") = New JArray("1.20.1", "1.21.1")
                               item("categories") = New JArray("quests", "magic", "fabric") : item("loaders") = New JArray("fabric")
                               Return item
                           End Function,
            .BeforeSendAsync = Async Function(uri, token)
                                   If uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) Then Return
                                   If Not uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) Then
                                       Interlocked.Increment(projectsFetched) : Return
                                   End If
                                   Interlocked.Increment(versionsFetched) : entered.TrySetResult(True)
                                   Await gate.Task : token.ThrowIfCancellationRequested()
                               End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        Dim searchRow = DirectCast(PackProjectRows.Children(3), PclResourceItem), project = DirectCast(searchRow.Tag, ModpackProject), originalName = project.Name, originalSummary = project.Summary
        OpenPackDetails(project) : Await entered.Task : layout()
        Dim request = _packDetailRequest, loadingCard = PackVersionCards.Children(0)
        BackToPackSearch(Nothing, Nothing) : layout()
        PackSearchGame.Text = "1.19.2" ' An unsubmitted edit must not retarget a pending detail.
        OpenPackDetails(project) : layout()
        Dim sharedRequest = _packDetailRequest Is request AndAlso Not request.IsCancellationRequested AndAlso PackVersionCards.Children(0) Is loadingCard
        gate.TrySetResult(True) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        Dim intro = DirectCast(PackProjectIdentity.Children(0), PclResourceItem)
        check(intro.IsHitTestVisible AndAlso Not intro.Focusable, "the static project header retains PCL tooltip hit testing without becoming a keyboard destination")
        File.WriteAllText(Path.Combine(directory, "pack-project-cache-state.json"), JsonConvert.SerializeObject(New With {.SharedRequest = sharedRequest, .ProjectRequests = projectsFetched, .VersionRequests = versionsFetched, .Intro = intro.ToolTip.ToString(), .SearchName = project.Name}, Formatting.Indented))
        Dim labels = intro.Children.OfType(Of Grid)().Single().Children.OfType(Of Grid)().Single().Children.OfType(Of TextBlock)().Select(Function(label) label.Text).ToArray()
        check(labels(0) = "已核对的整合包" AndAlso intro.SummaryLabel.Inlines.OfType(Of Run)().Single().Text = "详情接口返回的新简介。" AndAlso intro.ToolTip.ToString().Contains("详情接口返回的新简介。"), "accepted project metadata replaces the visible PCL title, description and tooltip instead of only changing hidden labels")
        check(sharedRequest AndAlso projectsFetched = 1 AndAlso versionsFetched = 1, "returning to the same pending project retains its loading card and accepted request without duplicate metadata or version downloads")
        check(intro.Tag IsNot project AndAlso DirectCast(intro.Tag, ModpackProject).Key = project.Key AndAlso DirectCast(intro.Tag, ModpackProject).Downloads = 777888 AndAlso _packProject Is project AndAlso project.Name = originalName AndAlso project.Summary = originalSummary, "verified detail presentation owns separate metadata without rewriting the cached search snapshot or its request identity")
        Dim detailLabels = intro.Children.OfType(Of Grid)().Single().Children.OfType(Of Grid)().Single().Children.OfType(Of Grid)().Single().Children.OfType(Of TextBlock)().Select(Function(label) label.Text).ToArray()
        check(detailLabels.Contains("Fabric 1.21, 1.20") AndAlso detailLabels.Contains("77 万"), "visible project version and download statistics also come from the accepted metadata rather than stale search values")
        check(intro.SummaryLabel.Inlines.OfType(Of InlineUIContainer)().Select(Function(item) DirectCast(DirectCast(item.Child, Border).Child, TextBlock).Text).SequenceEqual({"任务", "魔法"}) AndAlso project.Tags.Length = 0 AndAlso project.Loaders.Length = 0 AndAlso _packReleases.All(Function(item) item.Loaders.SequenceEqual({"forge"})),
            "accepted detail tags replace only their own header while cached search metadata and exact file loaders remain independent")
        check(_packTargetGame = "1.20.1", "reopening pending details retains the committed Minecraft query despite unsubmitted form edits")
        SaveControlVisual(host, Path.Combine(directory, "pack-project-verified.png"))
        Dim card = DirectCast(PackVersionCards.Children(0), PclCard), body = DirectCast(card.Body, StackPanel)
        BackToPackSearch(Nothing, Nothing) : layout() : OpenPackDetails(project) : layout()
        check(PackProjectIdentity.Children(0) Is intro AndAlso PackVersionCards.Children(0) Is card AndAlso card.Body Is body AndAlso projectsFetched = 1 AndAlso versionsFetched = 1, "reopening cached details preserves verified metadata and existing version rows without restoring an old search summary")
        DirectCast(body.Children(0), PclListItem).Activate() : layout()
        check(_onlinePrompt IsNot Nothing AndAlso OnlinePackName.Text = "已核对的整合包" AndAlso PackNameDescription.Text.StartsWith("已核对的整合包", StringComparison.Ordinal), "name confirmation uses the verified project name shown in the detail card")
        SaveControlVisual(host, Path.Combine(directory, "pack-project-confirmed-name.png"))
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()

        BackToPackSearch(Nothing, Nothing) : layout()
        Dim nextProject = DirectCast(DirectCast(PackProjectRows.Children(4), PclResourceItem).Tag, ModpackProject)
        _packCatalogFactory = Function() FailingPackCatalog(New IOException("fixture project metadata unavailable"))
        OpenPackDetails(nextProject) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(_packProject Is nextProject AndAlso ActivePackProject() Is nextProject AndAlso PackProjectTitle.Text = nextProject.Name AndAlso PackDetailHint.IsVisible, "a different project's failed request cannot inherit the previous verified title or actions")
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.ProjectPage = Function(uri)
                                                                                                                    Dim item = PackSearchFixtureHandler.ProjectJson(uri.Segments.Last())
                                                                                                                    item("title") = "重试后核对的项目" : item("description") = "成功重试后的项目简介。"
                                                                                                                    Return item
                                                                                                                End Function}, "")
        RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        check(ActivePackProject().Key = nextProject.Key AndAlso PackProjectTitle.Text = "重试后核对的项目" AndAlso Not PackDetailHint.IsVisible AndAlso DirectCast(PackProjectIdentity.Children(0), PclResourceItem).ToolTip.ToString().Contains("成功重试后的项目简介。"), "retry accepts only the new project's metadata and retires its previous error without restoring another project's cache")

        BackToPackSearch(Nothing, Nothing) : layout()
        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "clock-regression"
        Dim searches = 0
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Function(uri, token)
                                                                                                                          If uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) Then Interlocked.Increment(searches)
                                                                                                                          Return Task.CompletedTask
                                                                                                                      End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packProject Is Nothing AndAlso _packVerifiedProject Is Nothing, "an accepted new search clears both selected-project ownership and its verified detail snapshot")
        Dim session = _packSearchSession
        _packLastSearch = DateTime.UtcNow.AddMinutes(10)
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession IsNot session AndAlso searches = 2, "a wall-clock rollback cannot keep a future-dated search cache fresh indefinitely")
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
    End Function
End Class
