Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyMotionSlotBridgeUi(directory As String, passed As List(Of String))
        Dim observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        For Each transformTarget In {False, True}
            VerifyMotionSlotTarget(transformTarget, check)
        Next
        File.WriteAllText(Path.Combine(directory, "motion-slot-bridge.json"), observations.ToString())
        Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
        If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
    End Sub

    Private Shared Function MotionSlotBaseValue(target As DependencyObject, [property] As DependencyProperty) As Double
        If TypeOf target Is Animatable Then Return CDbl(DirectCast(target, Animatable).GetAnimationBaseValue([property]))
        Return CDbl(DirectCast(target, UIElement).GetAnimationBaseValue([property]))
    End Function

    Private Sub VerifyMotionSlotTarget(transformTarget As Boolean, check As Action(Of Boolean, String))
        Dim visual As New Border With {.Width = 100, .Height = 40, .Background = Brushes.SteelBlue, .Opacity = 0.1}
        Dim translation As New TranslateTransform With {.Y = 3}
        visual.RenderTransform = translation
        Dim target As DependencyObject = If(transformTarget, DirectCast(translation, DependencyObject), visual)
        Dim [property] = If(transformTarget, TranslateTransform.YProperty, OpacityProperty)
        Dim label As Func(Of String, String) = Function(value) value & "/target=" & If(transformTarget, "Animatable.Y", "UIElement.Opacity")
        Dim initial = If(transformTarget, 3.0, 0.1), seed = If(transformTarget, 10.0, 0.2), destination = If(transformTarget, 30.0, 0.8)
        Dim otherBase = If(transformTarget, 99.0, 0.9), nextDestination = If(transformTarget, 40.0, 0.6), reset = If(transformTarget, 5.0, 0.3)
        Dim naturalEnd = If(transformTarget, 25.0, 0.7), explicitFinal = If(transformTarget, 12.0, 0.4)
        Dim close As Func(Of Double, Double, Boolean) = Function(actual, expected) Math.Abs(actual - expected) < 0.000001
        Dim host As New Border With {.Width = 220, .Height = 120, .Child = visual}, layout = ControlLayout(host)
        Dim slot As New PclMotionSlot(target, [property])
        Try
            Using surface = PreviewSurface.Attach(host, 220, 120)
                layout()
                check(slot.Clock Is Nothing AndAlso close(CDbl(target.GetValue([property])), initial) AndAlso Not DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("constructing a motion slot preserves the existing property and base value"))
                slot.SetValue(seed)
                check(close(CDbl(target.GetValue([property])), seed) AndAlso close(MotionSlotBaseValue(target, [property]), seed) AndAlso slot.Clock Is Nothing,
                    label("setting a stable slot value writes the caller's local base value"))
                slot.Animate(New DoubleAnimation(seed, destination, TimeSpan.FromMilliseconds(100)), destination, True)
                Dim first = slot.Clock
                check(first IsNot Nothing AndAlso close(CDbl(target.GetValue([property])), seed) AndAlso close(MotionSlotBaseValue(target, [property]), seed),
                    label("a paused slot entry starts at its displayed origin without a final-state flash"))
                If first IsNot Nothing Then SeekControl(first, 40)
                Dim sampled = seed + (destination - seed) * 0.4
                check(first IsNot Nothing AndAlso close(CDbl(target.GetValue([property])), sampled) AndAlso close(MotionSlotBaseValue(target, [property]), seed),
                    label("a slot exposes an actual intermediate frame independently of its local base value"))
                target.SetValue([property], otherBase)
                slot.Animate(New DoubleAnimation(nextDestination, TimeSpan.FromMilliseconds(100)), nextDestination, True)
                Dim replacement = slot.Clock
                check(replacement IsNot Nothing AndAlso replacement IsNot first AndAlso close(CDbl(target.GetValue([property])), sampled) AndAlso close(MotionSlotBaseValue(target, [property]), sampled),
                    label("replacing a slot samples its shown value after an external base-value change"))
                If replacement IsNot Nothing Then SeekControl(replacement, 50)
                Dim midpoint = sampled + (nextDestination - sampled) * 0.5
                check(replacement IsNot Nothing AndAlso close(CDbl(target.GetValue([property])), midpoint),
                    label("a replacement slot interpolates from the shown frame rather than an obsolete base value"))
                first?.Controller.SkipToFill() : FlushControlLayout()
                check(slot.Clock Is replacement AndAlso replacement IsNot Nothing AndAlso close(CDbl(target.GetValue([property])), midpoint),
                    label("a retired clock completion cannot settle or overwrite its replacement slot"))
                slot.SetValue(reset)
                check(slot.Clock Is Nothing AndAlso close(CDbl(target.GetValue([property])), reset) AndAlso close(MotionSlotBaseValue(target, [property]), reset) AndAlso Not DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("setting a value during motion removes the owned clock and its animated property source"))
                slot.SetValue(reset) : FlushControlLayout()
                check(slot.Clock Is Nothing AndAlso close(CDbl(target.GetValue([property])), reset) AndAlso Not DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("repeating a stable slot value leaves no clock or changed property source"))
                Dim reentrant As AnimationClock = Nothing, replacing = False
                Dim ending As New DoubleAnimation(reset, destination, TimeSpan.FromMilliseconds(100))
                ' Timeline handlers are copied when the clock is created, before
                ' the slot adds its own completion handler. Reenter through that
                ' actual event so the old handler runs after a new owner exists.
                AddHandler ending.Completed, Sub()
                                                 If replacing Then Return
                                                 replacing = True
                                                 slot.Animate(New DoubleAnimation(CDbl(target.GetValue([property])), nextDestination, TimeSpan.FromMilliseconds(100)), nextDestination, True)
                                                 reentrant = slot.Clock
                                             End Sub
                slot.Animate(ending, destination, True)
                slot.Clock?.Controller.SkipToFill()
                Dim replaced = PumpMotionUntil(Function() reentrant IsNot Nothing)
                check(replaced AndAlso slot.Clock Is reentrant AndAlso reentrant IsNot Nothing AndAlso DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("an old completion handler cannot clear the new slot created by an earlier completion callback"))
                slot.SetValue(reset)
                slot.Animate(New DoubleAnimation(reset, naturalEnd, TimeSpan.FromMilliseconds(20)), naturalEnd)
                Dim natural = slot.Clock
                Dim completed = PumpMotionUntil(Function() slot.Clock Is Nothing)
                check(natural IsNot Nothing AndAlso completed AndAlso close(CDbl(target.GetValue([property])), naturalEnd) AndAlso close(MotionSlotBaseValue(target, [property]), naturalEnd) AndAlso Not DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("natural clock completion releases ownership and commits the final local value"))
                slot.Animate(New DoubleAnimation(naturalEnd, destination, TimeSpan.FromMilliseconds(100)), explicitFinal, True)
                Dim finalClock = slot.Clock
                finalClock?.Controller.SkipToFill()
                Dim settled = PumpMotionUntil(Function() slot.Clock Is Nothing)
                check(finalClock IsNot Nothing AndAlso settled AndAlso close(CDbl(target.GetValue([property])), explicitFinal) AndAlso close(MotionSlotBaseValue(target, [property]), explicitFinal) AndAlso Not DependencyPropertyHelper.GetValueSource(target, [property]).IsAnimated,
                    label("clock completion applies the caller's final value even when the timeline target differs"))
            End Using
        Finally
            slot.SetValue(reset)
        End Try
    End Sub
End Class
