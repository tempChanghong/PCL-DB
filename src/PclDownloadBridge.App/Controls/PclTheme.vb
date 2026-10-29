Imports System.Windows
Imports System.Windows.Media

Namespace Controls
    ' Port of PCL ModBase.MyColor.FromHSL2 and ModSecret.ThemeRefreshMain.
    Friend Module PclTheme
        Private Event PaletteChanged As EventHandler
        Private Event PaletteFrameChanged As EventHandler
        Friend Property Changing As Boolean
        Private _darkMode As Boolean
        Friend ReadOnly Property IsDarkMode As Boolean
            Get
                Return _darkMode
            End Get
        End Property
        Friend Sub Watch(control As FrameworkElement, refresh As Action, Optional refreshFrame As Action = Nothing)
            Dim watching = False
            Dim handler As EventHandler = Sub() refresh()
            ' A moving hue is not a full appearance/input reset. Frame callbacks
            ' may recolor, but must retain presses and geometry/opacity clocks.
            Dim frameHandler As EventHandler = Sub()
                                                   If control.IsVisible Then refreshFrame?.Invoke()
                                               End Sub
            AddHandler control.Loaded, Sub()
                                          If Not watching Then
                                              AddHandler PaletteChanged, handler
                                              If refreshFrame IsNot Nothing Then AddHandler PaletteFrameChanged, frameHandler
                                              watching = True
                                          End If
                                          Dim previous = Changing
                                          Changing = True
                                          Try
                                              refresh()
                                          Finally
                                              Changing = previous
                                          End Try
                                      End Sub
            AddHandler control.Unloaded, Sub()
                                            If watching Then RemoveHandler PaletteChanged, handler
                                            If watching AndAlso refreshFrame IsNot Nothing Then RemoveHandler PaletteFrameChanged, frameHandler
                                            watching = False
                                        End Sub
        End Sub
        Friend Function Hsl2(hue As Double, saturation As Double, light As Double) As Color
            hue = (hue + 3600000) Mod 360
            Dim corrections = {0.1, -0.06, -0.3, -0.19, -0.15, -0.24, -0.32, -0.09, 0.18, 0.05, -0.12, -0.02, 0.1, -0.06}
            Dim index = CInt(Math.Floor(hue / 30))
            Dim fraction = hue / 30 - index
            Dim center = 50 - ((1 - fraction) * corrections(index) + fraction * corrections(index + 1)) * saturation
            light = If(light < center, light / center, 1 + (light - center) / (100 - center)) * 0.5
            saturation /= 100
            Dim chroma = (1 - Math.Abs(2 * light - 1)) * saturation
            Dim x = chroma * (1 - Math.Abs((hue / 60 Mod 2) - 1))
            Dim values = If(hue < 60, {chroma, x, 0.0}, If(hue < 120, {x, chroma, 0.0}, If(hue < 180, {0.0, chroma, x}, If(hue < 240, {0.0, x, chroma}, If(hue < 300, {x, 0.0, chroma}, {chroma, 0.0, x})))))
            Dim m = light - chroma / 2
            Return Color.FromRgb(CByte(Math.Max(0, Math.Min(255, (values(0) + m) * 255))), CByte(Math.Max(0, Math.Min(255, (values(1) + m) * 255))), CByte(Math.Max(0, Math.Min(255, (values(2) + m) * 255))))
        End Function
        Private Sub CycleColor(index As String, color As Color)
            Dim brush = TryCast(Application.Current.Resources("ColorBrush" & index), SolidColorBrush)
            If brush IsNot Nothing AndAlso Not brush.IsFrozen Then brush.Color = color Else Application.Current.Resources("ColorBrush" & index) = New SolidColorBrush(color)
            Application.Current.Resources("ColorObject" & index) = color
        End Sub
        Friend Sub ApplyCycleFrame(title As System.Windows.Controls.Grid, shell As System.Windows.Controls.Grid, appearance As AppearancePreferences)
            Dim hue = appearance.Hue, sat = appearance.Saturation, adjustment = appearance.Lightness
            Dim lightness = If(appearance.DarkMode, {90.0, 75.0, 65.0, 58.0, 40.0, 30.0, 23.0, 20.0}, {25 + adjustment * 0.3, 45 + adjustment, 55 + adjustment, 65 + adjustment, 80 + adjustment * 0.4, 91 + adjustment * 0.1, 95, 97})
            For i = 0 To 7
                CycleColor((i + 1).ToString(Globalization.CultureInfo.InvariantCulture), If(appearance.DarkMode AndAlso i = 0, Color.FromRgb(228, 234, 242), Hsl2(hue, If(i = 0, sat * 0.2, sat), lightness(i))))
            Next
            Dim c4 = DirectCast(Application.Current.Resources("ColorBrush4"), SolidColorBrush).Color, c5 = DirectCast(Application.Current.Resources("ColorBrush5"), SolidColorBrush).Color
            CycleColor("Bg0", Color.FromRgb(CByte(c4.R * 0.4 + c5.R * 0.4 + 166 * 0.2), CByte(c4.G * 0.4 + c5.G * 0.4 + 166 * 0.2), CByte(c4.B * 0.4 + c5.B * 0.4 + 166 * 0.2)))
            Dim c7 = DirectCast(Application.Current.Resources("ColorBrush7"), SolidColorBrush).Color
            CycleColor("Bg1", Color.FromArgb(190, c7.R, c7.G, c7.B))
            Dim top = TryCast(title.Background, LinearGradientBrush)
            If top IsNot Nothing AndAlso Not top.IsFrozen AndAlso top.GradientStops.Count = 3 Then
                For i = 0 To 2 : top.GradientStops(i).Color = Hsl2(hue, sat, If(i = 1, 54, 48) + adjustment) : Next
            End If
            Dim background = TryCast(shell.Background, LinearGradientBrush)
            If Not appearance.DarkMode AndAlso appearance.ColorfulBackground AndAlso background IsNot Nothing AndAlso Not background.IsFrozen AndAlso background.GradientStops.Count = 3 Then
                For i = 0 To 2 : background.GradientStops(i).Color = Hsl2(hue + (i - 1) * 15, sat * 0.8, 91) : Next
            End If
            Dim previous = Changing : Changing = True
            Try
                RaiseEvent PaletteFrameChanged(Nothing, EventArgs.Empty)
            Finally
                Changing = previous
            End Try
        End Sub
        Friend Sub Apply(title As System.Windows.Controls.Grid, shell As System.Windows.Controls.Grid, Optional appearance As AppearancePreferences = Nothing)
            appearance = If(appearance, New AppearancePreferences)
            _darkMode = appearance.DarkMode
            ' Only the default blue arguments are present in public PCL ModSecret.
            ' Other named presets are independent HSL adaptations, not recovered hidden-theme data.
            Dim hue As Double = 210, saturation As Double = 85, adjustment As Double = 0
            Dim deltas = {0.0, 0.0, 0.0}
            Select Case appearance.Theme
                Case "lightblue" : saturation = 70 : adjustment = 6
                Case "deepblue" : hue = 215 : saturation = 80 : adjustment = -8
                Case "cyan" : hue = 175 : saturation = 65
                Case "green" : hue = 125 : saturation = 55
                Case "yellow" : hue = 50 : saturation = 80 : adjustment = -7
                Case "brown" : hue = 28 : saturation = 45 : adjustment = -10
                Case "black" : hue = 0 : saturation = 0 : adjustment = -10
                Case "colorful" : hue = 350 : saturation = 65 : adjustment = 3 : deltas = {-10.0, 5.0, 40.0}
                Case "custom" : hue = appearance.Hue : saturation = appearance.Saturation : adjustment = appearance.Lightness
            End Select
            Dim lightness = {25 + adjustment * 0.3, 45 + adjustment, 55 + adjustment, 65 + adjustment, 80 + adjustment * 0.4, 91 + adjustment * 0.1, 95, 97}
            If appearance.DarkMode Then lightness = {90.0, 75.0, 65.0, 58.0, 40.0, 30.0, 23.0, 20.0}
            For i = 0 To lightness.Length - 1
                Dim color = Hsl2(hue, If(i = 0, saturation * 0.2, saturation), lightness(i))
                Application.Current.Resources("ColorBrush" & (i + 1)) = New SolidColorBrush(color)
                Application.Current.Resources("ColorObject" & (i + 1)) = color
            Next
            Dim grayLight = {"#404040", "#737373", "#8c8c8c", "#a6a6a6", "#cccccc", "#ebebeb", "#f0f0f0", "#f5f5f5"}
            Dim grayDark = {"#dce3ed", "#b2becd", "#9eabbc", "#8795a8", "#607086", "#39475a", "#2b3748", "#222d3d"}
            For index = 0 To 7
                Dim color = CType(ColorConverter.ConvertFromString(If(appearance.DarkMode, grayDark(index), grayLight(index))), Color)
                Application.Current.Resources("ColorBrushGray" & (index + 1)) = New SolidColorBrush(color)
                Application.Current.Resources("ColorObjectGray" & (index + 1)) = color
            Next
            ' PCL MyResourceItem uses Gray1 at forty-percent opacity for its subtitle.
            ' A separate brush preserves that hierarchy without fading the main title.
            Application.Current.Resources("PclResourceSubtitleInk") = New SolidColorBrush(DirectCast(Application.Current.Resources("ColorBrushGray1"), SolidColorBrush).Color) With {.Opacity = 0.4}
            If appearance.DarkMode Then Application.Current.Resources("ColorBrush1") = New SolidColorBrush(Color.FromRgb(228, 234, 242))
            Application.Current.Resources("PclIconButtonInk") = New SolidColorBrush(If(appearance.DarkMode, DirectCast(Application.Current.Resources("ColorBrushGray1"), SolidColorBrush).Color, Colors.Black))
            ' PCL MyResourceItem.Tags: #11000000 plate / #868686 text in light mode.
            Application.Current.Resources("PclResourceTagBackground") = New SolidColorBrush(If(appearance.DarkMode, Color.FromArgb(24, 255, 255, 255), Color.FromArgb(17, 0, 0, 0)))
            Application.Current.Resources("PclResourceTagInk") = New SolidColorBrush(If(appearance.DarkMode, Color.FromRgb(178, 190, 205), Color.FromRgb(134, 134, 134)))
            Application.Current.Resources("ColorBrushHalfWhite") = New SolidColorBrush(If(appearance.DarkMode, Color.FromArgb(220, 36, 48, 65), Color.FromArgb(85, 255, 255, 255)))
            Application.Current.Resources("CardSurfaceBrush") = New SolidColorBrush(If(appearance.DarkMode, Color.FromArgb(250, 29, 39, 53), Color.FromArgb(245, 255, 255, 255)))
            Application.Current.Resources("WindowSurfaceBrush") = New SolidColorBrush(If(appearance.DarkMode, Color.FromRgb(19, 27, 38), Colors.White))
            Application.Current.Resources("ColorBrushRedDark") = New SolidColorBrush(If(appearance.DarkMode, Color.FromRgb(255, 142, 134), Color.FromRgb(206, 33, 17)))
            Dim color4 = DirectCast(Application.Current.Resources("ColorBrush4"), SolidColorBrush).Color
            Dim color5 = DirectCast(Application.Current.Resources("ColorBrush5"), SolidColorBrush).Color
            Dim backgroundOutline = Color.FromRgb(CByte(color4.R * 0.4 + color5.R * 0.4 + 166 * 0.2), CByte(color4.G * 0.4 + color5.G * 0.4 + 166 * 0.2), CByte(color4.B * 0.4 + color5.B * 0.4 + 166 * 0.2))
            Application.Current.Resources("ColorBrushBg0") = New SolidColorBrush(backgroundOutline)
            Application.Current.Resources("ColorObjectBg0") = backgroundOutline
            Dim color7 = DirectCast(Application.Current.Resources("ColorBrush7"), SolidColorBrush).Color
            Dim pressedBackground = Color.FromArgb(190, color7.R, color7.G, color7.B)
            Application.Current.Resources("ColorBrushBg1") = New SolidColorBrush(pressedBackground)
            Application.Current.Resources("ColorObjectBg1") = pressedBackground
            Dim top As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(1, 0)}
            top.GradientStops.Add(New GradientStop(Hsl2(hue + deltas(0), saturation, 48 + adjustment), 0))
            top.GradientStops.Add(New GradientStop(Hsl2(hue + deltas(1), saturation, 54 + adjustment), 0.5))
            top.GradientStops.Add(New GradientStop(Hsl2(hue + deltas(2), saturation, 48 + adjustment), 1))
            title.Background = top
            Dim background As New LinearGradientBrush With {.StartPoint = New Point(0.9, 0), .EndPoint = New Point(0.1, 1)}
            background.GradientStops.Add(New GradientStop(Hsl2(hue - 15, saturation * 0.8, 91), -0.1))
            background.GradientStops.Add(New GradientStop(Hsl2(hue, saturation * 0.8, 91), 0.4))
            background.GradientStops.Add(New GradientStop(Hsl2(hue + 15, saturation * 0.8, 91), 1.1))
            shell.Background = If(appearance.ColorfulBackground, DirectCast(background, Brush), New SolidColorBrush(Color.FromRgb(245, 245, 245)))
            If appearance.DarkMode Then shell.Background = New SolidColorBrush(Color.FromRgb(19, 27, 38))
            Changing = True
            Try
                RaiseEvent PaletteChanged(Nothing, EventArgs.Empty)
            Finally
                Changing = False
            End Try
        End Sub
    End Module
End Namespace
