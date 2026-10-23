Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyLoadingPresentation(directory As String, check As Action(Of Boolean, String))
        Dim content As New StackPanel(), ghost As New Canvas(), panel As New Grid()
        panel.Children.Add(content) : panel.Children.Add(ghost)
        Dim host As New Border With {.Width = 420, .Height = 180, .Child = panel}
        Dim layout = ControlLayout(host), enabled = True, now As Double = 0, shown = 0
        Dim motion As New PclSurfaceMotion(panel, ghost, Function() True, Function(surface) PclSurfaceMotion.Units(surface)) With {.PauseForChecks = True}
        Dim presentation As New PclLoadingPresentation(panel, Function() enabled, Function() motion) With {.NowForChecks = Function() now}
        Dim prepare As Action = Sub() motion.Change(content, content, Sub() content.Children.Clear(), contentRefresh:=True)
        Dim show As Action = Sub()
                                 shown += 1
                                 motion.Change(content, content, Sub() content.Children.Add(New PclCard With {.ShowHeader = False, .Body = New PclLoadingView("正在获取版本列表")} ), contentRefresh:=True)
                             End Sub
        Try
            Using source = PreviewSurface.Attach(host, 420, 180)
                layout()
                check(PclLoadingPresentation.WaitMilliseconds = 400 AndAlso PclLoadingPresentation.MinimumMilliseconds = 400, "PCL loader wait and post-entry minimum stay each preserve the source 400 ms duration")
                Dim owner As New Object()
                presentation.Start(owner, CancellationToken.None, prepare, show) : layout()
                check(presentation.Phase = PclLoadingPresentation.Phases.Waiting AndAlso shown = 0 AndAlso content.Children.Count = 0, "a timed request begins with empty content and no prematurely visible loading view")
                now = 399 : presentation.Pulse()
                check(shown = 0 AndAlso presentation.Phase = PclLoadingPresentation.Phases.Waiting, "a request at 399 ms cannot reveal the PCL loading view")
                now = 400 : presentation.Pulse() : layout()
                check(shown = 1 AndAlso presentation.Phase = PclLoadingPresentation.Phases.Entering AndAlso motion.Transition IsNot Nothing, "the 400 ms boundary starts the existing PCL entry controller exactly once")
                Dim pending = presentation.CompleteAsync(owner)
                now = 1000 : presentation.Pulse()
                check(Not pending.IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Entering, "network completion cannot consume minimum stay while the loader entry animation is still running")
                motion.Finish() : layout()
                check(presentation.Phase = PclLoadingPresentation.Phases.MinimumStay AndAlso Not pending.IsCompleted, "minimum stay begins only when the actual entry owner settles")
                now = 1399 : presentation.Pulse()
                check(Not pending.IsCompleted, "399 ms after entry is still inside the source forced-stay interval")
                now = 1400 : presentation.Pulse()
                check(pending.IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not presentation.TimerArmed, "the 400 ms post-entry boundary releases completed data and removes its timer")

                owner = New Object() : now = 2000 : presentation.Start(owner, CancellationToken.None, prepare, show)
                pending = presentation.CompleteAsync(owner) : now = 3000 : presentation.Pulse()
                check(pending.IsCompleted AndAlso shown = 1 AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle, "a fast completion bypasses loading and its obsolete wait callback cannot flash a spinner later")
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                now += 400 : presentation.Pulse() : motion.Finish() : now += 400 : presentation.Pulse()
                check(presentation.Phase = PclLoadingPresentation.Phases.Loading AndAlso Not presentation.TimerArmed, "an unfinished request continues loading without a repeating timer after minimum stay")
                check(presentation.CompleteAsync(owner).IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle, "a later response can enter content immediately after minimum stay has already elapsed")

                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                now += 400 : presentation.Pulse() : pending = presentation.CompleteAsync(owner)
                Dim retired = motion.Transition, replacement As New Object(), count = shown
                presentation.Start(replacement, CancellationToken.None, prepare, show) : presentation.Cancel(owner)
                retired.Controller.SkipToFill() : layout()
                check(pending.IsCompleted AndAlso shown = count AndAlso presentation.Phase = PclLoadingPresentation.Phases.Waiting, "request replacement releases the retired waiter while stale clocks and cancellation cannot advance the new request")
                presentation.Cancel(replacement) : now += 1000 : presentation.Pulse()
                check(presentation.Phase = PclLoadingPresentation.Phases.Idle AndAlso shown = count, "canceling the accepted owner removes its future loading callback")

                Using cancellation As New CancellationTokenSource()
                    owner = New Object() : presentation.Start(owner, cancellation.Token, prepare, show)
                    now += 400 : presentation.Pulse() : pending = presentation.CompleteAsync(owner)
                    cancellation.Cancel() : FlushControlLayout()
                    check(pending.IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not presentation.TimerArmed, "token cancellation releases a pending entry waiter and unregisters its timer")
                    motion.Finish() : presentation.Pulse()
                    check(presentation.Phase = PclLoadingPresentation.Phases.Idle, "a canceled loader's eventual entry completion cannot arm another minimum-stay timer")
                End Using
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                panel.Visibility = Visibility.Collapsed : layout() : count = shown : now += 1000 : presentation.Pulse()
                check(presentation.Phase = PclLoadingPresentation.Phases.Hidden AndAlso shown = count AndAlso Not presentation.TimerArmed, "leaving during initial wait suspends loader presentation and prevents hidden spinner creation")
                check(presentation.CompleteAsync(owner).IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle, "accepted hidden results bypass presentation delay and remain immediately cacheable")
                panel.Visibility = Visibility.Visible : layout()
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                panel.Visibility = Visibility.Collapsed : layout() : now += 1000 : panel.Visibility = Visibility.Visible : layout()
                check(presentation.Phase = PclLoadingPresentation.Phases.Waiting AndAlso shown = count, "returning to an unfinished request restarts only its visual wait after the parent visibility transaction")
                now += 399 : presentation.Pulse()
                check(shown = count, "returning to a pending page preserves a fresh full initial wait")
                now += 1 : presentation.Pulse() : motion.Finish() : pending = presentation.CompleteAsync(owner)
                panel.Visibility = Visibility.Collapsed : layout()
                check(pending.IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not presentation.TimerArmed, "hiding during minimum stay releases ready data without delaying the new page")
                owner = New Object() : count = shown : presentation.Start(owner, CancellationToken.None, prepare, show)
                check(presentation.Phase = PclLoadingPresentation.Phases.Hidden AndAlso shown = count, "a request started while hidden cannot create a loading view before page entry")
                panel.Visibility = Visibility.Visible : layout()
                check(presentation.Phase = PclLoadingPresentation.Phases.Waiting AndAlso shown = count, "a hidden-start request receives its initial wait when its page actually becomes visible")
                presentation.CompleteAsync(owner).GetAwaiter().GetResult()
                enabled = False : owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                check(presentation.Phase = PclLoadingPresentation.Phases.Loading AndAlso presentation.CompleteAsync(owner).IsCompleted, "disabled page motion adds no artificial wait or forced loader stay")
                enabled = True : owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                enabled = False : count = shown : presentation.Pulse()
                check(shown = count + 1 AndAlso presentation.Phase = PclLoadingPresentation.Phases.Loading AndAlso Not presentation.TimerArmed, "disabling timing during wait switches directly to the immediate loading path")
                presentation.Cancel(owner) : enabled = True
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                now += 400 : presentation.Pulse() : count = shown : enabled = False : motion.Finish()
                check(presentation.Phase = PclLoadingPresentation.Phases.Loading AndAlso shown = count AndAlso Not presentation.TimerArmed, "disabling timing during loader entry bypasses minimum stay without recreating the visible loader")
                presentation.Cancel(owner) : enabled = True
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                now += 400 : presentation.Pulse() : motion.Finish() : count = shown : enabled = False : presentation.Pulse()
                check(presentation.Phase = PclLoadingPresentation.Phases.Loading AndAlso shown = count AndAlso Not presentation.TimerArmed, "disabling timing during minimum stay preserves the current loader instead of restarting its animation")
                presentation.Cancel(owner) : enabled = True

                ' Exercise real dispatcher timers as well as deterministic boundaries.
                presentation.NowForChecks = Nothing : owner = New Object()
                Dim elapsed = System.Diagnostics.Stopwatch.StartNew()
                presentation.Start(owner, CancellationToken.None, prepare, show)
                If Not PumpMotionUntil(Function() presentation.Phase <> PclLoadingPresentation.Phases.Waiting, 1500) Then Throw New TimeoutException("Real loader wait did not finish.")
                check(elapsed.Elapsed.TotalMilliseconds >= 400 AndAlso presentation.Phase = PclLoadingPresentation.Phases.Entering, "the real WPF dispatcher timer respects the monotonic initial wait")
                pending = presentation.CompleteAsync(owner) : elapsed.Restart() : motion.Finish()
                If Not PumpMotionUntil(Function() pending.IsCompleted, 1500) Then Throw New TimeoutException("Real loader minimum stay did not finish.")
                check(elapsed.Elapsed.TotalMilliseconds >= 400 AndAlso Not presentation.TimerArmed, "the real WPF dispatcher timer respects post-entry minimum stay and stops after completion")
                presentation.NowForChecks = Function() now
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                now += 400 : presentation.Pulse() : pending = presentation.CompleteAsync(owner)
                host.Child = Nothing : FlushControlLayout()
                check(pending.IsCompleted AndAlso presentation.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not presentation.TimerArmed, "unloading the host removes entry subscriptions, timers and pending waiters")
                owner = New Object() : presentation.Start(owner, CancellationToken.None, prepare, show)
                host.Child = panel : layout()
                check(presentation.Phase = PclLoadingPresentation.Phases.Waiting, "a request started before host loading resumes only after the real Loaded event")
                presentation.Cancel(owner)
            End Using
        Finally
            presentation.Reset() : motion.Finish()
        End Try
    End Sub
End Class
