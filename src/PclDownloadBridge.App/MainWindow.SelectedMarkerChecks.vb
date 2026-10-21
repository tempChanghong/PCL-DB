Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyListSelectionMotion(directory As String, check As Action(Of Boolean, String))
        For Each start In {0.0, 12.0, 50.0}
            For Each target In {0.0, 30.0}
                Dim animation As New PclSelectedHeightAnimation(start, target)
                Dim frozen = DirectCast(animation.Clone(), PclSelectedHeightAnimation) : frozen.Freeze()
                Dim matches = frozen.IsFrozen
                For Each milliseconds In {0.0, 15, 50, 100, 150, 200, 250, 300, 400}
                    matches = matches AndAlso Math.Abs(frozen.Sample(milliseconds) - ReferenceSelectedHeight(start, target, milliseconds)) < 0.000001
                Next
                check(matches, "selected-marker frozen curve retains both PCL deltas from " & start & " to " & target)
            Next
        Next
        Dim row As New PclListItem With {.Title = "选中版本", .Height = 42}
        Dim host As New Border With {.Width = 400, .Height = 120, .Child = row}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(400, 120)) : host.Arrange(New Rect(0, 0, 400, 120))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 400, 120)
            layout()
            Dim marker = row.Children.OfType(Of Border)().Last()
            Dim idle = DirectCast(row.Foreground, SolidColorBrush).Color
            row.Selected = True
            Dim animated = marker.HasAnimatedProperties
            Dim continuous = DirectCast(row.Foreground, SolidColorBrush).Color = idle
            row.Height = 36 : layout()
            row.Visibility = Visibility.Collapsed : layout()
            Dim compactInk = DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color
            IO.File.WriteAllText(IO.Path.Combine(directory, "selected-marker-baseline.json"), New JObject From {
                {"markerAnimated", animated}, {"inkPreservedAtStart", continuous}, {"compactInkCorrect", compactInk},
                {"selected", row.Selected}, {"markerHeight", marker.ActualHeight}}.ToString())
            check(animated AndAlso continuous, "a visible row selection starts its own marker and ink transition without jumping to the final state")
            check(compactInk AndAlso row.Selected, "changing selected row height and hiding it settles the correct compact accent without losing selection")
            row.Visibility = Visibility.Visible : layout()
            VerifySelectedMarkerTimelines(row, marker, host, layout, check, directory)
            VerifySelectedMarkerPalette(row, marker, layout, check)
        End Using
    End Sub

    Private Shared Function ReferenceSelectedHeight(start As Double, target As Double, milliseconds As Double) As Double
        Dim fluent = 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, milliseconds / 200)), 2)
        Dim time = Math.Max(0, Math.Min(1, milliseconds / 300))
        Dim back = 1 - Math.Pow(1 - time, 2) * Math.Cos(1.5 * Math.PI * time)
        Return Math.Max(0, start + (target - start) * (0.4 * fluent + 0.6 * back))
    End Function

    Private Shared Sub VerifySelectedMarkerTimelines(row As PclListItem, marker As Border, host As Border, layout As Action, check As Action(Of Boolean, String), directory As String)
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                       clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime) : layout()
                                                   End Sub
        Dim settle As Action = Sub()
                                   row.IsHitTestVisible = False : row.IsHitTestVisible = True : layout()
                               End Sub
        row.Selected = False : settle() : row.Height = 42 : layout()
        check(row.SelectionTransition Is Nothing AndAlso marker.Height = 0 AndAlso marker.Opacity = 0,
            "initial and stable unselected rows do not create empty selection clocks during layout")
        row.Selected = True
        Dim selected = row.SelectionTransition : selected.Controller.Pause() : layout()
        Dim plan = DirectCast(selected.Timeline, ParallelTimeline)
        check(plan.Children(0).Duration.TimeSpan.TotalMilliseconds = 300 AndAlso plan.Children(1).Duration.TimeSpan.TotalMilliseconds = 30 AndAlso plan.Children(2).Duration.TimeSpan.TotalMilliseconds = 200,
            "PCL marker entry uses parallel 200/300ms height curves, a 30ms fade and 200ms ink transition")
        check(marker.VerticalAlignment = VerticalAlignment.Center AndAlso marker.Margin = New Thickness(-1, 0, 0, 0),
            "an animating selection marker stays centered with the source zero vertical margin")
        seek(selected, 15)
        Dim sampleTime = selected.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(marker.Height - ReferenceSelectedHeight(0, 30, sampleTime)) < 0.00001 AndAlso Math.Abs(marker.Opacity - Math.Min(1, sampleTime / 30)) < 0.00001,
            "live marker entry height and opacity match the source near the first visible frames")
        seek(selected, 200)
        sampleTime = selected.CurrentTime.Value.TotalMilliseconds
        check(marker.Height > 30 AndAlso Math.Abs(marker.Height - ReferenceSelectedHeight(0, 30, sampleTime)) < 0.00001,
            "selected marker height has PCL's weak cosine overshoot rather than a plain size tween")
        Dim retained = row.Foreground
        For index = 1 To 500
            row.Selected = True
        Next
        check(row.SelectionTransition Is selected AndAlso row.Foreground Is retained,
            "five hundred identical selections preserve the marker clock and animated ink brush")
        SetSelectionHover(row, True)
        Dim hover = row.HoverTransition : hover.Controller.Pause() : layout()
        check(row.SelectionTransition Is selected AndAlso hover IsNot Nothing,
            "hover entry and the selected-marker transition own independent clock groups")
        Dim originHeight = marker.ActualHeight, originOpacity = marker.Opacity, originInk = DirectCast(row.Foreground, SolidColorBrush).Color
        row.Selected = False
        Dim clearing = row.SelectionTransition : clearing.Controller.Pause()
        check(marker.Height = originHeight AndAlso marker.Opacity = originOpacity AndAlso DirectCast(row.Foreground, SolidColorBrush).Color = originInk AndAlso row.HoverTransition Is hover,
            "reversing selection preserves displayed marker height, opacity and ink without touching hover")
        layout()
        plan = DirectCast(clearing.Timeline, ParallelTimeline)
        Dim shrink = DirectCast(plan.Children(0), DoubleAnimation), fade = DirectCast(plan.Children(1), DoubleAnimation)
        check(shrink.Duration.TimeSpan.TotalMilliseconds = 120 AndAlso DirectCast(shrink.EasingFunction, PowerEase).Power = 2 AndAlso
            DirectCast(shrink.EasingFunction, PowerEase).EasingMode = EasingMode.EaseIn AndAlso fade.BeginTime.Value.TotalMilliseconds = 40 AndAlso
            fade.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso plan.Children(2).Duration.TimeSpan.TotalMilliseconds = 120,
            "PCL deselection uses 120ms quadratic shrink, a 40ms delayed 70ms fade and 120ms ink")
        seek(clearing, 20)
        sampleTime = clearing.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(marker.Height - originHeight * (1 - Math.Pow(sampleTime / 120, 2))) < 0.00001 AndAlso marker.Opacity = originOpacity,
            "deselection shrinks before its delayed opacity fade starts")
        seek(clearing, 80)
        sampleTime = clearing.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(marker.Height - originHeight * (1 - Math.Pow(sampleTime / 120, 2))) < 0.00001 AndAlso
            Math.Abs(marker.Opacity - originOpacity * (1 - Math.Max(0, Math.Min(1, (sampleTime - 40) / 70)))) < 0.00001,
            "live deselection height and delayed opacity follow the source at the actual WPF clock time")
        originHeight = marker.ActualHeight : originOpacity = marker.Opacity : originInk = DirectCast(row.Foreground, SolidColorBrush).Color
        row.Selected = True
        Dim replacement = row.SelectionTransition : replacement.Controller.Pause()
        check(marker.Height = originHeight AndAlso marker.Opacity = originOpacity AndAlso DirectCast(row.Foreground, SolidColorBrush).Color = originInk,
            "rapid reselection begins at the partially cleared marker and ink")
        layout()
        selected.Controller.SkipToFill() : clearing.Controller.SkipToFill() : layout()
        check(row.SelectionTransition Is replacement AndAlso row.Selected AndAlso row.HoverTransition Is hover,
            "superseded selection completions cannot clear a newer selection or hover group")
        seek(replacement, 301)
        check(row.SelectionTransition Is Nothing AndAlso Double.IsNaN(marker.Height) AndAlso marker.VerticalAlignment = VerticalAlignment.Stretch AndAlso
            marker.Margin = New Thickness(-1, 6, 0, 6) AndAlso marker.Opacity = 1 AndAlso Not marker.HasAnimatedProperties AndAlso
            Not DirectCast(row.Foreground, SolidColorBrush).HasAnimatedProperties AndAlso DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color,
            "selection completion releases clocks and restores adaptive marker layout and palette resources")
        row.Height = 36 : layout()
        check(row.SelectionTransition Is Nothing AndAlso Math.Abs(marker.ActualHeight - 24) < 0.001 AndAlso DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
            "a stable selected row follows compact height and changes accent without replaying entry")
        row.Selected = False
        clearing = row.SelectionTransition : clearing.Controller.Pause() : layout()
        row.Height = 8 : layout()
        check(row.SelectionTransition Is clearing, "row-height changes do not restart a deselection whose target is already zero")
        seek(clearing, 121)
        check(marker.Height = 0 AndAlso marker.Opacity = 0 AndAlso Not marker.HasAnimatedProperties AndAlso row.SelectionTransition Is Nothing,
            "deselection removes its clocks at the exact invisible zero-height endpoint")

        row.Height = 42 : layout() : row.Selected = True
        selected = row.SelectionTransition : selected.Controller.Pause() : layout() : seek(selected, 80)
        originHeight = marker.ActualHeight
        row.Height = 66 : layout()
        replacement = row.SelectionTransition : replacement.Controller.Pause() : layout()
        Dim heightPlan = DirectCast(DirectCast(replacement.Timeline, ParallelTimeline).Children(0), PclSelectedHeightAnimation)
        check(replacement IsNot selected AndAlso Math.Abs(heightPlan.Sample(0) - originHeight) < 0.00001 AndAlso Math.Abs(heightPlan.Sample(300) - 54) < 0.00001,
            "resizing during marker entry retargets from the displayed height to the new row geometry")
        seek(replacement, 100)
        check(Math.Abs(marker.Height - heightPlan.Sample(replacement.CurrentTime.Value.TotalMilliseconds)) < 0.00001,
            "a resized marker continues on the original parallel source curves")
        selected.Controller.SkipToFill() : layout()
        check(row.SelectionTransition Is replacement, "completion from the old row geometry cannot end a retargeted marker")
        seek(replacement, 301)
        check(Double.IsNaN(marker.Height) AndAlso Math.Abs(marker.ActualHeight - 54) < 0.001 AndAlso row.SelectionTransition Is Nothing,
            "the resized selected marker returns to adaptive layout at the new final height")

        SetSelectionHover(row, False)
        Dim frames = IO.Path.Combine(directory, "selected-marker-frames") : IO.Directory.CreateDirectory(frames)
        row.Selected = False : settle() : row.Height = 42 : layout() : row.Selected = True
        selected = row.SelectionTransition : selected.Controller.Pause() : layout()
        For index = 0 To 12
            seek(selected, index * 25)
            SaveControlVisual(host, IO.Path.Combine(frames, index.ToString("D2") & ".png"))
        Next
        seek(selected, 301)
        check(row.Selected AndAlso row.SelectionTransition Is Nothing, "the recorded marker entry finishes without retaining a held clock")
        For Each interruption In {"hidden", "input-gate", "disabled", "detached"}
            row.Selected = False : settle() : row.Selected = True
            selected = row.SelectionTransition : selected.Controller.Pause() : layout() : seek(selected, 80)
            Select Case interruption
                Case "hidden" : host.Visibility = Visibility.Collapsed : layout()
                Case "input-gate" : row.IsHitTestVisible = False : layout()
                Case "disabled" : row.IsEnabled = False : layout()
                Case Else : host.Child = Nothing : layout()
            End Select
            check(row.Selected AndAlso row.SelectionTransition Is Nothing AndAlso Not marker.HasAnimatedProperties AndAlso Double.IsNaN(marker.Height) AndAlso marker.Opacity = 1,
                "selected marker " & interruption & " settles geometry while preserving logical selection")
            Select Case interruption
                Case "hidden" : host.Visibility = Visibility.Visible : layout()
                Case "input-gate" : row.IsHitTestVisible = True : layout()
                Case "disabled" : row.IsEnabled = True : layout()
                Case Else : host.Child = row : layout()
            End Select
            selected.Controller.SkipToFill() : layout()
            check(row.Selected AndAlso row.SelectionTransition Is Nothing AndAlso Math.Abs(marker.ActualHeight - 30) < 0.001,
                "restoring marker after " & interruption & " cannot revive an old clock")
        Next
    End Sub

    Private Sub VerifySelectedMarkerPalette(row As PclListItem, marker As Border, layout As Action, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson()
        Try
            row.Selected = False
            Dim clearing = row.SelectionTransition : clearing.Controller.Pause() : layout()
            RestoreAppearance(New JObject From {{"theme", "deepblue"}, {"darkMode", True}}) : layout()
            check(Not row.Selected AndAlso row.SelectionTransition Is Nothing AndAlso marker.Height = 0 AndAlso marker.Opacity = 0 AndAlso
                DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush1").Color,
                "theme replacement settles deselection to the current palette without retaining old ink")
            row.Selected = True
            Dim entry = row.SelectionTransition : entry.Controller.Pause() : layout()
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}}) : layout()
            check(row.Selected AndAlso row.SelectionTransition Is Nothing AndAlso marker.Opacity = 1 AndAlso Double.IsNaN(marker.Height) AndAlso
                DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color AndAlso
                DirectCast(marker.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                "theme replacement settles a selected marker and its ink to the new resource colors")
            clearing.Controller.SkipToFill() : entry.Controller.SkipToFill() : layout()
            check(row.Selected AndAlso row.SelectionTransition Is Nothing AndAlso Not marker.HasAnimatedProperties,
                "stale selection clocks from another palette cannot restore their old colors or geometry")
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub
End Class
