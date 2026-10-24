Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    ' Exercise accepted supplements against the shared cards, lazy rows and WPF
    ' fold clocks as part of the catalogue refresh workflow.
    Private Function VerifySupplementalGrouping(directory As String) As JObject
        Dim result As New JObject(), observations As New JArray(), owner As New MainWindow(True), original = owner.Content
        Dim appearance = _appearance.ToJson(), host As New Border With {.Width = 850, .Height = 700}
        Dim layout = ControlLayout(host)
        Dim supplemental As Func(Of String, String, String, VersionEntry) =
            Function(id, kind, released)
                Dim entry = BrowserEntry(id, kind, released)
                entry.SupplementalId = id
                Return entry
            End Function
        Dim find As Func(Of String, PclCard) =
            Function(category) owner.VersionCards.Children.OfType(Of PclCard)().Single(Function(card) card.Title.StartsWith(category & " (", StringComparison.Ordinal))
        Dim observe As Action(Of String, PclCard) =
            Sub(stage, card)
                Dim body = TryCast(card?.Body, FrameworkElement)
                observations.Add(New JObject From {{"stage", stage}, {"hostLoaded", host.IsLoaded}, {"hostVisible", host.IsVisible},
                    {"hostWidth", host.ActualWidth}, {"hostHeight", host.ActualHeight}, {"view", owner._downloadView.ToString()},
                    {"downloadLoaded", owner.DownloadPage.IsLoaded}, {"downloadVisible", owner.DownloadPage.IsVisible},
                    {"versionsLoaded", owner.VersionPage.IsLoaded}, {"versionsVisible", owner.VersionPage.IsVisible},
                    {"viewportHeight", owner.VersionScroll.ViewportHeight}, {"extentHeight", owner.VersionScroll.ExtentHeight},
                    {"offset", owner.VersionScroll.VerticalOffset}, {"cardLoaded", If(card Is Nothing, False, card.IsLoaded)},
                    {"cardVisible", If(card Is Nothing, False, card.IsVisible)}, {"collapsed", If(card Is Nothing, True, card.Collapsed)},
                    {"cardHeight", If(card Is Nothing, 0, card.Height)}, {"actualHeight", If(card Is Nothing, 0, card.ActualHeight)},
                    {"desiredHeight", If(card Is Nothing, 0, card.DesiredSize.Height)}, {"bodyHeight", If(body Is Nothing, 0, body.ActualHeight)},
                    {"clock", card IsNot Nothing AndAlso card.HeightTransition IsNot Nothing}})
            End Sub
        Dim settle As Action =
            Sub()
                owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
                layout()
                For pass = 1 To 4
                    For Each card In owner.VersionCards.Children.OfType(Of PclCard)().ToArray()
                        Dim clock = card.HeightTransition
                        If clock Is Nothing Then Continue For
                        ' SkipToFill posts a timing command; ApplicationIdle layout
                        ' alone can still leave ActualHeight at the previous frame.
                        ' Sample the final tick and require Completed before the next fold.
                        clock.Controller.Pause()
                        If Not PumpMotionUntil(Function() clock.IsPaused) Then Throw New TimeoutException("Supplemental card clock did not pause while settling.")
                        clock.Controller.SeekAlignedToLastTick(clock.Timeline.Duration.TimeSpan.Add(TimeSpan.FromMilliseconds(1)), TimeSeekOrigin.BeginTime)
                        layout()
                        If Not PumpMotionUntil(Function() card.HeightTransition IsNot clock) Then Throw New TimeoutException("Supplemental card clock did not complete while settling.")
                    Next
                    layout()
                    If owner.VersionCards.Children.OfType(Of PclCard)().All(Function(card) card.HeightTransition Is Nothing) Then Return
                Next
                Throw New TimeoutException("Supplemental card layout kept retargeting its height clock.")
            End Sub
        Try
            owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
            Using source = PreviewSurface.Attach(host, 850, 700)
                ' Disable page handoff animation only. PclCard runs its own loaded
                ' fold clocks, which are sampled explicitly below.
                owner._motionPreview = False
                owner.DownloadTab.MotionPolicy.EnabledForChecks = False
                owner.SettingsTab.MotionPolicy.EnabledForChecks = False
                Dim release = BrowserEntry("1.21.1", "release", "2024-08-08T12:00:00Z")
                Dim officialOld = BrowserEntry("b1.7.3", "old_beta", "2011-07-08T12:00:00Z")
                Dim officialFool = BrowserEntry("15w14a", "snapshot", "2015-04-01T12:00:00Z")
                Dim officialTie = BrowserEntry("ordinary-tie", "snapshot", "2013-03-12T12:00:00Z")
                Dim normal As New List(Of VersionEntry) From {release, officialOld, officialFool}
                For index = 0 To 79
                    normal.Add(BrowserEntry("ordinary-preview-" & index, "snapshot", New DateTimeOffset(2014, 12, 31, 12, 0, 0, TimeSpan.Zero).AddDays(-index).ToString("o")))
                Next
                normal.Add(officialTie)
                owner._versions = normal : owner.UpdateVersions()
                owner.SetDownloadView(DownloadView.Versions) : owner.ShowDownload(Nothing, Nothing) : layout() : settle()
                Dim previewCard = find("预览版"), oldCard = find("远古版"), foolsCard = find("愚人节版")
                observe("initial-catalogue", previewCard)
                If Not PumpMotionUntil(Function() previewCard.IsLoaded AndAlso previewCard.IsVisible AndAlso owner.VersionScroll.ViewportHeight > 0) Then
                    Throw New InvalidOperationException("Supplemental grouping fixture requires a loaded visible version viewport.")
                End If
                Dim releaseRow = owner.BrowserRows("正式版").Single()
                Dim latestRow = owner.BrowserRows("最新版本").Single()
                previewCard.Collapsed = False : layout() : settle() : observe("initial-expanded", previewCard)
                Dim body = DirectCast(previewCard.Body, StackPanel)
                Dim firstRow = TryCast(body.Children(0), PclListItem)
                If firstRow Is Nothing Then firstRow = DirectCast(body.Children(0), PclLazyRow).Materialize()
                Dim untouchedLazy = body.Children.OfType(Of PclLazyRow)().Last()
                owner._keyboardVersionReturn = New WeakReference(Of PclListItem)(firstRow)
                owner.VersionScroll.ScrollToVerticalOffset(100) : layout()
                Dim offset = owner.VersionScroll.VerticalOffset
                Dim equalOne = supplemental("13w10b", "snapshot", "2013-03-12T12:00:00Z")
                Dim equalTwo = supplemental("13w10a", "snapshot", "2013-03-12T12:00:00Z")
                Dim oneTwo = supplemental("1.2", "snapshot", "2012-03-01T12:00:00Z")
                Dim alpha = supplemental("a1.1.1", "old_alpha", "2010-09-18T12:00:00Z")
                Dim beta = supplemental("b1.9-pre6", "old_beta", "2011-11-11T12:00:00Z")
                Dim blue = supplemental("2.0_blue", "snapshot", "2013-04-01T12:00:00Z")
                Dim purple = supplemental("2.0_purple", "snapshot", "2013-04-01T12:00:00Z")
                Dim additions As New List(Of VersionEntry) From {equalOne, equalTwo, oneTwo, alpha, beta, blue, purple}
                owner._versions = SupplementalVersions.Merge(owner._versions, additions)
                owner.AddSupplementalVersionCards() : layout()
                Dim returned As PclListItem = Nothing
                result("mixedExpandedCardBodyRowsAndLazyRetained") = find("预览版") Is previewCard AndAlso previewCard.Body Is body AndAlso
                    body.Children.Contains(firstRow) AndAlso body.Children.Contains(untouchedLazy) AndAlso
                    owner._keyboardVersionReturn.TryGetTarget(returned) AndAlso returned Is firstRow AndAlso Not previewCard.Collapsed AndAlso
                    Math.Abs(owner.VersionScroll.VerticalOffset - offset) < 0.5 AndAlso owner.BrowserRows("正式版").Single() Is releaseRow AndAlso owner.BrowserRows("最新版本").Single() Is latestRow
                result("mixedUnbuiltFoldedCardsStayDeferred") = find("远古版") Is oldCard AndAlso find("愚人节版") Is foolsCard AndAlso
                    oldCard.Collapsed AndAlso oldCard.Body Is Nothing AndAlso foolsCard.Collapsed AndAlso foolsCard.Body Is Nothing
                Dim rows = owner.BrowserRows("预览版")
                result("mixedChronologicalOrderingAndStableEqualTimes") = rows.Select(Function(row) DirectCast(row.Tag, VersionEntry)).SequenceEqual(
                    owner._versions.Where(Function(entry) New VersionPresentation(entry).Category = "预览版").OrderByDescending(Function(entry) entry.Released)) AndAlso
                    rows.Where(Function(row) DirectCast(row.Tag, VersionEntry).Released = officialTie.Released).Select(Function(row) row.Tag).SequenceEqual(New Object() {officialTie, equalOne, equalTwo})
                result("mixedHistoricalOneTwoKeepsSnapshotIdentity") = New VersionPresentation(oneTwo).Kind = "snapshot" AndAlso
                    New VersionPresentation(oneTwo).Category = "预览版" AndAlso rows.Single(Function(row) row.Tag Is oneTwo).Logo.EndsWith("/CommandBlock.png", StringComparison.Ordinal) AndAlso oneTwo.Kind = "snapshot"
                result("mixedCategoryAndSourceLabels") = owner.BrowserRows("远古版").Select(Function(row) row.Tag).SequenceEqual(New Object() {beta, officialOld, alpha}) AndAlso
                    owner.BrowserRows("愚人节版").Select(Function(row) row.Tag).SequenceEqual(New Object() {officialFool, blue, purple}) AndAlso
                    owner.VersionCards.Children.OfType(Of PclCard)().Count() = 5 AndAlso
                    owner.VersionCards.Children.OfType(Of PclCard)().All(Function(card) Not {"特供版本", "远古版本补充", "历史快照补充"}.Any(Function(title) card.Title.StartsWith(title & " (", StringComparison.Ordinal))) AndAlso
                    additions.All(Function(entry) New VersionPresentation(entry).SourceTag = "UVMC 特供下载") AndAlso
                    additions.All(Function(entry) owner.BrowserRows(New VersionPresentation(entry).Category).Single(Function(row) row.Tag Is entry).InfoTag = "UVMC 特供下载")
                Dim before = body.Children.Cast(Of UIElement)().ToArray()
                owner.AddSupplementalVersionCards() : layout()
                result("mixedRepeatedMergeIsVisuallyIdempotent") = previewCard.Body Is body AndAlso before.SequenceEqual(body.Children.Cast(Of UIElement)()) AndAlso
                    owner.BrowserRows("预览版").Count = 84 AndAlso owner.BrowserRows("最新版本").Single().Tag Is release

                settle() : previewCard.Collapsed = True : settle() : observe("built-folded", previewCard)
                Dim foldedBody = previewCard.Body
                Dim foldedExtra = supplemental("12w19a", "snapshot", "2012-05-10T12:00:00Z")
                owner._versions = SupplementalVersions.Merge(owner._versions, {foldedExtra})
                owner.AddSupplementalVersionCards() : layout()
                result("mixedBuiltFoldedCardKeepsBodyAndDoesNotOpen") = previewCard.Collapsed AndAlso previewCard.Body Is foldedBody AndAlso previewCard.HeightTransition Is Nothing
                previewCard.Collapsed = False : layout() : settle() : observe("reopened-before-exit-check", previewCard)
                result("mixedFoldedBuilderIncludesAcceptedEntry") = owner.BrowserRows("预览版").Any(Function(row) row.Tag Is foldedExtra)

                layout()
                If Not previewCard.IsLoaded OrElse Not previewCard.IsVisible OrElse previewCard.Collapsed OrElse previewCard.ActualHeight <= 41 Then
                    observe("invalid-exit-precondition", previewCard)
                    Throw New InvalidOperationException("Supplemental grouping fixture requires an expanded loaded card before checking its fold clock.")
                End If
                previewCard.Collapsed = True
                Dim closing = previewCard.HeightTransition
                observe("closing-clock-created", previewCard)
                If closing Is Nothing Then Throw New InvalidOperationException("Supplemental grouping fixture did not start the loaded fold clock.")
                closing.Controller.Pause() : closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(25), TimeSeekOrigin.BeginTime) : layout()
                Dim closingBody = previewCard.Body
                Dim closingExtra = supplemental("12w17a", "snapshot", "2012-04-26T12:00:00Z")
                owner._versions = SupplementalVersions.Merge(owner._versions, {closingExtra})
                owner.AddSupplementalVersionCards() : layout()
                result("mixedClosingAppendKeepsExitClockAndBody") = previewCard.Collapsed AndAlso previewCard.HeightTransition Is closing AndAlso previewCard.Body Is closingBody AndAlso
                    owner._surfaceMotion.Transition Is Nothing AndAlso owner._categoryMotion.Transition Is Nothing
                settle()
                previewCard.Collapsed = False : layout() : settle()
                result("mixedClosingThenReopeningHasNoStaleRows") = owner.BrowserRows("预览版").Count = 86 AndAlso
                    owner.BrowserRows("预览版").Any(Function(row) row.Tag Is closingExtra) AndAlso previewCard.Body Is body

                Dim future = supplemental("11w47a", "snapshot", "2035-02-02T12:00:00Z")
                owner.SelectVersion(release) : settle()
                Dim selected = owner._selectedEntry
                owner._versions = SupplementalVersions.Merge(owner._versions, {future}) : owner.AddSupplementalVersionCards() : layout()
                result("mixedHiddenAppendPreservesInstallSelection") = owner._selectedEntry Is selected AndAlso owner._downloadView = DownloadView.Install AndAlso
                    owner._surfaceMotion.Transition Is Nothing AndAlso owner._categoryMotion.Transition Is Nothing AndAlso owner.BrowserRows("最新版本").Single().Tag Is release
                owner.SetDownloadView(DownloadView.Versions) : owner.ShowDownload(Nothing, Nothing) : settle()
                Dim officialRows = owner.BrowserRows("预览版")
                result("mixedFutureSupplementNeverEntersLatest") = officialRows.First().Tag Is future AndAlso owner.BrowserRows("最新版本").Single().Tag Is release

                If directory IsNot Nothing Then
                    For Each card In owner.VersionCards.Children.OfType(Of PclCard)()
                        If card.Collapsible AndAlso Not card.Title.StartsWith("愚人节版 (", StringComparison.Ordinal) Then card.Collapsed = True
                    Next
                    foolsCard.Collapsed = False : settle()
                    owner.VersionScroll.ScrollToHome() : layout()
                    SaveControlVisual(host, IO.Path.Combine(directory, "supplemental-grouping-light.png"))
                    owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                    SaveControlVisual(host, IO.Path.Combine(directory, "supplemental-grouping-dark.png"))
                End If
            End Using
        Finally
            If directory IsNot Nothing Then IO.File.WriteAllText(IO.Path.Combine(directory, "supplemental-grouping-fixture.json"),
                New JObject From {{"observations", observations}, {"results", result.DeepClone()}}.ToString())
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(appearance)
        End Try
        Return result
    End Function
End Class
