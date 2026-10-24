Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyPclActionColorOwnership(directory As String, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson(), clicks = 0
        Dim web As New PclActionButton With {.Text = "转到 Modrinth", .Highlight = True,
            .Logo = Geometry.Parse(CStr(Application.Current.FindResource("IconResourceSource"))), .Margin = New Thickness(0, 0, 8, 0)}
        Dim copy As New PclActionButton With {.Text = "复制名称", .Logo = Geometry.Parse("M320 80H864V704H752V192H320ZM160 288H656V944H160ZM272 400V832H544V400Z")}
        Dim row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(20)}, host As New Border With {.Width = 400, .Height = 90, .Child = row}
        host.SetResourceReference(BackgroundProperty, "CardSurfaceBrush")
        row.Children.Add(web) : row.Children.Add(copy) : AddHandler copy.Click, Sub() clicks += 1
        Dim layout = ControlLayout(host)
        Try
            Using surface = PreviewSurface.Attach(host, 400, 90)
                layout()
                copy.MotionPolicy.EnabledForChecks = True : copy.MotionPolicy.PauseForChecks = True
                For Each dark In {False, True}
                    copy.Highlight = False : ControlPointer(copy, False)
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    Dim normal = PclAnimation.Brush("ColorBrush1").Color, accent = PclAnimation.Brush("ColorBrush3").Color
                    check(DirectCast(web.Foreground, SolidColorBrush).Color = accent AndAlso DirectCast(copy.Foreground, SolidColorBrush).Color = normal AndAlso
                        web.BorderThickness.Equals(New Thickness(0)) AndAlso copy.BorderThickness.Equals(New Thickness(0)),
                        "PCL highlighted and neutral actions retain distinct idle ink without permanent outlines: " & dark)
                    Dim glyph = DirectCast(copy.Template.FindName("ActionIcon", copy), Shapes.Path), ratio = copy.Logo.Bounds.Width / copy.Logo.Bounds.Height
                    check(glyph.ActualHeight <= 16 AndAlso glyph.ActualWidth <= 16 AndAlso Math.Abs(glyph.ActualWidth / glyph.ActualHeight - ratio) < 0.001,
                        "PCL icon measurement preserves the narrow clipboard's natural aspect and text spacing: " & dark)
                    ControlPointer(copy, True)
                    Dim hoverInk = copy.InkTransition
                    SeekControl(hoverInk, 35) : ControlKey(copy, Key.Space, True)
                    Dim heldFill = copy.FillTransition
                    check(copy.InkTransition Is hoverInk AndAlso heldFill IsNot Nothing AndAlso heldFill.Timeline.Duration.TimeSpan.TotalMilliseconds = 70,
                        "press updates only the background and retains the in-flight PCL hover ink: " & dark)
                    SeekControl(hoverInk, 101)
                    check(copy.InkTransition Is Nothing AndAlso copy.FillTransition Is heldFill AndAlso DirectCast(copy.Foreground, SolidColorBrush).Color = accent,
                        "hover ink completes independently while the pressed background retains its own clock: " & dark)
                    ControlKey(copy, Key.Space, False) : ControlPointer(copy, False)
                    Dim leaving = copy.InkTransition
                    SeekControl(leaving, 75)
                    Dim middle = DirectCast(copy.Foreground, SolidColorBrush).Color
                    IO.File.WriteAllText(IO.Path.Combine(directory, "pcl-action-midpoint-" & If(dark, "dark", "light") & ".json"), New JObject From {
                        {"origin", accent.ToString()}, {"destination", normal.ToString()}, {"actual", middle.ToString()},
                        {"milliseconds", 75}, {"duration", leaving.Timeline.Duration.TimeSpan.TotalMilliseconds}, {"animation", leaving.Timeline.GetType().Name}}.ToString())
                    check(leaving.Timeline.Duration.TimeSpan.TotalMilliseconds = 150 AndAlso Math.Abs(middle.R - (accent.R + CInt(normal.R)) / 2.0) <= 1 AndAlso
                        Math.Abs(middle.G - (accent.G + CInt(normal.G)) / 2.0) <= 1 AndAlso Math.Abs(middle.B - (accent.B + CInt(normal.B)) / 2.0) <= 1,
                        "PCL leave reaches the actual midpoint of its hundred-and-fifty-millisecond ink transition: " & dark)
                    copy.Highlight = True
                    check(copy.InkTransition Is Nothing AndAlso copy.FillTransition Is Nothing AndAlso DirectCast(copy.Foreground, SolidColorBrush).Color = accent,
                        "changing the action role retires both old clocks and applies current accent ink: " & dark)
                    leaving.Controller.SkipToFill() : layout()
                    check(DirectCast(copy.Foreground, SolidColorBrush).Color = accent AndAlso copy.InkTransition Is Nothing,
                        "an old neutral leave cannot overwrite a newer highlighted action: " & dark)
                    copy.IsEnabled = False
                    check(DirectCast(copy.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrushGray5").Color AndAlso copy.InkTransition Is Nothing AndAlso copy.FillTransition Is Nothing,
                        "disabled highlighted actions use PCL's gray ink and release their clocks: " & dark)
                    copy.IsEnabled = True
                    Dim before = clicks
                    ControlKey(copy, Key.Enter, True) : ControlKey(copy, Key.Enter, False)
                    check(clicks = before + 1 AndAlso DirectCast(copy.Foreground, SolidColorBrush).Color = accent,
                        "a re-enabled highlighted action preserves fresh native Enter activation: " & dark)
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not dark}}) : layout()
                    check(DirectCast(web.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color AndAlso web.InkTransition Is Nothing AndAlso copy.InkTransition Is Nothing,
                        "live palette replacement keeps action roles and removes old ink animations: " & dark)
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    copy.Highlight = False
                    SaveControlVisual(host, IO.Path.Combine(directory, "pcl-actions-" & If(dark, "dark", "light") & ".png"))
                Next
            End Using
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub
End Class
