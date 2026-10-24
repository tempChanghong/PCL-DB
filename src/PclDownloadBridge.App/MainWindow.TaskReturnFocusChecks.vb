Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyTaskReturnFocus(directory As String, passed As List(Of String))
        Dim initial = passed.Count, observations As New JArray
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        For Each motion In {True, False}
            Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
            Dim host As New Border With {.Width = 810, .Height = 470}
            FocusManager.SetIsFocusScope(host, True)
            owner.Content = Nothing : host.Child = content
            Dim layout = ControlLayout(host)
            Dim seek As Action = Sub()
                                     Dim clock = owner._categoryMotion.Transition
                                     If clock Is Nothing Then Return
                                     PauseSelectionClock(clock)
                                     clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(175), TimeSeekOrigin.BeginTime)
                                     layout()
                                 End Sub
            Dim settle As Action = Sub()
                                       owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._pageSwitch.Finish()
                                       owner._installActionMotion.Finish() : owner._titleNavigation.Finish() : layout()
                                   End Sub
            Dim suffix = If(motion, "animated", "static")
            Try
                Using surface = PreviewSurface.Attach(host, 810, 470)
                    owner.RestoreAppearance(New JObject From {{"darkMode", Not motion}})
                    owner.SelectVersion(owner._versions.Single(Function(value) value.Id = "1.20.1"))
                    owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                    owner.InstanceName.Text = "任务返回保留视口"
                    settle()
                    owner.InstallScroll.ScrollToVerticalOffset(200) : layout()
                    Dim offset = owner.InstallScroll.VerticalOffset
                    Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), body = owner._componentViews(ComponentKind.Fabric).Card.Body
                    check(offset > 100 AndAlso owner.InstanceName.TranslatePoint(New Point(), owner.InstallScroll).Y + owner.InstanceName.ActualHeight < 0,
                        "task return fixture has a genuinely scrolled installation form with its name editor offscreen: " & suffix)
                    owner.ResetTaskView("保留安装视口", True) : owner.ShowProgress(Nothing, Nothing)
                    owner.FinishTaskView(True, "受控任务已完成") : settle()
                    FocusManager.SetFocusedElement(host, owner.TaskReturnButton)
                    owner._motionPreview = motion : owner._categoryMotion.PauseForChecks = True
                    owner.ResumeInstallation(Nothing, Nothing) : layout()
                    check(owner._downloadView = DownloadView.Install AndAlso (owner._categoryMotion.Transition IsNot Nothing) = motion,
                        "task return keeps the requested animation policy while restoring its actual installation page: " & suffix)
                    check(Math.Abs(owner.InstallScroll.VerticalOffset - offset) < 1,
                        "task return initially keeps the scrolled viewport before focus handoff: " & suffix)
                    seek() : settle()
                    Dim focused = TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)
                    observations.Add(New JObject From {{"mode", suffix}, {"before", offset}, {"after", owner.InstallScroll.VerticalOffset},
                        {"focus", If(focused?.Name, "none")}, {"scrollFocusable", owner.InstallScroll.Focusable}})
                    File.WriteAllText(Path.Combine(directory, "task-return-focus-observations.json"), observations.ToString())
                    check(FocusManager.GetFocusedElement(host) Is owner.InstallScroll AndAlso Math.Abs(owner.InstallScroll.VerticalOffset - offset) < 1,
                        "settled task return focuses the installation viewport without pulling its offscreen name editor into view: " & suffix)
                    check(owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso owner._componentViews(ComponentKind.Fabric).Card.Body Is body AndAlso
                        owner.InstanceName.Text = "任务返回保留视口" AndAlso owner._taskOutcome = TaskOutcome.Succeeded,
                        "viewport focus preserves the instance name component identity body and completed task result: " & suffix)
                    check(Not owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus Is Nothing AndAlso owner._pendingSurfaceFocus Is Nothing,
                        "settled viewport handoff consumes only its own pending focus request: " & suffix)
                    SaveControlVisual(host, Path.Combine(directory, "task-return-focus-" & suffix & ".png"))

                    owner.ShowProgress(Nothing, Nothing) : settle()
                    FocusManager.SetFocusedElement(host, owner.TaskReturnButton)
                    owner.NavigateTitleBack() : layout() : seek()
                    check(owner.DownloadViewHost.IsHitTestVisible AndAlso owner.InstanceName.IsEnabled,
                        "task return permits a deliberate editor choice after content reveal: " & suffix)
                    owner.InstallScroll.ScrollToTop() : layout()
                    FocusManager.SetFocusedElement(host, owner.InstanceName)
                    PointerEvent(owner.InstanceName, Mouse.PreviewMouseDownEvent)
                    settle()
                    check(FocusManager.GetFocusedElement(host) Is owner.InstanceName AndAlso owner.InstallScroll.VerticalOffset = 0,
                        "a newer user choice of the name editor wins over queued viewport restoration: " & suffix)

                    owner.InstallScroll.ScrollToVerticalOffset(offset) : layout()
                    owner.ShowProgress(Nothing, Nothing) : settle()
                    FocusManager.SetFocusedElement(host, owner.TaskReturnButton)
                    owner.ResumeInstallation(Nothing, Nothing) : layout()
                    Dim retired = owner._categoryMotion.Transition
                    owner._surfaceMotion.PauseForChecks = True
                    owner.ShowSettings(Nothing, Nothing) : layout()
                    FocusManager.SetFocusedElement(host, owner.FolderList) : settle()
                    retired?.Controller.SkipToFill() : layout()
                    check(owner.SettingsPage.IsVisible AndAlso FocusManager.GetFocusedElement(host) Is owner.FolderList AndAlso
                        Not owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus Is Nothing AndAlso owner._pendingSurfaceFocus Is Nothing,
                        "leaving task return for settings discards the viewport request and ignores the retired clock: " & suffix)

                    owner.ShowDownload(Nothing, Nothing) : settle()
                    owner.InstallScroll.ScrollToVerticalOffset(offset) : layout()
                    owner._busy = True : owner.SetBusy(True)
                    owner.ResetTaskView("仍在进行的任务", True) : owner.ShowProgress(Nothing, Nothing) : settle()
                    Dim taskGeneration = owner._taskGeneration
                    FocusManager.SetFocusedElement(host, owner.TaskReturnButton)
                    owner.ResumeInstallation(Nothing, Nothing) : layout() : seek() : settle()
                    check(FocusManager.GetFocusedElement(host) Is owner.InstallScroll AndAlso Math.Abs(owner.InstallScroll.VerticalOffset - offset) < 1 AndAlso
                        owner._busy AndAlso owner._taskGeneration = taskGeneration AndAlso Not owner.InstanceName.IsEnabled,
                        "returning from a running task preserves its viewport without focusing a disabled editor or cancelling the task: " & suffix)
                    owner._busy = False : owner.SetBusy(False)

                    owner.SetDownloadView(DownloadView.Versions) : settle()
                    owner.SelectVersion(owner._versions.Single(Function(value) value.Id = "1.21.1")) : settle()
                    check(owner.InstallScroll.VerticalOffset = 0 AndAlso owner._selectedEntry.Id = "1.21.1" AndAlso owner.InstanceName.IsEnabled,
                        "a fresh version selection still starts at the top rather than inheriting a task-return viewport flag: " & suffix)
                    owner.ShowProgress(Nothing, Nothing) : settle()
                    owner.ResumeInstallation(Nothing, Nothing) : layout()
                    Dim closing = owner._categoryMotion.Transition
                    surface.RootVisual = Nothing : FlushControlLayout()
                    closing?.Controller.SkipToFill() : layout()
                    check(owner._pendingDownloadFocus Is Nothing AndAlso Not owner._focusAfterPageSwitch AndAlso owner.DownloadGhost.Children.Count = 0,
                        "unloading task return retires its focus request and page visuals: " & suffix)
                End Using
            Finally
                owner._busy = False : owner._motionPreview = False : owner.ClearDownloadFocus()
                owner._pendingSurfaceFocus = Nothing : settle()
                owner.CancelComponentRequests() : owner._pclThemeMonitor.Dispose()
                host.Child = Nothing : owner.Content = content
                ApplyAppearance()
            End Try
        Next
        File.WriteAllText(Path.Combine(directory, "task-return-focus-count.json"), New JObject From {{"checks", passed.Count - initial}}.ToString())
    End Sub
End Class
