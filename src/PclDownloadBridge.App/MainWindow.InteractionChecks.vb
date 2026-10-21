Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub PointerEvent(target As UIElement, routedEvent As RoutedEvent)
        target.RaiseEvent(New MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) With {.RoutedEvent = routedEvent})
    End Sub

    Private Shared Sub LeavePointer(target As UIElement)
        target.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.MouseLeaveEvent})
    End Sub

    Private Sub VerifyPointerAlignment(check As Action(Of Boolean, String))
        Dim row As New PclListItem With {.Title = "不会误选的版本", .Height = 42}
        Dim clicks = 0
        AddHandler row.Click, Sub() clicks += 1
        PointerEvent(row, Mouse.MouseUpEvent)
        check(clicks = 0, "PCL version rows ignore a mouse release without a press on that row")
        PointerEvent(row, Mouse.MouseDownEvent)
        Dim background = row.Children.OfType(Of Border)().First()
        check(DirectCast(background.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush6").Color AndAlso DirectCast(row.RenderTransform, ScaleTransform).ScaleX = 0.98, "pressed version rows use the original PCL background and 0.98 scale")
        PointerEvent(row, Mouse.MouseUpEvent)
        PointerEvent(row, Mouse.MouseUpEvent)
        check(clicks = 1, "a complete version-row press releases exactly one click")
        PointerEvent(row, Mouse.MouseDownEvent)
        LeavePointer(row)
        PointerEvent(row, Mouse.MouseUpEvent)
        check(clicks = 1, "leaving a version row cancels its pending selection")
        PointerEvent(row, Mouse.MouseDownEvent)
        row.IsEnabled = False : row.IsEnabled = True
        PointerEvent(row, Mouse.MouseUpEvent)
        check(clicks = 1, "disabling and reenabling a row cannot revive an unfinished press")
        row.Selected = True
        check(DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color, "selected full-height rows use PCL ColorBrush2 rather than the sidebar accent")
        check(background.Opacity = 0, "a selected version row does not retain PCL's hover background after the pointer leaves")

        Dim tab As New PclRadioButton With {.Text = "下载"}
        Dim tabClicks = 0
        AddHandler tab.Click, Sub() tabClicks += 1
        PointerEvent(tab, Mouse.MouseUpEvent)
        check(tabClicks = 0, "top navigation ignores a mouse release that started elsewhere")
        PointerEvent(tab, Mouse.MouseDownEvent)
        check(DirectCast(tab.Background, SolidColorBrush).Color.A = 120, "pressed top navigation uses PCL's white theme alpha 120")
        PointerEvent(tab, Mouse.MouseUpEvent)
        check(tabClicks = 1, "a complete top-navigation press activates the target page")
        tab.Checked = True
        PointerEvent(tab, Mouse.MouseDownEvent)
        PointerEvent(tab, Mouse.MouseUpEvent)
        check(tabClicks = 1, "the already selected top tab does not restart its page action")
        tab.Checked = False
        PointerEvent(tab, Mouse.MouseDownEvent)
        LeavePointer(tab)
        PointerEvent(tab, Mouse.MouseUpEvent)
        check(tabClicks = 1, "leaving a top-navigation tab cancels its pending action")

        Dim button As New PclButton With {.Text = "刷新列表"}
        Dim buttonClicks = 0
        AddHandler button.Click, Sub() buttonClicks += 1
        PointerEvent(button, Mouse.MouseDownEvent)
        LeavePointer(button)
        PointerEvent(button, Mouse.MouseUpEvent)
        check(buttonClicks = 0 AndAlso Not button.IsMouseCaptured, "leaving a standard button cancels the press without retaining mouse capture")
        PointerEvent(button, Mouse.MouseDownEvent)
        PointerEvent(button, Mouse.MouseUpEvent)
        check(buttonClicks = 1, "standard buttons retain their ordinary complete-click behavior")

        Dim card As New PclCard With {.Collapsible = True, .Collapsed = True, .Builder = Function() New TextBlock With {.Text = "内容"}}
        Dim header = card.Children.OfType(Of Grid)().Single()
        PointerEvent(header, Mouse.MouseUpEvent)
        check(card.Collapsed, "a card header cannot expand on an unmatched mouse release")
        PointerEvent(header, Mouse.MouseDownEvent)
        PointerEvent(header, Mouse.MouseUpEvent)
        check(Not card.Collapsed AndAlso card.Body IsNot Nothing, "a complete card-header click lazily builds and expands its content")
        PointerEvent(header, Mouse.MouseDownEvent)
        LeavePointer(header)
        PointerEvent(header, Mouse.MouseUpEvent)
        check(Not card.Collapsed, "leaving a card header cancels the pending fold action")
        Dim folds = 0
        AddHandler card.FoldChanged, Sub() folds += 1
        card.Collapsed = False
        check(folds = 0, "assigning the current fold state does not emit a redundant PCL swap")
        Dim builds = 0
        Dim latest As New PclCard With {.Builder = Function()
            builds += 1
            Return New TextBlock With {.Text = "最新版本"}
        End Function}
        latest.Collapsed = False : latest.Collapsed = False
        check(builds = 1 AndAlso latest.Body IsNot Nothing, "an initially expanded card still builds once when its current state is assigned")
        ShowDownload(Nothing, Nothing)
        PointerEvent(SettingsTab, Mouse.MouseDownEvent)
        PointerEvent(SettingsTab, Mouse.MouseUpEvent)
        check(SettingsTab.Checked AndAlso SettingsPage.Visibility = Visibility.Visible, "real top-tab clicks navigate without bubbling a window-drag request")
        PointerEvent(SettingsTab, Mouse.MouseDownEvent)
        PointerEvent(SettingsTab, Mouse.MouseUpEvent)
        check(SettingsTab.Checked AndAlso SettingsPage.Visibility = Visibility.Visible, "pressing the selected real top tab remains a stable page action")
        PointerEvent(DownloadTab, Mouse.MouseDownEvent)
        PointerEvent(DownloadTab, Mouse.MouseUpEvent)
        check(DownloadTab.Checked AndAlso DownloadPage.Visibility = Visibility.Visible AndAlso SettingsPage.Visibility = Visibility.Collapsed, "the real download tab still returns from settings after click-state alignment")
    End Sub

    Private Sub VerifyComponentBodyRetention(check As Action(Of Boolean, String))
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        Dim entries As New List(Of ComponentEntry)
        For i = 0 To 149
            entries.Add(New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "0.16." & i})
        Next
        AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, entries)
        Dim view = _componentViews(ComponentKind.Fabric)
        view.Card.Collapsed = False
        Dim body = DirectCast(view.Card.Body, StackPanel)
        check(body.Children.Count = 150 AndAlso Not body.Children.OfType(Of PclButton)().Any(), "the component list exposes its complete catalogue without a show-more action")
        AcceptComponentList(ComponentKind.NeoForge, _selectedEntry, _componentGeneration, _componentEntries(ComponentKind.NeoForge))
        check(view.Card.Body Is body AndAlso body.Children.Count = 150, "an unrelated late catalogue cannot discard the expanded component catalogue")
        RefreshComponents()
        check(view.Card.Body Is body, "an unchanged selection refresh reuses the existing component body")
        SelectComponent(entries(0))
        view.Card.Collapsed = False
        Dim selectedBody = DirectCast(view.Card.Body, StackPanel)
        check(MaterializeComponentRows(selectedBody).First().Selected, "a changed component selection still refreshes its selected-row highlight")
        Dim replacement As New List(Of ComponentEntry) From {New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "0.16.0"}}
        AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, replacement)
        view.Card.Collapsed = False
        check(view.Card.Body IsNot selectedBody AndAlso DirectCast(view.Card.Body, StackPanel).Children.Count = 1, "a newly loaded catalogue replaces old rows and rebinds the selected version")
        check(view.Summary.TextTrimming = TextTrimming.CharacterEllipsis, "component summaries use PCL character ellipsis to protect clear and fold buttons")
        PreviewLongComponentSummary()
        Dim summaryView = _componentViews(ComponentKind.FabricApi)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        content.Measure(New Size(850, 500)) : content.Arrange(New Rect(0, 0, 850, 500)) : content.UpdateLayout()
        Dim summaryRight = summaryView.Summary.TranslatePoint(New Point(summaryView.Summary.ActualWidth, 0), summaryView.Card).X
        Dim clearLeft = summaryView.Clear.TranslatePoint(New Point(0, 0), summaryView.Card).X
        check(summaryView.Summary.ActualWidth > 0 AndAlso summaryRight <= clearLeft AndAlso CStr(summaryView.Summary.ToolTip) = _componentSelection.Item(ComponentKind.FabricApi).Version, "long component text preserves a measurable clear-button area and the complete version tooltip")
        summaryView.Card.Collapsed = False
        PointerEvent(summaryView.Clear, Mouse.MouseDownEvent)
        PointerEvent(summaryView.Clear, Mouse.MouseUpEvent)
        check(Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso _componentSelection.Has(ComponentKind.Fabric) AndAlso summaryView.Card.Collapsed, "the real component-clear click keeps its parent loader and cannot bubble into a second fold")
    End Sub

    Private Sub PreviewLongComponentSummary()
        SelectVersion(_versions(0))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        Dim longApi As New ComponentEntry With {.Kind = ComponentKind.FabricApi, .Minecraft = _selectedEntry.Id,
            .Version = "0.110.0+1.21.1-" & New String("B"c, 130)}
        _componentEntries(ComponentKind.FabricApi) = New List(Of ComponentEntry) From {longApi}
        SelectComponent(longApi)
    End Sub

    Private Sub VerifyDefaultNameAlignment(check As Action(Of Boolean, String))
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        Dim fabric As New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "0.16.9+build.2"}
        SelectComponent(fabric)
        check(InstanceName.Text = "1.20.1-Fabric 0.16.9", "default Fabric instance names omit the build suffix like PCL GetSelectName")
        check(DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/Fabric.png", StringComparison.Ordinal), "the name card displays PCL's selected Fabric icon instead of the vanilla grass block")
        InstanceName.Text = "保留自定义名称"
        ClearComponent(ComponentKind.Fabric)
        check(InstanceName.Text = "保留自定义名称" AndAlso DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/Grass.png", StringComparison.Ordinal), "clearing a loader restores its vanilla icon even when the custom name stays untouched")
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        SelectComponent(New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = "1.20.1", .Version = "47.2.18"})
        SelectComponent(New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.20.1", .Version = "I6 pre1", .RequiredForge = "47.2.18"})
        check(InstanceName.Text = "1.20.1-Forge_47.2.18-OptiFine_I6_pre1" AndAlso DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/Anvil.png", StringComparison.Ordinal), "Forge OptiFine names and name-card icon priority match PCL")
        SelectVersion(New VersionEntry With {.Id = "1.12.2", .Kind = "release", .Released = New DateTimeOffset(2017, 9, 18, 0, 0, 0, TimeSpan.Zero)})
        SelectComponent(New ComponentEntry With {.Kind = ComponentKind.LiteLoader, .Minecraft = "1.12.2", .Version = "1.12.2-SNAPSHOT"})
        SelectComponent(New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.12.2", .Version = "G5"})
        check(InstanceName.Text = "1.12.2-LiteLoader-OptiFine_G5" AndAlso DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/Egg.png", StringComparison.Ordinal), "legacy LiteLoader names omit its repeated version and precede OptiFine like PCL")
        SelectVersion(_versions(0))
        SelectComponent(New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = _selectedEntry.Id, .Version = "21.1.77+snapshot-123"})
        check(InstanceName.Text = _selectedEntry.Id & "-NeoForge_21.1.77" AndAlso DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/NeoForge.png", StringComparison.Ordinal), "NeoForge default names and icons follow PCL without changing the selected component version")
    End Sub
End Class
