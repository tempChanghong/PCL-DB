Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyProjectTagsUi(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim savedIntro = PackProjectIdentity.Children(0), savedAppearance = _appearance.ToJson(), savedWidth = host.Width
        Dim project = _packProject, releases = _packReleases, generation = _taskGeneration
        Dim fixture As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "tagfixture", .Name = "Fabulously Optimized",
            .Summary = "Beautiful graphics, speedy performance and familiar features in a simple package.", .Downloads = 19194999, .Updated = DateTimeOffset.UtcNow.AddDays(-2),
            .GameVersions = {"1.20.1", "1.21.1"}, .Tags = {"多人", "轻量", "性能优化"}, .Loaders = {"fabric"}}
        Dim row As New PclResourceItem(fixture) With {.Tag = fixture, .CanInteract = False}, clicks = 0
        AddHandler row.Click, Sub() clicks += 1
        Try
            FinishPackContentMotion() : RestoreAppearance(New JObject From {{"theme", "blue"}})
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(row) : ModpackPage.ScrollToHome() : layout()
            Dim badges = row.SummaryLabel.Inlines.OfType(Of InlineUIContainer)().Select(Function(item) DirectCast(item.Child, Border)).ToArray()
            check(badges.Select(Function(badge) DirectCast(badge.Child, TextBlock).Text).SequenceEqual(fixture.Tags), "project category badges precede the description in their PCL translation order")
            check(badges.All(Function(badge) badge.Padding.Equals(New Thickness(3, 1, 3, 1)) AndAlso badge.CornerRadius.Equals(New CornerRadius(3)) AndAlso badge.Margin.Equals(New Thickness(0, 0, 3, 0)) AndAlso DirectCast(badge.Child, TextBlock).FontSize = 11), "resource badges retain PCL's 11 DIP text, 3/1 padding, 3 DIP radius and trailing gap")
            check(row.VersionLabel.Text = "Fabric 1.21, 1.20" AndAlso row.SummaryLabel.Inlines.OfType(Of Run)().Single().Text = fixture.Summary, "loader and game summaries remain separate from the unchanged full description")
            check(row.ToolTip.ToString().Contains("分类：多人、轻量、性能优化") AndAlso row.ToolTip.ToString().Contains("项目加载器标签：Fabric") AndAlso row.ToolTip.ToString().Contains("以版本列表和包内清单为准"), "the full tooltip retains project labels without presenting them as per-file installation support")
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                Dim expectedBack = If(dark, Color.FromArgb(24, 255, 255, 255), Color.FromArgb(17, 0, 0, 0)), expectedInk = If(dark, Color.FromRgb(178, 190, 205), Color.FromRgb(134, 134, 134))
                check(badges.All(Function(badge) DirectCast(badge.Background, SolidColorBrush).Color = expectedBack AndAlso DirectCast(DirectCast(badge.Child, TextBlock).Foreground, SolidColorBrush).Color = expectedInk), "badges use PCL light colors and readable dark colors after a live theme change: " & dark)
                check(row.Height = 64 AndAlso row.SummaryLabel.ActualHeight <= 18 AndAlso badges.All(Function(badge) badge.ActualHeight <= 18), "inline category badges remain in the existing 18 DIP description band without changing the 64 DIP row: " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "project-tags-" & If(dark, "dark", "blue") & ".png"))
            Next
            ControlPointer(row, True) : PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            ControlKey(row, Key.Enter, True) : ControlKey(row, Key.Enter, False)
            check(clicks = 0 AndAlso row.HoverTransition Is Nothing AndAlso row.IsHitTestVisible AndAlso Not row.Focusable, "adding badge visuals cannot make a static detail header clickable or restart its hover motion")
            row.CanInteract = True : ControlKey(row, Key.Space, True) : row.CanInteract = False : row.CanInteract = True : ControlKey(row, Key.Space, False)
            check(clicks = 0, "a tagged row still discards the old keyboard gesture when its interaction owner is revoked")
            ControlKey(row, Key.Enter, True) : ControlKey(row, Key.Enter, False)
            check(clicks = 1 AndAlso row.Tag Is fixture, "a tagged search-style row activates its original project exactly once")
            row.CanInteract = False : ControlPointer(row, False)
            For index = 1 To 6
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", index Mod 2 = 1}})
            Next
            layout()
            check(row.HoverTransition Is Nothing AndAlso badges.All(Function(badge) Not badge.HasAnimatedProperties AndAlso Not DirectCast(badge.Child, TextBlock).HasAnimatedProperties), "rapid theme reversals leave no badge clocks or obsolete resource-row hover animations")
            Dim dense As New PclResourceItem(New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Name = New String("长"c, 180),
                .Summary = New String("长"c, 180), .Updated = DateTimeOffset.UtcNow, .GameVersions = {}, .Loaders = {"forge", "fabric", "quilt"},
                .Tags = {"原版改良", "多人", "科技", "魔法", "冒险", "探索", "战斗", "任务", "轻量", "大型", "基于地图", "小游戏"}}) With {.CanInteract = False}
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(dense)
            For Each windowWidth As Double In {850, 810}
                host.Width = windowWidth : layout()
                check(dense.Height = 64 AndAlso dense.SummaryLabel.ActualWidth > 0 AndAlso dense.SummaryLabel.ActualHeight <= 18 AndAlso dense.SummaryLabel.ClipToBounds AndAlso dense.SummaryLabel.TextWrapping = TextWrapping.NoWrap AndAlso dense.SummaryLabel.TextTrimming = TextTrimming.CharacterEllipsis,
                    "many source categories remain one clipped ellipsis line rather than overflowing the project card: " & windowWidth)
                check(dense.SummaryLabel.TranslatePoint(New Point(dense.SummaryLabel.ActualWidth, dense.SummaryLabel.ActualHeight), dense).Y <= dense.Height AndAlso dense.StatisticsGrid.TranslatePoint(New Point(), dense).Y >= dense.SummaryLabel.TranslatePoint(New Point(), dense).Y + dense.SummaryLabel.ActualHeight - 0.01,
                    "dense badge content cannot displace or overlap the statistics band at supported width " & windowWidth)
            Next
            check(dense.VersionLabel.Text = "Forge / Fabric / Quilt 版本未知" AndAlso dense.ToolTip.ToString().Contains("小游戏"), "all declared loader labels and cropped categories remain available without guessing absent game compatibility")
            SaveControlVisual(host, IO.Path.Combine(directory, "project-tags-overflow.png"))
            Dim plain As New PclResourceItem(New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Name = "缺少标签的项目", .Summary = "普通简介", .Tags = Nothing, .Loaders = Nothing})
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(plain) : layout()
            check(plain.SummaryLabel.Text = "普通简介" AndAlso Not plain.SummaryLabel.Inlines.OfType(Of InlineUIContainer)().Any() AndAlso plain.VersionLabel.Text = "版本未知", "missing optional project metadata preserves the original plain summary without empty badges or loader guesses")
            check(_packProject Is project AndAlso _packReleases Is releases AndAlso _taskGeneration = generation, "category rendering, theme changes and input checks preserve accepted project, exact releases and installation ownership")
        Finally
            row.CanInteract = False
            PackProjectIdentity.Children.Clear() : PackProjectIdentity.Children.Add(savedIntro)
            host.Width = savedWidth : RestoreAppearance(savedAppearance) : layout()
        End Try
    End Sub
End Class
