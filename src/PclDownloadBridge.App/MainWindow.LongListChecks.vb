Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyLongVersionLists(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim savedVersions = _versions
        Dim savedDirty = _versionsDirty
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim previewSource As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout()
                                   FlushControlLayout()
                                   host.UpdateLayout()
                               End Sub
        Try
            Dim creations = 0
            Dim once As New PclLazyRow(Function()
                                           creations += 1
                                           Return New PclListItem With {.Title = "只创建一次", .Height = 42}
                                       End Function)
            Dim siblings As New StackPanel
            Dim before As New TextBlock, after As New TextBlock
            siblings.Children.Add(before) : siblings.Children.Add(once) : siblings.Children.Add(after)
            Dim realized = once.Materialize()
            check(once.Materialize() Is realized AndAlso creations = 1 AndAlso siblings.Children.Count = 3 AndAlso siblings.Children(0) Is before AndAlso siblings.Children(1) Is realized AndAlso siblings.Children(2) Is after, "explicit lazy initialization is idempotent and preserves the row's sibling position")
            _versionsDirty = False
            _versions = Enumerable.Range(0, 1200).Select(Function(i) New VersionEntry With {.Id = "1.99." & (1200 - i), .Kind = "release", .Released = New DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i), .Url = "https://example.invalid/" & i & ".json"}).ToList()
            SetDownloadView(DownloadView.Versions)
            SwitchMainPage(False)
            UpdateVersions()
            Dim card = VersionCards.Children.OfType(Of PclCard)().Single(Function(c) c.Title.StartsWith("正式版 (", StringComparison.Ordinal))
            check(card.Body Is Nothing, "a closed long category does not create its version content")
            card.Collapsed = False
            Dim body = DirectCast(card.Body, StackPanel)
            check(body.Children.OfType(Of PclListItem)().Count() < 30, "opening a 1200-version category does not construct every expensive PCL row")
            check(body.Children.Count = 1200 AndAlso body.Children.OfType(Of FrameworkElement)().All(Function(row) row.Height = 42), "long categories reserve every 42 DIP row without pagination or missing scroll extent")
            Me.Content = Nothing : host.Child = content
            previewSource = PreviewSurface.Attach(host, 850, 700)
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            layout()
            Dim visibleCount = body.Children.OfType(Of PclListItem)().Count()
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "long-list-layout-0.24.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.Count = visibleCount, .HostVisible = host.IsVisible, .BodyVisible = body.IsVisible, .BodySize = body.RenderSize.ToString(), .Viewport = VersionScroll.ViewportHeight, .Offset = VersionScroll.VerticalOffset, .Bounds = body.TranslatePoint(New Point(), VersionScroll).ToString(), .FirstSize = DirectCast(body.Children(0), FrameworkElement).RenderSize.ToString(), .ScrollVisibility = VersionScroll.Visibility.ToString()}, Newtonsoft.Json.Formatting.Indented))
            check(VersionScroll.ViewportHeight > 0 AndAlso visibleCount > 0 AndAlso visibleCount < 30, "layout creates only the viewport's rows in a 1200-version catalogue")
            check(Math.Abs(body.ActualHeight - 1200 * 42) < 0.5 AndAlso VersionScroll.ScrollableHeight > 49000, "deferred rows preserve the full measured list height and scrollbar range")
            Dim head = TryCast(body.Children(0), PclListItem)
            check(head IsNot Nothing AndAlso head.Tag Is _versions(0), "the first visible deferred row carries its original manifest entry")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "align-long-head-after-0.24.png"))
            Dim listTop = body.TranslatePoint(New Point(), VersionScroll).Y + VersionScroll.VerticalOffset
            Dim reattached = DirectCast(body.Children(300), PclLazyRow)
            body.Children.Remove(reattached) : layout()
            body.Children.Insert(300, reattached) : layout()
            VersionScroll.ScrollToVerticalOffset(listTop + 300 * 42)
            layout()
            check(TypeOf body.Children(300) Is PclListItem AndAlso DirectCast(body.Children(300), PclListItem).Tag Is _versions(300), "an unloaded placeholder resumes viewport loading after it is reattached")
            VersionScroll.ScrollToVerticalOffset(listTop + 600 * 42)
            layout()
            check(TypeOf body.Children(600) Is PclListItem AndAlso DirectCast(body.Children(600), PclListItem).Tag Is _versions(600), "jumping to the middle materializes the correct row and download identity")
            check(body.Children.OfType(Of PclListItem)().Count() < 60, "a middle jump does not instantiate the hundreds of skipped versions")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "align-long-middle-after-0.24.png"))
            VersionScroll.ScrollToEnd()
            layout()
            Dim tail = TryCast(body.Children(1199), PclListItem)
            check(tail IsNot Nothing AndAlso tail.Tag Is _versions(1199), "the last deferred version is reachable with the ordinary page scrollbar")
            check(body.Children.OfType(Of PclListItem)().Count() < 90, "jumping to the end still leaves unvisited rows deferred")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "align-long-tail-after-0.24.png"))
            PointerEvent(tail, Mouse.MouseDownEvent) : PointerEvent(tail, Mouse.MouseUpEvent)
            check(_selectedEntry Is _versions(1199) AndAlso InstanceName.Text = _versions(1199).Id AndAlso InstallPage.Visibility = Visibility.Visible, "a real click on the last lazy row opens the matching installation options")
            Dim hiddenCount = body.Children.OfType(Of PclListItem)().Count()
            layout()
            check(body.Children.OfType(Of PclListItem)().Count() = hiddenCount, "hidden version categories do not keep creating rows behind installation options")
            BackToVersions(Nothing, Nothing)
            VersionScroll.ScrollToTop()
            layout()
            check(card.Body Is body AndAlso body.Children(0) Is head, "returning and revisiting the start reuses already materialized PCL rows")
            card.Collapsed = True : layout()
            card.Collapsed = False : layout()
            check(card.Body Is body AndAlso body.Children(0) Is head, "folding and reopening a long category retains its row instances")

            Dim oldCount = body.Children.OfType(Of PclListItem)().Count()
            _versions = _versions.Take(30).Select(Function(v) New VersionEntry With {.Id = v.Id, .Kind = v.Kind, .Released = v.Released, .Url = v.Url & "?refreshed"}).ToList()
            UpdateVersions()
            Dim newCard = VersionCards.Children.OfType(Of PclCard)().Single(Function(c) c.Title.StartsWith("正式版 (", StringComparison.Ordinal))
            newCard.Collapsed = False
            VersionScroll.ScrollToTop()
            layout()
            Dim newBody = DirectCast(newCard.Body, StackPanel)
            Dim refreshed = TryCast(newBody.Children(0), PclListItem)
            check(body.Children.OfType(Of PclListItem)().Count() = oldCount AndAlso newBody IsNot body, "catalogue refresh cannot revive offscreen initializers from a detached old category")
            PointerEvent(refreshed, Mouse.MouseDownEvent) : PointerEvent(refreshed, Mouse.MouseUpEvent)
            check(_selectedEntry Is _versions(0) AndAlso _selectedEntry.Url.EndsWith("?refreshed", StringComparison.Ordinal), "refreshed lazy rows select the new metadata rather than the old same-name entry")
        Finally
            If previewSource IsNot Nothing Then
                previewSource.RootVisual = Nothing
                previewSource.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            _versions = savedVersions : _versionsDirty = savedDirty
            SetDownloadView(DownloadView.Versions)
            UpdateVersions()
        End Try
    End Sub
End Class
