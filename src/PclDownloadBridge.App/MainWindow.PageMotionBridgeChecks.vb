Imports System.IO
Imports System.Reflection
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyPageMotionBridgeUi(directory As String, passed As List(Of String))
        ' Fixed PCL 5ef81e4 MyPageRight: 100/70-ms alpha, 25/15-ms stagger,
        ' parallel 5/250 + 11/350 Y tracks and 350/90-ms scrollbar motion.
        ' CE 915ac297 retains those values. Observe the original VB entry points
        ' so this exact test source also runs against the pre-migration assembly.
        Dim before = passed.Count, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        IO.Directory.CreateDirectory(directory)
        Try
            VerifyPageBridgeContracts(check)
            VerifyPageBridgeCards(check, observations)
            VerifyPageBridgeSurface(check, observations)
            If passed.Count - before <> 30 Then Throw New InvalidOperationException("Page motion bridge must report exactly 30 checks.")
        Finally
            observations("Count") = passed.Count - before
            observations("Checks") = New JArray(passed.Skip(before))
            observations("WindowsConfigurationChanged") = False
            File.WriteAllText(Path.Combine(directory, "page-motion-bridge.json"), observations.ToString())
        End Try
    End Sub

    Private Shared Function PageBridgeReferenceY(milliseconds As Double) As Double
        Dim f = Math.Max(0, Math.Min(1, milliseconds / 250)), b = Math.Max(0, Math.Min(1, milliseconds / 350))
        Return -16 + 5 * (1 - Math.Pow(1 - f, 3)) + 11 * (1 - Math.Pow(1 - b, 1.5) * Math.Cos(1.5 * Math.PI * b))
    End Function
    Private Shared Function PageBridgeNear(actual As Double, expected As Double) As Boolean
        Return Math.Abs(actual - expected) < 0.000001
    End Function
    Private Shared Function PageBridgeOffset(target As FrameworkElement) As TranslateTransform
        Return DirectCast(DirectCast(target.RenderTransform, TransformGroup).Children.Last(), TranslateTransform)
    End Function
    Private Shared Function PageBridgeFrames(plan As ParallelTimeline, index As Integer) As DoubleAnimationUsingKeyFrames
        Return DirectCast(plan.Children(index), DoubleAnimationUsingKeyFrames)
    End Function
    Private Shared Sub VerifyPageBridgeContracts(check As Action(Of Boolean, String))
        Dim app = GetType(MainWindow).Assembly, kind = GetType(PclPageCardYAnimation)
        check(kind.Assembly Is app AndAlso kind.IsSealed AndAlso kind.BaseType Is GetType(DoubleAnimationBase),
            "page bridge retains the original sealed VB card Y animation identity")
        Dim origin = DirectCast(kind.GetField("OriginProperty", BindingFlags.Static Or BindingFlags.NonPublic).GetValue(Nothing), DependencyProperty)
        check(origin.OwnerType Is kind AndAlso origin.Name = "Origin" AndAlso origin.PropertyType Is GetType(Double) AndAlso CDbl(origin.GetMetadata(kind).DefaultValue) = -16,
            "page bridge retains the Origin dependency property owner name type and default")
        Dim animation As New PclPageCardYAnimation()
        check(kind.GetConstructor(Type.EmptyTypes) IsNot Nothing AndAlso animation.Duration.TimeSpan.TotalMilliseconds = 350,
            "page bridge retains the public parameterless animation constructor and 350-ms duration")
        Dim half As New PclPageCardYAnimation(-8)
        check(animation.Sample(0) = -16 AndAlso animation.Sample(350) = 0 AndAlso half.Sample(0) = -8 AndAlso
            PageBridgeNear(half.Sample(125), PageBridgeReferenceY(125) / 2),
            "page bridge scales the two independent upstream Y increments from a sampled reversal origin")
        Dim clone = DirectCast(half.Clone(), PclPageCardYAnimation)
        clone.Freeze()
        check(clone.IsFrozen AndAlso clone.GetType() Is kind AndAlso clone.Duration = half.Duration AndAlso CDbl(clone.GetValue(origin)) = -8 AndAlso clone.Sample(125) = half.Sample(125),
            "page bridge WPF clone and freeze retain the VB animation origin and sampled curve")
        check(Double.IsNaN(New PclPageCardYAnimation(Double.NaN).Sample(30)) AndAlso Double.IsNaN(New PclPageCardYAnimation(Double.PositiveInfinity).Sample(350)) AndAlso animation.Sample(Double.NegativeInfinity) = -16 AndAlso animation.Sample(Double.PositiveInfinity) = 0,
            "page bridge preserves existing nonfinite origin and clamped-time sample behavior")
        Dim flags = BindingFlags.Public Or BindingFlags.NonPublic Or BindingFlags.Instance
        Dim cardType = GetType(PclPageCardMotion), surfaceType = GetType(PclSurfaceMotion)
        check(cardType.Assembly Is app AndAlso surfaceType.Assembly Is app AndAlso cardType.IsSealed AndAlso surfaceType.IsSealed AndAlso
            cardType.GetProperty("Transition", flags).PropertyType Is GetType(ClockGroup) AndAlso
            surfaceType.GetProperty("Transition", flags).PropertyType Is GetType(ClockGroup) AndAlso
            cardType.GetProperty("PauseForChecks", flags).CanWrite AndAlso surfaceType.GetProperty("PauseForChecks", flags).CanWrite,
            "page bridge retains both original VB choreography types and their clock and pause contracts")
        Dim hostName As String = Nothing, unitsName As String = Nothing
        Try
            Dim unused As New PclPageCardMotion(Nothing, Function(root) Enumerable.Empty(Of FrameworkElement)())
        Catch ex As ArgumentNullException
            hostName = ex.ParamName
        End Try
        Try
            Dim unused As New PclPageCardMotion(New Border(), Nothing)
        Catch ex As ArgumentNullException
            unitsName = ex.ParamName
        End Try
        check(hostName = "host" AndAlso unitsName = "units",
            "page bridge retains the existing constructor guard parameter names and order")
    End Sub

    Private Shared Sub VerifyPageBridgeCards(check As Action(Of Boolean, String), observations As JObject)
        Dim outgoing As New Canvas(), incoming As New Canvas(), panel As New Grid()
        panel.Children.Add(outgoing) : panel.Children.Add(incoming)
        Dim oldCard As New Border With {.Width = 400, .Height = 50, .Background = Brushes.LightBlue, .Opacity = 0.7}
        Dim first As New Border With {.Width = 400, .Height = 50, .Background = Brushes.White, .Tag = 0.8}
        Dim second As New Border With {.Width = 400, .Height = 50, .Background = Brushes.Azure, .Opacity = 0.9, .IsHitTestVisible = False}
        Canvas.SetLeft(oldCard, 10) : Canvas.SetTop(oldCard, 10) : outgoing.Children.Add(oldCard)
        Canvas.SetLeft(first, 10) : Canvas.SetTop(first, 10) : incoming.Children.Add(first)
        Canvas.SetLeft(second, 10) : Canvas.SetTop(second, 80) : incoming.Children.Add(second)
        Dim original As New ScaleTransform(0.98, 0.98)
        first.RenderTransform = original
        Dim binding As New Binding("Tag") With {.RelativeSource = New RelativeSource(RelativeSourceMode.Self), .Mode = BindingMode.OneWay}
        BindingOperations.SetBinding(first, OpacityProperty, binding)
        Dim host As New Border With {.Width = 460, .Height = 230, .Child = panel}, layout = ControlLayout(host)
        Dim motion As New PclPageCardMotion(host, Function(root) If(root Is incoming, New FrameworkElement() {first, second}, New FrameworkElement() {oldCard})) With {.PauseForChecks = True}
        Dim settled = 0
        AddHandler motion.Settled, Sub() settled += 1
        Using source = PreviewSurface.Attach(host, 460, 230)
            Try
                layout() : motion.Change(outgoing, incoming)
                Dim entry = motion.Transition, late = FeedbackCompleted(entry), plan = DirectCast(entry.Timeline, ParallelTimeline)
                check(motion.ItemCount = 3 AndAlso plan.Children.Count = 6 AndAlso plan.Duration.TimeSpan.TotalMilliseconds = 475 AndAlso
                    motion.DelayFor(oldCard).Value = 0 AndAlso motion.DelayFor(first).Value = 100 AndAlso motion.DelayFor(second).Value = 125,
                    "page bridge card plan preserves child order total duration and separate entry exit staggering")
                Dim exitAlpha = PageBridgeFrames(plan, 0), enterAlpha = PageBridgeFrames(plan, 2), secondAlpha = PageBridgeFrames(plan, 4)
                check(exitAlpha.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso exitAlpha.KeyFrames.Count = 2 AndAlso
                    TypeOf exitAlpha.KeyFrames(0) Is DiscreteDoubleKeyFrame AndAlso exitAlpha.KeyFrames(0).Value = 0.7 AndAlso
                    exitAlpha.KeyFrames(1).Value = 0 AndAlso DirectCast(exitAlpha.KeyFrames(1), EasingDoubleKeyFrame).EasingFunction Is Nothing,
                    "page bridge zero-delay exit alpha keeps its explicit 70-ms duration and linear final frame")
                Dim ease = TryCast(DirectCast(enterAlpha.KeyFrames(2), EasingDoubleKeyFrame).EasingFunction, PowerEase)
                check(enterAlpha.BeginTime.Value = TimeSpan.Zero AndAlso enterAlpha.Duration.TimeSpan.TotalMilliseconds = 200 AndAlso enterAlpha.KeyFrames.Count = 3 AndAlso
                    TypeOf enterAlpha.KeyFrames(0) Is DiscreteDoubleKeyFrame AndAlso enterAlpha.KeyFrames(0).KeyTime.TimeSpan = TimeSpan.Zero AndAlso
                    TypeOf enterAlpha.KeyFrames(1) Is DiscreteDoubleKeyFrame AndAlso enterAlpha.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 100 AndAlso
                    enterAlpha.KeyFrames(2).Value = 0.8 AndAlso ease.Power = 2 AndAlso ease.EasingMode = EasingMode.EaseOut,
                    "page bridge incoming alpha stays active from zero and holds a discrete sampled origin until its weak fluent fade")
                check(secondAlpha.Duration.TimeSpan.TotalMilliseconds = 225 AndAlso secondAlpha.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 125 AndAlso
                    secondAlpha.KeyFrames(2).KeyTime.TimeSpan.TotalMilliseconds = 225 AndAlso secondAlpha.KeyFrames(2).Value = 0.9,
                    "page bridge second incoming card retains its 25-ms stagger without changing the fade duration")
                Dim entryY = DirectCast(plan.Children(3), PclPageCardYAnimation), exitY = DirectCast(plan.Children(1), DoubleAnimation)
                check(entryY.GetType().Assembly Is GetType(MainWindow).Assembly AndAlso entryY.BeginTime.Value.TotalMilliseconds = 100 AndAlso entryY.Duration.TimeSpan.TotalMilliseconds = 350 AndAlso
                    exitY.From.Value = 0 AndAlso exitY.To.Value = -6 AndAlso exitY.BeginTime.Value = TimeSpan.Zero AndAlso exitY.Duration.TimeSpan.TotalMilliseconds = 70,
                    "page bridge card Y tracks retain the original VB animation and the linear six-DIP exit")
                check(first.Opacity = 0 AndAlso PageBridgeOffset(first).Y = -16 AndAlso Not first.IsHitTestVisible AndAlso
                    DirectCast(first.RenderTransform, TransformGroup).Children(0) Is original,
                    "page bridge primes the transparent entry frame while preserving the exact caller transform")
                FeedbackSeek(entry, 90, layout)
                check(first.Opacity = 0 AndAlso second.Opacity = 0 AndAlso PageBridgeOffset(first).Y = -16 AndAlso Not first.IsHitTestVisible,
                    "page bridge card alpha and input remain hidden throughout the pre-entry delay")
                FeedbackSeek(entry, 140, layout)
                check(PageBridgeNear(first.Opacity, 0.512) AndAlso PageBridgeNear(PageBridgeOffset(first).Y, PageBridgeReferenceY(40)) AndAlso PageBridgeNear(second.Opacity, 0.24975),
                    "page bridge actual entry samples match independent weak-fluent alpha and parallel rebound values")
                FeedbackSeek(entry, 200, layout)
                check(first.IsHitTestVisible AndAlso Not second.IsHitTestVisible AndAlso motion.Transition Is entry AndAlso
                    first.ReadLocalValue(IsHitTestVisibleProperty) Is DependencyProperty.UnsetValue,
                    "page bridge releases only the first completed alpha gate while the rebound remains owned")
                FeedbackSeek(entry, 140, layout)
                Dim alpha = first.Opacity, y = PageBridgeOffset(first).Y, decorated = first.RenderTransform
                motion.Change(incoming, outgoing)
                Dim returning = motion.Transition
                check(returning IsNot entry AndAlso first.RenderTransform Is decorated AndAlso PageBridgeNear(first.Opacity, alpha) AndAlso PageBridgeNear(PageBridgeOffset(first).Y, y),
                    "page bridge rapid card reversal samples displayed alpha and Y without resetting its decoration")
                late(entry, EventArgs.Empty)
                check(motion.Transition Is returning AndAlso first.RenderTransform Is decorated AndAlso Not first.IsHitTestVisible AndAlso settled = 0,
                    "page bridge delivered old card completion cannot settle the accepted reverse plan")
                first.Tag = 0.6
                motion.Finish() : layout()
                check(motion.Transition Is Nothing AndAlso motion.ItemCount = 0 AndAlso first.RenderTransform Is original AndAlso
                    BindingOperations.GetBindingBase(first, OpacityProperty) Is binding AndAlso first.Opacity = 0.6 AndAlso
                    first.ReadLocalValue(IsHitTestVisibleProperty) Is DependencyProperty.UnsetValue AndAlso Not second.IsHitTestVisible AndAlso settled = 1,
                    "page bridge finalization restores the current original opacity binding and exact caller input values")
                observations("CardChildren") = plan.Children.Count
                observations("CardDurationMs") = plan.Duration.TimeSpan.TotalMilliseconds
            Finally
                motion.Finish()
            End Try
        End Using
    End Sub

    Private Shared Sub VerifyPageBridgeSurface(check As Action(Of Boolean, String), observations As JObject)
        Dim oldBody As New StackPanel(), nextBody As New StackPanel()
        Dim oldCard As New Border With {.Height = 75, .Background = Brushes.LightBlue, .Opacity = 0.6}
        Dim nextCard As New Border With {.Height = 75, .Background = Brushes.White, .Opacity = 0.6}
        oldBody.Children.Add(oldCard) : oldBody.Children.Add(New Border With {.Height = 500})
        nextBody.Children.Add(nextCard) : nextBody.Children.Add(New Border With {.Height = 500})
        Dim oldView As New PclScrollViewer With {.Content = oldBody, .VerticalScrollBarVisibility = ScrollBarVisibility.Visible, .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, .Opacity = 0.7}
        Dim nextView As New PclScrollViewer With {.Content = nextBody, .VerticalScrollBarVisibility = ScrollBarVisibility.Visible, .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, .Opacity = 0.7, .Visibility = Visibility.Collapsed}
        Dim panel As New Grid(), ghost As New Canvas With {.IsHitTestVisible = False}
        panel.Children.Add(oldView) : panel.Children.Add(nextView) : panel.Children.Add(ghost)
        Dim host As New Border With {.Width = 460, .Height = 230, .Child = panel}, layout = ControlLayout(host)
        Dim motion As New PclSurfaceMotion(panel, ghost, Function() True, Function(root) If(root Is oldView, New FrameworkElement() {oldCard}, New FrameworkElement() {nextCard})) With {.PauseForChecks = True}
        Dim revealed = 0
        AddHandler motion.ContentRevealed, Sub() revealed += 1
        Using source = PreviewSurface.Attach(host, 460, 230)
            Try
                layout() : motion.Change(Nothing, oldView, Sub() Return)
                Dim firstClock = motion.Transition, firstPlan = DirectCast(firstClock.Timeline, ParallelTimeline)
                Dim reveal = PageBridgeFrames(firstPlan, 0), fade = PageBridgeFrames(firstPlan, 1)
                check(reveal.Duration = Duration.Automatic AndAlso reveal.KeyFrames.Count = 2 AndAlso
                    reveal.KeyFrames.Cast(Of DoubleKeyFrame)().All(Function(frame) TypeOf frame Is DiscreteDoubleKeyFrame AndAlso frame.KeyTime.TimeSpan = TimeSpan.Zero) AndAlso reveal.KeyFrames(1).Value = 0.7,
                    "page bridge first surface reveal retains automatic duration and both discrete zero-time frames")
                check(fade.Duration = Duration.Automatic AndAlso fade.KeyFrames.Count = 3 AndAlso fade.KeyFrames(0).KeyTime.TimeSpan = TimeSpan.Zero AndAlso
                    fade.KeyFrames(1).KeyTime.TimeSpan = TimeSpan.Zero AndAlso fade.KeyFrames(2).KeyTime.TimeSpan.TotalMilliseconds = 100 AndAlso
                    DirectCast(DirectCast(fade.KeyFrames(2), EasingDoubleKeyFrame).EasingFunction, PowerEase).Power = 2,
                    "page bridge first surface alpha retains automatic duration duplicate origin and 100-ms weak fluent endpoint")
                Dim bar = oldView.VerticalBar, barEntry = firstPlan.Children.OfType(Of DoubleAnimation)().Single()
                check(bar IsNot Nothing AndAlso barEntry.From.Value = 10 AndAlso barEntry.To.Value = 0 AndAlso barEntry.BeginTime.Value = TimeSpan.Zero AndAlso
                    barEntry.Duration.TimeSpan.TotalMilliseconds = 350 AndAlso DirectCast(barEntry.EasingFunction, PowerEase).Power = 3 AndAlso
                    DirectCast(barEntry.EasingFunction, PowerEase).EasingMode = EasingMode.EaseOut,
                    "page bridge first scrollbar entry retains the source 350-ms cubic plan")
                check(oldView.Opacity = 0.7 AndAlso oldCard.Opacity = 0 AndAlso PageBridgeOffset(oldCard).Y = -16 AndAlso
                    firstPlan.Children(2).GetType() Is GetType(PclPageYAnimation),
                    "page bridge surface primes card alpha and its original VB Y animation before the first render")
                FeedbackSeek(firstClock, 50, layout)
                check(PageBridgeNear(oldCard.Opacity, 0.45) AndAlso PageBridgeNear(PageBridgeOffset(oldCard).Y, PageBridgeReferenceY(50)) AndAlso
                    PageBridgeNear(PageBridgeOffset(bar).X, 10 * Math.Pow(1 - 50.0 / 350, 3)) AndAlso panel.IsHitTestVisible,
                    "page bridge live surface card and scrollbar samples preserve independent upstream curves")
                motion.Finish() : layout()
                check(oldCard.Opacity = 0.6 AndAlso oldView.Opacity = 0.7 AndAlso Not TypeOf oldCard.RenderTransform Is TransformGroup AndAlso
                    Not TypeOf bar.RenderTransform Is TransformGroup AndAlso panel.IsHitTestVisible,
                    "page bridge surface finish removes its owned transforms and restores the original surface alpha")
                revealed = 0
                motion.Change(oldView, nextView, Sub()
                                                     oldView.Visibility = Visibility.Collapsed
                                                     nextView.Visibility = Visibility.Visible
                                                 End Sub)
                Dim clock = motion.Transition, plan = DirectCast(clock.Timeline, ParallelTimeline)
                Dim fades = plan.Children.OfType(Of DoubleAnimation)().Where(Function(item) item.From.HasValue AndAlso item.From.Value = 1 AndAlso item.To.Value = 0).ToArray()
                Dim exitBar = plan.Children.OfType(Of DoubleAnimation)().Single(Function(item) item.From.Value = 0 AndAlso item.To.Value = 10)
                Dim exitY = plan.Children.OfType(Of DoubleAnimation)().Where(Function(item) item.From.Value = 0 AndAlso item.To.Value = -6).ToArray()
                check(ghost.Children.Count > 0 AndAlso fades.Length >= 2 AndAlso fades.All(Function(item) item.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso item.EasingFunction Is Nothing) AndAlso
                    exitY.Length = fades.Length AndAlso exitY.All(Function(item) item.Duration.TimeSpan.TotalMilliseconds = 70) AndAlso
                    exitBar.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso DirectCast(exitBar.EasingFunction, PowerEase).Power = 3 AndAlso DirectCast(exitBar.EasingFunction, PowerEase).EasingMode = EasingMode.EaseIn,
                    "page bridge snapshot exit keeps linear 70-ms alpha Y and the 90-ms cubic scrollbar departure")
                Dim keyframes = plan.Children.OfType(Of DoubleAnimationUsingKeyFrames)().ToArray()
                Dim delayedReveal = keyframes(0), delayedFade = keyframes(1)
                check(delayedReveal.Duration = Duration.Automatic AndAlso delayedReveal.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 140 AndAlso
                    delayedFade.Duration = Duration.Automatic AndAlso delayedFade.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 140 AndAlso delayedFade.KeyFrames(2).KeyTime.TimeSpan.TotalMilliseconds = 240 AndAlso
                    Not panel.IsHitTestVisible AndAlso nextView.Opacity = 0 AndAlso nextCard.Opacity = 0,
                    "page bridge snapshot handoff preserves the 140-ms chrome reveal and delayed card fade without an initial flash")
                FeedbackSeek(clock, 110, layout)
                check(ghost.Children.Count = 0 AndAlso nextView.Opacity = 0 AndAlso Not panel.IsHitTestVisible AndAlso revealed = 0,
                    "page bridge clears the frozen exit at 110 ms while retaining the incoming input and chrome gate")
                FeedbackSeek(clock, 190, layout)
                Dim incomingBar = nextView.VerticalBar
                check(nextView.Opacity = 0.7 AndAlso PageBridgeNear(nextCard.Opacity, 0.45) AndAlso panel.IsHitTestVisible AndAlso revealed = 1 AndAlso
                    PageBridgeNear(PageBridgeOffset(incomingBar).X, 10 * Math.Pow(1 - 50.0 / 350, 3)),
                    "page bridge snapshot handoff reveals once and retains 50-ms card and scrollbar samples after its delay")
                observations("SurfaceEntryChildren") = firstPlan.Children.Count
                observations("SurfaceExitChildren") = plan.Children.Count
            Finally
                motion.Finish()
            End Try
        End Using
    End Sub
End Class
