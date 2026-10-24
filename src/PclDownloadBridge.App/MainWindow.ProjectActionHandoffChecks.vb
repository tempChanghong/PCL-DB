Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Windows
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Async Function VerifyProjectActionHandoffAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim action = _packProjectAction, wait = _packProjectActionWait, motion = _motionPreview
        Dim gate As TaskCompletionSource(Of Boolean) = Nothing, calls = 0, gates As New List(Of TaskCompletionSource(Of Boolean))
        Dim owner = _packProject, session = _packSearchSession, generation = _taskGeneration, cards = PackVersionCards.Children.Cast(Of Object)().ToArray()
        Dim start As Func(Of String, Task) =
            Function(kind)
                calls = 0 : gate = PackCheckGate() : gates.Add(gate)
                Dim held = gate
                _packProjectActionWait = Function() held.Task
                _packProjectAction = Sub(operation, value)
                                         calls += 1
                                         If operation = "link" OrElse calls = If(kind = "link", 2, 1) Then Throw New ExternalException("fixture contention")
                                     End Sub
                Return DispatchPackProjectActionAsync(kind)
            End Function
        Dim fresh As Func(Of Task) =
            Function()
                _packProjectAction = Sub(kind, value) calls += 1
                Return DispatchPackProjectActionAsync("copy")
            End Function
        Dim observations As New JArray
        Try
            For Each animated In {False, True}
                _motionPreview = animated
                Dim pending = start("copy")
                If calls <> 1 OrElse pending.IsCompleted Then Throw New InvalidOperationException("Project handoff fixture did not hold a real retry.")
                Dim prompt = PromptOnlinePackAsync("输入版本名称", "复制操作交接", _selectedFolder.Location, "Action-Handoff", False, CancellationToken.None)
                If animated Then PauseSelectionClock(_onlinePromptMotion)
                layout() : CancelOnlinePackName(Nothing, Nothing)
                _onlinePromptMotion?.Controller.SkipToFill()
                Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                Await prompt : layout()
                gate.TrySetResult(True) : Await pending : layout()
                observations.Add(New JObject From {{"modalAnimated", animated}, {"callsAfterReturn", calls}, {"feedback", PackProjectActionStatus.Text}})
                IO.File.WriteAllText(IO.Path.Combine(directory, "project-action-modal-handoff.json"), observations.ToString())
                check(calls = 1 AndAlso PackProjectActionStatus.Text = "", "a completed real modal round trip cannot resurrect a pending project clipboard retry: animated=" & animated)
                Await fresh() : layout()
                check(calls = 2 AndAlso PackProjectActionStatus.Text = "已成功复制！", "a fresh project copy succeeds after a real modal round trip: animated=" & animated)
            Next
            _motionPreview = False
            For Each boundary In {"intro-disabled", "ancestor-disabled", "intro-gate", "ancestor-gate"}
                Dim pending = start("copy")
                Select Case boundary
                    Case "intro-disabled" : PackProjectIntro.IsEnabled = False
                    Case "ancestor-disabled" : PageSurface.IsEnabled = False
                    Case "intro-gate" : PackProjectIntro.IsHitTestVisible = False
                    Case "ancestor-gate" : PageSurface.IsHitTestVisible = False
                End Select
                layout()
                PackProjectIntro.IsEnabled = True : PageSurface.IsEnabled = True : PackProjectIntro.IsHitTestVisible = True : PageSurface.IsHitTestVisible = True : layout()
                gate.TrySetResult(True) : Await pending : layout()
                observations.Add(New JObject From {{"boundary", boundary}, {"callsAfterReturn", calls}, {"feedback", PackProjectActionStatus.Text}})
                check(calls = 1 AndAlso PackProjectActionStatus.Text = "", "restoring project interaction cannot revive an old external action: " & boundary)
                Await fresh() : layout()
                check(calls = 2 AndAlso PackProjectActionStatus.Text = "已成功复制！", "restoring project interaction accepts a new external action: " & boundary)
            Next
            Dim fallback = start("link")
            If calls <> 2 OrElse fallback.IsCompleted Then Throw New InvalidOperationException("Browser fallback did not hold a real copy retry.")
            PageSurface.IsHitTestVisible = False : PageSurface.IsHitTestVisible = True : layout()
            gate.TrySetResult(True) : Await fallback : layout()
            observations.Add(New JObject From {{"boundary", "browser-fallback"}, {"callsAfterReturn", calls}, {"feedback", PackProjectActionStatus.Text}})
            check(calls = 2 AndAlso PackProjectActionStatus.Text = "", "browser copy fallback is retired with the same input owner before its delayed external write")
            Await fresh() : layout()
            check(calls = 3 AndAlso PackProjectActionStatus.Text = "已成功复制！", "a fresh action remains usable after the retired browser fallback")
            check(_packProject Is owner AndAlso _packSearchSession Is session AndAlso _taskGeneration = generation AndAlso PackVersionCards.Children.Cast(Of Object)().SequenceEqual(cards) AndAlso
                Not _choosingOnline AndAlso _onlinePrompt Is Nothing, "project action handoff preserves accepted project, search, version cards and installation ownership")
            IO.File.WriteAllText(IO.Path.Combine(directory, "project-action-handoff-states.json"), observations.ToString())
        Finally
            For Each held In gates : held.TrySetResult(True) : Next
            CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            PackProjectIntro.IsEnabled = True : PackProjectIntro.IsHitTestVisible = True : PageSurface.IsEnabled = True : PageSurface.IsHitTestVisible = True
            RetirePackProjectAction()
            _packProjectAction = action : _packProjectActionWait = wait : _motionPreview = motion
        End Try
    End Function
End Class
