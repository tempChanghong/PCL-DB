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
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Class CommunityFixtureHandler
        Inherits HttpMessageHandler
        Friend Delay As Integer
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            If Delay > 0 Then Await Task.Delay(Delay) ' Deliberately late, even after cancellation.
            Dim uri = request.RequestUri, path = uri.AbsolutePath, data As JToken
            If path = "/v2/search" Then
                Dim decoded = Uri.UnescapeDataString(uri.Query), kind = CommunityResourceKind.Modpack
                For Each value In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
                    If decoded.Contains("project_type:" & CommunityResources.ProjectType(value) & """") Then kind = value
                Next
                data = New JObject From {{"hits", New JArray(CommunityProjectFixture(kind, kind.ToString() & "Project"))}, {"total_hits", 1}}
            ElseIf path.StartsWith("/v2/version/") Then
                data = New JObject From {{"id", "depver1"}, {"project_id", "dep1"}}
            Else
                Dim id = path.Split("/"c)(3), kind = CommunityResourceKind.Mod
                [Enum].TryParse(id.Replace("Project", ""), kind)
                If id = "dep1" Then kind = CommunityResourceKind.Mod
                If path.EndsWith("/version") Then
                    Dim release = CommunityReleaseFixture(kind, id)
                    data = If(kind = CommunityResourceKind.DataPack,
                        New JArray(release, CommunityReleaseFixture(CommunityResourceKind.Mod, id, "wrongMod"), CommunityReleaseFixture(CommunityResourceKind.Mod, id, "plugin", "paper")), New JArray(release))
                Else
                    data = CommunityProjectFixture(kind, id)
                End If
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Encoding.UTF8, "application/json")}
        End Function
    End Class
    Private Shared Function CommunityProjectFixture(kind As CommunityResourceKind, id As String) As JObject
        Return New JObject From {{"project_id", id}, {"id", id}, {"project_type", If(kind = CommunityResourceKind.DataPack, "mod", CommunityResources.ProjectType(kind))},
            {"all_project_types", If(kind = CommunityResourceKind.DataPack, New JArray("mod", "datapack", "plugin"), New JArray(CommunityResources.ProjectType(kind)))},
            {"title", If(id = "dep1", "Fabric 前置库", CommunityResources.Name(kind) & " 测试资源")}, {"name", "test"}, {"slug", id}, {"description", "PCL 社区资源检查"}, {"icon_url", ""}, {"downloads", 1000}, {"date_modified", "2026-10-01T00:00:00Z"}, {"updated", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}, {"game_versions", New JArray("1.20.1")}}
    End Function
    Private Shared Function CommunityReleaseFixture(kind As CommunityResourceKind, project As String, Optional id As String = Nothing, Optional loader As String = Nothing) As JObject
        Dim file = If(kind = CommunityResourceKind.Mod, "resource.jar", "resource.zip")
        Dim result As New JObject From {{"id", If(id, If(project = "dep1", "depver1", "release1"))}, {"project_id", project}, {"name", "测试发布版本"}, {"version_number", "1.0"}, {"status", "listed"}, {"version_type", "release"}, {"date_published", "2026-10-01T00:00:00Z"}, {"game_versions", New JArray("1.20.1")}, {"loaders", New JArray(If(loader, If(kind = CommunityResourceKind.Mod, "fabric", If(kind = CommunityResourceKind.DataPack, "datapack", If(kind = CommunityResourceKind.Shader, "iris", "minecraft")))))},
            {"files", New JArray(New JObject From {{"filename", file}, {"url", "https://cdn.modrinth.com/data/" & project & "/" & file}, {"primary", True}, {"size", 100}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}}}})}}
        If project = "ModProject" Then result("dependencies") = New JArray(New JObject From {{"project_id", "dep1"}, {"version_id", "depver1"}, {"dependency_type", "required"}})
        Return result
    End Function
    Public Sub VerifyCommunityResourceUi(directory As String, passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host), nativeFocus = LocalModCheckGetNativeFocus()
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 960, 740)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckCommunityResourceUiAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Community resource UI checks timed out.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner._resourceNamesRequest?.Cancel()
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckCommunityResourceUiAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        _packCatalogFactory = Function() New ModpackCatalogClient(New CommunityFixtureHandler(), "")
        layout()
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
            SelectResourceKind(kind) : _categoryMotion.Finish() : layout()
            PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = kind.ToString() & " search" : PackSearchGame.Text = "1.20.1"
            If kind = CommunityResourceKind.Mod Then ResourceLoader.SelectedIndex = 1
            RunPackSearch(Nothing, Nothing)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            Dim project = DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject)
            check(project.Kind = kind AndAlso _packSearchSession.Query.Kind = kind AndAlso PackResultsCard.Title = CommunityResources.Name(kind), "resource sidebar search retains its own kind and accepted result: " & kind.ToString())
            If kind = CommunityResourceKind.Mod Then
                check(ResourceLoader.ActualHeight >= 27 AndAlso ResourceLoader.TranslatePoint(New Point(0, ResourceLoader.ActualHeight), PackSearchFilters).Y <= PackSearchFilters.ActualHeight - 10, "Mod loader selector occupies its own complete PCL filter row without clipping")
                host.Width = 810 : layout()
                check(ResourceLoader.TranslatePoint(New Point(ResourceLoader.ActualWidth, 0), PackSearchFilters).X < PackSearchFilters.ActualWidth - 15 AndAlso ResourceLoader.ActualWidth > 150 AndAlso PackSearchReset.IsVisible, "minimum-width Mod filters retain the loader selector and reset action inside the card")
                host.Width = 960 : layout()
                SaveControlVisual(host, Path.Combine(directory, "community-search.png"))
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                SaveControlVisual(host, Path.Combine(directory, "community-search-dark.png"))
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}}) : layout()
            End If
            check(PackSearchImport.Visibility = Visibility.Collapsed AndAlso (ResourceLoaderFilters.Visibility = Visibility.Visible) = (kind = CommunityResourceKind.Mod) AndAlso PackSearchCategory.Items.Count > 10, "resource filters use fixed PCL categories and show loader controls only for Mods: " & kind.ToString())
            OpenPackDetails(project)
            Await WaitPackUiAsync(Function() _packDetailCompletedProject Is project) : layout()
            check(_packReleases.Count = 1 AndAlso _packReleases(0).Kind = kind AndAlso DownloadSidebar.Visibility = Visibility.Collapsed AndAlso PackProjectTitle.Text.Contains(CommunityResources.Name(kind)), "resource detail selects only matching releases and uses PCL child navigation: " & kind.ToString())
            If kind = CommunityResourceKind.Mod Then
                Dim rows = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel)
                Dim dependencyButton = rows.Children.OfType(Of WrapPanel)().Single().Children.OfType(Of PclButton)().Single()
                Await WaitPackUiAsync(Function() dependencyButton.Text = "Fabric 前置库")
                check(dependencyButton.Text = "Fabric 前置库", "dependency labels resolve to readable project names without altering release identity")
                OpenResourceDependency(ActivePackProject(), _packReleases(0).Dependencies(0), dependencyButton)
                Await WaitPackUiAsync(Function() _packProject.Id = "dep1" AndAlso _packDetailCompletedProject Is _packProject) : layout()
                check(_resourceBack.Count = 1 AndAlso _packReleases.Single().Id = "depver1" AndAlso _packProject.PreferredVersionId = "depver1", "dependency detail retains a fixed version and one parent detail frame")
                NavigateTitleBack() : _categoryMotion.Finish() : layout()
                check(_packProject Is project AndAlso _packReleases.Single().ProjectId = project.Id AndAlso _packMode = PackBrowserMode.Detail AndAlso _resourceBack.Count = 0, "title Back returns from dependency to its exact parent detail instead of search")
            End If
            If kind = CommunityResourceKind.Shader OrElse kind = CommunityResourceKind.DataPack Then SaveControlVisual(host, Path.Combine(directory, "community-" & kind.ToString() & ".png"))
            NavigateTitleBack() : _categoryMotion.Finish() : layout()
            check(_packMode = PackBrowserMode.Search AndAlso PackSearchText.Text = kind.ToString() & " search" AndAlso _packSearchSession.Query.Kind = kind AndAlso PackProjectRows.Children.Count = 1, "resource detail Back retains its search draft results and category: " & kind.ToString())
        Next
        SelectResourceKind(CommunityResourceKind.Mod) : _categoryMotion.Finish()
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(PackSearchText.Text = "Mod search" AndAlso ResourceLoader.SelectedIndex = 1 AndAlso _packSearchSession.Query.Loader = "fabric", "returning to a category restores its own draft loader and accepted search")
        _packCatalogFactory = Function() New ModpackCatalogClient(New CommunityFixtureHandler With {.Delay = 120}, "")
        PackSearchText.Text = "slow previous" : RunPackSearch(Nothing, Nothing)
        Dim retired = _packSearchRequest
        SelectResourceKind(CommunityResourceKind.Shader) : _categoryMotion.Finish()
        _packCatalogFactory = Function() New ModpackCatalogClient(New CommunityFixtureHandler(), "")
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : Await Task.Delay(180) : layout()
        check(retired IsNot Nothing AndAlso _resourceKind = CommunityResourceKind.Shader AndAlso _packSearchSession.Query.Kind = CommunityResourceKind.Shader AndAlso DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject).Kind = CommunityResourceKind.Shader, "a retired category response cannot overwrite current resource results even if its handler ignores cancellation")
        _motionPreview = True : _categoryMotion.PauseForChecks = True
        SelectResourceKind(CommunityResourceKind.DataPack) : layout()
        Dim oldClock = _categoryMotion.Transition
        SelectResourceKind(CommunityResourceKind.Shader) : layout()
        Dim newClock = _categoryMotion.Transition
        oldClock.Controller.SkipToFill() : layout()
        check(newClock IsNot Nothing AndAlso newClock IsNot oldClock AndAlso _resourceKind = CommunityResourceKind.Shader AndAlso ShaderNav.Selected AndAlso Not DownloadViewHost.IsHitTestVisible, "rapid resource category reversal keeps the current animation owner and input gate")
        _categoryMotion.Finish() : _motionPreview = False : _categoryMotion.PauseForChecks = False
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        _packResultMotion.Finish() : layout()
        check(DownloadViewHost.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible, "settling category and result entry restores only the current resource input surface")
        Dim shader = DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject)
        OpenPackDetails(shader) : Await WaitPackUiAsync(Function() _packDetailCompletedProject Is shader) : layout()
        Dim selected = _packReleases.Single(), saved = Path.Combine(directory, "resource-ui-" & Guid.NewGuid().ToString("N") & ".zip"), starts = 0
        _resourceSaveRunner = Async Function(release, destination, progress, token)
            starts += 1 : Await Task.Delay(1000, token)
        End Function
        Dim releaseRow = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children.OfType(Of PclListItem)().Single()
        Dim choices = 0
        _resourceSaveChooser = Function(release)
            choices += 1
            If release.Id <> selected.Id OrElse release.Kind <> CommunityResourceKind.Shader Then Throw New InvalidOperationException("Save dialog received another selection.")
            Return saved
        End Function
        releaseRow.Activate()
        Await WaitPackUiAsync(Function() _busy AndAlso DownloadProgress.IsVisible) : layout()
        RenderTaskProgress(New TransferProgress With {.Phase = "archive", .Stage = "下载并校验文件", .StageFraction = 0.5, .NetworkBytes = 50, .Total = 1})
        check(_taskResourceTransfer AndAlso Math.Abs(_taskOverall - 0.475) < 0.001 AndAlso _taskRows("archive").Label.Text.Contains("校验文件") AndAlso Not DirectCast(_taskRows("files").Label.Parent, UIElement).IsVisible, "resource task progress maps the actual file transfer without displaying Minecraft installation phases")
        CancelInstall(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _busy) : layout()
        check(_taskOutcome = TaskOutcome.Cancelled AndAlso Not File.Exists(saved) AndAlso starts = 1 AndAlso choices = 1, "real version-row activation hands its exact resource to Save As and cancellation finishes without a destination")
        Await ResumeResourceTaskForChecksAsync() : layout()
        check(_downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail AndAlso _packProject Is shader, "cancelled resource task returns to the preserved release selection")
        _resourceSaveRunner = Async Function(release, destination, progress, token)
            starts += 1 : Await Task.Delay(1, token) : File.WriteAllText(destination, "UI save fixture")
        End Function
        releaseRow.Activate() : Await WaitPackUiAsync(Function() Not _busy) : layout()
        check(_taskOutcome = TaskOutcome.Succeeded AndAlso _taskResourceFile = saved AndAlso TaskFolderPath() = Path.GetDirectoryName(saved) AndAlso starts = 2 AndAlso choices = 2 AndAlso _taskInstancePath Is Nothing, "version-row retry exposes its saved-file folder without pretending to create a Minecraft instance")
        Await ResumeResourceTaskForChecksAsync() : layout()
        Dim generation = _taskGeneration
        _resourceSaveChooser = Function(release) Nothing
        releaseRow.Activate() : layout()
        check(Not _busy AndAlso _taskGeneration = generation AndAlso starts = 2, "cancelling the Save As chooser creates no resource task or file operation")
        SaveControlVisual(host, Path.Combine(directory, "community-resource-return.png"))
        _packReleases = New List(Of ModpackRelease) : InitializePackVersions() : layout()
        check(PackDetailHint.IsVisible AndAlso PackDetailHint.Text.Contains("可转到项目页面") AndAlso PackProjectWebsite.IsEnabled, "projects without downloadable files retain a clear terminal hint and their external project action")
        SelectResourceKind(CommunityResourceKind.Modpack) : _categoryMotion.Finish() : layout()
        releaseRow.Activate() : SaveCommunityResource(selected, releaseRow) : layout()
        check(Not _busy AndAlso _taskGeneration = generation AndAlso starts = 2, "retired resource rows cannot open a Save As chooser or start a task on another category")
        check(_resourceKind = CommunityResourceKind.Modpack AndAlso PackSearchImport.IsVisible AndAlso PackSearchCategory.Items.Count = 10 AndAlso ResourceLoaderFilters.Visibility = Visibility.Collapsed, "returning to modpacks restores the existing import entry and original category choices")
    End Function
    Private Async Function ResumeResourceTaskForChecksAsync() As Task
        ResumeInstallation(Nothing, Nothing) : _categoryMotion.Finish() : _surfaceMotion.Finish()
        Await Task.Delay(1)
    End Function
End Class
