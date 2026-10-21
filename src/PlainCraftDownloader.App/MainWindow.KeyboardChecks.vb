Imports System.Windows
Imports System.Windows.Input
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Interop
Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Controls
Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Private Sub VerifyKeyboardWorkflow(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        FocusManager.SetIsFocusScope(host, True)
        Dim source As HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim saved = _appearance.ToJson()
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            RestoreAppearance(New JObject From {{"theme", "colorful"}})
            _versionsDirty = False
            ShowDownload(Nothing, Nothing) : BackToVersions(Nothing, Nothing) : layout()
            check(SettingsTab.Focusable, "top navigation exposes a keyboard focus target")
            FocusManager.SetFocusedElement(host, DownloadTab)
            Dim predicted = DownloadTab.PredictFocus(FocusNavigationDirection.Right)
            File.WriteAllText(Path.Combine(artifactDirectory, "keyboard-tab-focus-0.30.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.PredictedRight = TryCast(predicted, FrameworkElement)?.Name, .KeyboardTarget = TryCast(Keyboard.FocusedElement, FrameworkElement)?.Name, .NativeWindowActivated = False}))
            check(predicted Is SettingsTab, "WPF directional focus prediction reaches settings from the download tab")
            PressControlKey(SettingsTab, Key.Enter) : layout()
            check(SettingsPage.Visibility = Visibility.Visible AndAlso SettingsTab.Checked, "Enter activates the settings tab through the real navigation handler")
            Dim offset = SettingsPage.VerticalOffset
            PressControlKey(SettingsTab, Key.Space)
            check(SettingsTab.Checked AndAlso SettingsPage.VerticalOffset = offset, "Space on the selected tab leaves its current page and scroll position stable")
            PressControlKey(DownloadTab, Key.Space) : layout()
            check(DownloadPage.Visibility = Visibility.Visible AndAlso DownloadTab.Checked, "Space activates the download tab")
            SettingsTab.IsEnabled = False
            PressControlKey(SettingsTab, Key.Enter)
            check(DownloadTab.Checked AndAlso DownloadPage.Visibility = Visibility.Visible, "a disabled top tab cannot navigate through keyboard input")
            SettingsTab.IsEnabled = True
            Dim category = VersionCards.Children.OfType(Of PclCard)().First(Function(c) c.Title.StartsWith("正式版", StringComparison.Ordinal))
            category.Collapsed = True
            Dim header = category.Children.OfType(Of Grid)().Single()
            check(header.Focusable, "a collapsible version category exposes a keyboard focus target")
            PressControlKey(header, Key.Enter) : layout()
            Dim body = DirectCast(category.Body, StackPanel)
            check(Not category.Collapsed AndAlso body.Children.Count > 0, "Enter expands the real version category and builds its existing catalogue")
            PressControlKey(header, Key.Space) : layout()
            check(category.Collapsed AndAlso category.Body Is body, "Space collapses a category without discarding its browsed version rows")
            PressControlKey(header, Key.Enter) : layout()
            Dim row = TryCast(body.Children(0), PclListItem)
            If row Is Nothing Then row = DirectCast(body.Children(0), PclLazyRow).Materialize()
            Dim entry = DirectCast(row.Tag, VersionEntry)
            PressControlKey(row, Key.Enter) : layout()
            FinishPageSwitchForCheck(layout)
            check(_selectedEntry Is entry AndAlso InstallPage.Visibility = Visibility.Visible AndAlso Not _busy AndAlso _cancellation Is Nothing, "keyboard version selection opens installation options without submitting a download")
            Dim returnRow As PclListItem = Nothing
            check(InstanceName.Focusable AndAlso InstanceName.IsEnabled AndAlso _keyboardVersionReturn.TryGetTarget(returnRow) AndAlso returnRow Is row, "keyboard selection retains its return row and exposes an editable name target")
            PressControlKey(InstanceName, Key.Escape) : layout()
            FinishPageSwitchForCheck(layout)
            File.WriteAllText(Path.Combine(artifactDirectory, "keyboard-page-return-0.43.json"), New JObject From {
                {"view", _downloadView.ToString()}, {"browser", VersionPage.Visibility.ToString()}, {"install", InstallPage.Visibility.ToString()},
                {"rowVisible", row.IsVisible}, {"rowFocusable", row.Focusable}, {"cardCollapsed", category.Collapsed},
                {"pendingFocus", _focusAfterPageSwitch}, {"clock", _pageSwitch.Transition IsNot Nothing},
                {"sameRow", _keyboardVersionReturn.TryGetTarget(returnRow) AndAlso returnRow Is row}}.ToString())
            check(VersionScroll.Visibility = Visibility.Visible AndAlso _selectedEntry Is entry, "Escape returns from install options while preserving the selected base version")
            check(row.IsVisible AndAlso row.Focusable AndAlso _keyboardVersionReturn.TryGetTarget(returnRow) AndAlso returnRow Is row, "Escape makes the originating version row available for keyboard focus again")
            PressControlKey(row, Key.Enter) : layout()
            FinishPageSwitchForCheck(layout)
            Dim fabric = _componentViews(ComponentKind.Fabric)
            Dim fabricHeader = fabric.Card.Children.OfType(Of Grid)().Single()
            PressControlKey(fabricHeader, Key.Space) : layout()
            Dim componentBody = DirectCast(fabric.Card.Body, StackPanel)
            Dim componentRow = MaterializeComponentRows(componentBody).First()
            PressControlKey(componentRow, Key.Escape) : layout()
            check(fabric.Card.Collapsed AndAlso InstallPage.Visibility = Visibility.Visible AndAlso _selectedEntry Is entry, "Escape closes the expanded component card before leaving the installation page")
            PressControlKey(fabricHeader, Key.Space) : layout()
            PressControlKey(componentRow, Key.Space) : layout()
            check(_componentSelection.Item(ComponentKind.Fabric) Is DirectCast(componentRow.Tag, ComponentEntry) AndAlso fabric.Card.Collapsed AndAlso _componentSelection.Has(ComponentKind.FabricApi), "Space selects the real Fabric row with its dependency rules and collapses the list")
            check(fabricHeader.Focusable AndAlso fabricHeader.IsVisible, "the selected component exposes a reachable keyboard header when its row collapses")
            Dim frame As New System.Windows.Threading.DispatcherFrame()
            Dim timer As New System.Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(250)}
            AddHandler timer.Tick, Sub() frame.Continue = False
            timer.Start() : System.Windows.Threading.Dispatcher.PushFrame(frame) : timer.Stop()
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            ApplyAppearance() : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-keyboard-selection-after-0.30.png"))
            PressControlKey(fabric.Clear, Key.Space) : layout()
            check(Not _componentSelection.Has(ComponentKind.Fabric) AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso fabric.Card.Collapsed, "Space on a header clear button removes its component without bubbling a second fold action")
            PressControlKey(SettingsTab, Key.Enter) : layout()
            PressControlKey(AboutEntry, Key.Enter) : layout()
            check(Not AboutProductItem.Focusable AndAlso Not AboutVersionItem.Focusable, "read-only about information does not introduce dead Tab stops")
            PressControlKey(AboutBack, Key.Escape) : layout()
            check(AboutPage.Visibility = Visibility.Collapsed AndAlso SettingsPage.Visibility = Visibility.Visible, "Escape returns from about to settings through the normal back handler")
            SourceBox.ApplyTemplate()
            Dim popup = DirectCast(SourceBox.Template.FindName("PART_Popup", SourceBox), Popup)
            DirectCast(popup.Child, Grid).Opacity = 0
            Try
                SourceBox.IsDropDownOpen = True : FlushControlLayout()
                Dim index = SourceBox.SelectedIndex
                PressControlKey(SourceBox, Key.Escape) : layout()
                check(Not SourceBox.IsDropDownOpen AndAlso SettingsPage.Visibility = Visibility.Visible AndAlso SourceBox.SelectedIndex = index, "Escape closes the source menu without navigating away or changing the source")
            Finally
                SourceBox.IsDropDownOpen = False : DirectCast(popup.Child, Grid).Opacity = 1
            End Try
            PressControlKey(SourceBox, Key.Escape) : layout()
            check(DownloadPage.Visibility = Visibility.Visible AndAlso InstallPage.Visibility = Visibility.Visible, "a second Escape leaves settings and restores the existing install page")
            Using cancellation As New System.Threading.CancellationTokenSource()
                _busy = True : _cancellation = cancellation
                ResetTaskView("键盘返回中的任务", False) : SetBusy(True) : ShowProgress(Nothing, Nothing)
                RenderTaskProgress(QueuedTaskValue("保留下载进度", 0.4))
                Dim progress = TaskProgress.Value
                TransferCard.Collapsed = False : layout()
                Dim logButton = DirectCast(TransferCard.Body, StackPanel).Children.OfType(Of PclButton)().Single()
                PressControlKey(logButton, Key.Escape) : layout()
                check(TransferCard.Collapsed AndAlso DownloadProgress.Visibility = Visibility.Visible AndAlso Not cancellation.IsCancellationRequested, "Escape collapses transfer details before leaving a running task")
                PressControlKey(CancelButton, Key.Escape) : layout()
                check(InstallPage.Visibility = Visibility.Visible AndAlso _busy AndAlso Not cancellation.IsCancellationRequested AndAlso TaskProgress.Value = progress, "Escape from an active task returns to options without cancelling or resetting progress")
                PressControlKey(SettingsTab, Key.Enter) : layout()
                PressControlKey(SettingsPage, Key.Escape) : layout()
                check(DownloadProgress.Visibility = Visibility.Visible AndAlso _busy AndAlso Not cancellation.IsCancellationRequested, "Escape from settings during a task restores its active progress page")
                _busy = False : _cancellation = Nothing : SetBusy(False)
            End Using
            BackToVersions(Nothing, Nothing) : layout()
            PressControlKey(RefreshButton, Key.Escape)
            check(VersionScroll.Visibility = Visibility.Visible AndAlso Not _closing, "Escape at the version browser does not close the application")

            host.Child = Nothing
            Dim lazyPanel As New StackPanel()
            Dim created = 0
            For i = 0 To 1199
                Dim index = i
                lazyPanel.Children.Add(New PclLazyRow(Function()
                                                        created += 1
                                                        Return New PclListItem With {.Title = "键盘长列表样本 " & index, .Tag = index, .Height = 42}
                                                    End Function))
            Next
            Dim scroll As New ScrollViewer With {.Content = lazyPanel, .VerticalScrollBarVisibility = ScrollBarVisibility.Auto}
            host.Child = scroll : layout()
            Dim pending = DirectCast(lazyPanel.Children(900), PclLazyRow)
            check(pending.Focusable AndAlso created < 60, "unvisited version placeholders are keyboard targets without eagerly creating 1200 rows")
            FocusManager.SetFocusedElement(host, pending)
            If TypeOf lazyPanel.Children(900) Is PclLazyRow Then
                pending.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, Nothing, pending) With {.RoutedEvent = Keyboard.GotKeyboardFocusEvent})
            End If
            layout()
            Dim materialized = DirectCast(lazyPanel.Children(900), PclListItem)
            Dim top = materialized.TranslatePoint(New Point(), scroll).Y
            check(created < 90 AndAlso CInt(materialized.Tag) = 900 AndAlso top >= 0 AndAlso top + 42 <= scroll.ViewportHeight, "keyboard focus materializes and reveals a distant row while preserving lazy creation")
            check(materialized.Focusable AndAlso pending.Parent Is Nothing AndAlso lazyPanel.Children.Count = 1200, "keyboard materialization replaces the placeholder with one focusable row without a duplicate stop")
            Dim before = lazyPanel.Children(899)
            check(materialized.PredictFocus(FocusNavigationDirection.Up) Is before, "WPF upward focus prediction reaches the preceding item after keyboard materialization")
            File.WriteAllText(Path.Combine(artifactDirectory, "keyboard-lazy-focus-0.30.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.Created = created, .Total = lazyPanel.Children.Count, .FocusedIndex = 900, .Offset = scroll.VerticalOffset, .RowTop = top, .Viewport = scroll.ViewportHeight}, Newtonsoft.Json.Formatting.Indented))
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-keyboard-lazy-after-0.30.png"))
        Finally
            SettingsTab.IsEnabled = True
            _busy = False : _cancellation = Nothing : SetBusy(False)
            FocusManager.SetFocusedElement(host, Nothing)
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            RestoreAppearance(saved)
        End Try
    End Sub
End Class
