Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub SetButtonPointer(button As PclButton, over As Boolean)
        ' Hidden previews cannot move the real pointer or activate a desktop window.
        ' Change only this fixture's WPF read-only hover flag, then deliver the routed event.
        Dim setter = GetType(UIElement).GetMethod("WriteFlag", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)
        If setter Is Nothing Then Throw New InvalidOperationException("The net48 WPF hover fixture is unavailable.")
        Dim flag = [Enum].Parse(setter.GetParameters()(0).ParameterType, "IsMouseOverCache")
        setter.Invoke(button, New Object() {flag, over})
        button.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = If(over, Mouse.MouseEnterEvent, Mouse.MouseLeaveEvent)})
    End Sub

    Private Sub VerifyButtonStates(check As Action(Of Boolean, String), directory As String)
        Dim saved = _appearance.ToJson()
        Dim host As New Border With {.Width = 560, .Height = 145, .Padding = New Thickness(20)}
        Dim panel As New StackPanel With {.Orientation = Orientation.Horizontal}
        Dim button As New PclButton With {.Text = "下载按钮", .Width = 125, .Height = 35, .Margin = New Thickness(0, 0, 15, 0)}
        Dim disabled As New PclButton With {.Text = "暂不可用", .Width = 125, .Height = 35, .IsEnabled = False, .Margin = New Thickness(0, 0, 15, 0)}
        Dim icon As New PclIconButton With {.Logo = InstallBack.Logo, .Width = 32, .Height = 32}
        panel.Children.Add(button) : panel.Children.Add(disabled) : panel.Children.Add(icon)
        host.Child = panel : host.SetResourceReference(BackgroundProperty, "CardSurfaceBrush")
        Dim source As Interop.HwndSource = Nothing
        Dim observations As New JObject
        Dim colorOf As Func(Of Brush, Color) = Function(value) DirectCast(value, SolidColorBrush).Color
        Try
            source = PreviewSurface.Attach(host, 560, 145)
            host.Measure(New Size(560, 145)) : host.Arrange(New Rect(0, 0, 560, 145)) : host.UpdateLayout() : FlushControlLayout()
            RestoreAppearance(Nothing)
            Dim idle = colorOf(button.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrush1").Color AndAlso colorOf(button.PanFore.Background) = PclAnimation.Brush("ColorBrushHalfWhite").Color
            Dim disabledGray = colorOf(disabled.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrushGray4").Color AndAlso disabled.Opacity = 1
            Dim lightIcon = colorOf(icon.Path.Fill) = Color.FromArgb(160, 0, 0, 0)
            SetButtonPointer(button, True) : ApplyAppearance()
            Dim hovered = colorOf(button.PanFore.Background) = PclAnimation.Brush("ColorBrush7").Color AndAlso colorOf(button.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrush3").Color
            Dim changing = PclTheme.Changing
            PclTheme.Changing = True
            Try
                button.Highlight = True
            Finally
                PclTheme.Changing = changing
            End Try
            Dim hoverHighlight = colorOf(button.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrush3").Color
            SetButtonPointer(button, False) : ApplyAppearance()
            button.Highlight = True
            ApplyAppearance()
            Dim highlight = colorOf(button.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrush2").Color
            disabled.Highlight = True
            ApplyAppearance()
            Dim disabledHighlight = colorOf(disabled.PanFore.BorderBrush) = PclAnimation.Brush("ColorBrushGray4").Color
            DarkModeBox.IsChecked = True
            Dim darkInk = colorOf(icon.Path.Fill)
            Dim darkIcon = darkInk.R > 200 AndAlso darkInk.G > 200 AndAlso darkInk.B > 200
            Dim darkButton = colorOf(button.PanFore.Background) = PclAnimation.Brush("ColorBrushHalfWhite").Color
            icon.IsEnabled = False : ApplyAppearance()
            Dim disabledIcon = colorOf(icon.Path.Fill) = PclAnimation.Brush("ColorBrushGray5").Color
            icon.IsEnabled = True
            Dim clicks = 0
            AddHandler icon.Click, Sub() clicks += 1
            PointerEvent(icon, Mouse.MouseDownEvent)
            icon.IsEnabled = False : icon.IsEnabled = True
            PointerEvent(icon, Mouse.MouseUpEvent)
            Dim cancelled = clicks = 0
            PointerEvent(icon, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
            Dim ordinaryClick = clicks = If(cancelled, 1, 2)
            Dim beforeDetach = clicks
            PointerEvent(icon, Mouse.MouseDownEvent)
            panel.Children.Remove(icon) : FlushControlLayout()
            panel.Children.Add(icon) : FlushControlLayout()
            PointerEvent(icon, Mouse.MouseUpEvent)
            Dim detachCancelled = clicks = beforeDetach AndAlso DirectCast(icon.PanBack.RenderTransform, ScaleTransform).ScaleX = 1
            ApplyAppearance()
            SaveControlVisual(host, Path.Combine(directory, "button-states-dark.png"))
            DarkModeBox.IsChecked = False
            Dim restoredIcon = colorOf(icon.Path.Fill) = Color.FromArgb(160, 0, 0, 0)
            observations = New JObject From {{"Idle", idle}, {"DisabledGray", disabledGray}, {"LightIcon", lightIcon}, {"HoverBackground", hovered}, {"Highlight", highlight}, {"HoverHighlight", hoverHighlight}, {"DisabledHighlight", disabledHighlight}, {"DarkIcon", darkIcon}, {"DarkButton", darkButton}, {"DisableCancelsPress", cancelled}, {"DetachCancelsPress", detachCancelled}, {"DisabledIcon", disabledIcon}, {"RestoredLightIcon", restoredIcon}, {"NormalClick", ordinaryClick}, {"SimulatedPointerState", True}, {"NativeWindowActivated", False}}
            File.WriteAllText(Path.Combine(directory, "button-states.json"), observations.ToString())
            check(cancelled, "disabling and reenabling an icon button cancels its pending press")
            check(ordinaryClick, "an icon button still activates once after a complete new press")
            check(idle, "idle action buttons retain PCL's normal outline and half-white surface")
            check(disabledGray, "disabled action buttons use PCL Gray4 without dimming the whole control")
            check(lightIcon, "light-mode navigation icons retain PCL's original alpha-160 black ink")
            check(hovered, "hovered action buttons expose PCL's ColorBrush3 outline and ColorBrush7 surface")
            check(highlight, "highlighted idle buttons retain PCL's ColorBrush2 outline")
            check(hoverHighlight, "changing highlight while hovered does not replace the active PCL outline")
            check(disabledHighlight, "changing highlight while disabled cannot revive the enabled outline")
            check(darkIcon, "navigation icons refresh to readable neutral ink when dark mode changes")
            check(darkButton, "idle action-button surfaces retain the current dark palette")
            check(disabledIcon, "disabled navigation icons use the current palette's Gray5 ink")
            check(detachCancelled, "unloading and reloading a pressed icon clears its action and restores its scale")
            check(restoredIcon, "returning to light mode restores the original alpha-160 black icon ink")
        Finally
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            RestoreAppearance(saved)
        End Try
    End Sub
End Class
