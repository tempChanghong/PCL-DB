Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Function MaterializeComponentRows(body As StackPanel) As List(Of PclListItem)
        For Each pending In body.Children.OfType(Of PclLazyRow)().ToList()
            pending.Materialize()
        Next
        Return body.Children.OfType(Of PclListItem)().ToList()
    End Function

    Private Shared Function ComponentListSample(kind As ComponentKind, mc As String, count As Integer) As List(Of ComponentEntry)
        Return Enumerable.Range(0, count).Select(Function(i) New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = "0.16." & (count - i), .Url = "https://example.invalid/component/" & i, .Sha1 = New String("a"c, 40)}).ToList()
    End Function

    Private Sub VerifyLongComponentLists(check As Action(Of Boolean, String), artifactDirectory As String)
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
            SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
            Dim entry = _selectedEntry, generation = _componentGeneration
            Dim catalogue = ComponentListSample(ComponentKind.Fabric, entry.Id, 1200)
            AcceptComponentList(ComponentKind.Fabric, entry, generation, catalogue)
            Dim view = _componentViews(ComponentKind.Fabric)
            check(view.Card.Body Is Nothing, "closed component catalogues do not build their long row lists")
            view.Card.Collapsed = False
            Dim body = DirectCast(view.Card.Body, StackPanel)
            check(body.Children.Count = 1200 AndAlso Not body.Children.OfType(Of PclButton)().Any(), "a component catalogue exposes all 1200 rows without show-more pagination")
            check(body.Children.OfType(Of PclListItem)().Count() = 0 AndAlso body.Children.OfType(Of FrameworkElement)().All(Function(row) row.Height = 42), "opening a long component catalogue reserves row heights without eagerly creating its text and icons")
            Me.Content = Nothing : host.Child = content
            previewSource = PreviewSurface.Attach(host, 850, 700)
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            Dim scroll = InstallPage.Children.OfType(Of ScrollViewer)().Single()
            scroll.ScrollToTop()
            layout()
            check(body.Children.OfType(Of PclListItem)().Any() AndAlso body.Children.OfType(Of PclListItem)().Count() < 30 AndAlso scroll.ScrollableHeight > 49000, "the component viewport creates only visible rows while retaining the full scrollbar range")
            Dim first = TryCast(body.Children(0), PclListItem)
            Dim metrics As New Dictionary(Of String, Integer) From {{"Catalogue", catalogue.Count}, {"FirstViewport", body.Children.OfType(Of PclListItem)().Count()}}
            check(first IsNot Nothing AndAlso first.Tag Is catalogue(0), "visible component rows retain the actual catalogue entry including its download metadata")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "align-component-head-after-0.25.png"))
            Dim listTop = body.TranslatePoint(New Point(), scroll).Y + scroll.VerticalOffset
            scroll.ScrollToVerticalOffset(listTop + 600 * 42)
            layout()
            Dim middle = TryCast(body.Children(600), PclListItem)
            metrics("AfterMiddleJump") = body.Children.OfType(Of PclListItem)().Count()
            check(middle IsNot Nothing AndAlso middle.Tag Is catalogue(600) AndAlso body.Children.OfType(Of PclListItem)().Count() < 60, "jumping to a distant component version materializes it without creating every skipped row")
            AcceptComponentList(ComponentKind.NeoForge, entry, generation, _componentEntries(ComponentKind.NeoForge))
            layout()
            check(view.Card.Body Is body AndAlso body.Children(600) Is middle, "an unrelated late component list preserves already visited loader rows")
            scroll.ScrollToEnd()
            layout()
            Dim tail = TryCast(body.Children(1199), PclListItem)
            metrics("AfterEndJump") = body.Children.OfType(Of PclListItem)().Count()
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "component-list-layout-0.25.json"), Newtonsoft.Json.JsonConvert.SerializeObject(metrics, Newtonsoft.Json.Formatting.Indented))
            check(tail IsNot Nothing AndAlso tail.Tag Is catalogue(1199) AndAlso body.Children.OfType(Of PclListItem)().Count() < 90, "the final component version is directly reachable without paging through earlier entries")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "align-component-tail-after-0.25.png"))
            PointerEvent(tail, Mouse.MouseDownEvent) : PointerEvent(tail, Mouse.MouseUpEvent)
            check(_componentSelection.Item(ComponentKind.Fabric) Is catalogue(1199) AndAlso _componentSelection.Has(ComponentKind.FabricApi) AndAlso view.Card.Collapsed AndAlso InstanceName.Text.EndsWith("-Fabric " & catalogue(1199).Version, StringComparison.Ordinal), "clicking the last lazy loader selects its exact metadata and retains dependency and name behavior")
            check(view.Card.Body Is body AndAlso tail.Selected, "a component selection updates its row highlight without discarding visited rows")
            view.Card.Collapsed = False
            scroll.ScrollToEnd() : layout()
            check(view.Card.Body Is body AndAlso body.Children(1199) Is tail AndAlso tail.Selected, "reopening the selected component retains its materialized tail and highlight")
            SelectComponent(catalogue(300))
            check(view.Card.Body Is body AndAlso TypeOf body.Children(300) Is PclLazyRow AndAlso Not tail.Selected, "selecting an unvisited component does not force that offscreen row to initialize")
            view.Card.Collapsed = False
            scroll.ScrollToVerticalOffset(listTop + 300 * 42) : layout()
            Dim newlyVisible = TryCast(body.Children(300), PclListItem)
            check(newlyVisible IsNot Nothing AndAlso newlyVisible.Tag Is catalogue(300) AndAlso newlyVisible.Selected, "a newly visible lazy row reads the current selected component rather than an obsolete captured state")
            InstanceName.Text = "保留长列表选择"
            SelectComponent(catalogue(0))
            check(first.Selected AndAlso Not tail.Selected AndAlso view.Card.Body Is body AndAlso InstanceName.Text = "保留长列表选择", "changing component selection moves existing highlights and preserves a custom name")
            view.Card.Collapsed = False
            Dim replacement = ComponentListSample(ComponentKind.Fabric, entry.Id, 30)
            replacement(0).Version = catalogue(0).Version
            replacement(0).Url &= "?refreshed"
            Dim oldCount = body.Children.OfType(Of PclListItem)().Count()
            AcceptComponentList(ComponentKind.Fabric, entry, generation, replacement)
            scroll.ScrollToTop() : layout()
            Dim newBody = DirectCast(view.Card.Body, StackPanel)
            check(newBody IsNot body AndAlso newBody.Children.Count = 30 AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso InstanceName.Text = "保留长列表选择", "refreshing component metadata rebuilds only the changed list and rebinds its retained selection")
            Dim refreshed = TryCast(newBody.Children(0), PclListItem)
            check(refreshed IsNot Nothing AndAlso refreshed.Tag Is replacement(0) AndAlso refreshed.Selected AndAlso refreshed.Logo.EndsWith("/Fabric.png", StringComparison.Ordinal), "refreshed lazy rows use the new entry, selected state and PCL component icon")
            check(body.Children.OfType(Of PclListItem)().Count() = oldCount, "a detached component catalogue stops creating stale rows after its replacement arrives")
            PointerEvent(refreshed, Mouse.MouseDownEvent) : PointerEvent(refreshed, Mouse.MouseUpEvent)
            check(_componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso _componentSelection.Item(ComponentKind.Fabric).Url.EndsWith("?refreshed", StringComparison.Ordinal) AndAlso InstanceName.Text = "保留长列表选择", "clicking a refreshed component row retains its new download metadata and the custom instance name")
            ClearComponent(ComponentKind.Fabric)
            check(view.Card.Body Is newBody AndAlso Not refreshed.Selected AndAlso Not _componentSelection.Has(ComponentKind.FabricApi), "clearing a loader deselects existing rows and clears its dependencies without rebuilding its list")

            Dim allLazy = True
            For Each kind As ComponentKind In [Enum].GetValues(GetType(ComponentKind))
                Dim sample = DirectCast(BuildComponentBody(kind, ComponentListSample(kind, entry.Id, 301), Nothing), StackPanel)
                allLazy = allLazy AndAlso sample.Children.Count = 301 AndAlso sample.Children.OfType(Of PclListItem)().Count() = 0 AndAlso Not sample.Children.OfType(Of PclButton)().Any()
            Next
            check(allLazy, "all seven component types use deferred full lists rather than a 100-row pager")
        Finally
            If previewSource IsNot Nothing Then
                previewSource.RootVisual = Nothing
                previewSource.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            SelectVersion(_versions(0))
        End Try
    End Sub
End Class
