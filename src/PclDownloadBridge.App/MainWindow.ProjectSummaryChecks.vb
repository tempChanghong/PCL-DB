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
            VerifyProjectStatisticGeometryUi(directory, host, layout, check)
            check(_packProject Is project AndAlso _packReleases Is releases AndAlso _taskGeneration = generation, "metadata presentation and its canceled gestures preserve the accepted project, release list and installation ownership")
        Finally
            row.CanInteract = False
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(savedIntro)
            host.Width = savedWidth : RestoreAppearance(savedAppearance) : layout()
        End Try
    End Sub

    Private Sub VerifyProjectStatisticGeometryUi(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        ' Fixed PCL MyResourceItem.xaml: download column 55, time Auto/Min57,
        ' version label left4, other labels left5 and time glyph right1.
        Dim savedChildren = PackProjectIdentity.Children.Cast(Of UIElement)().ToArray(), savedAppearance = _appearance.ToJson(), savedWidth = host.Width
        Dim savedRows = PackProjectRows.Children.Cast(Of UIElement)().ToArray(), savedSearch = PackSearchSurface.Visibility, savedDetail = PackDetailSurface.Visibility
        Dim savedSidebar = DownloadSidebar.Visibility, savedToolbar = DownloadToolbar.Visibility, savedColumn = DownloadPage.ColumnDefinitions(0).Width
        Dim savedResults = PackResultsCard.Visibility, savedAlignment = PackResultsCard.HorizontalAlignment, savedScroll = ModpackPage.VerticalOffset
        Dim savedFocus = Keyboard.FocusedElement, project = _packProject, releases = _packReleases, generation = _taskGeneration
        Dim families = {"26.3", "26.2", "26.1", "1.21", "1.20", "1.19", "1.18", "1.17", "1.16", "1.15", "1.14", "1.13", "1.12"}
        Dim projects = {
            New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "statistic-short", .Name = "Short metadata", .Summary = "A short compatibility, count and date fixture.",
                .GameVersions = {"1.21.1"}, .Downloads = 27, .Updated = DateTimeOffset.UtcNow.AddHours(-2)},
            New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Id = "statistic-long", .Name = "Long metadata", .Summary = "Compact compatibility with two loaders, separated version ranges and a larger count.",
                .GameVersions = families.Take(9).Concat({"1.12"}).ToArray(), .Loaders = {"fabric", "neoforge"}, .Downloads = Long.MaxValue, .Updated = DateTimeOffset.UtcNow.AddDays(-40)}}
        Dim rows = projects.Select(Function(item) New PclResourceItem(item, families) With {.Tag = item}).ToArray(), samples As New StackPanel, clicks = 0
        For Each row In rows
            row.MotionPolicy.EnabledForChecks = False
            AddHandler row.Click, Sub() clicks += 1
        Next
        Dim observations As New JArray
        Try
            For Each search In {False, True}
                PackProjectIdentity.Children.Clear() : PackProjectRows.Children.Clear() : samples.Children.Clear()
                If search Then
                    For Each row In rows : PackProjectRows.Children.Add(row) : Next
                Else
                    For Each row In rows : samples.Children.Add(row) : Next
                    PackProjectIdentity.Children.Add(samples)
                End If
                PackResultsCard.Visibility = Visibility.Visible : PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                PackSearchSurface.Visibility = If(search, Visibility.Visible, Visibility.Collapsed)
                PackDetailSurface.Visibility = If(search, Visibility.Collapsed, Visibility.Visible)
                DownloadSidebar.Visibility = If(search, Visibility.Visible, Visibility.Collapsed)
                DownloadToolbar.Visibility = If(search, Visibility.Visible, Visibility.Collapsed)
                DownloadPage.ColumnDefinitions(0).Width = New GridLength(If(search, 135, 0))
                ModpackPage.ScrollToHome()
                For Each dark In {False, True}
                    For Each width As Double In {850, 810}
                        host.Width = width : layout()
                        For index = 0 To rows.Length - 1
                            Dim row = rows(index), title = row.TitleLabel, statistics = row.StatisticsGrid
                            Dim labels = {row.VersionLabel, row.DownloadLabel, row.UpdatedLabel, row.SourceLabel}
                            Dim glyphs = statistics.Children.OfType(Of Shapes.Path)().OrderBy(Function(glyph) Grid.GetColumn(glyph)).ToArray()
                            FocusLocalModElementForChecks(row) : ControlPointer(row, True)
                            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not dark}}) : layout()
                            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                            Dim suffix = If(search, "search", "detail") & "/" & If(dark, "dark", "light") & "/" & width & "/" & If(index = 0, "short", "long")
                            Dim download = statistics.ColumnDefinitions(4), updated = statistics.ColumnDefinitions(7)
                            check(download.Width.IsAbsolute AndAlso Math.Abs(download.Width.Value - 55) < 0.01 AndAlso Math.Abs(download.ActualWidth - 55) < 0.1 AndAlso
                                updated.Width.IsAuto AndAlso Math.Abs(updated.MinWidth - 57) < 0.01 AndAlso updated.ActualWidth >= 56.9 AndAlso row.UpdatedLabel.ActualWidth >= 49.9,
                                "PCL resource statistics reserve the measured 55 DIP count and Auto minimum 57 DIP date columns: " & suffix)
                            Dim expectedGaps = {4.0, 5.0, 6.0, 5.0}, marginsAndGaps = True
                            For pair = 0 To labels.Length - 1
                                Dim glyphRight = glyphs(pair).TranslatePoint(New Point(glyphs(pair).ActualWidth, 0), statistics).X
                                Dim labelLeft = labels(pair).TranslatePoint(New Point(), statistics).X
                                marginsAndGaps = marginsAndGaps AndAlso labels(pair).Margin = New Thickness(If(pair = 0, 4, 5), 0, 2, 0) AndAlso
                                    glyphs(pair).Margin = New Thickness(0, 0, If(pair = 2, 1, 0), 0) AndAlso Math.Abs(labelLeft - glyphRight - expectedGaps(pair)) < 0.1
                            Next
                            check(marginsAndGaps, "actual glyph and text positions retain PCL's 4 5 6 5 DIP gaps including the time glyph's right margin: " & suffix)
                            Dim elements = glyphs.Cast(Of FrameworkElement)().Concat(labels).ToArray()
                            Dim bounded = elements.All(Function(element) element.ActualWidth > 0 AndAlso element.TranslatePoint(New Point(), statistics).X >= -0.1 AndAlso
                                element.TranslatePoint(New Point(element.ActualWidth, element.ActualHeight), statistics).X <= statistics.ActualWidth + 0.1 AndAlso
                                element.TranslatePoint(New Point(), statistics).Y >= -0.1 AndAlso element.TranslatePoint(New Point(0, element.ActualHeight), statistics).Y <= statistics.ActualHeight + 0.1)
                            Dim noOverlap = Enumerable.Range(0, 3).All(Function(pair) labels(pair).TranslatePoint(New Point(labels(pair).ActualWidth, 0), statistics).X <= glyphs(pair + 1).TranslatePoint(New Point(), statistics).X + 0.1)
                            Dim natural As New TextBlock With {.Text = row.VersionLabel.Text, .FontFamily = row.VersionLabel.FontFamily, .FontSize = row.VersionLabel.FontSize}
                            natural.Measure(New Size(Double.PositiveInfinity, Double.PositiveInfinity))
                            check(bounded AndAlso noOverlap AndAlso (search OrElse width <> 850 OrElse row.VersionLabel.ActualWidth + 0.1 >= natural.DesiredSize.Width) AndAlso
                                labels.All(Function(label) label.TextTrimming = TextTrimming.CharacterEllipsis) AndAlso row.ToolTip.ToString().Contains(projects(index).Downloads.ToString("N0", Globalization.CultureInfo.InvariantCulture)),
                                "metadata remains bounded without overlap preserves exact tooltip counts and retains complete compatibility in the 850 DIP detail: " & suffix)
                            check(row.TitleLabel Is title AndAlso row.StatisticsGrid Is statistics AndAlso labels.SequenceEqual({row.VersionLabel, row.DownloadLabel, row.UpdatedLabel, row.SourceLabel}) AndAlso
                                row.Tag Is projects(index) AndAlso row.Height = 64 AndAlso row.Focusable AndAlso Keyboard.FocusedElement Is row AndAlso row.HoverTransition Is Nothing AndAlso clicks = 0 AndAlso
                                DirectCast(row.SourceLabel.Foreground, SolidColorBrush).Color = DirectCast(FindResource("ColorBrushGray4"), SolidColorBrush).Color AndAlso
                                _packProject Is project AndAlso _packReleases Is releases AndAlso _taskGeneration = generation,
                                "palette changes preserve the focused resource row its labels project ownership and reduced-motion hover state: " & suffix)
                            observations.Add(New JObject From {{"case", suffix}, {"statisticsWidth", statistics.ActualWidth}, {"statisticsHeight", statistics.ActualHeight},
                                {"columns", New JArray(statistics.ColumnDefinitions.Select(Function(column) New JObject From {{"width", column.Width.Value}, {"unit", column.Width.GridUnitType.ToString()}, {"minimum", column.MinWidth}, {"actual", column.ActualWidth}}))},
                                {"glyphs", New JArray(glyphs.Select(Function(glyph) ProjectStatisticBoundsForChecks(glyph, statistics)))},
                                {"labels", New JArray(labels.Select(Function(label) ProjectStatisticBoundsForChecks(label, statistics)))}, {"compatibilityNaturalWidth", natural.DesiredSize.Width},
                                {"focused", Keyboard.FocusedElement Is row}, {"hasHoverClock", row.HoverTransition IsNot Nothing}})
                            ControlPointer(row, False)
                        Next
                        SaveControlVisual(host, IO.Path.Combine(directory, "project-statistic-geometry-" & If(search, "search", "detail") & "-" & If(dark, "dark", "light") & "-" & width & ".png"))
                    Next
                Next
                Next
            IO.File.WriteAllText(IO.Path.Combine(directory, "project-statistic-geometry.json"), observations.ToString())
        Finally
            For Each row In rows : row.CanInteract = False : Next
            PackProjectIdentity.Children.Clear() : PackProjectRows.Children.Clear() : samples.Children.Clear()
            For Each child In savedChildren : PackProjectIdentity.Children.Add(child) : Next
            For Each child In savedRows : PackProjectRows.Children.Add(child) : Next
            PackSearchSurface.Visibility = savedSearch : PackDetailSurface.Visibility = savedDetail
            DownloadSidebar.Visibility = savedSidebar : DownloadToolbar.Visibility = savedToolbar : DownloadPage.ColumnDefinitions(0).Width = savedColumn
            PackResultsCard.Visibility = savedResults : PackResultsCard.HorizontalAlignment = savedAlignment
            host.Width = savedWidth : RestoreAppearance(savedAppearance) : layout()
            ModpackPage.ScrollToVerticalOffset(savedScroll)
            If savedFocus IsNot Nothing Then Keyboard.Focus(savedFocus)
        End Try
    End Sub

    Private Shared Function ProjectStatisticBoundsForChecks(element As FrameworkElement, statistics As Grid) As JObject
        Dim origin = element.TranslatePoint(New Point(), statistics)
        Return New JObject From {{"column", Grid.GetColumn(element)}, {"x", origin.X}, {"y", origin.Y}, {"width", element.ActualWidth}, {"height", element.ActualHeight},
            {"margin", element.Margin.ToString()}, {"text", If(TypeOf element Is TextBlock, DirectCast(element, TextBlock).Text, "")}}
    End Function
End Class
