Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyTaskPageMotion(directory As String, passed As List(Of String))
        Dim initialCount = passed.Count
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        owner.Content = Nothing : host.Child = content
        Dim layout = ControlLayout(host)
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, ms)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Dim gates As New List(Of TaskCompletionSource(Of InstallResult)), starts = 0
        Try
            Using surface = PreviewSurface.Attach(host, 850, 700)
                Dim root = Path.Combine(directory, "task-motion-fixture", ".minecraft")
                IO.Directory.CreateDirectory(root)
                owner.SelectManualFolder(root)
                owner.SelectVersion(owner._versions.Single(Function(value) value.Id = "1.20.1"))
                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "动画任务保留名称"
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : layout()
                owner.InstallScroll.ScrollToVerticalOffset(90) : layout()
                Dim scroll = owner.InstallScroll.VerticalOffset
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), body = owner._componentViews(ComponentKind.Fabric).Card.Body
                owner._installRunner = Function(entry, gameRoot, name, selection, isolation, defaultIsolation, token)
                                           starts += 1
                                           Dim gate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                           gates.Add(gate)
                                           Return gate.Task
                                       End Function
                owner._motionPreview = True : owner._categoryMotion.PauseForChecks = True
                owner.StartInstall(Nothing, Nothing) : layout()
                check(owner._busy AndAlso starts = 1 AndAlso owner._downloadView = DownloadView.Progress,
                    "task transition starts the actual install handler once and immediately owns the progress state")
                Dim entering = owner._categoryMotion.Transition
                check(entering IsNot Nothing,
                    "vanilla download activation starts a PCL right-page exit and entry clock instead of a visibility-only switch")
                check(owner._pageSwitch.Transition Is Nothing AndAlso owner.InstallActionHost.Visibility = Visibility.Collapsed AndAlso owner.DownloadSidebar.Visibility = Visibility.Collapsed,
                    "task entry retires horizontal and floating action ownership before revealing its full-width page")
                check(Not owner.DownloadViewHost.IsHitTestVisible AndAlso owner.DownloadGhost.Children.Count > 0 AndAlso owner.DownloadProgress.Opacity = 0,
                    "task entry freezes inert outgoing cards and gates the initially invisible progress page")
                Dim plan = DirectCast(entering.Timeline, ParallelTimeline)
                check(plan.Children.OfType(Of PclPageYAnimation)().Select(Function(value) value.BeginTime.Value.TotalMilliseconds).First() = 140,
                    "task cards use PCL's 110 plus 30 millisecond page handoff before their fluent-back entry")
                Dim generation = owner._taskGeneration, progress = owner.CreateTaskProgress()
                owner.StartInstall(Nothing, Nothing)
                check(starts = 1 AndAlso owner._taskGeneration = generation AndAlso owner._categoryMotion.Transition Is entering,
                    "duplicate activation during task entry neither restarts installation nor replays its page clock")
                seek(entering, 70)
                SaveControlVisual(host, Path.Combine(directory, "task-page-entry-exit-70.png"))
                PressControlKey(owner.TaskReturnButton, Input.Key.Enter)
                check(owner._downloadView = DownloadView.Progress AndAlso owner._categoryMotion.Transition Is entering,
                    "keyboard activation of the hidden incoming task return is blocked during exit")
                owner.UpdateProgress(generation, QueuedTaskValue("动画期间下载", 0.4, 4096))
                check(owner._categoryMotion.Transition Is entering AndAlso owner.TaskProgress.Value > 0 AndAlso owner.TaskTitle.Text.Contains("动画任务保留名称"),
                    "live task progress updates do not finish or restart its entry animation")
                gates(0).SetResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions", owner.InstanceName.Text), .FileCount = 3, .Reused = 2})
                check(PumpMotionUntil(Function() Not owner._busy) AndAlso owner._taskFinished AndAlso owner._taskOutcome = TaskOutcome.Succeeded AndAlso owner._categoryMotion.Transition Is entering,
                    "installation may finish during entry without blocking its runner or discarding the active page clock")
                Dim completedTitle = owner.TaskTitle.Text
                owner.UpdateProgress(generation, QueuedTaskValue("过期进度", 0.1))
                check(owner.TaskTitle.Text = completedTitle AndAlso owner.TaskProgress.Value = 1,
                    "a queued progress callback cannot regress a completed result during animation")
                seek(entering, 175)
                check(owner.DownloadViewHost.IsHitTestVisible AndAlso owner.DownloadGhost.Children.Count = 0 AndAlso owner.DownloadProgress.Opacity = 1,
                    "task input resumes at content reveal while the card rebound continues")
                SaveControlVisual(host, Path.Combine(directory, "task-page-entry-175.png"))
                owner._categoryMotion.Finish() : layout()
                check(owner.DownloadProgress.IsVisible AndAlso owner.TaskProgress.Value = 1 AndAlso owner.TaskResultActions.IsVisible,
                    "settled task entry preserves the completed result and its actions")
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                Dim returning = owner._categoryMotion.Transition
                check(returning IsNot Nothing AndAlso owner._downloadView = DownloadView.Install AndAlso owner._progressReturnView = DownloadView.Install,
                    "completed task return animates back to its actual retained installation form")
                check(owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso owner._componentViews(ComponentKind.Fabric).Card.Body Is body AndAlso
                    owner.InstanceName.Text = "动画任务保留名称" AndAlso owner.InstallScroll.VerticalOffset = scroll,
                    "task return retains component identity card body instance name and install scroll position")
                check(Not owner.DownloadViewHost.IsHitTestVisible AndAlso owner.InstallPage.Opacity = 0 AndAlso owner._installActionMotion.Transition Is Nothing,
                    "task return has one page owner and gates the incoming form without a competing action animation")
                entering.Controller.SkipToFill() : layout()
                check(owner._categoryMotion.Transition Is returning AndAlso Not owner.DownloadViewHost.IsHitTestVisible,
                    "the retired task-entry clock cannot finish a later return or open its input gate")
                plan = DirectCast(returning.Timeline, ParallelTimeline)
                Dim delays = plan.Children.OfType(Of PclPageYAnimation)().Select(Function(value) value.BeginTime.Value.TotalMilliseconds).ToArray()
                check(delays.Length > 2 AndAlso delays.Select(Function(value, index) value = 140 + 25 * index).All(Function(value) value),
                    "returning installation cards enter in visible order at PCL's 25 millisecond intervals")
                seek(returning, 70) : SaveControlVisual(host, Path.Combine(directory, "task-page-return-exit-70.png"))
                seek(returning, 175) : SaveControlVisual(host, Path.Combine(directory, "task-page-return-175.png"))
                Dim frame = PclExitSnapshot.Capture(owner.DownloadViewHost, Enumerable.Empty(Of FrameworkElement)(), Nothing)
                owner.StartInstall(Nothing, Nothing) : layout()
                Dim retry = owner._categoryMotion.Transition
                Dim frozen = owner.DownloadGhost.Children.OfType(Of Canvas)().SelectMany(Function(layer) layer.Children.OfType(Of Image)()).FirstOrDefault()
                check(starts = 2 AndAlso retry IsNot Nothing AndAlso retry IsNot returning AndAlso frozen IsNot Nothing AndAlso owner.DownloadGhost.Children.Count = 1,
                    "retry during return captures a single composited frame and starts only the next task")
                check(SameDownloadFrame(frame, DirectCast(frozen.Source, BitmapSource), New Rect(0, 0, frame.Width, frame.Height)),
                    "retry captures the displayed returning form before settling its old card transforms")
                returning.Controller.SkipToFill() : layout()
                check(owner._categoryMotion.Transition Is retry AndAlso owner._downloadView = DownloadView.Progress AndAlso Not owner.InstallPage.IsVisible,
                    "a stale return clock cannot reveal installation controls over the retried task")
                owner.CancelInstall(Nothing, Nothing) : gates(1).SetCanceled()
                check(PumpMotionUntil(Function() Not owner._busy) AndAlso owner._taskOutcome = TaskOutcome.Cancelled AndAlso owner._categoryMotion.Transition Is retry,
                    "cancellation completes independently while retry entry retains visual ownership")
                owner.UpdateProgress(generation, QueuedTaskValue("前一任务迟到进度"))
                check(owner.TaskTitle.Text.Contains("已取消") AndAlso owner._taskGeneration <> generation,
                    "callbacks from the previous task cannot replace a cancellation result")
                owner._categoryMotion.Finish()
                owner.NavigateTitleBack() : layout()
                check(owner._categoryMotion.Transition IsNot Nothing AndAlso owner._downloadView = DownloadView.Install,
                    "title-bar return follows the same animated task return path as the card button")
                seek(owner._categoryMotion.Transition, 70)
                owner._surfaceMotion.PauseForChecks = True
                owner.ShowSettings(Nothing, Nothing) : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.DownloadGhost.Children.Count = 0 AndAlso owner.SettingsPage.IsVisible AndAlso owner._surfaceMotion.Transition IsNot Nothing,
                    "settings handoff retires task-return clocks snapshots and local gates under one surface owner")
                owner._surfaceMotion.Finish() : owner.ShowDownload(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.ShowProgress(Nothing, Nothing) : layout()
                check(owner._categoryMotion.Transition IsNot Nothing AndAlso owner._taskOutcome = TaskOutcome.Cancelled,
                    "reopening an existing task animates without resetting its result")
                host.Width = 900 : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.DownloadGhost.Children.Count = 0 AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso owner.DownloadProgress.Opacity = 1,
                    "resizing task entry settles its final page and removes every owned overlay")
                owner._categoryMotion.PauseForChecks = False
                owner.ResumeInstallation(Nothing, Nothing)
                check(PumpMotionUntil(Function() owner._categoryMotion.Transition Is Nothing) AndAlso owner.InstallPage.IsVisible AndAlso owner.DownloadViewHost.IsHitTestVisible,
                    "unpaused task return completes on the real dispatcher and restores the retained form")
                owner._motionPreview = False : owner.ShowProgress(Nothing, Nothing) : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.DownloadProgress.Opacity = 1 AndAlso owner.DownloadViewHost.IsHitTestVisible,
                    "the no-animation preview branch still settles task entry immediately")
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.InstallPage.Opacity = 1 AndAlso owner.InstanceName.Text = "动画任务保留名称",
                    "the no-animation return preserves its form without a residual hidden state")
                owner._motionPreview = True : owner._categoryMotion.PauseForChecks = True
                owner.ShowProgress(Nothing, Nothing) : layout()
                Dim closing = owner._categoryMotion.Transition
                surface.RootVisual = Nothing : FlushControlLayout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.DownloadGhost.Children.Count = 0 AndAlso owner.DownloadViewHost.IsHitTestVisible,
                    "unloading task entry disposes its page clock and input gate")
                closing.Controller.SkipToFill() : layout()
                check(owner._downloadView = DownloadView.Progress AndAlso owner.DownloadProgress.Opacity = 1,
                    "a removed task clock cannot resurrect another page after unloading")
            End Using
        Finally
            For Each gate In gates
                gate.TrySetCanceled()
            Next
            PumpMotionUntil(Function() Not owner._busy)
            owner._motionPreview = False : owner._installRunner = Nothing
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
            owner.CancelComponentRequests() : owner._pclThemeMonitor.Dispose()
            host.Child = Nothing : owner.Content = content
            ApplyAppearance()
        End Try
        File.WriteAllText(Path.Combine(directory, "task-page-motion-count.json"), New JObject From {{"checks", passed.Count - initialCount}}.ToString())
    End Sub
End Class
