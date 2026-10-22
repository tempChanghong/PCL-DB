Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
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
                check(plan.Children.OfType(Of PclPageYAnimation)().Single().BeginTime.Value.TotalMilliseconds = 140 AndAlso _categoryMotion.ItemCount = 1, "CurseForge card enters with the PCL 110 plus 30 millisecond delay")
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
                ShowSettings(Me, New RoutedEventArgs()) : layout()
                check(_categoryMotion.Transition Is Nothing AndAlso DownloadGhost.Children.Count = 0 AndAlso CBool(DownloadViewHost.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso SettingsPage.IsVisible, "settings handoff releases category snapshots clocks and local input gating")
                _surfaceMotion.Finish() : ShowDownload(Me, New RoutedEventArgs()) : _surfaceMotion.Finish() : layout()
                ShowModpackDownloads(Me, New RoutedEventArgs()) : layout()
                FocusDownloadPage()
                check(_focusAfterPageSwitch AndAlso _categoryMotion.Transition IsNot Nothing, "keyboard focus waits for category entry instead of targeting an invisible card")
                seek(_categoryMotion.Transition, 491) : _categoryMotion.Finish() : layout()
                ' The preview HWND is intentionally inactive; verify the handoff
                ' and reachable target without claiming foreground keyboard focus.
                check(Not _focusAfterPageSwitch AndAlso ImportPackButton.Focusable AndAlso ImportPackButton.IsVisible AndAlso ImportPackButton.IsEnabled AndAlso DownloadViewHost.IsHitTestVisible AndAlso ModpackPage.Opacity = 1 AndAlso DownloadGhost.Children.Count = 0, "settled category entry releases pending focus exposes the import target and removes owned visual state")
                ShowVanillaDownloads(Me, New RoutedEventArgs()) : layout()
                host.Width = 900 : layout()
                check(_categoryMotion.Transition Is Nothing AndAlso DownloadGhost.Children.Count = 0 AndAlso DownloadViewHost.IsHitTestVisible AndAlso InstallPage.IsVisible, "resizing during category entry settles the correct page without leftover overlays")
                _categoryMotion.PauseForChecks = False
                ShowModpackDownloads(Me, New RoutedEventArgs())
                check(PumpMotionUntil(Function() _categoryMotion.Transition Is Nothing) AndAlso ModpackPage.IsVisible AndAlso DownloadGhost.Children.Count = 0 AndAlso DownloadViewHost.IsHitTestVisible, "unpaused category motion completes and restores input without a manual seek")
                SaveControlVisual(host, IO.Path.Combine(directory, "download-category-settled.png"))
            End Using
        Finally
            _motionPreview = False : _focusAfterPageSwitch = False
            _categoryMotion.Finish() : _surfaceMotion.Finish() : _pageSwitch.Finish()
            host.Child = Nothing : Me.Content = content
        End Try
    End Sub
End Class
