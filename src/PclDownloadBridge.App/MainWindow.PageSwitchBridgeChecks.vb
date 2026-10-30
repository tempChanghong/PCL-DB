Imports System.IO
Imports System.Reflection
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyPageSwitchBridgeUi(directory As String, passed As List(Of String))
        ' Fixed PCL 5ef81e4 PageDownloadInstall.EnterSelectPage/ExitSelectPage;
        ' CE 915ac297 retains the 10/100-ms starts and 260-ms horizontal finish.
        ' Call the original VB API so this same file runs in the old production tree.
        Dim before = passed.Count, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        IO.Directory.CreateDirectory(directory)
        Try
            VerifySwitchBridgeContract(check)
            VerifySwitchBridgeClocks(check, observations)
            VerifySwitchBridgeCards(check, observations)
            If passed.Count - before <> 32 Then Throw New InvalidOperationException("Page switch bridge must report exactly 32 checks.")
        Finally
            observations("Count") = passed.Count - before
            observations("Checks") = New JArray(passed.Skip(before))
            observations("WindowsConfigurationChanged") = False
            File.WriteAllText(Path.Combine(directory, "page-switch-bridge.json"), observations.ToString())
        End Try
    End Sub

    Private Shared Function SwitchBridgeNear(actual As Double, expected As Double) As Boolean
        Return Math.Abs(actual - expected) < 0.000001
    End Function
    Private Shared Function SwitchBridgeX(target As FrameworkElement) As TranslateTransform
        Return DirectCast(target.RenderTransform, TranslateTransform)
    End Function
    Private Shared Function SwitchBridgeTrack(plan As ParallelTimeline, index As Integer) As DoubleAnimation
        Return DirectCast(plan.Children(index), DoubleAnimation)
    End Function
    Private Shared Function SwitchBridgePause(motion As PclPageSwitch) As ClockGroup
        Dim clock = motion.Transition
        If clock Is Nothing Then Throw New InvalidOperationException("Expected an accepted horizontal page clock.")
        PauseSelectionClock(clock)
        Return clock
    End Function

    Private Shared Sub VerifySwitchBridgeContract(check As Action(Of Boolean, String))
        Dim kind = GetType(PclPageSwitch), flags = BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic
        check(kind.Assembly Is GetType(MainWindow).Assembly AndAlso kind.IsSealed AndAlso Not kind.IsPublic,
            "switch bridge retains the original sealed VB page controller identity")
        Dim method = kind.GetMethod("TransitionPlan", BindingFlags.Static Or BindingFlags.Public Or BindingFlags.NonPublic)
        check(method.IsAssembly AndAlso method.ReturnType Is GetType(ParallelTimeline) AndAlso
            method.GetParameters().Select(Function(item) item.ParameterType).SequenceEqual({GetType(Double), GetType(Double), GetType(Double), GetType(Double), GetType(Boolean)}),
            "switch bridge retains the original Friend shared five-argument plan signature")
        Dim constructor = kind.GetConstructors(flags).Single(), parameters = constructor.GetParameters()
        check(constructor.IsAssembly AndAlso parameters.Length = 6 AndAlso parameters.Skip(3).All(Function(item) item.IsOptional AndAlso item.DefaultValue Is Nothing) AndAlso
            kind.GetProperty("Transition", flags).PropertyType Is GetType(ClockGroup) AndAlso kind.GetProperty("CardMotion", flags).PropertyType Is GetType(PclPageCardMotion),
            "switch bridge retains its optional constructor arguments and original clock and card properties")
        Dim plan = PclPageSwitch.TransitionPlan(0.7, 13.5, 0.2, 40, True), returning = PclPageSwitch.TransitionPlan(0.7, 13.5, 0.2, -50, False)
        check(plan.Children.Count = 4 AndAlso plan.Duration.TimeSpan.TotalMilliseconds = 260 AndAlso plan.FillBehavior = FillBehavior.HoldEnd,
            "switch bridge retains four ordered tracks and the 260-ms parent duration")
        Dim alpha = SwitchBridgeTrack(plan, 0), x = SwitchBridgeTrack(plan, 1), incoming = SwitchBridgeTrack(plan, 2), arrival = SwitchBridgeTrack(plan, 3)
        check(alpha.From.Value = 0.7 AndAlso alpha.To.Value = 0 AndAlso alpha.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso alpha.BeginTime.Value.TotalMilliseconds = 10 AndAlso alpha.EasingFunction Is Nothing,
            "switch bridge outgoing opacity remains linear for 70 ms after its ten-ms delay")
        check(x.From.Value = 13.5 AndAlso x.To.Value = -50 AndAlso SwitchBridgeTrack(returning, 1).To.Value = 50 AndAlso
            x.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso x.BeginTime.Value.TotalMilliseconds = 10 AndAlso x.EasingFunction Is Nothing,
            "switch bridge outgoing translation retains direction and its linear 90-ms track")
        Dim ease = DirectCast(arrival.EasingFunction, PowerEase)
        check(incoming.From.Value = 0.2 AndAlso incoming.To.Value = 1 AndAlso incoming.BeginTime.Value.TotalMilliseconds = 100 AndAlso incoming.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso
            arrival.From.Value = 40 AndAlso arrival.To.Value = 0 AndAlso arrival.BeginTime.Value.TotalMilliseconds = 100 AndAlso arrival.Duration.TimeSpan.TotalMilliseconds = 160 AndAlso
            ease.Power = 5 AndAlso ease.EasingMode = EasingMode.EaseOut,
            "switch bridge incoming alpha and fifth-power translation retain the original hundred-ms handoff")
        Dim clone = DirectCast(plan.Clone(), ParallelTimeline)
        clone.Freeze()
        check(clone.IsFrozen AndAlso clone.GetType() Is GetType(ParallelTimeline) AndAlso clone.Children.Count = 4 AndAlso clone.Duration = plan.Duration AndAlso
            SwitchBridgeTrack(clone, 1).From.Value = x.From.Value AndAlso SwitchBridgeTrack(clone, 3).BeginTime.Value = arrival.BeginTime.Value AndAlso
            DirectCast(SwitchBridgeTrack(clone, 3).EasingFunction, PowerEase).Power = 5,
            "switch bridge WPF cloning and freezing retain the ordered original horizontal plan")
    End Sub

    Private Shared Sub VerifySwitchBridgeClocks(check As Action(Of Boolean, String), observations As JObject)
        Dim versions As New Canvas(), install As New Canvas(), panel As New Grid()
        panel.Children.Add(versions) : panel.Children.Add(install)
        Dim host As New Border With {.Width = 520, .Height = 260, .Child = panel}, layout = ControlLayout(host)
        Dim motion As New PclPageSwitch(host, versions, install, Function() True)
        Dim settled = 0, exactSender = True
        AddHandler motion.Settled, Sub(sender, args)
                                      settled += 1
                                      exactSender = exactSender AndAlso sender Is motion AndAlso args Is EventArgs.Empty
                                  End Sub
        Using source = PreviewSurface.Attach(host, 520, 260)
            Try
                layout()
                check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Collapsed AndAlso
                    versions.Opacity = 1 AndAlso install.Opacity = 0 AndAlso SwitchBridgeX(versions).X = 0 AndAlso SwitchBridgeX(install).X = 40 AndAlso host.IsHitTestVisible,
                    "switch bridge starts on the version list with the original forty-DIP install offset")
                motion.SelectInstall(True, True)
                Dim entry = SwitchBridgePause(motion), late = FeedbackCompleted(entry)
                check(versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Visible AndAlso Not host.IsHitTestVisible AndAlso
                    Not versions.IsHitTestVisible AndAlso Not install.IsHitTestVisible AndAlso Not CBool(versions.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso
                    CBool(install.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso entry.Children.Count = 4 AndAlso settled = 0,
                    "switch bridge accepts installation immediately while retaining both animated pages and the root input gate")
                FeedbackSeek(entry, 9, layout)
                check(versions.Opacity = 1 AndAlso install.Opacity = 0 AndAlso SwitchBridgeX(versions).X = 0 AndAlso SwitchBridgeX(install).X = 40 AndAlso versions.Visibility = Visibility.Visible,
                    "switch bridge retains the exact sampled bases during the ten-ms outgoing delay")
                FeedbackSeek(entry, 55, layout)
                check(SwitchBridgeNear(versions.Opacity, 1 - 45.0 / 70) AndAlso SwitchBridgeNear(SwitchBridgeX(versions).X, -25) AndAlso
                    install.Opacity = 0 AndAlso SwitchBridgeX(install).X = 40,
                    "switch bridge real outgoing alpha and translation retain their independent linear speeds")
                FeedbackSeek(entry, 100, layout)
                check(versions.Visibility = Visibility.Collapsed AndAlso install.Visibility = Visibility.Visible AndAlso install.Opacity = 0 AndAlso SwitchBridgeX(install).X = 40 AndAlso Not host.IsHitTestVisible,
                    "switch bridge hides only the outgoing version list at the original hundred-ms handoff")
                FeedbackSeek(entry, 180, layout)
                check(install.Opacity = 1 AndAlso SwitchBridgeNear(SwitchBridgeX(install).X, 1.25) AndAlso Not host.IsHitTestVisible,
                    "switch bridge real incoming translation uses fifth-power easing after alpha has completed")
                Dim versionAlpha = versions.Opacity, versionX = SwitchBridgeX(versions).X, installAlpha = install.Opacity, installX = SwitchBridgeX(install).X
                motion.SelectInstall(False, True)
                Dim returning = SwitchBridgePause(motion)
                check(returning IsNot entry AndAlso SwitchBridgeNear(versions.Opacity, versionAlpha) AndAlso SwitchBridgeNear(SwitchBridgeX(versions).X, versionX) AndAlso
                    SwitchBridgeNear(install.Opacity, installAlpha) AndAlso SwitchBridgeNear(SwitchBridgeX(install).X, installX),
                    "switch bridge rapid return starts from all four displayed values instead of neutral page values")
                late.Invoke(entry, EventArgs.Empty)
                check(motion.Transition Is returning AndAlso motion.HasMotion AndAlso Not host.IsHitTestVisible AndAlso versions.Visibility = Visibility.Visible AndAlso settled = 0,
                    "switch bridge a delivered old completion cannot retire the accepted reverse clock or release its input gate")
                FeedbackSeek(returning, 100, layout)
                check(versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Visible AndAlso Not host.IsHitTestVisible AndAlso motion.Transition Is returning,
                    "switch bridge a return tick does not apply the entering-only version-list collapse")
                FeedbackSeek(returning, 261, layout)
                check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso host.IsHitTestVisible AndAlso versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Collapsed AndAlso
                    versions.Opacity = 1 AndAlso install.Opacity = 0 AndAlso SwitchBridgeX(versions).X = 0 AndAlso SwitchBridgeX(install).X = 50 AndAlso
                    settled = 1 AndAlso exactSender AndAlso Not versions.HasAnimatedProperties AndAlso Not install.HasAnimatedProperties,
                    "switch bridge natural return settles synchronously once through the original VB event sender and fifty-DIP resting offset")
                Dim vx = SwitchBridgeX(versions), ix = SwitchBridgeX(install)
                For Each target In {versions, install}
                    target.BeginAnimation(OpacityProperty, New DoubleAnimation(0.4, 0.6, TimeSpan.FromSeconds(5)))
                Next
                vx.BeginAnimation(TranslateTransform.XProperty, New DoubleAnimation(2, 8, TimeSpan.FromSeconds(5)))
                ix.BeginAnimation(TranslateTransform.XProperty, New DoubleAnimation(2, 8, TimeSpan.FromSeconds(5)))
                Dim externalChannelsActive = versions.HasAnimatedProperties AndAlso install.HasAnimatedProperties AndAlso vx.HasAnimatedProperties AndAlso ix.HasAnimatedProperties
                GetType(PclPageSwitch).GetMethod("StopTransition", BindingFlags.Instance Or BindingFlags.NonPublic).Invoke(motion, Nothing)
                check(externalChannelsActive AndAlso motion.Transition Is Nothing AndAlso Not versions.HasAnimatedProperties AndAlso Not install.HasAnimatedProperties AndAlso Not vx.HasAnimatedProperties AndAlso Not ix.HasAnimatedProperties,
                    "switch bridge stopping without an owned clock still detaches all four private page channels")
                Dim previous = settled
                motion.SelectInstall(False, True)
                check(motion.Transition Is Nothing AndAlso settled = previous AndAlso versions.Opacity = 1 AndAlso ix.X = 50,
                    "switch bridge selecting the accepted version view remains a no-op without a second notification")
                motion.SelectInstall(True, False)
                check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso install.Visibility = Visibility.Visible AndAlso versions.Visibility = Visibility.Collapsed AndAlso
                    install.Opacity = 1 AndAlso versions.Opacity = 0 AndAlso host.IsHitTestVisible AndAlso settled = previous + 1,
                    "switch bridge an explicitly unanimated selection settles immediately through the original notification path")
                motion.SelectInstall(False, True) : returning = SwitchBridgePause(motion)
                host.Visibility = Visibility.Collapsed : layout()
                check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Collapsed AndAlso host.IsHitTestVisible,
                    "switch bridge hiding its host retires the horizontal clock and settles the accepted return")
                host.Visibility = Visibility.Visible : layout()
                motion.SelectInstall(True, True) : entry = SwitchBridgePause(motion)
                host.Width = 530 : layout()
                check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso install.Visibility = Visibility.Visible AndAlso versions.Visibility = Visibility.Collapsed AndAlso host.IsHitTestVisible,
                    "switch bridge resizing the host settles installation without retaining an old horizontal clock")
                motion.SelectInstall(False, True) : returning = SwitchBridgePause(motion)
                Dim unloadedCompletion = FeedbackCompleted(returning)
                source.RootVisual = Nothing : FlushControlLayout()
                Dim retiredOnUnload = motion.Transition Is Nothing AndAlso Not motion.HasMotion
                unloadedCompletion.Invoke(returning, EventArgs.Empty)
                source.RootVisual = host : layout()
                check(retiredOnUnload AndAlso motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso versions.IsVisible AndAlso versions.Opacity = 1 AndAlso install.Visibility = Visibility.Collapsed AndAlso host.IsHitTestVisible,
                    "switch bridge unload and reattachment cannot resurrect a retired return completion")
                Dim acceptReentry = True, emptyClockAtEvent = False
                Dim reenter As EventHandler = Sub(sender, args)
                                                 If Not acceptReentry Then Return
                                                 acceptReentry = False
                                                 emptyClockAtEvent = motion.Transition Is Nothing
                                                 motion.SelectInstall(False, True)
                                                 motion.Transition?.Controller.Pause()
                                             End Sub
                motion.SelectInstall(True, True) : entry = SwitchBridgePause(motion)
                AddHandler motion.Settled, reenter
                Try
                    FeedbackSeek(entry, 261, layout)
                    returning = SwitchBridgePause(motion)
                    check(emptyClockAtEvent AndAlso returning IsNot entry AndAlso versions.Visibility = Visibility.Visible AndAlso Not host.IsHitTestVisible AndAlso exactSender,
                        "switch bridge a synchronous settled subscriber can accept a new reverse clock after old ownership is cleared")
                    previous = settled : motion.Finish()
                    check(motion.Transition Is Nothing AndAlso Not motion.HasMotion AndAlso host.IsHitTestVisible AndAlso versions.Visibility = Visibility.Visible AndAlso install.Visibility = Visibility.Collapsed AndAlso settled = previous + 1,
                        "switch bridge explicit finish of a reentrant return restores the accepted state and sends one notification")
                Finally
                    RemoveHandler motion.Settled, reenter
                End Try
                observations("HorizontalChildren") = DirectCast(entry.Timeline, ParallelTimeline).Children.Count
                observations("HorizontalDurationMs") = entry.Timeline.Duration.TimeSpan.TotalMilliseconds
            Finally
                motion.Finish()
            End Try
        End Using
    End Sub

    Private Shared Sub VerifySwitchBridgeCards(check As Action(Of Boolean, String), observations As JObject)
        Dim versions As New Canvas(), install As New Canvas(), panel As New Grid()
        panel.Children.Add(versions) : panel.Children.Add(install)
        Dim oldCard As New Border With {.Width = 370, .Height = 50, .Background = Brushes.LightBlue}
        Dim first As New Border With {.Width = 370, .Height = 50, .Background = Brushes.White, .Opacity = 0.8}
        Dim second As New Border With {.Width = 370, .Height = 50, .Background = Brushes.Azure}
        Canvas.SetLeft(oldCard, 10) : Canvas.SetTop(oldCard, 10) : versions.Children.Add(oldCard)
        Canvas.SetLeft(first, 10) : Canvas.SetTop(first, 10) : install.Children.Add(first)
        Canvas.SetLeft(second, 10) : Canvas.SetTop(second, 80) : install.Children.Add(second)
        Dim body As New StackPanel()
        For index = 1 To 20
            body.Children.Add(New TextBlock With {.Height = 20, .Text = "独立高度 " & index})
        Next
        Dim independent As New PclCard With {.Title = "独立", .Width = 90, .HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Top,
            .Margin = New Thickness(430, 10, 0, 0), .Body = body, .BodyMargin = New Thickness(0, 40, 0, 0), .Collapsible = True, .Collapsed = True}
        panel.Children.Add(independent)
        Dim host As New Border With {.Width = 520, .Height = 300, .ClipToBounds = True, .Child = panel}, layout = ControlLayout(host)
        Dim motion As New PclPageSwitch(host, versions, install, Function() True,
            Function(root) If(root Is versions, New FrameworkElement() {oldCard}, New FrameworkElement() {first, second}), Function() True)
        motion.CardMotion.PauseForChecks = True
        Dim settled = 0, exactSender = True
        AddHandler motion.Settled, Sub(sender, args)
                                      settled += 1
                                      exactSender = exactSender AndAlso sender Is motion
                                  End Sub
        Using source = PreviewSurface.Attach(host, 520, 300)
            Try
                layout() : independent.Collapsed = False
                Dim height = independent.HeightTransition
                If height Is Nothing Then Throw New InvalidOperationException("Expected the independent card height clock.")
                FeedbackSeek(height, 75, layout)
                Dim displayedHeight = independent.Height
                motion.SelectInstall(True, True)
                Dim horizontal = SwitchBridgePause(motion), cards = motion.CardMotion.Transition
                PauseSelectionClock(cards)
                check(motion.Transition Is horizontal AndAlso motion.CardMotion.Transition Is cards AndAlso motion.HasMotion AndAlso cards IsNot horizontal AndAlso settled = 0,
                    "switch bridge horizontal and staggered card clocks retain independent concurrent ownership")
                FeedbackSeek(cards, 90, layout) : FeedbackSeek(horizontal, 261, layout)
                check(motion.Transition Is Nothing AndAlso motion.CardMotion.Transition Is cards AndAlso motion.HasMotion AndAlso host.IsHitTestVisible AndAlso install.Visibility = Visibility.Visible AndAlso
                    Not first.IsHitTestVisible AndAlso first.Opacity = 0 AndAlso settled = 0,
                    "switch bridge 260-ms page completion releases root input while delayed cards retain their local gates and rebound")
                check(independent.HeightTransition Is height AndAlso SwitchBridgeNear(independent.Height, displayedHeight) AndAlso independent.Body Is body AndAlso independent.ClipToBounds,
                    "switch bridge page handoff preserves the independent accepted height clock body and sampled height")
                FeedbackSeek(cards, 300, layout)
                check(motion.Transition Is Nothing AndAlso motion.CardMotion.Transition Is cards AndAlso host.IsHitTestVisible AndAlso first.IsHitTestVisible AndAlso second.IsHitTestVisible AndAlso first.Opacity = 0.8 AndAlso settled = 0,
                    "switch bridge card fade completion releases local input without ending the remaining rebound or notifying twice")
                FeedbackSeek(cards, 476, layout)
                check(Not motion.HasMotion AndAlso motion.CardMotion.Transition Is Nothing AndAlso settled = 1 AndAlso exactSender AndAlso
                    independent.HeightTransition Is height AndAlso SwitchBridgeNear(independent.Height, displayedHeight),
                    "switch bridge the last card completion sends one original-controller notification and preserves unrelated height motion")
                motion.SelectInstall(False, True) : horizontal = SwitchBridgePause(motion)
                cards = motion.CardMotion.Transition : PauseSelectionClock(cards)
                Dim previous = settled
                motion.Finish()
                check(Not motion.HasMotion AndAlso motion.Transition Is Nothing AndAlso motion.CardMotion.Transition Is Nothing AndAlso host.IsHitTestVisible AndAlso versions.Visibility = Visibility.Visible AndAlso
                    settled = previous + 1 AndAlso exactSender AndAlso independent.HeightTransition Is height AndAlso independent.Body Is body,
                    "switch bridge explicit finish suppresses nested card notifications and settles both owned clocks once")
                observations("ParallelCardDurationMs") = cards.Timeline.Duration.TimeSpan.TotalMilliseconds
            Finally
                motion.Finish()
                independent.HeightTransition?.Controller.SkipToFill()
            End Try
        End Using
    End Sub
End Class
