Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyTitleNavigation(check As Action(Of Boolean, String), directory As String, motion As Boolean)
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                layout()
                owner.SetDownloadView(DownloadView.Modpacks)
                owner._packProject = New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "title-fixture", .Name = "Fabulously Optimized", .Summary = "Navigation fixture"}
                owner.RenderPackProject(owner._packProject)
                Dim row As New PclListItem With {.Title = "保留搜索结果", .Height = 1200}
                owner.PackProjectRows.Children.Add(row) : owner.PackSearchText.Text = "保留搜索条件"
                layout() : owner.ModpackPage.ScrollToVerticalOffset(140) : layout()
                Dim searchOffset = owner.ModpackPage.VerticalOffset
                If motion Then
                    owner._motionPreview = True : owner._titleNavigation.PauseForChecks = True : owner._categoryMotion.PauseForChecks = True
                End If
                Dim previousViewport = owner.DownloadViewHost.TranslatePoint(New Point(), owner.PageSurface)
                owner.ChangePackMode(PackBrowserMode.Detail) : layout()
                If motion Then
                    Dim opening = owner._titleNavigation.Transition
                    check(opening IsNot Nothing AndAlso Not owner.TitleMain.IsHitTestVisible AndAlso owner.TitleInner.IsHitTestVisible, "child-title entry immediately gates the old main navigation")
                    Dim exitLayer = TryCast(owner.DownloadGhost.Children.OfType(Of Canvas)().FirstOrDefault(), Canvas)
                    Dim frozenOffset = If(exitLayer Is Nothing, Nothing, TryCast(exitLayer.RenderTransform, TranslateTransform))
                    Dim currentViewport = owner.DownloadViewHost.TranslatePoint(New Point(), owner.PageSurface)
                    check(frozenOffset IsNot Nothing AndAlso Math.Abs(currentViewport.X + frozenOffset.X - previousViewport.X) < 0.1 AndAlso Math.Abs(currentViewport.Y + frozenOffset.Y - previousViewport.Y) < 0.1, "removing detail sidebar and toolbar keeps the frozen outgoing frame at its original root position")
                    opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime) : layout()
                    check(Math.Abs(owner.TitleMain.Opacity - 1.0 / 3) < 0.02 AndAlso Math.Abs(DirectCast(owner.TitleMain.RenderTransform, TranslateTransform).X - 12 * Math.Pow(100.0 / 150, 2)) < 0.02 AndAlso owner.TitleInner.Opacity = 0, "title first layer follows PCL's 150 ms exit and 200 ms entry delay")
                    opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(275), TimeSeekOrigin.BeginTime) : layout()
                    Dim expected = -16 * Math.Pow(1 - 75.0 / 350, 1.5) * Math.Cos(1.5 * Math.PI * 75.0 / 350)
                    check(Math.Abs(owner.TitleInner.Opacity - 0.5) < 0.02 AndAlso Math.Abs(DirectCast(owner.TitleInner.RenderTransform, TranslateTransform).X - expected) < 0.02, "child title uses PCL's 350 ms cosine-back curve rather than WPF's polynomial back")
                    owner.RefreshTitleNavigation()
                    check(owner._titleNavigation.Transition Is opening, "same-title metadata refresh does not restart the title transition")
                    owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                    check(owner._titleNavigation.Transition Is opening AndAlso DirectCast(owner.InnerTitle.Foreground, SolidColorBrush).Color = Colors.White, "dark-mode changes preserve the active title clock and its white foreground")
                    owner.NavigateTitleBack() : layout()
                    Dim returning = owner._titleNavigation.Transition
                    opening.Controller.SkipToFill() : layout()
                    check(returning IsNot Nothing AndAlso owner._titleNavigation.Transition Is returning AndAlso owner._packMode = PackBrowserMode.Search AndAlso owner.TitleMain.IsHitTestVisible, "obsolete title entry cannot replace a rapid return or its logical input owner")
                    owner._titleNavigation.Finish() : owner._categoryMotion.Finish()
                    check(DirectCast(owner.TitleInner.RenderTransform, TranslateTransform).X = -18, "a completed title return retains PCL's -18 DIP hidden offset instead of its first-entry -16 DIP offset")
                    owner.ResetTaskView("保持返回位置", True) : owner.SetDownloadView(DownloadView.Progress) : layout()
                    owner._categoryMotion.Finish() : owner._titleNavigation.Finish() : layout()
                    Using cancellation As New CancellationTokenSource()
                        owner._cancellation = cancellation
                        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(owner.TaskReturnButton), owner.TaskReturnButton)
                        owner.TaskReturnButton.Focus() : ControlKey(owner.TaskReturnButton, Key.Enter, True) : layout()
                        check(owner._categoryMotion.Transition IsNot Nothing AndAlso owner._focusAfterPageSwitch AndAlso owner._restorePackViewportFocus,
                            "task card return queues viewport focus while the outgoing task frame still owns page motion")
                        owner._categoryMotion.Finish() : layout()
                        check(Not owner._focusAfterPageSwitch AndAlso Not owner._restorePackViewportFocus AndAlso
                            FocusManager.GetFocusedElement(FocusManager.GetFocusScope(owner.ModpackPage)) Is owner.ModpackPage AndAlso
                            Math.Abs(owner.ModpackPage.VerticalOffset - searchOffset) < 1 AndAlso owner._hasTask AndAlso Not cancellation.IsCancellationRequested,
                            "settling task return releases its focus request without scrolling the restored search or canceling the task")
                        owner._titleNavigation.Finish()
                    End Using
                    owner._cancellation = Nothing
                    owner.ChangePackMode(PackBrowserMode.Detail) : layout()
                    Dim reopened = owner._titleNavigation.Transition
                    reopened.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(275), TimeSeekOrigin.BeginTime) : layout()
                    expected = -18 * Math.Pow(1 - 75.0 / 350, 1.5) * Math.Cos(1.5 * Math.PI * 75.0 / 350)
                    check(Math.Abs(DirectCast(owner.TitleInner.RenderTransform, TranslateTransform).X - expected) < 0.02, "subsequent child entry starts from the retained PCL exit offset and follows the same cosine-back curve")
                    owner._titleNavigation.Finish() : owner._categoryMotion.Finish()
                    owner._titleNavigation.SelectTitle("新的详情标题")
                    Dim caption = owner._titleNavigation.CaptionTransition
                    caption.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(140), TimeSeekOrigin.BeginTime) : layout()
                    check(owner.InnerTitle.Text = "新的详情标题" AndAlso owner.InnerTitle.Opacity = 0, "nested title changes after the 130 ms fade before the 150 ms reveal")
                    owner._titleNavigation.SelectTitle(Nothing) : layout()
                    returning = owner._titleNavigation.Transition
                    caption.Controller.SkipToFill() : layout()
                    check(owner._titleNavigation.Transition Is returning AndAlso owner._titleNavigation.CaptionTransition Is Nothing, "obsolete nested-title completion cannot overwrite a return to the main page")
                    owner.RefreshTitleNavigation() : layout()
                    host.Width = 810 : layout()
                    check(owner._titleNavigation.Transition Is Nothing AndAlso owner.TitleInner.Opacity = 1 AndAlso Not owner.TitleInner.HasAnimatedProperties, "window resize releases title clocks and settles the selected child page")
                    owner._titleNavigation.SelectTitle(Nothing) : layout()
                    surface.RootVisual = Nothing : FlushControlLayout()
                    check(owner._titleNavigation.Transition Is Nothing AndAlso owner.TitleMain.Opacity = 1, "unloading a title transition removes its clocks without retaining an input gate")
                    surface.RootVisual = host : layout() : owner._motionPreview = False
                    owner.RefreshTitleNavigation() : layout()
                    check(owner._titleNavigation.Transition Is Nothing AndAlso owner.TitleInner.Visibility = Visibility.Visible, "disabled page motion selects the child title immediately after reattachment")
                    Return
                End If
                check(owner.TitleInner.IsVisible AndAlso Not owner.TitleMain.IsVisible AndAlso owner.InnerTitle.Text = "Fabulously Optimized", "pack detail replaces the product title and main tabs with the project back title")
                check(owner.TitleBack.Width = 28 AndAlso owner.TitleBack.Margin.Left = 12 AndAlso owner.TitleBack.LogoScale = 0.87 AndAlso owner.TitleBack.Logo = owner.InstallBack.Logo AndAlso owner.InnerTitle.Margin.Left = 47, "child title uses the fixed PCL back geometry, scale and text inset")
                check(owner.DownloadSidebar.Visibility = Visibility.Collapsed AndAlso owner.DownloadToolbar.Visibility = Visibility.Collapsed AndAlso owner.DownloadPage.ColumnDefinitions(0).ActualWidth = 0 AndAlso owner.DownloadRight.ActualWidth = owner.DownloadPage.ActualWidth, "detail uses the full page width without the category sidebar or install-folder toolbar")
                check(Not DirectCast(owner.PackProjectIntro.Body, StackPanel).Children.OfType(Of WrapPanel)().Single().Children.OfType(Of PclButton)().Any(Function(button) button.Text = "返回搜索结果"), "detail intro no longer duplicates the title-bar return action")
                If directory IsNot Nothing Then SaveTitleFrame(host, Path.Combine(directory, "title-detail-light.png"))
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                check(owner.TitleBack.Theme = PclIconButton.Themes.White AndAlso owner.InnerTitle.IsVisible AndAlso DirectCast(owner.InnerTitle.Foreground, SolidColorBrush).Color = Colors.White, "child back and project title remain white and legible in dark mode")
                If directory IsNot Nothing Then SaveTitleFrame(host, Path.Combine(directory, "title-detail-dark.png"))
                owner.BackFromTitle(owner.TitleBack, New RoutedEventArgs()) : layout()
                check(owner._packMode = PackBrowserMode.Search AndAlso owner.PackProjectRows.Children.Contains(row) AndAlso owner.PackSearchText.Text = "保留搜索条件" AndAlso Math.Abs(owner.ModpackPage.VerticalOffset - searchOffset) < 1 AndAlso
                    FocusManager.GetFocusedElement(FocusManager.GetFocusScope(owner.ModpackPage)) Is owner.ModpackPage, "title back restores the same search controls, rows and scroll position without focusing an offscreen editor")
                check(owner.TitleMain.IsVisible AndAlso Not owner.TitleInner.IsVisible AndAlso owner.DownloadSidebar.IsVisible AndAlso owner.DownloadToolbar.IsVisible, "return to search restores the main title and download navigation")
                owner.ChangePackMode(PackBrowserMode.Detail) : layout()
                owner.ResetTaskView("保持任务", True) : owner.SetDownloadView(DownloadView.Progress) : layout()
                Using cancellation As New CancellationTokenSource()
                    owner._cancellation = cancellation
                    check(owner.InnerTitle.Text = "任务管理" AndAlso owner.TitleInner.IsVisible, "task management is a child title above the selected pack detail")
                    owner.BackFromTitle(owner.TitleBack, New RoutedEventArgs()) : layout()
                    check(owner._downloadView = DownloadView.Modpacks AndAlso owner._packMode = PackBrowserMode.Detail AndAlso owner.InnerTitle.Text = "Fabulously Optimized" AndAlso Not cancellation.IsCancellationRequested AndAlso owner._hasTask, "task back restores the same detail without canceling or replacing its download")
                    owner.BackFromTitle(owner.TitleBack, New RoutedEventArgs()) : layout()
                    check(owner._packMode = PackBrowserMode.Search AndAlso Not cancellation.IsCancellationRequested, "second back returns from detail to the original search without canceling the task")
                    For Each mode In {PackBrowserMode.Search, PackBrowserMode.Detail, PackBrowserMode.Local}
                        owner.ChangePackMode(mode) : layout()
                        owner.ModpackPage.ScrollToVerticalOffset(140) : layout()
                        Dim offset = owner.ModpackPage.VerticalOffset
                        owner.SetDownloadView(DownloadView.Progress) : layout()
                        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(owner.TaskReturnButton), owner.TaskReturnButton)
                        owner.TaskReturnButton.Focus()
                        ControlKey(owner.TaskReturnButton, Key.Enter, True) : layout()
                        check(owner._downloadView = DownloadView.Modpacks AndAlso owner._packMode = mode AndAlso Not owner._restorePackViewportFocus AndAlso
                            FocusManager.GetFocusedElement(FocusManager.GetFocusScope(owner.ModpackPage)) Is owner.ModpackPage,
                            "task card return restores visible viewport focus through the button's real Enter handler: " & mode.ToString())
                        check(Math.Abs(owner.ModpackPage.VerticalOffset - offset) < 1 AndAlso owner.PackProjectRows.Children.Contains(row) AndAlso
                            owner.PackSearchText.Text = "保留搜索条件" AndAlso owner._hasTask AndAlso owner._cancellation Is cancellation AndAlso Not cancellation.IsCancellationRequested,
                            "task card return retains pack viewport, accepted search and task ownership: " & mode.ToString())
                    Next
                End Using
                owner._cancellation = Nothing
                owner.ChangePackMode(PackBrowserMode.Detail) : owner.ShowSettings(Nothing, Nothing) : layout()
                check(Not owner.NavigateTitleBack() AndAlso owner.TitleMain.IsVisible AndAlso owner.SettingsPage.IsVisible, "a hidden child return cannot navigate while settings owns the page")
                owner.ShowDownload(Nothing, Nothing) : layout()
                check(owner.TitleInner.IsVisible AndAlso owner._packMode = PackBrowserMode.Detail, "settings round trip restores the child title without adding a return level")
                owner._choosingOnline = True
                check(Not owner.NavigateTitleBack() AndAlso owner._packMode = PackBrowserMode.Detail, "online confirmation gates title back without abandoning the chosen release")
                owner._choosingOnline = False
                Dim keyEvent As New KeyEventArgs(Keyboard.PrimaryDevice, surface, 0, Key.Escape) With {.RoutedEvent = Keyboard.KeyDownEvent}
                owner.ShellKeyDown(owner.Shell, keyEvent) : layout()
                check(keyEvent.Handled AndAlso owner._packMode = PackBrowserMode.Search, "Escape shares the title return route")
                For Each button In {MouseButton.XButton1, MouseButton.XButton2}
                    owner.ChangePackMode(PackBrowserMode.Detail)
                    Dim mouseEvent As New MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) With {.RoutedEvent = Mouse.PreviewMouseDownEvent}
                    owner.ShellBackMouseDown(owner.Shell, mouseEvent) : layout()
                    check(mouseEvent.Handled AndAlso owner._packMode = PackBrowserMode.Search, "mouse side button shares PCL's title return route: " & button.ToString())
                Next
                owner.ChangePackMode(PackBrowserMode.Detail) : layout()
                owner._packProject.Name = New String("长"c, 180) : owner.RenderPackProject(owner._packProject) : layout()
                host.Width = 810 : host.Height = 470 : layout()
                check(owner.InnerTitle.TextTrimming = TextTrimming.CharacterEllipsis AndAlso owner.InnerTitle.TranslatePoint(New Point(owner.InnerTitle.ActualWidth, 0), owner.TitleBar).X < owner.TitleMinimize.TranslatePoint(New Point(), owner.TitleBar).X, "long project title is trimmed before window buttons instead of covering them")
                If directory IsNot Nothing Then SaveTitleFrame(host, Path.Combine(directory, "title-detail-long.png"))
            End Using
        Finally
            owner._choosingOnline = False : owner._cancellation = Nothing
            owner._titleNavigation.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = content
        End Try
    End Sub

    Private Shared Sub SaveTitleFrame(host As FrameworkElement, path As String)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        Dim image As New RenderTargetBitmap(CInt(host.ActualWidth), CInt(host.ActualHeight), 96, 96, PixelFormats.Pbgra32)
        image.Render(host)
        Dim encoder As New PngBitmapEncoder : encoder.Frames.Add(BitmapFrame.Create(image))
        Using stream = File.Create(path)
            encoder.Save(stream)
        End Using
    End Sub
End Class
