Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyPackFilterBarUi(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim savedReleases = _packReleases, savedTarget = _packTargetGame, savedWidth = host.Width, savedAppearance = _appearance.ToJson()
        Dim generation = _taskGeneration, session = _packSearchSession, project = _packProject
        Dim games = {"26.3", "26.2", "26.1", "1.21", "1.20", "1.19", "1.18", "1.17", "1.16"}
        Dim releases As Func(Of IEnumerable(Of String), List(Of ModpackRelease)) = Function(versions)
                                                                                     Return versions.Select(Function(game, index) New ModpackRelease With {
                                                                                         .Platform = ModpackPlatform.Modrinth, .ProjectId = project.Id, .Id = "filterbar" & index,
                                                                                         .Name = "布局验证 " & game, .Version = game, .GameVersions = {game}, .Loaders = {"fabric"},
                                                                                         .FileName = "filterbar-" & index & ".mrpack", .Url = "https://cdn.modrinth.com/filterbar-" & index & ".mrpack",
                                                                                         .Size = 100, .Sha1 = New String("a"c, 40), .ReleaseType = "release", .Published = DateTimeOffset.UtcNow.AddDays(-2)}).ToList()
                                                                                 End Function
        Dim wheelObservations As New JArray
        Dim wheel As Action(Of Integer) = Sub(delta)
                                              Dim beforeX = PackVersionFilterViewport.HorizontalOffset, beforeY = ModpackPage.VerticalOffset
                                              Dim args As New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent}
                                              PackVersionFilterViewport.RaiseEvent(args)
                                              Dim previewHandled = args.Handled
                                              If Not args.Handled Then
                                                  args.RoutedEvent = Mouse.MouseWheelEvent : PackVersionFilterViewport.RaiseEvent(args)
                                              End If
                                              ModpackPage.MotionClock?.Controller.SkipToFill() : layout()
                                              wheelObservations.Add(New JObject From {{"delta", delta}, {"overflow", PackVersionFilterViewport.ScrollableWidth},
                                                  {"previewHandled", previewHandled}, {"beforeX", beforeX}, {"afterX", PackVersionFilterViewport.HorizontalOffset},
                                                  {"beforeY", beforeY}, {"afterY", ModpackPage.VerticalOffset}})
                                              IO.File.WriteAllText(IO.Path.Combine(directory, "filter-wheel-routing-observations.json"), wheelObservations.ToString())
                                          End Sub
        Try
            FinishPackContentMotion()
            _packReleases = releases(games) : _packTargetGame = "" : InitializePackVersions() : layout()
            check(PackVersionFilters.Orientation = Orientation.Horizontal AndAlso PackVersionFilters.HorizontalAlignment = HorizontalAlignment.Left AndAlso PackVersionFilters.VerticalAlignment = VerticalAlignment.Top,
                "the version filter stack matches PCL's horizontal left/top alignment")
            check(PackVersionFilters.Children.OfType(Of PclRadioButton)().Select(Function(button) button.Text).SequenceEqual(New String() {"全部"}.Concat(games)),
                "the screenshot's nine Minecraft families remain distinct and ordered after all four PCL grouping passes")
            For Each windowWidth As Double In {850, 810}
                host.Width = windowWidth : ModpackPage.ScrollToHome() : layout()
                Dim buttons = PackVersionFilters.Children.OfType(Of PclRadioButton)().ToArray(), allFilter = buttons(0), oldestFilter = buttons.Last()
                check(buttons.All(Function(button) Math.Abs(button.TranslatePoint(New Point(), PackVersionFilters).Y) < 0.01) AndAlso Math.Abs(PackVersionFilterCard.ActualHeight - 47) < 0.01,
                    "all ten source-shaped capsules stay on one 47 DIP card at window width " & windowWidth)
                check(PackVersionFilterViewport.ScrollableWidth = 0 AndAlso PackVersionFilterViewport.ComputedHorizontalScrollBarVisibility = Visibility.Collapsed AndAlso
                    oldestFilter.TranslatePoint(New Point(oldestFilter.ActualWidth, 0), PackVersionFilterViewport).X <= PackVersionFilterViewport.ActualWidth + 0.01,
                    "the minimum supported width shows every filter without clipping or a redundant horizontal scrollbar: " & windowWidth)
                SelectPackVersionFilter(oldestFilter) : layout()
                Dim card = DirectCast(PackVersionCards.Children(0), PclCard), rows = card.Body
                check(_packGameFilter = "1.16" AndAlso PackVersionCards.Children.Count = 1 AndAlso CStr(card.Tag) = "1.16" AndAlso Not card.Collapsed AndAlso oldestFilter.Checked AndAlso Not allFilter.Checked,
                    "selecting the final capsule shows only its exact versions and one selected state at width " & windowWidth)
                SelectPackVersionFilter(allFilter) : layout()
                check(PackVersionCards.Children.Count = games.Length AndAlso PackVersionCards.Children.Contains(card) AndAlso card.Body Is rows AndAlso allFilter.Checked AndAlso Not oldestFilter.Checked,
                    "the all capsule restores existing cards and built rows without resetting their expansion at width " & windowWidth)
                ModpackPage.ScrollToHome() : layout() : wheel(-120)
                check(ModpackPage.VerticalOffset > 0 AndAlso PackVersionFilterViewport.HorizontalOffset = 0,
                    "a fitting filter row lets the surrounding details page consume ordinary wheel input at width " & windowWidth)
            Next
            host.Width = 850 : ModpackPage.ScrollToHome() : layout()
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                Dim allFilter = DirectCast(PackVersionFilters.Children(0), PclRadioButton)
                check(allFilter.Checked AndAlso PackVersionFilters.Children.OfType(Of PclRadioButton)().Where(Function(button) button.Checked).Count() = 1,
                    "theme changes keep the source filter selection and single-row layout in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "filter-bar-" & If(dark, "dark", "blue") & ".png"))
            Next
            VerifyFilterGestureOwnership(directory, layout, check)
            VerifyDetailCardGestureOwnership(directory, layout, check)
            Dim middleFilter = PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) button.Text = "1.20")
            Dim cached = _packVersionCache("1.20")
            For index = 1 To 20
                SelectPackVersionFilter(middleFilter) : SelectPackVersionFilter(DirectCast(PackVersionFilters.Children(0), PclRadioButton))
            Next
            layout()
            check(PackVersionCards.Children.Contains(cached) AndAlso _packVersionCache.Count = games.Length AndAlso _packGameFilter = "" AndAlso
                PackVersionFilters.Children.OfType(Of PclRadioButton)().Where(Function(button) button.Checked).Count() = 1,
                "rapid filter reversals preserve card ownership and exactly one selected capsule")
            SelectPackVersionFilter(middleFilter) : layout()
            Dim selectedRow = DirectCast(DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children(0), PclListItem)
            selectedRow.Activate() : layout()
            check(_choosingOnline AndAlso PackNameDescription.Text.Contains(DirectCast(selectedRow.Tag, ModpackRelease).FileName) AndAlso _taskGeneration = generation,
                "the filtered version opens confirmation for its original archive without starting installation before approval")
            CancelOnlinePackName(Nothing, Nothing)
            If Not PumpMotionUntil(Function() Not _choosingOnline) Then Throw New InvalidOperationException("Filter confirmation did not cancel.")
            layout()

            ' Hypothetical future families exercise overflow without shrinking the real window minimum.
            _packReleases = releases(Enumerable.Range(260, 30).Select(Function(family) (family \ 10).ToString(Globalization.CultureInfo.InvariantCulture) & "." & (family Mod 10).ToString(Globalization.CultureInfo.InvariantCulture)))
            host.Width = 810 : InitializePackVersions() : ModpackPage.ScrollToHome() : layout()
            check(PackVersionFilterViewport.ScrollableWidth > 0 AndAlso PackVersionFilterViewport.ComputedHorizontalScrollBarVisibility = Visibility.Visible AndAlso
                PackVersionFilters.Children.OfType(Of PclRadioButton)().All(Function(button) Math.Abs(button.TranslatePoint(New Point(), PackVersionFilters).Y) < 0.01),
                "overflowing future families stay in one horizontal row with an accessible scroll range")
            Dim bar = TryCast(PackVersionFilterViewport.Template.FindName("PART_HorizontalScrollBar", PackVersionFilterViewport), ScrollBar)
            bar?.ApplyTemplate()
            Dim track = If(bar Is Nothing, Nothing, TryCast(bar.Template.FindName("PART_Track", bar), Track))
            check(bar IsNot Nothing AndAlso track IsNot Nothing AndAlso bar.Orientation = Orientation.Horizontal AndAlso track.Orientation = Orientation.Horizontal AndAlso Math.Abs(bar.ActualHeight - 8) < 0.01,
                "overflow uses the existing PCL eight-DIP horizontal scrollbar and correctly oriented native track")
            wheel(-120)
            check(PackVersionFilterViewport.HorizontalOffset > 0 AndAlso ModpackPage.VerticalOffset = 0,
                "an overflowing filter row consumes wheel input horizontally without moving the details page")
            VerifyPackFilterWheelRoutes(directory, host, layout, check)
            Dim offset = PackVersionFilterViewport.HorizontalOffset
            ScrollBar.LineRightCommand.Execute(Nothing, bar) : layout()
            check(PackVersionFilterViewport.HorizontalOffset > offset, "native horizontal scrollbar commands move the overflow viewport")
            Dim last = DirectCast(PackVersionFilters.Children(PackVersionFilters.Children.Count - 1), PclRadioButton)
            last.BringIntoView() : layout()
            check(last.TranslatePoint(New Point(last.ActualWidth, 0), PackVersionFilterViewport).X <= PackVersionFilterViewport.ActualWidth + 0.01,
                "bringing an offscreen capsule into view exposes the full target for keyboard navigation")
            offset = PackVersionFilterViewport.HorizontalOffset
            SelectPackVersionFilter(last) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "filter-overflow-selection.json"), New JObject From {
                {"before", offset}, {"after", PackVersionFilterViewport.HorizontalOffset}, {"maximum", PackVersionFilterViewport.ScrollableWidth},
                {"checked", last.Checked}, {"filter", _packGameFilter}, {"target", CStr(last.Tag)}, {"cards", New JArray(PackVersionCards.Children.OfType(Of PclCard)().Select(Function(card) CStr(card.Tag)))}}.ToString())
            check(last.Checked AndAlso PackVersionCards.Children.Count = 1 AndAlso CStr(DirectCast(PackVersionCards.Children(0), PclCard).Tag) = CStr(last.Tag) AndAlso
                Math.Abs(PackVersionFilterViewport.HorizontalOffset - Math.Min(offset, PackVersionFilterViewport.ScrollableWidth)) < 0.01,
                "selecting an overflowed capsule retains its valid viewport position and the exact release family")
            offset = PackVersionFilterViewport.HorizontalOffset
            PackDetailResultsHost.IsHitTestVisible = False : wheel(120)
            check(PackVersionFilterViewport.HorizontalOffset = offset, "page input gating also blocks horizontal filter scrolling")
            PackDetailResultsHost.IsHitTestVisible = True : PackVersionFilterViewport.IsEnabled = False : wheel(120)
            check(PackVersionFilterViewport.HorizontalOffset = offset, "disabled filter viewports cannot consume horizontal wheel input")
            PackVersionFilterViewport.IsEnabled = True
            _packReleases = releases(games) : InitializePackVersions() : layout()
            SelectPackVersionFilter(last)
            check(PackVersionFilterViewport.HorizontalOffset = 0 AndAlso PackVersionFilterViewport.ComputedHorizontalScrollBarVisibility = Visibility.Collapsed AndAlso _packGameFilter = "",
                "a refreshed normal filter row retires stale capsules and resets only its horizontal viewport")
            _packReleases = releases({"1.20.1"}) : InitializePackVersions() : layout()
            check(Not PackVersionFilterCard.IsVisible AndAlso PackVersionCards.Children.Count = 1 AndAlso Not DirectCast(PackVersionCards.Children(0), PclCard).Collapsed,
                "one real game group hides the filter card and keeps its versions directly accessible")
            check(_taskGeneration = generation AndAlso _packSearchSession Is session AndAlso _packProject Is project,
                "filter layout, overflow, selection and canceled confirmation preserve search and installation ownership")
        Finally
            If _onlinePrompt IsNot Nothing Then CompleteOnlinePrompt(Nothing)
            PackDetailResultsHost.IsHitTestVisible = True : PackVersionFilterViewport.IsEnabled = True
            ModpackPage.StopScrolling() : RestoreAppearance(savedAppearance)
            host.Width = savedWidth : _packReleases = savedReleases : _packTargetGame = savedTarget
            InitializePackVersions() : ModpackPage.ScrollToHome() : layout()
        End Try
    End Sub
End Class
