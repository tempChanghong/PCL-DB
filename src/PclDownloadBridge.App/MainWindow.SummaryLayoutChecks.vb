Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifySummaryLayout(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 810, .Height = 470}
        owner.Content = Nothing : host.Child = content
        Dim savedAppearance = owner._appearance.ToJson(), observations As New JArray
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                        If clock Is Nothing Then Return
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 810, 470)
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}})
                owner.SelectVersion(owner._versions.Single(Function(item) item.Id = "1.20.1"))
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : layout()
                Dim summary = owner.VanillaVersionSummary, icon = owner.VanillaVersionIcon, label = owner.SelectedVersionText
                check(owner.VanillaCard.HeaderContent Is summary AndAlso summary.ColumnDefinitions.Count = 2 AndAlso
                    summary.ColumnDefinitions(0).Width.IsAuto AndAlso summary.ColumnDefinitions(1).Width.IsStar AndAlso
                    Grid.GetColumn(label) = 1,
                    "Minecraft header uses the fixed PCL auto-icon and bounded star-text columns")
                check(Math.Abs(owner.VanillaCard.ActualHeight - 40) < 0.01 AndAlso Math.Abs(summary.ActualHeight - 18) < 0.01 AndAlso
                    Math.Abs(summary.TranslatePoint(New Point(), owner.VanillaCard).Y - 11) < 0.01,
                    "Minecraft summary retains the source forty-DIP card and eighteen-DIP header at eleven DIP")
                check(Math.Abs(icon.ActualWidth - 18) < 0.01 AndAlso Math.Abs(icon.ActualHeight - 18) < 0.01 AndAlso
                    Math.Abs(icon.TranslatePoint(New Point(), owner.VanillaCard).X - 132) < 0.01 AndAlso
                    Math.Abs(label.TranslatePoint(New Point(), owner.VanillaCard).X - 157) < 0.01 AndAlso label.Text = "1.20.1",
                    "short Minecraft versions retain the PCL icon size and seven-DIP gap without shifting the text origin")

                Dim longId = "1.20.1-layout-" & New String("长"c, 120)
                Dim longEntry = BrowserEntry(longId, "release", "2023-06-12T00:00:00Z")
                owner.SelectVersion(longEntry) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : layout()
                check(label.Text = longId AndAlso label.TextTrimming = TextTrimming.CharacterEllipsis AndAlso
                    CStr(label.ToolTip) = longId AndAlso BindingOperations.IsDataBound(label, ToolTipProperty),
                    "selecting a long Minecraft ID retains its complete value and live tooltip while the label uses ellipsis")
                Dim narrowTextWidth = label.ActualWidth
                Dim textRight = label.TranslatePoint(New Point(label.ActualWidth, 0), owner.VanillaCard).X
                check(label.ActualWidth > 0 AndAlso label.ActualWidth <= summary.ActualWidth - 25 + 0.01 AndAlso
                    textRight <= owner.VanillaCard.ActualWidth - 35 + 0.01,
                    "minimum-window long Minecraft text is measured inside its star column and stops before the card right inset")
                check(Math.Abs(icon.ActualWidth - 18) < 0.01 AndAlso Math.Abs(icon.ActualHeight - 18) < 0.01 AndAlso
                    Math.Abs(owner.VanillaCard.ActualHeight - 40) < 0.01 AndAlso owner._selectedEntry Is longEntry,
                    "long Minecraft labels cannot shrink their icon or grow the fixed header or replace the chosen manifest entry")
                observations.Add(New JObject From {{"scenario", "minimum-long-light"}, {"labelWidth", narrowTextWidth},
                    {"cardWidth", owner.VanillaCard.ActualWidth}, {"textRight", textRight}, {"iconWidth", icon.ActualWidth}})
                SaveControlVisual(host, IO.Path.Combine(directory, "summary-layout-long-light.png"))

                host.Width = 1050 : layout()
                check(label.ActualWidth > narrowTextWidth + 200 AndAlso Math.Abs(icon.ActualWidth - 18) < 0.01 AndAlso
                    CStr(label.ToolTip) = longId AndAlso Math.Abs(owner.VanillaCard.ActualHeight - 40) < 0.01,
                    "resizing the window gives extra width to Minecraft text while retaining the full tooltip icon and header height")
                host.Width = 1012.5 : host.Height = 587.5
                content.LayoutTransform = New ScaleTransform(1.25, 1.25) : layout()
                Dim scaledOrigin = icon.TranslatePoint(New Point(), host), scaledEdge = icon.TranslatePoint(New Point(icon.ActualWidth, 0), host)
                check(Math.Abs(icon.ActualWidth - 18) < 0.01 AndAlso Math.Abs(scaledEdge.X - scaledOrigin.X - 22.5) < 0.02 AndAlso
                    Math.Abs(label.TranslatePoint(New Point(), owner.VanillaCard).X - 157) < 0.02 AndAlso
                    label.TranslatePoint(New Point(label.ActualWidth, 0), owner.VanillaCard).X <= owner.VanillaCard.ActualWidth - 35 + 0.02,
                    "a one-and-a-quarter layout scale preserves logical PCL geometry and the bounded long-version text")
                content.ClearValue(LayoutTransformProperty) : host.Width = 810 : host.Height = 470 : layout()
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                check(DirectCast(label.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush1").Color AndAlso
                    DirectCast(label.Foreground, SolidColorBrush).Color.R > 200 AndAlso label.ActualWidth = narrowTextWidth AndAlso
                    CStr(label.ToolTip) = longId AndAlso label.Text = longId,
                    "dark mode replaces Minecraft text ink without changing long-ID bounds the full label or tooltip")
                SaveControlVisual(host, IO.Path.Combine(directory, "summary-layout-long-dark.png"))
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}}) : layout()
                check(DirectCast(label.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush1").Color AndAlso
                    DirectCast(label.Foreground, SolidColorBrush).Color.R < 200 AndAlso label.ActualWidth = narrowTextWidth,
                    "returning to light mode keeps the same bounded Minecraft summary and adopts current resource ink")

                Dim shadow = owner.DownloadSidebarShadow, fill = TryCast(shadow.Fill, LinearGradientBrush)
                check(fill IsNot Nothing AndAlso fill.StartPoint = New Point(0, 0) AndAlso fill.EndPoint = New Point(1, 0) AndAlso
                    Math.Abs(fill.Opacity - 0.04) < 0.00001 AndAlso fill.GradientStops.Count = 2 AndAlso
                    fill.GradientStops(0).Offset = 0 AndAlso fill.GradientStops(0).Color = Colors.Black AndAlso
                    fill.GradientStops(1).Offset = 1 AndAlso fill.GradientStops(1).Color.A = 0,
                    "download sidebar edge uses FormMain's four-percent black-to-transparent horizontal gradient")
                check(Math.Abs(shadow.ActualWidth - 4) < 0.01 AndAlso Not shadow.IsHitTestVisible AndAlso
                    shadow.Parent Is owner.DownloadPage AndAlso Grid.GetColumn(shadow) = 1 AndAlso
                    owner.DownloadPage.ColumnDefinitions.Count = 2 AndAlso owner.DownloadPage.Children.IndexOf(shadow) > owner.DownloadPage.Children.IndexOf(owner.DownloadRight),
                    "sidebar shadow overlays the right pane without an extra layout column or pointer input")
                check(Math.Abs(shadow.TranslatePoint(New Point(), owner.DownloadPage).X - owner.DownloadRight.TranslatePoint(New Point(), owner.DownloadPage).X) < 0.01 AndAlso
                    Math.Abs(owner.DownloadRight.ActualWidth + owner.DownloadSidebar.ActualWidth - owner.DownloadPage.ActualWidth) < 0.01 AndAlso
                    BindingOperations.IsDataBound(shadow, VisibilityProperty),
                    "sidebar shadow starts on the existing pane boundary and follows sidebar visibility through its binding")

                owner.SelectVersion(owner._versions.Single(Function(item) item.Id = "1.20.1")) : owner._pageSwitch.Finish()
                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "布局验证保留组件与名称"
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), selectedEntry = owner._selectedEntry
                Dim iconSource = icon.Source
                owner._motionPreview = True : owner._pageSwitch.CardMotion.PauseForChecks = True : owner._installActionMotion.PauseForChecks = True
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SetDownloadView(DownloadView.Install)
                Dim enteringPage = owner._pageSwitch.Transition, enteringCards = owner._pageSwitch.CardMotion.Transition
                seek(enteringPage, 140) : seek(enteringCards, 140) : seek(owner._installActionMotion.Transition, 140)
                check(enteringPage IsNot Nothing AndAlso enteringCards IsNot Nothing AndAlso
                    owner._pageSwitch.CardMotion.IncomingTargets.Contains(owner.VanillaCard) AndAlso
                    Math.Abs(summary.ActualHeight - 18) < 0.01 AndAlso Math.Abs(icon.ActualWidth - 18) < 0.01 AndAlso
                    label.TranslatePoint(New Point(label.ActualWidth, 0), summary).X <= summary.ActualWidth + 0.01,
                    "bounded Minecraft summary keeps its layout while the real horizontal and per-card entry clocks run")
                SaveControlVisual(host, IO.Path.Combine(directory, "summary-layout-entry-140.png"))
                owner.SetDownloadView(DownloadView.Versions)
                Dim retiring = owner._pageSwitch.CardMotion.Transition
                seek(owner._pageSwitch.Transition, 35) : seek(retiring, 35) : seek(owner._installActionMotion.Transition, 35)
                owner.SetDownloadView(DownloadView.Install)
                Dim reopening = owner._pageSwitch.CardMotion.Transition
                PauseSelectionClock(owner._pageSwitch.Transition) : PauseSelectionClock(reopening)
                If owner._installActionMotion.Transition IsNot Nothing Then PauseSelectionClock(owner._installActionMotion.Transition)
                enteringCards.Controller.SkipToFill() : retiring.Controller.SkipToFill() : layout()
                check(owner._pageSwitch.CardMotion.Transition Is reopening AndAlso owner._selectedEntry Is selectedEntry AndAlso
                    owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso owner._componentSelection.Has(ComponentKind.FabricApi) AndAlso
                    owner.InstanceName.Text = "布局验证保留组件与名称" AndAlso label.Text = "1.20.1" AndAlso icon.Source Is iconSource,
                    "rapid vanilla reversal rejects obsolete card completions while preserving base icon text exact component selection and custom name")
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : layout()
                check(Not owner._pageSwitch.HasMotion AndAlso owner._installActionMotion.Transition Is Nothing AndAlso
                    Not owner.VanillaCard.HasAnimatedProperties AndAlso owner.VanillaCard.Opacity = 1 AndAlso owner.DownloadViewHost.IsHitTestVisible AndAlso
                    Not TypeOf owner.VanillaCard.RenderTransform Is TransformGroup AndAlso Math.Abs(owner.VanillaCard.ActualHeight - 40) < 0.01,
                    "final vanilla layout cleanup releases card and button clocks without a stale transform or input gate")

                owner._motionPreview = False : owner.SetDownloadView(DownloadView.Modpacks) : layout()
                owner._packProject = New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "summary-layout-fixture", .Name = "布局检查整合包", .Summary = "Offline layout fixture"}
                owner.RenderPackProject(owner._packProject) : owner.ChangePackMode(PackBrowserMode.Detail) : layout()
                check(owner.DownloadSidebar.Visibility = Visibility.Collapsed AndAlso shadow.Visibility = Visibility.Collapsed AndAlso
                    Not shadow.IsVisible AndAlso owner.DownloadPage.ColumnDefinitions(0).ActualWidth = 0 AndAlso
                    Math.Abs(owner.DownloadRight.ActualWidth - owner.DownloadPage.ActualWidth) < 0.01,
                    "pack detail hides the bound sidebar shadow and receives the complete right-pane width")
                owner.NavigateTitleBack() : layout()
                check(owner._packMode = PackBrowserMode.Search AndAlso owner.DownloadSidebar.IsVisible AndAlso shadow.IsVisible AndAlso
                    Math.Abs(shadow.ActualWidth - 4) < 0.01 AndAlso owner._componentSelection.Item(ComponentKind.Fabric) Is selected,
                    "returning from pack detail restores the existing shadow boundary while retaining the vanilla component plan")
                owner.ResetTaskView("布局检查任务", True) : owner.SetDownloadView(DownloadView.Progress) : layout()
                check(owner.DownloadSidebar.Visibility = Visibility.Collapsed AndAlso shadow.Visibility = Visibility.Collapsed AndAlso
                    Not shadow.IsVisible AndAlso owner.DownloadProgress.IsVisible,
                    "task management also hides the sidebar shadow through the same visibility binding")
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                check(owner._downloadView = DownloadView.Modpacks AndAlso owner._packMode = PackBrowserMode.Search AndAlso
                    shadow.IsVisible AndAlso owner.DownloadSidebar.IsVisible AndAlso owner._componentSelection.Item(ComponentKind.Fabric) Is selected,
                    "task return restores the category boundary and original modpack origin without mutating vanilla choices")
            End Using
        Finally
            owner._motionPreview = False : owner.CancelComponentRequests()
            owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish()
            content.ClearValue(LayoutTransformProperty) : owner.RestoreAppearance(savedAppearance)
            IO.File.WriteAllText(IO.Path.Combine(directory, "summary-layout-observations.json"), observations.ToString())
            host.Child = Nothing : owner.Content = content
        End Try
    End Sub
End Class
