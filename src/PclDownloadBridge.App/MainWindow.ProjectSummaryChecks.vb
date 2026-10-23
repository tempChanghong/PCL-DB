Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyProjectSummaryUi(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim savedIntro = PackProjectIdentity.Children(0), savedAppearance = _appearance.ToJson(), savedWidth = host.Width
        Dim project = _packProject, releases = _packReleases, generation = _taskGeneration
        Dim fixture As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "summaryfixture", .Name = "Fabulously Optimized",
            .Summary = "Beautiful graphics, speedy performance and familiar features in a simple package.", .Downloads = 19194999,
            .Updated = DateTimeOffset.UtcNow.AddDays(-2), .GameVersions = {"1.9", "1.20.1", "26.3", "1.21.1", "1.20.2"}}
        Dim row As New PclResourceItem(fixture) With {.Tag = fixture, .CanInteract = False}, clicks = 0
        AddHandler row.Click, Sub() clicks += 1
        Try
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(row) : ModpackPage.ScrollToHome() : layout()
            check(row.VersionLabel.Text = "26.3, 1.21, 1.20" AndAlso row.DownloadLabel.Text = "1919 万" AndAlso row.UpdatedLabel.Text = "2 天前" AndAlso row.SourceLabel.Text = "Modrinth",
                "the visible header shares PCL's compact project counts, short dates and numerically sorted version families")
            check(row.StatisticsGrid.ColumnDefinitions.Count = 12 AndAlso row.StatisticsGrid.Children.OfType(Of Shapes.Path)().Count() = 4 AndAlso
                row.StatisticsGrid.Children.OfType(Of Shapes.Path)().Select(Function(glyph) glyph.Height).SequenceEqual({11.0, 10.5, 11.5, 11.5}),
                "project statistics use four source glyphs and the source twelve-column icon/text rhythm")
            check(row.ToolTip.ToString().Contains("19,194,999") AndAlso fixture.GameVersions.All(Function(game) row.ToolTip.ToString().Contains(game)) AndAlso row.ToolTip.ToString().Contains(fixture.Updated.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")),
                "compact metadata retains the exact download count, complete compatibility list and timestamp in its tooltip")
            ControlPointer(row, True) : PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            ControlKey(row, Key.Enter, True) : ControlKey(row, Key.Enter, False)
            check(row.IsHitTestVisible AndAlso Not row.Focusable AndAlso row.Cursor Is Cursors.Arrow AndAlso clicks = 0 AndAlso row.HoverTransition Is Nothing,
                "a static header remains tooltip-reachable while pointer and keyboard gestures cannot animate or navigate")
            row.CanInteract = True : ControlKey(row, Key.Space, True) : row.CanInteract = False : row.CanInteract = True : ControlKey(row, Key.Space, False)
            check(clicks = 0 AndAlso row.Focusable AndAlso row.Cursor Is Cursors.Hand, "changing a resource row to a static header cancels an in-flight gesture before interaction can be restored")
            ControlKey(row, Key.Enter, True) : ControlKey(row, Key.Enter, False)
            check(clicks = 1, "an interactive resource row still activates exactly once after restoring its interaction policy")
            row.CanInteract = False : ControlPointer(row, False)
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                check(row.StatisticsGrid.Children.OfType(Of Shapes.Path)().All(Function(glyph) DirectCast(glyph.Fill, SolidColorBrush).Color = DirectCast(FindResource(If(Grid.GetColumn(glyph) = 0, "ColorBrushGray3", "ColorBrushGray4")), SolidColorBrush).Color),
                    "project statistic glyphs follow current theme resources in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "project-summary-" & If(dark, "dark", "blue") & ".png"))
            Next
            For Each windowWidth As Double In {850, 810}
                host.Width = windowWidth : layout()
                Dim labels = row.StatisticsGrid.Children.OfType(Of TextBlock)().ToArray()
                check(labels.All(Function(label) label.TranslatePoint(New Point(), row.StatisticsGrid).X >= 0 AndAlso label.TranslatePoint(New Point(label.ActualWidth, 0), row.StatisticsGrid).X <= row.StatisticsGrid.ActualWidth + 0.01),
                    "all four metadata labels stay within the statistics row at supported width " & windowWidth)
                check(labels.Zip(labels.Skip(1), Function(left, right) left.TranslatePoint(New Point(left.ActualWidth, 0), row.StatisticsGrid).X <= right.TranslatePoint(New Point(), row.StatisticsGrid).X).All(Function(value) value),
                    "version, download, date and source labels cannot overlap at supported width " & windowWidth)
            Next
            Dim narrow As New PclResourceItem(New ModpackProject With {.Name = New String("长"c, 200), .Summary = "第一行" & vbCrLf & "第二行", .Platform = ModpackPlatform.CurseForge,
                .GameVersions = {}, .Downloads = Long.MaxValue, .Updated = DateTimeOffset.UtcNow.AddHours(-25)})
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(narrow) : layout()
            check(row.StatisticsGrid.Children.OfType(Of Shapes.Path)().Zip(narrow.StatisticsGrid.Children.OfType(Of Shapes.Path)(), Function(left, right) left.Data Is right.Data AndAlso left.Data.IsFrozen).All(Function(value) value),
                "every resource row reuses immutable glyph geometry instead of parsing the four complex paths on each render")
            check(narrow.VersionLabel.Text = "版本未知" AndAlso narrow.SourceLabel.Text = "CurseForge" AndAlso narrow.UpdatedLabel.Text = "25 小时前" AndAlso narrow.Height = 64,
                "missing CurseForge project compatibility stays unknown and a 25-hour date never wraps to one hour")
            check(narrow.Children.OfType(Of Grid)().Single().Children.OfType(Of Grid)().Single().Children.OfType(Of TextBlock)().Last().Text = "第一行第二行" AndAlso narrow.ToolTip.ToString().Contains(vbCrLf),
                "multiline remote descriptions remain on the source's single summary line while the tooltip preserves full text")
            SaveControlVisual(host, IO.Path.Combine(directory, "project-summary-long.png"))
            check(_packProject Is project AndAlso _packReleases Is releases AndAlso _taskGeneration = generation, "metadata presentation and its canceled gestures preserve the accepted project, release list and installation ownership")
        Finally
            row.CanInteract = False
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(savedIntro)
            host.Width = savedWidth : RestoreAppearance(savedAppearance) : layout()
        End Try
    End Sub
End Class
