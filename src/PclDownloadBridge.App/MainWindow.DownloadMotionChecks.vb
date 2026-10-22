Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyDownloadCategoryMotion(directory As String, check As Action(Of Boolean, String))
        Dim content = DirectCast(Me.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        Me.Content = Nothing : host.Child = content
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, ms)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                layout() : _motionPreview = True : _categoryMotion.PauseForChecks = True
                Dim gate As New Grid With {.IsHitTestVisible = False}, action As New PclButton(), icon As New PclIconButton(), keyClicks = 0
                gate.Width = 1 : gate.Height = 1 : gate.Opacity = 0
                Dim root = Shell
                root.Children.Add(gate)
                gate.Children.Add(action) : gate.Children.Add(icon)
                AddHandler action.Click, Sub() keyClicks += 1
                AddHandler icon.Click, Sub() keyClicks += 1
                PressControlKey(action, Key.Enter) : PressControlKey(icon, Key.Space)
                check(keyClicks = 0, "animated input gating blocks action and icon keyboard activation inherited from the page")
                Dim optionalBox As New PclCheckBox With {.Content = "可选文件"}
                gate.Children.Add(optionalBox)
                Dim pressed = RouteDownloadKey(optionalBox, Key.Space)
                IO.File.WriteAllText(IO.Path.Combine(directory, "download-keyboard-gate.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.PreviewHandled = pressed, .OptionalSelected = optionalBox.IsChecked.GetValueOrDefault()}))
                check(pressed AndAlso Not optionalBox.IsChecked.GetValueOrDefault(), "page input gate blocks keyboard toggles of optional pack files during animation")
                Dim editor As New PclTextBox With {.Text = "保留名称"}
                gate.Children.Add(editor)
                Dim text As New TextCompositionEventArgs(Keyboard.PrimaryDevice, New TextComposition(InputManager.Current, editor, "x")) With {.RoutedEvent = TextCompositionManager.PreviewTextInputEvent}
                editor.RaiseEvent(text)
                check(text.Handled AndAlso editor.Text = "保留名称", "page input gate blocks composed text input while preserving the edited instance name")
                Dim focus As New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, Nothing, editor) With {.RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent}
                editor.RaiseEvent(focus)
                check(focus.Handled, "page input gate rejects keyboard focus into hidden incoming content")
                gate.IsHitTestVisible = True
                check(Not RouteDownloadKey(optionalBox, Key.Space) AndAlso optionalBox.IsChecked.GetValueOrDefault(), "optional pack keyboard selection resumes when the input gate opens")
                optionalBox.IsChecked = False
                RouteDownloadKey(optionalBox, Key.Space, Sub() gate.IsHitTestVisible = False)
                check(Not optionalBox.IsChecked.GetValueOrDefault() AndAlso Not optionalBox.IsPressed, "a space released after page exit cannot change the selection or leave the checkbox pressed")
                gate.IsHitTestVisible = True
                text = New TextCompositionEventArgs(Keyboard.PrimaryDevice, New TextComposition(InputManager.Current, editor, "x")) With {.RoutedEvent = TextCompositionManager.PreviewTextInputEvent}
                editor.RaiseEvent(text)
                check(Not text.Handled, "composed text input is available again after page entry")
                PressControlKey(action, Key.Enter) : PressControlKey(icon, Key.Space)
                check(keyClicks = 2, "action and icon keyboard activation resumes after the page input gate opens")
                root.Children.Remove(gate)
                PointerEvent(VanillaNav, Mouse.MouseDownEvent)
                seek(VanillaNav.HoverTransition, 120)
                Dim scale = DirectCast(VanillaNav.RenderTransform, ScaleTransform)
                check(scale.ScaleX = 1 AndAlso scale.ScaleY = 1, "PCL sidebar press keeps the full row at unit scale")
                PointerEvent(VanillaNav, Mouse.MouseUpEvent) : LeavePointer(VanillaNav)
                SelectVersion(_versions(0))
                Dim horizontal = _pageSwitch.Transition
                check(horizontal IsNot Nothing, "download category fixture begins with a live vanilla install transition")
                ShowModpackDownloads(Me, New RoutedEventArgs()) : layout()
                Dim entering = _categoryMotion.Transition
                check(entering IsNot Nothing AndAlso _pageSwitch.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing, "category entry releases horizontal ownership and uses one right-pane motion")
                check(ModpackPage.Visibility = Visibility.Visible AndAlso VersionPage.Visibility = Visibility.Collapsed AndAlso InstallPage.Visibility = Visibility.Collapsed AndAlso ModpackNav.Selected, "category ownership and sidebar selection change immediately")
                check(DownloadNav.IsHitTestVisible AndAlso DownloadSidebar.Opacity = 1 AndAlso DownloadPage.Opacity = 1 AndAlso Not DownloadViewHost.IsHitTestVisible, "category motion keeps sidebar and toolbar stable while gating the right pane")
                Dim plan = DirectCast(entering.Timeline, ParallelTimeline)
                check(plan.Children.OfType(Of PclPageYAnimation)().Select(Function(item) item.BeginTime.Value.TotalMilliseconds).SequenceEqual({140.0, 165.0}) AndAlso _categoryMotion.ItemCount = 2, "CurseForge and Modrinth cards enter with PCL first delay and 25 millisecond item staggering")
                seek(entering, 70)
                check(DownloadGhost.Children.Count > 0 AndAlso Not DownloadGhost.IsHitTestVisible AndAlso ModpackPage.Opacity = 0 AndAlso Not DownloadViewHost.IsHitTestVisible, "category exit snapshots are inert and hidden incoming cards cannot be clicked")
                UpdateVersions() : layout()
                check(_categoryMotion.Transition Is entering AndAlso Not DownloadViewHost.IsHitTestVisible AndAlso ModpackPage.Opacity = 0, "a hidden catalogue refresh does not finish category motion or unlock invisible input")
                horizontal.Controller.SkipToFill() : layout()
                check(_categoryMotion.Transition Is entering AndAlso InstallPage.Visibility = Visibility.Collapsed, "obsolete horizontal completion cannot restore the outgoing vanilla form")
                SaveControlVisual(host, IO.Path.Combine(directory, "download-category-exit.png"))
                ShowVanillaDownloads(Me, New RoutedEventArgs()) : layout()
                Dim returning = _categoryMotion.Transition
                check(returning IsNot Nothing AndAlso returning IsNot entering AndAlso _downloadView = DownloadView.Install AndAlso VanillaNav.Selected, "rapid category reversal retargets motion to the preserved vanilla install form")
                entering.Controller.SkipToFill() : layout()
                check(_categoryMotion.Transition Is returning AndAlso ModpackPage.Visibility = Visibility.Collapsed, "obsolete category completion cannot replace the newer navigation")
                _categoryMotion.Finish()
                BackToVersions(Me, New RoutedEventArgs()) : _pageSwitch.Finish()
                ShowModpackDownloads(Me, New RoutedEventArgs()) : _categoryMotion.Finish()
                ShowVanillaDownloads(Me, New RoutedEventArgs()) : layout()
                returning = _categoryMotion.Transition
                seek(returning, 70)
                Dim refreshFrame = PclExitSnapshot.Capture(DownloadViewHost, Enumerable.Empty(Of FrameworkElement)(), Nothing)
                UpdateVersions() : layout()
                check(_categoryMotion.Transition IsNot Nothing AndAlso _categoryMotion.Transition IsNot returning AndAlso _surfaceMotion.Transition Is Nothing, "a visible catalogue refresh retargets the active right-pane transition without starting a parent animation")
                Dim refreshed = DownloadGhost.Children.OfType(Of Canvas)().SelectMany(Function(layer) layer.Children.OfType(Of Image)()).FirstOrDefault()
                check(DownloadGhost.Children.Count = 1 AndAlso refreshed IsNot Nothing AndAlso Not DownloadViewHost.IsHitTestVisible AndAlso SameDownloadFrame(refreshFrame, DirectCast(refreshed.Source, BitmapSource), New Rect(DownloadViewHost.RenderSize)), "catalogue replacement preserves the displayed right-pane frame and its input gate")
                returning = _categoryMotion.Transition
                seek(returning, 70)
                Dim frame = PclExitSnapshot.Capture(PageSurface, Enumerable.Empty(Of FrameworkElement)(), Nothing)
                Dim region = DownloadViewHost.TransformToAncestor(PageSurface).TransformBounds(New Rect(DownloadViewHost.RenderSize))
                _surfaceMotion.PauseForChecks = True
                ShowSettings(Me, New RoutedEventArgs()) : layout()
                Dim frozen = PageGhost.Children.OfType(Of Canvas)().SelectMany(Function(layer) layer.Children.OfType(Of Image)()).FirstOrDefault()
                check(PageGhost.Children.Count = 1 AndAlso frozen IsNot Nothing AndAlso SameDownloadFrame(frame, DirectCast(frozen.Source, BitmapSource), region), "top-page handoff captures the displayed category frame before releasing its animation")
                check(_categoryMotion.Transition Is Nothing AndAlso DownloadGhost.Children.Count = 0 AndAlso CBool(DownloadViewHost.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso SettingsPage.IsVisible, "settings handoff releases category snapshots clocks and local input gating")
                _surfaceMotion.Finish() : ShowDownload(Me, New RoutedEventArgs()) : _surfaceMotion.Finish() : layout()
                ShowModpackDownloads(Me, New RoutedEventArgs()) : layout()
                FocusDownloadPage()
                check(_focusAfterPageSwitch AndAlso _categoryMotion.Transition IsNot Nothing, "keyboard focus waits for category entry instead of targeting an invisible card")
                seek(_categoryMotion.Transition, 491) : _categoryMotion.Finish() : layout()
                ' The preview HWND is intentionally inactive; verify the handoff
                ' and reachable target without claiming foreground keyboard focus.
                check(Not _focusAfterPageSwitch AndAlso PackSearchText.Focusable AndAlso PackSearchText.IsVisible AndAlso PackSearchText.IsEnabled AndAlso DownloadViewHost.IsHitTestVisible AndAlso ModpackPage.Opacity = 1 AndAlso DownloadGhost.Children.Count = 0, "settled category entry releases pending focus exposes the search target and removes owned visual state")
                ShowVanillaDownloads(Me, New RoutedEventArgs()) : layout()
                host.Width = 900 : layout()
                check(_categoryMotion.Transition Is Nothing AndAlso DownloadGhost.Children.Count = 0 AndAlso DownloadViewHost.IsHitTestVisible AndAlso VersionPage.IsVisible, "resizing during category entry settles the correct page without leftover overlays")
                _categoryMotion.PauseForChecks = False
                ShowModpackDownloads(Me, New RoutedEventArgs())
                check(PumpMotionUntil(Function() _categoryMotion.Transition Is Nothing) AndAlso ModpackPage.IsVisible AndAlso DownloadGhost.Children.Count = 0 AndAlso DownloadViewHost.IsHitTestVisible, "unpaused category motion completes and restores input without a manual seek")
                SaveControlVisual(host, IO.Path.Combine(directory, "download-category-settled.png"))
                ShowVanillaDownloads(Me, New RoutedEventArgs()) : _categoryMotion.Finish()
                SelectVersion(_versions(0)) : layout()
                check(_pageSwitch.Transition IsNot Nothing AndAlso Not DownloadViewHost.IsHitTestVisible, "Escape fixture starts inside a gated vanilla transition")
                RouteDownloadKey(InstanceName, Key.Escape) : layout()
                check(_downloadView = DownloadView.Versions AndAlso Not _closing AndAlso Not _busy, "Escape still reverses page navigation while content keyboard editing is gated")
            End Using
        Finally
            _motionPreview = False : _focusAfterPageSwitch = False
            _categoryMotion.Finish() : _surfaceMotion.Finish() : _pageSwitch.Finish()
            host.Child = Nothing : Me.Content = content
        End Try
    End Sub
    Private Shared Function RouteDownloadKey(control As UIElement, key As Key, Optional beforeRelease As Action = Nothing) As Boolean
        Dim down As New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, key) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent}
        control.RaiseEvent(down)
        Dim blocked = down.Handled
        If Not down.Handled Then
            down.RoutedEvent = Keyboard.KeyDownEvent : control.RaiseEvent(down)
        End If
        beforeRelease?.Invoke()
        Dim up As New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, key) With {.RoutedEvent = Keyboard.PreviewKeyUpEvent}
        control.RaiseEvent(up)
        If Not up.Handled Then
            up.RoutedEvent = Keyboard.KeyUpEvent : control.RaiseEvent(up)
        End If
        Return blocked
    End Function
    Private Shared Function SameDownloadFrame(frame As PclExitSnapshot, actual As BitmapSource, region As Rect) As Boolean
        Dim expected = frame.Bitmap
        If expected.PixelWidth <> actual.PixelWidth OrElse expected.PixelHeight <> actual.PixelHeight Then Return False
        Dim sx = expected.PixelWidth / frame.Width, sy = expected.PixelHeight / frame.Height
        Dim crop As New Int32Rect(CInt(Math.Ceiling(region.Left * sx)) + 4, CInt(Math.Ceiling(region.Top * sy)) + 4, CInt(Math.Floor(region.Width * sx)) - 8, CInt(Math.Floor(region.Height * sy)) - 8)
        Dim stride = crop.Width * 4, first(stride * crop.Height - 1) As Byte, second(first.Length - 1) As Byte
        expected.CopyPixels(crop, first, stride, 0) : actual.CopyPixels(crop, second, stride, 0)
        Return first.SequenceEqual(second)
    End Function
End Class
