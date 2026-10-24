Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyVanillaCardMotion(directory As String, check As Action(Of Boolean, String))
        Dim count = 0
        Dim observations As New List(Of Object)
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label)
                                                      count += 1
                                                  End Sub
        VerifyVanillaCardDecoration(directory, verify, observations)
        VerifyVanillaCardNavigation(directory, verify, observations)
        IO.File.WriteAllText(IO.Path.Combine(directory, "vanilla-card-motion-observations.json"),
            JsonConvert.SerializeObject(New With {.Checks = count, .Observations = observations}, Formatting.Indented))
    End Sub

    ' MyPageRight (5ef81e4): two parallel incremental Y tracks, 5 DIP over
    ' 250 ms fluent plus 11 DIP over 350 ms back, from an initial -16 DIP.
    Private Shared Function VanillaCardReferenceY(milliseconds As Double) As Double
        Dim fluent = Math.Min(1, Math.Max(0, milliseconds / 250))
        Dim rebound = Math.Min(1, Math.Max(0, milliseconds / 350))
        Return -16 + 5 * (1 - Math.Pow(1 - fluent, 3)) +
            11 * (1 - Math.Pow(1 - rebound, 1.5) * Math.Cos(1.5 * Math.PI * rebound))
    End Function

    Private Shared Function VanillaCardOffset(target As FrameworkElement) As TranslateTransform
        Dim group = TryCast(target.RenderTransform, TransformGroup)
        Return If(group Is Nothing, Nothing, TryCast(group.Children.LastOrDefault(), TranslateTransform))
    End Function

    Private Shared Sub VerifyVanillaCardDecoration(directory As String, check As Action(Of Boolean, String), observations As List(Of Object))
        Dim host As New Border With {.Width = 600, .Height = 400, .ClipToBounds = True}
        Dim panel As New Grid()
        host.Child = panel
        Dim outgoing As New Canvas(), incoming As New Canvas()
        panel.Children.Add(outgoing) : panel.Children.Add(incoming)
        Dim oldCard As New Border With {.Width = 550, .Height = 65, .Background = Brushes.LightSteelBlue}
        Canvas.SetLeft(oldCard, 20) : Canvas.SetTop(oldCard, 10) : outgoing.Children.Add(oldCard)
        Dim oldSecond As New Border With {.Width = 550, .Height = 65, .Background = Brushes.LightBlue}
        Canvas.SetLeft(oldSecond, 20) : Canvas.SetTop(oldSecond, 90) : outgoing.Children.Add(oldSecond)
        Dim first As New Border With {.Width = 550, .Height = 65, .Background = Brushes.White, .Opacity = 0.8}
        Dim second As New Border With {.Width = 550, .Height = 65, .Background = Brushes.Azure, .IsHitTestVisible = False}
        Dim inheritedGate As New Canvas With {.IsHitTestVisible = False}
        Dim third As New Border With {.Width = 550, .Height = 65, .Background = Brushes.Lavender, .IsHitTestVisible = True}
        Dim folded As New Border With {.Width = 550, .Height = 65, .Visibility = Visibility.Collapsed}
        Dim offscreen As New Border With {.Width = 550, .Height = 65}
        Dim original As New ScaleTransform(0.98, 0.98)
        first.RenderTransform = original
        Canvas.SetLeft(first, 20) : Canvas.SetTop(first, 10)
        Canvas.SetLeft(second, 20) : Canvas.SetTop(second, 90)
        Canvas.SetLeft(third, 20) : Canvas.SetTop(third, 170)
        Canvas.SetTop(offscreen, 600)
        incoming.Children.Add(first) : incoming.Children.Add(folded) : incoming.Children.Add(offscreen)
        incoming.Children.Add(second) : incoming.Children.Add(inheritedGate) : inheritedGate.Children.Add(third)
        Dim incomingUnits As FrameworkElement() = {first, folded, offscreen, second, third}
        Dim motion As New PclPageCardMotion(host,
            Function(root) If(root Is incoming, incomingUnits, New FrameworkElement() {oldCard, oldSecond})) With {.PauseForChecks = True}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Dim settled = 0
        AddHandler motion.Settled, Sub() settled += 1
        Using source = PreviewSurface.Attach(host, 600, 400)
            layout() : motion.Change(outgoing, incoming)
            Dim entry = motion.Transition
            check(entry IsNot Nothing AndAlso motion.ItemCount = 5 AndAlso motion.IncomingTargets.SequenceEqual({first, second, third}) AndAlso
                motion.OutgoingTargets.SequenceEqual({oldCard, oldSecond}),
                "vanilla card choreography includes only distinct viewport-visible units and excludes folded or offscreen entries")
            check(motion.IncomingTargets.Select(Function(target) motion.DelayFor(target).Value).SequenceEqual({100, 125, 150}) AndAlso
                motion.DelayFor(oldCard).Value = 0 AndAlso motion.DelayFor(oldSecond).Value = 15 AndAlso entry.Timeline.Duration.TimeSpan.TotalMilliseconds = 500,
                "vanilla card delays follow PCL's 25ms entry and 15ms exit schedule without gaps for invisible units")
            Dim originalRetained = TypeOf first.RenderTransform Is TransformGroup AndAlso DirectCast(first.RenderTransform, TransformGroup).Children(0) Is original
            Dim initialAlpha = first.Opacity, initialY = VanillaCardOffset(first)?.Y, initialHit = first.IsHitTestVisible
            IO.File.WriteAllText(IO.Path.Combine(directory, "vanilla-card-initial-state.json"), JsonConvert.SerializeObject(New With {
                .OriginalTransformRetained = originalRetained, .TransformType = first.RenderTransform.GetType().FullName,
                .Alpha = initialAlpha, .Y = initialY, .Hit = initialHit, .LocalAlpha = first.ReadLocalValue(OpacityProperty),
                .LocalHit = first.ReadLocalValue(IsHitTestVisibleProperty), .ClockPaused = entry.IsPaused, .ClockTime = entry.CurrentTime,
                .ClockState = entry.CurrentState.ToString(), .OpacityValueSource = DependencyPropertyHelper.GetValueSource(first, OpacityProperty).ToString(),
                .Children = entry.Children.Select(Function(child) New With {.Paused = child.IsPaused, .State = child.CurrentState.ToString(), .Time = child.CurrentTime}).ToArray()
            }, Formatting.Indented))
            check(TypeOf first.RenderTransform Is TransformGroup AndAlso DirectCast(first.RenderTransform, TransformGroup).Children(0) Is original AndAlso
                first.Opacity = 0 AndAlso VanillaCardOffset(first).Y = -16 AndAlso Not first.IsHitTestVisible,
                "vanilla card entry appends one translation while retaining the caller transform and gating its transparent frame")
            seek(entry, 90)
            IO.File.WriteAllText(IO.Path.Combine(directory, "vanilla-card-delay-state.json"), JsonConvert.SerializeObject(New With {
                .FirstAlpha = first.Opacity, .SecondAlpha = second.Opacity, .ThirdAlpha = third.Opacity,
                .FirstY = VanillaCardOffset(first).Y, .SecondY = VanillaCardOffset(second).Y,
                .FirstLocalAlpha = first.ReadLocalValue(OpacityProperty), .ClockPaused = entry.IsPaused, .ClockTime = entry.CurrentTime,
                .Children = entry.Children.Select(Function(child) New With {.Paused = child.IsPaused, .State = child.CurrentState.ToString(), .Time = child.CurrentTime}).ToArray()
            }, Formatting.Indented))
            check(first.Opacity = 0 AndAlso second.Opacity = 0 AndAlso VanillaCardOffset(first).Y = -16,
                "vanilla cards remain at the source entry origin before the hundred-millisecond page handoff")
            seek(entry, 140)
            check(Math.Abs(first.Opacity - 0.512) < 0.00001 AndAlso Math.Abs(VanillaCardOffset(first).Y - VanillaCardReferenceY(40)) < 0.00001 AndAlso
                Math.Abs(second.Opacity - 0.2775) < 0.00001 AndAlso third.Opacity = 0,
                "loaded vanilla card alpha and rebound match the independent PCL reference with per-card staggering")
            observations.Add(New With {.Scenario = "helper-entry-140", .FirstAlpha = first.Opacity, .FirstY = VanillaCardOffset(first).Y,
                .SecondAlpha = second.Opacity, .ThirdAlpha = third.Opacity})
            SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-card-helper-140.png"))
            seek(entry, 251)
            check(first.IsHitTestVisible AndAlso Not second.IsHitTestVisible AndAlso Not third.IsHitTestVisible AndAlso
                CBool(third.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso first.ReadLocalValue(IsHitTestVisibleProperty) Is DependencyProperty.UnsetValue,
                "completed card alpha releases only owned local gates while preserving explicit and inherited caller input restrictions")
            inheritedGate.IsHitTestVisible = True
            check(third.IsHitTestVisible AndAlso motion.Transition Is entry,
                "removing an ancestor gate reveals the retained local true value while the card rebound continues")
            inheritedGate.IsHitTestVisible = False
            seek(entry, 501)
            check(Not motion.HasMotion AndAlso motion.ItemCount = 0 AndAlso first.RenderTransform Is original AndAlso first.Opacity = 0.8 AndAlso
                Not first.HasAnimatedProperties AndAlso Not original.HasAnimatedProperties AndAlso settled = 1,
                "natural card completion removes opacity clocks and appended transforms and restores exact original references")
            check(first.ReadLocalValue(IsHitTestVisibleProperty) Is DependencyProperty.UnsetValue AndAlso
                Not CBool(second.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso CBool(third.ReadLocalValue(IsHitTestVisibleProperty)),
                "card cleanup restores unset false and inherited-true local input values independently")

            motion.Change(outgoing, incoming) : entry = motion.Transition : seek(entry, 140)
            Dim alpha = first.Opacity, y = VanillaCardOffset(first).Y, decorated = first.RenderTransform
            motion.Change(incoming, outgoing)
            Dim returning = motion.Transition
            check(returning IsNot entry AndAlso first.RenderTransform Is decorated AndAlso Math.Abs(first.Opacity - alpha) < 0.00001 AndAlso
                Math.Abs(VanillaCardOffset(first).Y - y) < 0.00001,
                "card reversal samples the visible alpha and translation instead of jumping to their neutral values")
            seek(returning, 35)
            check(Math.Abs(first.Opacity - alpha / 2) < 0.00001 AndAlso Math.Abs(VanillaCardOffset(first).Y - (y - 3)) < 0.00001,
                "returning vanilla cards fade and rise another six DIP over PCL's seventy-millisecond exit")
            entry.Controller.SkipToFill() : layout()
            check(motion.Transition Is returning AndAlso first.RenderTransform Is decorated AndAlso Not first.IsHitTestVisible,
                "obsolete entry completion cannot release the current card exit or its input gate")
            motion.Change(outgoing, incoming)
            Dim reopened = motion.Transition
            returning.Controller.SkipToFill() : layout()
            check(motion.Transition Is reopened AndAlso first.RenderTransform Is decorated,
                "obsolete card exit completion cannot clean up a rapidly reopened decoration")
            Dim removed = second.RenderTransform
            incoming.Children.Remove(second) : incomingUnits = {first, third}
            motion.Change(outgoing, incoming) : layout()
            check(second.RenderTransform IsNot removed AndAlso Not second.HasAnimatedProperties AndAlso
                Not motion.IncomingTargets.Contains(second) AndAlso second.IsHitTestVisible = False,
                "a replaced component card is released when it leaves the new visible-unit set")
            motion.Finish()
            Dim finished = settled
            motion.Finish()
            check(Not motion.HasMotion AndAlso first.RenderTransform Is original AndAlso first.Opacity = 0.8 AndAlso settled = finished,
                "explicit card finalization is idempotent and does not emit a duplicate settled notification")

            motion.Change(outgoing, incoming) : entry = motion.Transition : seek(entry, 140)
            host.Visibility = Visibility.Collapsed : layout()
            check(Not motion.HasMotion AndAlso first.RenderTransform Is original AndAlso Not first.HasAnimatedProperties,
                "hiding the vanilla viewport releases every decorated card even during an unfinished fade")
            host.Visibility = Visibility.Visible : layout() : motion.Change(outgoing, incoming)
            entry = motion.Transition : seek(entry, 140)
            host.Width = 620 : layout()
            check(Not motion.HasMotion AndAlso first.RenderTransform Is original AndAlso motion.ItemCount = 0,
                "resizing the vanilla viewport settles local choreography without keeping stale card geometry")
            motion.Change(outgoing, incoming) : entry = motion.Transition : seek(entry, 140)
            source.RootVisual = Nothing : FlushControlLayout()
            check(Not motion.HasMotion AndAlso first.RenderTransform Is original AndAlso Not first.HasAnimatedProperties,
                "unloading the viewport releases card clocks and transforms before reattachment")
            entry.Controller.SkipToFill() : layout()
            source.RootVisual = host : layout()
            check(Not motion.HasMotion AndAlso first.IsVisible AndAlso first.Opacity = 0.8,
                "reattaching the vanilla viewport cannot resurrect an obsolete card completion")
            motion.PauseForChecks = False : motion.Change(outgoing, incoming)
            check(PumpMotionUntil(Function() Not motion.HasMotion, 1500) AndAlso first.RenderTransform Is original,
                "unpaused vanilla card choreography completes on the dispatcher without manual clock advancement")

            Dim body As New StackPanel()
            For index = 1 To 12
                body.Children.Add(New TextBlock With {.Text = "组件版本 " & index, .Height = 25})
            Next
            Dim summary As New PclComponentSummary()
            Dim independent As New PclCard With {.Width = 550, .Title = "独立展开动画", .Body = body, .HeaderContent = summary,
                .Collapsible = True, .Collapsed = True, .BodyMargin = New Thickness(0, 40, 0, 0)}
            Canvas.SetLeft(independent, 20) : Canvas.SetTop(independent, 10)
            incoming.Children.Add(independent) : incomingUnits = {independent} : layout()
            independent.Collapsed = False : summary.SetShown(False)
            Dim fold = independent.HeightTransition, header = summary.Transition
            PauseSelectionClock(header)
            fold.Controller.Pause()
            If Not PumpMotionUntil(Function() fold.IsPaused, 1000) Then Throw New InvalidOperationException("Independent fold clock did not acknowledge Pause.")
            layout()
            fold.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(75), TimeSeekOrigin.BeginTime) : layout()
            Dim height = independent.Height
            motion.PauseForChecks = True : motion.Change(outgoing, incoming)
            entry = motion.Transition : seek(entry, 140)
            check(independent.HeightTransition Is fold AndAlso summary.Transition Is header AndAlso independent.Body Is body AndAlso
                Math.Abs(independent.Height - height) < 0.01 AndAlso independent.ClipToBounds,
                "card entrance leaves a concurrent PclCard height clock component summary clock body and clipping untouched")
            motion.Finish()
            check(independent.HeightTransition Is fold AndAlso summary.Transition Is header AndAlso independent.Body Is body AndAlso
                Math.Abs(independent.Height - height) < 0.01,
                "local entrance cleanup cannot finish or replace independent fold and component-summary animations")
            fold.Controller.SkipToFill() : header.Controller.SkipToFill() : layout()

            Dim alphaSource As New Border With {.Tag = 0.65}, hitSource As New Border With {.Tag = True}
            Dim bound As New Border With {.Width = 550, .Height = 65, .Background = Brushes.LightCyan}
            Dim alphaBinding As New Binding("Tag") With {.Source = alphaSource, .Mode = BindingMode.OneWay}
            Dim hitBinding As New Binding("Tag") With {.Source = hitSource, .Mode = BindingMode.OneWay}
            BindingOperations.SetBinding(bound, OpacityProperty, alphaBinding)
            BindingOperations.SetBinding(bound, IsHitTestVisibleProperty, hitBinding)
            incoming.Children.Remove(independent) : incoming.Children.Add(bound)
            Canvas.SetLeft(bound, 20) : Canvas.SetTop(bound, 10) : incomingUnits = {bound} : layout()
            motion.Change(outgoing, incoming) : entry = motion.Transition : seek(entry, 90)
            check(BindingOperations.GetBindingBase(bound, OpacityProperty) Is alphaBinding AndAlso
                BindingOperations.GetBindingBase(bound, IsHitTestVisibleProperty) Is hitBinding AndAlso bound.Opacity = 0 AndAlso Not bound.IsHitTestVisible,
                "vanilla card delay retains caller opacity and input bindings while holding its transparent frame inert")
            alphaSource.Tag = 0.35 : hitSource.Tag = False : layout()
            Dim boundAlpha = bound.Opacity, boundY = VanillaCardOffset(bound).Y
            motion.Change(incoming, outgoing)
            returning = motion.Transition
            check(BindingOperations.GetBindingBase(bound, OpacityProperty) Is alphaBinding AndAlso
                BindingOperations.GetBindingBase(bound, IsHitTestVisibleProperty) Is hitBinding AndAlso bound.Opacity = boundAlpha AndAlso
                VanillaCardOffset(bound).Y = boundY,
                "rapid bound-card reversal retains both caller bindings and the currently displayed frame after source changes")
            motion.Finish()
            check(bound.Opacity = 0.35 AndAlso Not bound.IsHitTestVisible AndAlso Not bound.HasAnimatedProperties AndAlso
                BindingOperations.GetBindingBase(bound, OpacityProperty) Is alphaBinding AndAlso BindingOperations.GetBindingBase(bound, IsHitTestVisibleProperty) Is hitBinding,
                "bound-card finalization reevaluates the latest source opacity and false input value instead of its captured originals")
            motion.Change(outgoing, incoming) : entry = motion.Transition : seek(entry, 201)
            alphaSource.Tag = 0.45 : hitSource.Tag = True : layout() : motion.Finish()
            check(bound.Opacity = 0.45 AndAlso bound.IsHitTestVisible AndAlso BindingOperations.GetBindingBase(bound, OpacityProperty) Is alphaBinding AndAlso
                BindingOperations.GetBindingBase(bound, IsHitTestVisibleProperty) Is hitBinding,
                "source updates after local card input release remain live and survive final rebound cleanup")
        End Using
    End Sub

    Private Sub VerifyVanillaCardNavigation(directory As String, check As Action(Of Boolean, String), observations As List(Of Object))
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Dim context = System.Threading.SynchronizationContext.Current
        System.Threading.SynchronizationContext.SetSynchronizationContext(New System.Windows.Threading.DispatcherSynchronizationContext(owner.Dispatcher))
        Dim pending As New TaskCompletionSource(Of List(Of ComponentEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim loading As Task = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Try
            Using source = PreviewSurface.Attach(host, 850, 600)
                layout()
                owner._pageSwitch.CardMotion.PauseForChecks = True : owner._installActionMotion.PauseForChecks = True
                owner._surfaceMotion.PauseForChecks = True : owner._categoryMotion.PauseForChecks = True
                owner.SelectVersion(owner._versions.Single(Function(item) item.Id = "1.20.1")) : owner._pageSwitch.Finish()
                check(Not owner._pageSwitch.CardMotion.HasMotion AndAlso owner.VanillaCard.Opacity = 1,
                    "ordinary static preview renders the vanilla install cards fully without starting local choreography")
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish()
                For index = 0 To 24
                    owner._versions.Add(BrowserEntry("1.0-test-" & index, "release", "2012-01-01T00:00:00Z"))
                Next
                owner.UpdateVersions()
                Dim rows = owner.BrowserRows("正式版")
                For Each card In owner.VersionCards.Children.OfType(Of PclCard)()
                    card.HeightTransition?.Controller.SkipToFill()
                Next
                layout() : owner.VersionScroll.ScrollToVerticalOffset(220) : layout()
                Dim browserOffset = owner.VersionScroll.VerticalOffset, selectedRow = rows.Single(Function(row) row.Title = "1.20.1")
                Dim entry = DirectCast(selectedRow.Tag, VersionEntry)
                owner._motionPreview = True : owner.SelectVersion(entry)
                Dim page = owner._pageSwitch.Transition, cards = owner._pageSwitch.CardMotion.Transition
                Dim actionClock = owner._installActionMotion.Transition
                check(page IsNot Nothing AndAlso cards IsNot Nothing AndAlso owner._pageSwitch.HasMotion AndAlso
                    owner._pageSwitch.CardMotion.IncomingTargets.Contains(owner.VanillaCard),
                    "choosing a loaded vanilla version decorates visible install cards alongside the existing horizontal transition")
                seek(page, 140) : seek(cards, 140)
                If owner._installActionMotion.Transition IsNot Nothing Then seek(owner._installActionMotion.Transition, 140)
                SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-cards-entry-140.png"))
                observations.Add(New With {.Scenario = "install-entry-140", .Cards = owner._pageSwitch.CardMotion.ItemCount,
                    .VanillaDelay = owner._pageSwitch.CardMotion.DelayFor(owner.VanillaCard), .VanillaAlpha = owner.VanillaCard.Opacity,
                    .VanillaY = VanillaCardOffset(owner.VanillaCard).Y})
                owner.FocusDownloadPage()
                seek(page, 261) : seek(cards, 261)
                check(owner._pageSwitch.Transition Is Nothing AndAlso owner._pageSwitch.CardMotion.Transition Is cards AndAlso
                    owner._pageSwitch.HasMotion AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso owner._focusAfterPageSwitch,
                    "the 260ms horizontal finish releases root input but preserves the staggered card tail and deferred focus")
                check(owner._installActionMotion.Transition Is actionClock AndAlso owner.InstallActionHost.RenderTransform Is owner._installActionMotion.Scale AndAlso
                    Not owner.InstallActionHost.IsDescendantOf(owner.InstallPage),
                    "vanilla card tails retain the separate install-action rebound and its independent transform ownership")
                check(owner._pageSwitch.CardMotion.IncomingTargets.All(Function(target) owner._pageSwitch.CardMotion.DelayFor(target).Value + 100 > 261 OrElse
                    target.ReadLocalValue(IsHitTestVisibleProperty) Is DependencyProperty.UnsetValue OrElse CBool(target.ReadLocalValue(IsHitTestVisibleProperty))),
                    "alpha-complete vanilla cards release their own input gate while later cards continue entering")
                If owner._installActionMotion.Transition IsNot Nothing Then seek(owner._installActionMotion.Transition, 261)
                SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-cards-entry-261.png"))
                seek(cards, cards.Timeline.Duration.TimeSpan.TotalMilliseconds + 1)
                check(Not owner._pageSwitch.HasMotion AndAlso Not owner._focusAfterPageSwitch AndAlso owner.VanillaCard.Opacity = 1 AndAlso
                    VanillaCardOffset(owner.VanillaCard) Is Nothing AndAlso owner.DownloadViewHost.IsHitTestVisible,
                    "the final vanilla card tail releases deferred focus and all local visual state without a second page transition")

                For Each milliseconds In {90.0, 140.0, 200.0}
                    owner._motionPreview = False : owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish()
                    owner._motionPreview = True : owner.SelectVersion(entry)
                    page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                    seek(page, milliseconds) : seek(cards, milliseconds)
                    Dim alpha = owner.VanillaCard.Opacity, y = VanillaCardOffset(owner.VanillaCard).Y
                    owner.SetDownloadView(DownloadView.Versions)
                    Dim returningPage = owner._pageSwitch.Transition, returningCards = owner._pageSwitch.CardMotion.Transition
                    check(returningCards IsNot cards AndAlso Math.Abs(owner.VanillaCard.Opacity - alpha) < 0.00001 AndAlso
                        Math.Abs(VanillaCardOffset(owner.VanillaCard).Y - y) < 0.00001,
                        "vanilla navigation reversal samples the displayed card frame at " & milliseconds & "ms")
                    seek(returningPage, 35) : seek(returningCards, 35)
                    Dim reversedAlpha = owner.VanillaCard.Opacity, reversedY = VanillaCardOffset(owner.VanillaCard).Y
                    owner.SetDownloadView(DownloadView.Install)
                    Dim reopenedCards = owner._pageSwitch.CardMotion.Transition
                    check(Math.Abs(owner.VanillaCard.Opacity - reversedAlpha) < 0.00001 AndAlso Math.Abs(VanillaCardOffset(owner.VanillaCard).Y - reversedY) < 0.00001,
                        "immediate vanilla reentry preserves the interrupted card exit at " & milliseconds & "ms")
                    cards.Controller.SkipToFill() : returningCards.Controller.SkipToFill() : page.Controller.SkipToFill() : returningPage.Controller.SkipToFill() : layout()
                    check(owner._pageSwitch.CardMotion.Transition Is reopenedCards AndAlso owner._downloadView = DownloadView.Install AndAlso
                        Not owner.DownloadViewHost.IsHitTestVisible,
                        "stale card and horizontal completions cannot finish the newer vanilla transition at " & milliseconds & "ms")
                    observations.Add(New With {.Scenario = "rapid-roundtrip", .Milliseconds = milliseconds, .Alpha = alpha, .Y = y,
                        .ReverseAlpha = reversedAlpha, .ReverseY = reversedY})
                    If milliseconds = 140 Then
                        seek(owner._pageSwitch.Transition, 140) : seek(reopenedCards, 140)
                        If owner._installActionMotion.Transition IsNot Nothing Then seek(owner._installActionMotion.Transition, 140)
                        SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-cards-rapid-reentry-140.png"))
                    End If
                    owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                Next

                owner.SetDownloadView(DownloadView.Versions)
                page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                seek(page, 261) : seek(cards, 261)
                check(owner._pageSwitch.Transition Is Nothing AndAlso owner._pageSwitch.HasMotion AndAlso PageX(owner.InstallPage) = 50,
                    "natural return keeps PCL's hidden install-page base at fifty DIP while version cards finish their tail")
                seek(cards, cards.Timeline.Duration.TimeSpan.TotalMilliseconds + 1)
                check(Not owner._pageSwitch.HasMotion AndAlso PageX(owner.InstallPage) = 50,
                    "finishing the return card tail cannot restore the constructor's forty-DIP install offset")
                owner.SetDownloadView(DownloadView.Install)
                check(PageX(owner.InstallPage) = 50,
                    "the next vanilla entry starts from the retained fifty-DIP return offset")
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish()

                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "卡片动画保留名称"
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), fabric = owner._componentViews(ComponentKind.Fabric)
                Dim body = fabric.Card.Body
                owner._componentProvider = Function(policy, kind, id, token) pending.Task
                loading = owner.RefreshComponentListsAsync(ComponentKind.NeoForge)
                Dim generation = owner._componentGeneration, request = owner._componentRequests(ComponentKind.NeoForge)
                owner.SetDownloadView(DownloadView.Versions)
                page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                seek(page, 140) : seek(cards, 140)
                check(Math.Abs(owner.VersionScroll.VerticalOffset - browserOffset) < 0.5 AndAlso
                    DirectCast(selectedRow.Parent, StackPanel).Children.Contains(selectedRow) AndAlso owner._selectedEntry Is entry,
                    "returning to vanilla versions retains the original browser row and scroll position")
                If owner._installActionMotion.Transition IsNot Nothing Then seek(owner._installActionMotion.Transition, 140)
                SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-cards-return-140.png"))
                owner.SetDownloadView(DownloadView.Install) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish()
                owner.SetDownloadView(DownloadView.Install)
                page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                seek(page, 140) : seek(cards, 140)
                pending.TrySetResult(New List(Of ComponentEntry)())
                check(PumpMotionUntil(Function() loading.IsCompleted, 1500) AndAlso Not loading.IsFaulted AndAlso
                    owner._pageSwitch.CardMotion.Transition Is cards AndAlso owner._componentGeneration = generation AndAlso
                    owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso owner._componentViews(ComponentKind.Fabric) Is fabric AndAlso
                    fabric.Card.Body Is body AndAlso owner.InstanceName.Text = "卡片动画保留名称" AndAlso owner._selectedEntry Is entry,
                    "a late component response preserves the current card motion selected component body name and version ownership")
                owner.RefreshTitleNavigation() : owner.RefreshComponents() : layout()
                check(owner._pageSwitch.CardMotion.Transition Is cards AndAlso owner._pageSwitch.Transition Is page AndAlso
                    Not owner._componentRequests.ContainsKey(ComponentKind.NeoForge) AndAlso request.IsCancellationRequested = False,
                    "same-view component and chrome refresh do not restart vanilla card or horizontal clocks")

                Dim frame = PclExitSnapshot.Capture(owner.PageSurface, Enumerable.Empty(Of FrameworkElement)(), Nothing)
                Dim region = owner.DownloadViewHost.TransformToAncestor(owner.PageSurface).TransformBounds(New Rect(owner.DownloadViewHost.RenderSize))
                owner.ShowSettings(Nothing, Nothing) : layout()
                Dim frozen = owner.PageGhost.Children.OfType(Of Canvas)().SelectMany(Function(layer) layer.Children.OfType(Of Image)()).FirstOrDefault()
                check(frozen IsNot Nothing AndAlso SameDownloadFrame(frame, DirectCast(frozen.Source, Imaging.BitmapSource), region) AndAlso
                    Not owner._pageSwitch.HasMotion AndAlso owner._pageSwitch.CardMotion.ItemCount = 0,
                    "settings handoff snapshots the displayed staggered vanilla frame before releasing its card tail")
                owner._surfaceMotion.Finish() : owner.ShowDownload(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner.SetDownloadView(DownloadView.Install)
                page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                seek(page, 140) : seek(cards, 140)
                frame = PclExitSnapshot.Capture(owner.DownloadViewHost, Enumerable.Empty(Of FrameworkElement)(), Nothing)
                owner.ShowModpackDownloads(Nothing, Nothing) : layout()
                frozen = owner.DownloadGhost.Children.OfType(Of Canvas)().SelectMany(Function(layer) layer.Children.OfType(Of Image)()).FirstOrDefault()
                check(frozen IsNot Nothing AndAlso SameDownloadFrame(frame, DirectCast(frozen.Source, Imaging.BitmapSource), New Rect(owner.DownloadViewHost.RenderSize)) AndAlso
                    Not owner._pageSwitch.HasMotion AndAlso owner.InstallPage.Visibility = Visibility.Collapsed,
                    "modpack navigation captures the composited vanilla card frame before category ownership changes")
                owner._categoryMotion.Finish() : owner.ShowVanillaDownloads(Nothing, Nothing) : owner._categoryMotion.Finish() : layout()
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner.SetDownloadView(DownloadView.Install)
                page = owner._pageSwitch.Transition : cards = owner._pageSwitch.CardMotion.Transition
                seek(page, 140) : seek(cards, 140)
                owner.ResetTaskView("卡片尾段任务交接", True) : owner.SetDownloadView(DownloadView.Progress) : layout()
                check(Not owner._pageSwitch.HasMotion AndAlso owner._pageSwitch.CardMotion.ItemCount = 0 AndAlso owner.DownloadProgress.IsVisible AndAlso
                    owner._categoryMotion.Transition IsNot Nothing AndAlso Not owner.DownloadViewHost.IsHitTestVisible AndAlso owner._componentSelection.Item(ComponentKind.Fabric) Is selected,
                    "task progress replaces vanilla card motion with its own gated right-page entry without component mutation")
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                check(owner._downloadView = DownloadView.Install AndAlso Not owner._pageSwitch.HasMotion AndAlso owner._categoryMotion.Transition IsNot Nothing AndAlso owner.InstanceName.Text = "卡片动画保留名称",
                    "returning from progress restores the retained form through one right-page entry without a competing horizontal clock or name reset")

                Dim retired = fabric.Card
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner.SetDownloadView(DownloadView.Install)
                cards = owner._pageSwitch.CardMotion.Transition : seek(cards, 140)
                owner.InstallScroll.ScrollToVerticalOffset(80) : layout()
                owner.SelectVersion(owner._versions.Single(Function(item) item.Id = "1.21.1")) : layout()
                check(Not retired.HasAnimatedProperties AndAlso VanillaCardOffset(retired) Is Nothing AndAlso
                    Not owner._pageSwitch.CardMotion.IncomingTargets.Contains(retired) AndAlso owner.InstallScroll.VerticalOffset = 0 AndAlso
                    owner._selectedEntry.Id = "1.21.1",
                    "a new vanilla selection releases removed component decorations and resets install scroll before constructing its new entry")
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner.SetDownloadView(DownloadView.Install)
                cards = owner._pageSwitch.CardMotion.Transition : seek(cards, 140)
                host.Width = 900 : layout()
                check(Not owner._pageSwitch.HasMotion AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso
                    owner._downloadView = DownloadView.Install AndAlso owner.VanillaCard.Opacity = 1,
                    "resizing real vanilla navigation settles both page and card ownership to the selected form")
                owner._installActionMotion.Finish()
                SaveControlVisual(host, IO.Path.Combine(directory, "vanilla-cards-settled.png"))
            End Using
        Finally
            pending.TrySetResult(New List(Of ComponentEntry)())
            If loading IsNot Nothing Then PumpMotionUntil(Function() loading.IsCompleted, 1500)
            owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._motionPreview = False : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = content
            System.Threading.SynchronizationContext.SetSynchronizationContext(context)
        End Try
    End Sub
End Class
