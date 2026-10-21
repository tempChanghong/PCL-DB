Imports System.Windows
Imports System.Windows.Media

Namespace Controls
    ' Port of PCL ModBase.MyColor.FromHSL2 and ModSecret.ThemeRefreshMain.
    Friend Module PclTheme
        Private Event PaletteChanged As EventHandler
        Friend Property Changing As Boolean
        Friend Sub Watch(control As FrameworkElement, refresh As Action)
            Dim watching = False
            Dim handler As EventHandler = Sub() refresh()
            AddHandler control.Loaded, Sub()
                                          If Not watching Then
                                              AddHandler PaletteChanged, handler
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
        Friend Sub Apply(title As System.Windows.Controls.Grid, shell As System.Windows.Controls.Grid, Optional appearance As AppearancePreferences = Nothing)
            appearance = If(appearance, New AppearancePreferences)
            ' Only the default blue arguments are present in public PCL ModSecret.
            ' Other named presets are independent HSL adaptations, not recovered hidden-theme data.
            Dim hue As Double = 210, saturation As Double = 85, adjustment As Double = 0
            Dim deltas = {0.0, 0.0, 0.0}
            Select Case appearance.Theme
                Case "cyan" : hue = 175 : saturation = 65
                Case "green" : hue = 125 : saturation = 55
                Case "yellow" : hue = 50 : saturation = 80 : adjustment = -7
                Case "brown" : hue = 28 : saturation = 45 : adjustment = -10
                Case "black" : hue = 0 : saturation = 0 : adjustment = -10
                Case "colorful" : hue = 350 : saturation = 65 : adjustment = 3 : deltas = {-10.0, 5.0, 40.0}
                Case "custom" : hue = appearance.Hue : saturation = appearance.Saturation : adjustment = appearance.Lightness
            End Select
            Dim lightness = {25 + adjustment * 0.3, 45 + adjustment, 55 + adjustment, 65 + adjustment, 80 + adjustment * 0.4, 91 + adjustment * 0.1, 95, 97}
            For i = 0 To lightness.Length - 1
                Dim color = Hsl2(hue, If(i = 0, saturation * 0.2, saturation), lightness(i))
                Application.Current.Resources("ColorBrush" & (i + 1)) = New SolidColorBrush(color)
                Application.Current.Resources("ColorObject" & (i + 1)) = color
            Next
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
            Changing = True
            Try
                RaiseEvent PaletteChanged(Nothing, EventArgs.Empty)
            Finally
                Changing = False
            End Try
        End Sub
    End Module
End Namespace
