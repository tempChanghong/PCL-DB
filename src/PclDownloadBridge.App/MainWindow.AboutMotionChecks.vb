Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.ComponentModel
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyAboutMotion(directory As String, check As Action(Of Boolean, String))
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim saved = _appearance.ToJson()
        Dim host As New Border With {.Width = 850, .Height = 600}
        FocusManager.SetIsFocusScope(host, True)
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Me.Content = Nothing : host.Child = content
        Using source = PreviewSurface.Attach(host, 850, 600)
            Try
                layout() : _motionPreview = True
                SwitchMainPage(True) : _surfaceMotion.Finish() : layout()
                SettingsPage.ScrollToTop() : layout()
                ShowAbout(AboutEntry, New RoutedEventArgs())
                Dim clock = _surfaceMotion.Transition
                clock.Controller.Pause() : layout()
                check(_pendingSurfaceFocus IsNot Nothing AndAlso AboutPage.Opacity = 0,
                    "opening about defers its keyboard target until the invisible page has entered")
                _surfaceMotion.Finish() : layout()
                check(_pendingSurfaceFocus Is Nothing AndAlso FocusManager.GetFocusedElement(host) Is AboutBack,
                    "about settling gives logical focus to its back button in an inactive preview host")
                BackFromAbout(AboutBack, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                check(_pendingSurfaceFocus IsNot Nothing, "the about back button defers focus through the same page transition as Escape")
                _surfaceMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is AboutEntry AndAlso SettingsPage.VerticalOffset = 0,
                    "about return restores the visible entry button without changing settings scrolling")
                ShowAbout(AboutEntry, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                SwitchMainPage(False)
                _surfaceMotion.Finish() : clock.Controller.SkipToFill() : layout()
                check(_pendingSurfaceFocus Is Nothing AndAlso AboutPage.Visibility = Visibility.Collapsed AndAlso
                    FocusManager.GetFocusedElement(host) IsNot AboutBack,
                    "leaving about before entry completes cannot restore focus to the hidden back button")
                ShowAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(300), TimeSeekOrigin.BeginTime) : layout()
                FocusManager.SetFocusedElement(host, AboutCopyVersion)
                _surfaceMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is AboutCopyVersion AndAlso _pendingSurfaceFocus Is Nothing,
                    "about completion respects a newer user focus choice made while cards are still entering")
                _motionPreview = False
                SwitchMainPage(True) : layout()
                SettingsPage.ScrollToEnd() : layout()
                Dim offset = SettingsPage.VerticalOffset
                ShowAbout(Me, New RoutedEventArgs()) : layout()
                check(_surfaceMotion.Transition Is Nothing AndAlso _pendingSurfaceFocus Is Nothing AndAlso FocusManager.GetFocusedElement(host) Is AboutBack,
                    "disabled motion gives the about keyboard target immediately without a pending callback")
                BackFromAbout(Me, New RoutedEventArgs()) : layout()
                check(offset > 0 AndAlso Math.Abs(SettingsPage.VerticalOffset - offset) < 0.5 AndAlso FocusManager.GetFocusedElement(host) Is SettingsPage,
                    "returning to scrolled settings uses the visible page rather than scrolling to an offscreen entry")
                SettingsPage.ScrollToVerticalOffset(offset / 2) : layout()
                Dim currentOffset = SettingsPage.VerticalOffset
                BackFromAbout(Me, New RoutedEventArgs()) : layout()
                check(SettingsPage.VerticalOffset = currentOffset, "a stale about back action cannot reset an already active settings page")
                ShowAbout(Me, New RoutedEventArgs()) : layout()
                host.Width = 810 : host.Height = 470 : layout()
                Dim titleHost = TryCast(AboutProductItem.Parent, FrameworkElement)
                check(titleHost IsNot Nothing AndAlso titleHost.IsHitTestVisible AndAlso CStr(titleHost.ToolTip) = ProductInfo.FullName AndAlso
                    Not AboutProductItem.IsHitTestVisible AndAlso Not AboutProductItem.Focusable,
                    "the truncated full product name has a hit-testable tooltip without adding a dead keyboard stop")
                Dim hit = AboutPage.InputHitTest(titleHost.TranslatePoint(New Point(titleHost.ActualWidth / 2, titleHost.ActualHeight / 2), AboutPage))
                check(hit Is titleHost, "WPF hit testing reaches the product tooltip host inside the minimum about viewport")
                SaveControlVisual(host, IO.Path.Combine(directory, "about-audit-minimum.png"))
                host.Width = 850 : host.Height = 700 : layout()
                SaveControlVisual(host, IO.Path.Combine(directory, "about-audit-blue.png"))
                RestoreAppearance(New JObject From {{"darkMode", True}}) : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "about-title-layout.json"), New JObject From {
                    {"titleY", ProductTitle.TranslatePoint(New Point(), TitleBar).Y}, {"titleHeight", ProductTitle.ActualHeight},
                    {"tabsY", TopTabs.TranslatePoint(New Point(), TitleBar).Y}, {"barHeight", TitleBar.ActualHeight}}.ToString())
                SaveControlVisual(host, IO.Path.Combine(directory, "about-audit-dark.png"))
                RestoreAppearance(saved)
                _motionPreview = True
                BackFromAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                ShellKeyDown(Me, New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) With {.RoutedEvent = Keyboard.KeyDownEvent})
                _surfaceMotion.Finish() : clock.Controller.SkipToFill() : layout()
                check(DownloadPage.IsVisible AndAlso _pendingSurfaceFocus Is Nothing AndAlso Not AboutPage.IsVisible,
                    "Escape during about return follows the current settings route and retires the older focus request")
                ShowAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                WindowClosing(Me, New CancelEventArgs())
                clock.Controller.SkipToFill() : layout()
                check(_pendingSurfaceFocus Is Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso PageGhost.Children.Count = 0,
                    "closing during about entry releases the frozen page and its deferred focus together")
            Finally
                _closing = False
                _pendingSurfaceFocus = Nothing : _surfaceMotion.Finish()
                source.RootVisual = Nothing : host.Child = Nothing : Me.Content = content
                RestoreAppearance(saved)
            End Try
        End Using
    End Sub
End Class
