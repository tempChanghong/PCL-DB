Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyInstallActionHandoff(directory As String, passed As List(Of String))
        Dim initial = passed.Count
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Dim layout = ControlLayout(host)
        Dim settle As Action = Sub()
                                   owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
                                   owner._installActionMotion.Finish() : owner._titleNavigation.Finish() : layout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, ms)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                owner.SelectVersion(owner._versions.Single(Function(value) value.Id = "1.20.1"))
                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "交接期间保留名称" : settle()
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), body = owner._componentViews(ComponentKind.Fabric).Card.Body
                owner.ResetTaskView("按钮交接验证", True) : owner.ShowProgress(Nothing, Nothing) : owner.FinishTaskView(True, "受控任务完成") : settle()
                owner._motionPreview = True : owner._categoryMotion.PauseForChecks = True
                owner._surfaceMotion.PauseForChecks = True : owner._installActionMotion.PauseForChecks = True
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                Dim page = owner._categoryMotion.Transition
                check(page IsNot Nothing AndAlso Not owner.DownloadViewHost.IsHitTestVisible,
                    "install action handoff begins under the task return page's existing input gate")
                File.WriteAllText(Path.Combine(directory, "install-action-handoff-initial.json"), New JObject From {
                    {"buttonVisible", owner.InstallActionHost.IsVisible}, {"opacity", owner.InstallActionHost.Opacity},
                    {"scale", owner._installActionMotion.Scale.ScaleX}, {"pageOpacity", owner.InstallPage.Opacity}}.ToString())
                check(Not owner.InstallActionHost.IsVisible AndAlso owner.InstallActionHost.Opacity = 0 AndAlso owner._installActionMotion.Transition Is Nothing,
                    "task return keeps the floating install action hidden until PCL's page-entry handoff")
                owner.RefreshTitleNavigation() : owner.ApplyAppearance() : layout()
                check(Not owner.InstallActionHost.IsVisible AndAlso owner._categoryMotion.Transition Is page AndAlso owner._installActionMotion.Transition Is Nothing,
                    "same-view chrome and appearance updates cannot expose a deferred install action")
                seek(page, 70)
                check(Not owner.InstallActionHost.IsVisible AndAlso owner.DownloadGhost.Children.Count > 0,
                    "the outgoing task snapshot cannot be covered by a prematurely shown install action")
                SaveControlVisual(host, Path.Combine(directory, "install-action-handoff-task-70.png"))
                seek(page, 140)
                Dim action = owner._installActionMotion.Transition
                check(action IsNot Nothing AndAlso owner._categoryMotion.Transition Is page AndAlso owner._pageSwitch.Transition Is Nothing,
                    "page reveal starts one independent source-scale action clock without restarting card entry")
                check(owner.InstallActionHost.IsVisible AndAlso owner.InstallActionHost.Opacity = 0 AndAlso owner._installActionMotion.Scale.ScaleX = 0 AndAlso Not owner.InstallActionHost.IsHitTestVisible,
                    "the revealed action starts at source zero scale and opacity with its own input gate")
                owner.RefreshTitleNavigation() : owner.RefreshComponents() : layout()
                check(owner._installActionMotion.Transition Is action AndAlso owner._categoryMotion.Transition Is page,
                    "repeated component and chrome refresh after reveal do not replay either clock")
                seek(page, 230) : seek(action, 90)
                check(Math.Abs(owner.InstallActionHost.Opacity - 0.5) < 0.00001 AndAlso
                    Math.Abs(owner._installActionMotion.Scale.ScaleX - ReferenceInstallAction(0, True, False, 90)) < 0.00001 AndAlso Not owner.InstallActionHost.IsHitTestVisible,
                    "task return action retains MyExtraTextButton's delayed fade fluent scale and local input gate")
                SaveControlVisual(host, Path.Combine(directory, "install-action-handoff-task-230.png"))
                seek(page, 270) : seek(action, 130)
                check(owner.InstallActionHost.IsHitTestVisible AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso owner._categoryMotion.Transition Is page,
                    "action input becomes available after its own fade while the page card rebound continues")
                owner.RestoreAppearance(New JObject From {{"darkMode", True}}) : layout()
                check(owner._installActionMotion.Transition Is action AndAlso owner._categoryMotion.Transition Is page AndAlso
                    owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso owner._componentViews(ComponentKind.Fabric).Card.Body Is body AndAlso owner.InstanceName.Text = "交接期间保留名称",
                    "dark theme changes preserve both handoff clocks component identity and the edited name")
                seek(action, 250)
                check(Math.Abs(owner._installActionMotion.Scale.ScaleX - 1.0375) < 0.00001,
                    "the returned action keeps the source overshoot instead of using a generic card translation")
                owner._categoryMotion.Finish() : layout()
                check(owner._installActionMotion.Transition Is action AndAlso owner.InstallActionHost.IsVisible,
                    "settling revealed cards does not truncate the independent floating action rebound")
                seek(action, 451)
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Opacity = 1 AndAlso owner._installActionMotion.Scale.ScaleX = 1,
                    "completed action handoff removes its clocks and leaves the source final geometry")

                owner.ShowProgress(Nothing, Nothing) : settle()
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                Dim retired = owner._categoryMotion.Transition
                seek(retired, 70)
                owner.ShowProgress(Nothing, Nothing) : layout()
                retired.Controller.SkipToFill() : layout()
                check(owner._downloadView = DownloadView.Progress AndAlso Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "a retired task-return reveal cannot show the action over a reopened task")
                settle() : owner.ResumeInstallation(Nothing, Nothing) : layout()
                retired = owner._categoryMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                retired.Controller.SkipToFill() : settle()
                check(owner.SettingsPage.IsVisible AndAlso Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "settings handoff retires the deferred action together with the old task-return page")

                owner.ShowDownload(Nothing, Nothing) : layout()
                Dim root = owner._surfaceMotion.Transition
                check(root IsNot Nothing AndAlso Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "returning from settings defers the floating action until root-page content reveal")
                seek(root, 140) : action = owner._installActionMotion.Transition
                check(action IsNot Nothing AndAlso owner._surfaceMotion.Transition Is root AndAlso owner.InstallActionHost.Opacity = 0,
                    "root-page reveal uses the same source action entry without a category or horizontal clock")
                seek(root, 230) : seek(action, 90)
                SaveControlVisual(host, Path.Combine(directory, "install-action-handoff-root-230.png"))
                owner.ShowSettings(Nothing, Nothing) : layout()
                action.Controller.SkipToFill() : root.Controller.SkipToFill() : settle()
                check(owner.SettingsPage.IsVisible AndAlso Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "leaving a revealed root entry removes the action tail and ignores both stale completions")
                owner.ShowDownload(Nothing, Nothing) : layout()
                root = owner._surfaceMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                root.Controller.SkipToFill() : settle()
                check(Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "a root page reversed before reveal cannot leave a deferred show on the settings page")

                owner.ShowDownload(Nothing, Nothing) : layout()
                host.Width = 900 : layout()
                check(owner._surfaceMotion.Transition Is Nothing AndAlso owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Opacity = 1,
                    "resizing before root reveal settles the requested action immediately rather than leaving it hidden")
                owner.ShowProgress(Nothing, Nothing) : settle()
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                host.Height = 650 : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.DownloadViewHost.IsHitTestVisible,
                    "resizing a deferred task return releases its owner and final input state")
                owner.SetDownloadView(DownloadView.Modpacks) : settle()
                owner.ShowVanillaDownloads(Nothing, Nothing) : layout()
                page = owner._categoryMotion.Transition
                check(page IsNot Nothing AndAlso Not owner.InstallActionHost.IsVisible,
                    "returning from the modpack category also waits for the retained vanilla page to reveal")
                seek(page, 140)
                check(owner._installActionMotion.Transition IsNot Nothing AndAlso owner._downloadView = DownloadView.Install,
                    "category reveal shows the retained vanilla action through its own scale animation")
                settle()
                owner._motionPreview = False
                owner.ShowProgress(Nothing, Nothing) : owner.ResumeInstallation(Nothing, Nothing) : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Opacity = 1,
                    "static task return settles the install action synchronously with no deferred state")
                owner.ShowSettings(Nothing, Nothing) : owner.ShowDownload(Nothing, Nothing) : layout()
                check(owner._surfaceMotion.Transition Is Nothing AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.IsVisible,
                    "static settings return also leaves no pending action or page clock")
                owner._motionPreview = True
                owner.ShowProgress(Nothing, Nothing) : settle() : owner.ResumeInstallation(Nothing, Nothing) : layout()
                retired = owner._categoryMotion.Transition
                surface.RootVisual = Nothing : FlushControlLayout()
                retired.Controller.SkipToFill() : layout()
                check(owner._categoryMotion.Transition Is Nothing AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.DownloadGhost.Children.Count = 0,
                    "unloading a deferred install action releases its page owner and every associated clock")
            End Using
        Finally
            owner._motionPreview = False : owner.ClearDownloadFocus() : owner._pendingSurfaceFocus = Nothing : settle()
            owner.CancelComponentRequests() : owner._pclThemeMonitor.Dispose()
            host.Child = Nothing : owner.Content = content : ApplyAppearance()
        End Try
        File.WriteAllText(Path.Combine(directory, "install-action-handoff-count.json"), New JObject From {{"checks", passed.Count - initial}}.ToString())
    End Sub
End Class
