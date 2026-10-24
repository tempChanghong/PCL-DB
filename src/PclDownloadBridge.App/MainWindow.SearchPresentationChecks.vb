Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Function HintContrast(ink As Color, back As Color) As Double
        Dim luminance As Func(Of Color, Double) = Function(color)
                                                     Dim channels = {color.R / 255.0, color.G / 255.0, color.B / 255.0}
                                                     Dim linear = channels.Select(Function(value) If(value <= 0.04045, value / 12.92, Math.Pow((value + 0.055) / 1.055, 2.4))).ToArray()
                                                     Return linear(0) * 0.2126 + linear(1) * 0.7152 + linear(2) * 0.0722
                                                 End Function
        Return (Math.Max(luminance(ink), luminance(back)) + 0.05) / (Math.Min(luminance(ink), luminance(back)) + 0.05)
    End Function

    Private Sub VerifyHintAndActionPresentation(directory As String, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson()
        Dim row As New StackPanel With {.Margin = New Thickness(15)}, host As New Border With {.Width = 430, .Height = 240, .Child = row}
        host.SetResourceReference(BackgroundProperty, "ColorBrushBg1")
        Dim hints = {New PclHint With {.Theme = PclHintTheme.Blue, .Text = "提示：保留已有实例设置。"},
            New PclHint With {.Theme = PclHintTheme.Yellow, .Text = "警告：部分文件尚未下载。"},
            New PclHint With {.Theme = PclHintTheme.Red, .Text = "错误：读取失败，请重试。"}}
        For Each hint In hints : row.Children.Add(hint) : Next
        Dim button As New PclActionButton With {.Text = "复制名称", .Logo = Geometry.Parse("M0 0H16V16H0Z"), .Margin = New Thickness(0, 15, 0, 0)}, actions = 0
        row.Children.Add(button) : AddHandler button.Click, Sub() actions += 1
        Dim layout = ControlLayout(host)
        Try
            Using surface = PreviewSurface.Attach(host, 430, 240)
                layout() : button.MotionPolicy.EnabledForChecks = True : button.MotionPolicy.PauseForChecks = True
                For Each dark In {False, True, False, True}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    For index = 0 To hints.Length - 1
                        Dim hint = hints(index), label = DirectCast(DirectCast(hint.Child, Grid).Children(0), TextBlock)
                        Dim ink = DirectCast(label.Foreground, SolidColorBrush).Color, back = DirectCast(hint.Background, SolidColorBrush).Color
                        If dark Then
                            check(HintContrast(ink, back) >= 4.5 AndAlso ink = DirectCast(hint.BorderBrush, SolidColorBrush).Color, "dark semantic hint retains readable text and matching border: " & index)
                        Else
                            Dim expected = {"#FFD9ECFF", "#FFFFEBD7", "#FFFFDDDF"}
                            check(back.ToString() = expected(index) AndAlso hint.BorderThickness.Left = 3 AndAlso label.LineHeight = 16, "light hint preserves fixed PCL palette and text geometry: " & index)
                        End If
                    Next
                    check(button.InkTransition Is Nothing AndAlso button.FillTransition Is Nothing AndAlso button.ActualHeight = 27, "theme replacement settles compact action clocks and preserves source height: " & dark)
                    SaveControlVisual(host, IO.Path.Combine(directory, "semantic-hints-" & If(dark, "dark", "light") & ".png"))
                Next
                hints(0).Theme = PclHintTheme.Red : layout()
                check(DirectCast(hints(0).Background, SolidColorBrush).Color = DirectCast(hints(2).Background, SolidColorBrush).Color, "changing hint meaning in dark mode updates its palette immediately")
                row.Children.Remove(hints(0)) : layout() : RestoreAppearance(New JObject From {{"darkMode", False}})
                row.Children.Insert(0, hints(0)) : layout()
                check(DirectCast(hints(0).Background, SolidColorBrush).Color.ToString() = "#FFFFDDDF", "an unloaded hint catches the current palette when reattached")
                ControlPointer(button, True)
                Dim old = button.FillTransition
                check(old IsNot Nothing AndAlso old.Timeline.Duration.TimeSpan.TotalMilliseconds = 100, "compact action uses PCL's hundred-millisecond hover color transition")
                SeekControl(old, 50) : ControlPointer(button, False)
                Dim replacement = button.FillTransition
                old.Controller.SkipToFill() : layout()
                check(replacement Is button.FillTransition AndAlso replacement.Timeline.Duration.TimeSpan.TotalMilliseconds = 200, "retired compact hover cannot overwrite a newer leave transition")
                SeekControl(replacement, 201)
                check(button.FillTransition Is Nothing, "compact leave removes its completed color clock")
                ControlPointer(button, True) : ControlKey(button, Key.Space, True)
                check(button.FillTransition IsNot Nothing AndAlso button.FillTransition.Timeline.Duration.TimeSpan.TotalMilliseconds = 70, "compact held Space uses the source seventy-millisecond pressed color")
                row.IsHitTestVisible = False : row.IsHitTestVisible = True : layout()
                Dim recoveredFill = PclAnimation.Brush(If(button.IsMouseOver OrElse button.IsKeyboardFocused, "ColorBrushBg1", "ColorBrushSemiTransparent")).Color
                check(DirectCast(button.Background, SolidColorBrush).Color = recoveredFill AndAlso button.FillTransition Is Nothing, "reopening a compact action gate cannot restore the cancelled pressed visual")
                ControlKey(button, Key.Space, False) : ControlPointer(button, False) : layout()
                check(actions = 0, "cancelled compact press also discards its delayed native release")
                ControlKey(button, Key.Enter, True) : ControlKey(button, Key.Enter, False)
                check(actions = 1, "native compact Enter gesture activates exactly once")
                For Each boundary In {"gate", "disable", "hide", "detach", "theme", "template"}
                    ControlKey(button, Key.Space, True)
                    Dim template = button.Template
                    Select Case boundary
                        Case "gate" : row.IsHitTestVisible = False
                        Case "disable" : button.IsEnabled = False
                        Case "hide" : row.Visibility = Visibility.Collapsed
                        Case "detach" : row.Children.Remove(button)
                        Case "theme" : RestoreAppearance(New JObject From {{"darkMode", True}})
                        Case "template" : button.Template = Nothing : button.ApplyTemplate()
                    End Select
                    layout()
                    check(button.FillTransition Is Nothing AndAlso button.InkTransition Is Nothing, "compact action releases clocks at boundary: " & boundary)
                    row.IsHitTestVisible = True : button.IsEnabled = True : row.Visibility = Visibility.Visible
                    If button.Parent Is Nothing Then row.Children.Add(button)
                    If button.Template Is Nothing Then button.Template = template : button.ApplyTemplate()
                    layout() : ControlKey(button, Key.Space, False) : layout()
                    check(actions = 1, "retired compact Space cannot invoke after boundary: " & boundary)
                Next
                button.MotionPolicy.EnabledForChecks = False
                ControlKey(button, Key.Space, True) : ControlKey(button, Key.Space, False)
                check(actions = 2 AndAlso button.FillTransition Is Nothing, "compact action supports Space with reduced motion")
                Dim peer = UIElementAutomationPeer.CreatePeerForElement(button), invoke = DirectCast(peer.GetPattern(PatternInterface.Invoke), IInvokeProvider)
                invoke.Invoke()
                check(PumpMotionUntil(Function() actions = 3) AndAlso peer.GetName() = "复制名称", "compact action retains native named Invoke automation")
                row.IsHitTestVisible = False : invoke.Invoke() : row.IsHitTestVisible = True : layout()
                check(actions = 3, "Invoke submitted while gated cannot activate after the gate reopens")
                invoke.Invoke() : row.IsHitTestVisible = False : row.IsHitTestVisible = True : layout()
                check(actions = 3, "queued compact Invoke is retired when navigation briefly closes and reopens its input gate")
                invoke.Invoke()
                check(PumpMotionUntil(Function() actions = 4), "a fresh compact Invoke still works after a queued action was retired")
                button.IsEnabled = False
                Dim rejected = False
                Try
                    invoke.Invoke()
                Catch ex As System.Windows.Automation.ElementNotEnabledException
                    rejected = True
                End Try
                check(rejected AndAlso actions = 4, "disabled compact Invoke preserves native rejection")
            End Using
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub

    Private Sub VerifySearchPresentation(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson(), text = PackSearchText.Text
        Dim source = PackSearchSource.SelectedIndex, category = PackSearchCategory.SelectedIndex
        Dim session = _packSearchSession, accepted = _packLastQuery, rows = PackProjectRows.Children.Cast(Of UIElement)().ToArray()
        Dim generation = _taskGeneration, width = host.Width, motionPreview = _motionPreview, pauseCategory = _categoryMotion.PauseForChecks
        Dim focusScope = FocusManager.GetIsFocusScope(host)
        Try
            FocusManager.SetIsFocusScope(host, True)
            BackToPackSearch(Nothing, Nothing) : _categoryMotion.Finish() : _surfaceMotion.Finish()
            ModpackPage.ScrollToHome() : layout()
            _searchInputPolicy.EnabledForChecks = True : _searchInputPolicy.PauseForChecks = True
            PackSearchText.Text = "" : RefreshSearchClear(False)
            check(Not PackSearchClear.IsEnabled AndAlso Not PackSearchClear.IsHitTestVisible, "empty search clear is neither a pointer target nor an enabled keyboard stop")
            PackSearchText.Text = "保留筛选条件"
            Dim first = _searchClearMotion.Clock
            check(first IsNot Nothing AndAlso first.Timeline.Duration.TimeSpan.TotalMilliseconds = 90, "search clear appears with PCL's ninety-millisecond opacity transition")
            SeekControl(first, 45)
            check(Math.Abs(PackSearchClear.Opacity - 0.5) < 0.02, "search clear advances through an intermediate opacity frame")
            PackSearchSource.SelectedIndex = 2 : PackSearchCategory.SelectedIndex = 2
            ClearPackSearchText(Nothing, Nothing)
            Dim fading = _searchClearMotion.Clock
            first.Controller.SkipToFill() : layout()
            check(_searchClearMotion.Clock Is fading AndAlso PackSearchText.Text = "" AndAlso Not PackSearchClear.IsEnabled, "clear retires its old animation and immediately disables the empty hit target")
            check(PackSearchSource.SelectedIndex = 2 AndAlso PackSearchCategory.SelectedIndex = 2 AndAlso _packSearchSession Is session AndAlso _packLastQuery = accepted AndAlso PackProjectRows.Children.Cast(Of UIElement)().SequenceEqual(rows), "clear only edits the name without resetting filters, results or the accepted search session")
            check(FocusManager.GetFocusedElement(host) Is PackSearchText, "explicit clear restores logical focus to the existing editor without activating the hidden fixture window")
            SeekControl(fading, 91)
            check(_searchClearMotion.Clock Is Nothing AndAlso PackSearchClear.Opacity = 0, "clear fade finishes with no owned animation")
            PackSearchText.Text = "门控中保留"
            Dim retiring = _searchClearMotion.Clock
            PageSurface.IsHitTestVisible = False : ClearPackSearchText(Nothing, Nothing)
            check(PackSearchText.Text = "门控中保留" AndAlso _searchClearMotion.Clock Is Nothing AndAlso Not PackSearchClear.IsHitTestVisible, "page input gate blocks stale clear activation and settles its clock")
            PageSurface.IsHitTestVisible = True : retiring.Controller.SkipToFill() : layout()
            check(PackSearchText.Text = "门控中保留" AndAlso PackSearchClear.IsHitTestVisible, "reopening input restores current clear state without replaying the old gesture")
            For Each dark In {False, True}
                PackSearchText.Text = "主题切换"
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                check(_searchClearMotion.Clock Is Nothing AndAlso PackSearchClear.Opacity = 1 AndAlso FocusManager.GetFocusedElement(host) Is PackSearchText, "theme switch settles the search affordance without stealing editor focus: " & dark)
                host.Width = 810 : layout()
                Dim inputTop = PackSearchInputCard.TranslatePoint(New Point(), PackSearchSurface).Y, filterTop = PackSearchFilters.TranslatePoint(New Point(), PackSearchSurface).Y
                check(PackSearchInputCard.ActualHeight = 40 AndAlso filterTop >= inputTop + 55 AndAlso PackSearchButton.ActualWidth = 60 AndAlso PackSearchImport.ActualHeight = 27, "separate search and filter cards retain PCL/CE geometry at minimum width: " & dark)
                check(PackSearchGame.ActualWidth > 130 AndAlso PackSearchCategory.ActualWidth > 130 AndAlso PackSearchImport.ActualWidth < PackSearchFilters.ActualWidth / 2, "filter editors and secondary import action fit without overlap: " & dark)
                Dim importIcon = DirectCast(PackSearchImport.Template.FindName("ActionIcon", PackSearchImport), Shapes.Path)
                check(PackSearchImport.OutlinedLogo AndAlso importIcon.StrokeThickness = 1.5 AndAlso importIcon.StrokeLineJoin = PenLineJoin.Round AndAlso DirectCast(importIcon.Stroke, SolidColorBrush).Color = DirectCast(PackSearchImport.Foreground, SolidColorBrush).Color, "import uses CE's light rounded line icon and follows current action ink: " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "layered-search-" & If(dark, "dark", "light") & ".png"))
            Next
            _motionPreview = True : _categoryMotion.PauseForChecks = True
            PackSearchText.Text = "" : RefreshSearchClear(False) : PackSearchText.Text = "取消旧动画"
            Dim oldClear = _searchClearMotion.Clock
            OpenLocalPacks(Nothing, Nothing) : layout()
            Dim oldPage = _categoryMotion.Transition
            check(oldPage IsNot Nothing AndAlso Not DownloadViewHost.IsHitTestVisible, "search departure uses the existing real page clock and inherited input gate")
            check(_searchClearMotion.Clock Is Nothing AndAlso Not PackSearchSurface.IsVisible, "leaving search releases the old clear clock")
            BackToPackSearch(Nothing, Nothing) : layout()
            Dim returning = _categoryMotion.Transition
            oldPage.Controller.SkipToFill() : oldClear.Controller.SkipToFill() : layout()
            check(_categoryMotion.Transition Is returning AndAlso returning IsNot oldPage AndAlso _packMode = PackBrowserMode.Search, "old page and clear clocks cannot replace a rapid animated return")
            _categoryMotion.Finish() : layout()
            check(_searchClearMotion.Clock Is Nothing AndAlso PackSearchText.Text = "取消旧动画" AndAlso PackSearchClear.Opacity = 1, "rapid search return restores current content without stale animation or automatic query")
            check(PackProjectWebsite.Height = 27 AndAlso PackProjectCopy.Height = 27 AndAlso Double.IsNaN(PackProjectWebsite.Width) AndAlso PackProjectWebsite.Logo IsNot Nothing AndAlso PackProjectCopy.Logo IsNot Nothing, "detail secondary actions use content-sized twenty-seven-DIP icon buttons")
            check(_taskGeneration = generation AndAlso _packSearchSession Is session AndAlso _packLastQuery = accepted, "presentation changes preserve task identity and accepted query ownership")
        Finally
            PageSurface.IsHitTestVisible = True
            _searchInputPolicy.EnabledForChecks = Nothing : _searchInputPolicy.PauseForChecks = False
            _categoryMotion.Finish() : _categoryMotion.PauseForChecks = pauseCategory : _motionPreview = motionPreview
            PackSearchText.Text = text : PackSearchSource.SelectedIndex = source : PackSearchCategory.SelectedIndex = category
            host.Width = width : RestoreAppearance(appearance) : layout()
            FocusManager.SetIsFocusScope(host, focusScope)
        End Try
    End Sub
End Class
