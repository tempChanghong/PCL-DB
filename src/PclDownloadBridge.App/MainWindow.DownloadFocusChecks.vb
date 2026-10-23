Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyDownloadFocusOwnership(check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = content
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim settle As Action = Sub()
                                   owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish() : layout()
                               End Sub
        Dim seek As Action(Of ClockGroup) = Sub(clock)
                                               clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime) : layout()
                                           End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                layout() : owner.SetDownloadView(DownloadView.Modpacks) : layout()
                Dim row As New PclListItem With {.Title = "用户选择的搜索结果", .Height = 1200}
                owner.PackProjectRows.Children.Add(row)
                owner._packProject = New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "focus-fixture", .Name = "焦点回归"}
                owner.RenderPackProject(owner._packProject)
                layout() : owner.ModpackPage.ScrollToVerticalOffset(140) : layout()
                Dim offset = owner.ModpackPage.VerticalOffset
                owner._motionPreview = True : owner._categoryMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                owner._titleNavigation.PauseForChecks = True

                owner.ChangePackMode(PackBrowserMode.Detail) : settle()
                owner.NavigateTitleBack() : layout()
                Dim category = owner._categoryMotion.Transition : seek(category)
                check(category IsNot Nothing AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus IsNot Nothing,
                    "download category accepts input before its delayed default focus request completes")
                FocusManager.SetFocusedElement(host, row) : row.Focus()
                owner._categoryMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is row AndAlso Not owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus Is Nothing AndAlso Not owner._restorePackViewportFocus AndAlso
                    Math.Abs(owner.ModpackPage.VerticalOffset - offset) < 1,
                    "category completion preserves a newer result-row focus without retaining the consumed viewport request")

                owner.ChangePackMode(PackBrowserMode.Detail) : settle()
                owner.NavigateTitleBack() : layout()
                Dim inputVersion = owner._downloadFocusInputVersion
                check(RouteDownloadKey(owner.PackSearchText, Key.A) AndAlso owner._downloadFocusInputVersion = inputVersion,
                    "gated editor input cannot supersede the saved focus request before page interaction is enabled")
                owner._categoryMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is owner.ModpackPage AndAlso Not owner._focusAfterPageSwitch AndAlso
                    Math.Abs(owner.ModpackPage.VerticalOffset - offset) < 1,
                    "category completion still restores the saved viewport when the user has made no newer focus choice")

                owner.ShowSettings(Nothing, Nothing) : settle()
                owner.ShowDownload(Nothing, Nothing) : owner.FocusDownloadPage() : layout()
                Dim root = owner._surfaceMotion.Transition : seek(root)
                check(owner.PageSurface.IsHitTestVisible AndAlso owner._pendingSurfaceFocus IsNot Nothing,
                    "root page entry accepts input before the queued download focus request completes")
                FocusManager.SetFocusedElement(host, row) : row.Focus()
                owner._surfaceMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is row AndAlso owner._pendingSurfaceFocus Is Nothing,
                    "root page completion preserves a newer result-row focus instead of moving it to the search editor")

                owner.ShowSettings(Nothing, Nothing) : settle()
                owner.ShowDownload(Nothing, Nothing) : owner.FocusDownloadPage() : layout()
                root = owner._surfaceMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                FocusManager.SetFocusedElement(host, owner.FolderList)
                settle() : root.Controller.SkipToFill() : layout()
                check(owner.DownloadPage.Visibility = Visibility.Collapsed AndAlso owner._pendingSurfaceFocus Is Nothing AndAlso
                    Not owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus Is Nothing AndAlso FocusManager.GetFocusedElement(host) Is owner.FolderList,
                    "leaving a pending download entry invalidates its old root focus callback")

                owner.ShowDownload(Nothing, Nothing) : settle()
                owner.ChangePackMode(PackBrowserMode.Detail) : settle()
                owner.NavigateTitleBack() : layout()
                category = owner._categoryMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                FocusManager.SetFocusedElement(host, owner.FolderList)
                settle() : category.Controller.SkipToFill() : layout()
                check(Not owner._focusAfterPageSwitch AndAlso owner._pendingDownloadFocus Is Nothing AndAlso owner._pendingSurfaceFocus Is Nothing AndAlso
                    FocusManager.GetFocusedElement(host) Is owner.FolderList AndAlso owner.SettingsPage.IsVisible,
                    "switching settings during a category return cancels its focus ownership and ignores the obsolete clock")

                owner._motionPreview = False
                owner.ShowDownload(Nothing, Nothing) : owner.FocusDownloadPage() : layout()
                check(owner._surfaceMotion.Transition Is Nothing AndAlso owner._categoryMotion.Transition Is Nothing AndAlso
                    owner._pendingSurfaceFocus Is Nothing AndAlso Not owner._focusAfterPageSwitch AndAlso
                    FocusManager.GetFocusedElement(host) Is owner.ModpackPage,
                    "disabled motion applies the saved download viewport focus immediately with no deferred request")

                owner._motionPreview = True
                owner.ChangePackMode(PackBrowserMode.Detail) : settle()
                FocusManager.SetFocusedElement(host, owner.PackSearchText)
                owner.NavigateTitleBack() : layout() : seek(owner._categoryMotion.Transition)
                owner.PackSearchText.Focus()
                check(Not RouteDownloadKey(owner.PackSearchText, Key.A),
                    "a returned search editor accepts typing while its category cards are still entering")
                owner._categoryMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is owner.PackSearchText AndAlso Not owner._restorePackViewportFocus,
                    "typing into the same logical focus target supersedes a queued viewport restoration")

                owner.ChangePackMode(PackBrowserMode.Detail) : settle()
                FocusManager.SetFocusedElement(host, owner.PackSearchText)
                owner.NavigateTitleBack() : layout() : seek(owner._categoryMotion.Transition)
                PointerEvent(owner.PackSearchText, Mouse.PreviewMouseDownEvent)
                owner._categoryMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is owner.PackSearchText AndAlso Not owner._restorePackViewportFocus,
                    "clicking the same logical focus target also supersedes a queued viewport restoration")

                owner.SetDownloadView(DownloadView.Versions) : settle()
                owner.SelectVersion(owner._versions(0)) : owner._pageSwitch.Finish() : layout()
                owner.BackToVersions(Nothing, Nothing) : owner.FocusDownloadPage() : layout()
                owner.UpdateVersions() : layout()
                check(owner._pageSwitch.Transition Is Nothing AndAlso owner._pendingSurfaceFocus IsNot Nothing AndAlso Not owner._focusAfterPageSwitch,
                    "catalogue replacement transfers the original horizontal focus request to fresh root content")
                seek(owner._surfaceMotion.Transition)
                FocusManager.SetFocusedElement(host, owner.RefreshButton)
                owner.UpdateVersions() : layout() : owner._surfaceMotion.Finish() : layout()
                check(FocusManager.GetFocusedElement(host) Is owner.RefreshButton AndAlso owner._pendingSurfaceFocus Is Nothing AndAlso Not owner._focusAfterPageSwitch,
                    "repeated catalogue refresh retains the original focus guard and respects a newer toolbar choice")
            End Using
        Finally
            owner._motionPreview = False : owner._pendingSurfaceFocus = Nothing : owner.ClearDownloadFocus()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = content
        End Try
    End Sub
End Class
