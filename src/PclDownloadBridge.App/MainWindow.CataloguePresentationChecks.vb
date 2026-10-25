Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyCataloguePresentationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, appearance = _appearance.ToJson()
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 960, 740)
                layout()
                Dim work = owner.CheckCataloguePresentationAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 20000) Then Throw New TimeoutException("Catalogue presentation did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            owner.RestoreAppearance(appearance) : host.Child = Nothing : owner.Content = original
        End Try
    End Sub
    Private Async Function CheckCataloguePresentationAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareManagementFixture(directory), root = folders(0).Location
        For Each id In {"26w14a", "3D Shareware v1.34", "rd-132211"}
            Dim path = IO.Path.Combine(root, "versions", id) : IO.Directory.CreateDirectory(path)
            File.WriteAllText(IO.Path.Combine(path, id & ".json"), New JObject From {{"id", id}, {"type", If(id.StartsWith("rd-"), "old_alpha", "snapshot")}, {"mainClass", "net.minecraft.client.main.Main"}, {"libraries", New JArray()}}.ToString())
        Next
        Dim snapshot = IO.Directory.EnumerateFiles(IO.Path.GetDirectoryName(root), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : _surfaceMotion.Finish() : _managementMotion.Finish() : layout()
        Dim fools = _managementInstances.Where(Function(item) item.Name = "26w14a" OrElse item.Name = "3D Shareware v1.34").ToArray()
        Dim old = _managementInstances.Single(Function(item) item.Name = "rd-132211")
        check(fools.Length = 2 AndAlso fools.All(Function(item) ManagedSubtitle(item).Contains("愚人节版本") AndAlso ManagedIcon(item).EndsWith("/GoldBlock.png", StringComparison.Ordinal)), "management classifies both reported April Fools instances with the PCL gold-block icon")
        check(ManagedSubtitle(old).Contains("远古版本") AndAlso ManagedIcon(old).EndsWith("/CobbleStone.png", StringComparison.Ordinal), "management uses the ancient-version description and cobblestone icon for rd-132211")
        SelectManagedInstance(fools(0)) : _managementMotion.Finish() : layout()
        check(ManagedIntro.Info.Contains("愚人节版本") AndAlso ManagedIntro.Logo.EndsWith("/GoldBlock.png", StringComparison.Ordinal), "selected instance detail keeps the same category and icon as its management row")
        BackFromManagement(Nothing, Nothing) : _managementMotion.Finish() : layout()
        ManagementSearch.Text = "远古" : layout()
        check(ManagementRows.Children.Count = 1 AndAlso ManagementListPage.IsVisible, "category text is searchable after returning from details without a rescan")
        ManagementSearch.Text = "" : layout()
        SaveControlVisual(host, IO.Path.Combine(directory, "installed-version-categories.png"))
        check(snapshot.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso snapshot.Count = IO.Directory.EnumerateFiles(IO.Path.GetDirectoryName(root), "*", SearchOption.AllDirectories).Count(), "management rendering and return leave all external and instance configuration bytes unchanged")

        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod) : _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion()
        _resourceVersionFamilies = {"26.3", "26.2", "26.1", "1.21", "1.20", "1.19", "1.18", "1.17", "1.16", "1.15", "1.14", "1.13", "1.12", "1.11"}
        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "AANobbMI", .Slug = "sodium", .Name = "Sodium",
            .Summary = "A high-performance rendering engine.", .GameVersions = _resourceVersionFamilies.Take(9).Concat({"1.12"}).ToArray(), .Loaders = {"fabric", "neoforge"},
            .Tags = ModpackProjectMetadata.ReadTags(ModpackPlatform.Modrinth, New JArray("optimization"), CommunityResourceKind.Mod), .Updated = DateTimeOffset.UtcNow}
        Dim row As New PclResourceItem(project, _resourceVersionFamilies) With {.Tag = project}, clicks = 0
        AddHandler row.Click, Sub() clicks += 1
        PackProjectRows.Children.Clear() : PackProjectRows.Children.Add(row) : PackResultsCard.Visibility = Visibility.Visible : layout()
        SaveControlVisual(host, IO.Path.Combine(directory, "localized-resource-probe.png"))
        check(New TextRange(row.TitleLabel.ContentStart, row.TitleLabel.ContentEnd).Text = "钠 | Sodium" AndAlso row.SubtitleRun.FontSize = 12 AndAlso row.VersionLabel.Text = "Fabric / NeoForge 1.16+, 1.12", "resource row renders a Chinese title with subordinate English and the verified discontinuous support range")
        check(row.SummaryLabel.Inlines.OfType(Of InlineUIContainer)().Count() = 1 AndAlso row.ToolTip.ToString().Contains("性能优化") AndAlso row.ToolTip.ToString().Contains("1.12"), "Chinese category badge and full support tooltip survive compact presentation")
        Dim versionProbe As New TextBlock With {.Text = row.VersionLabel.Text, .FontFamily = row.VersionLabel.FontFamily, .FontSize = row.VersionLabel.FontSize}
        versionProbe.Measure(New Size(Double.PositiveInfinity, Double.PositiveInfinity))
        check(row.VersionLabel.ActualWidth + 0.1 >= versionProbe.DesiredSize.Width AndAlso row.SourceLabel.TranslatePoint(New Point(row.SourceLabel.ActualWidth, 0), row.StatisticsGrid).X <= row.StatisticsGrid.ActualWidth + 0.1,
            "compact compatibility text receives space before weighted gaps while source statistics stay within the row")
        ControlKey(row, Key.Enter, True) : ControlKey(row, Key.Enter, False)
        check(clicks = 1 AndAlso row.Tag Is project AndAlso project.Name = "Sodium" AndAlso project.Key = "Modrinth:AANobbMI:Mod", "localized row activation retains exact platform identity and unmodified original name")
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
            check(DirectCast(row.SubtitleRun.Foreground, SolidColorBrush).Color = DirectCast(FindResource("ColorBrushGray4"), SolidColorBrush).Color AndAlso row.TitleLabel.ActualHeight <= 17,
                "translated title and subdued subtitle fit their PCL title band and adapt to dark mode: " & dark)
            SaveControlVisual(host, IO.Path.Combine(directory, "localized-resource-" & If(dark, "dark", "blue") & ".png"))
        Next
        Dim before = _taskGeneration
        _packProject = project : ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project) : FinishPackContentMotion() : layout()
        Dim detailTitle = DirectCast(PackProjectIdentity.Children(0), PclResourceItem).TitleLabel
        check(InnerTitle.Text = "钠" AndAlso New TextRange(detailTitle.ContentStart, detailTitle.ContentEnd).Text = "钠 | Sodium", "detail heading and title back navigation share the accepted project's localized title")
        BackToPackSearch(Nothing, Nothing) : FinishPackContentMotion() : layout()
        check(_packMode = PackBrowserMode.Search AndAlso _taskGeneration = before AndAlso row.Tag Is project, "return from translated detail preserves the search snapshot and installation generation")
    End Function
End Class
