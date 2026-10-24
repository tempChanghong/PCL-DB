Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifySupplementalTags(directory As String, check As Action(Of Boolean, String))
        Dim savedAppearance = _appearance.ToJson(), samples As New StackPanel(), rows As New List(Of PclListItem)
        For Each colour In {"blue", "red", "purple"}
            Dim entry As New VersionEntry With {.Id = "2.0_" & colour, .SupplementalId = "2point0_" & colour, .Kind = "snapshot", .Released = DateTimeOffset.Parse("2013-04-01T00:00:00Z")}
            Dim row = CreateVersionPlaceholder(New VersionPresentation(entry), False).Materialize()
            rows.Add(row) : samples.Children.Add(row)
        Next
        Dim host As New Border With {.Width = 980, .Height = 150, .Padding = New Thickness(12), .Child = samples}
        host.SetResourceReference(Border.BackgroundProperty, "WindowSurfaceBrush")
        Dim layout = ControlLayout(host), first = rows(0), originalIdentity = first.Tag
        Dim info As Func(Of PclListItem, TextBlock) = Function(row) row.Children.OfType(Of TextBlock)().Single(Function(label) Grid.GetRow(label) = 2)
        Dim badge As Func(Of PclListItem, Border) = Function(row) row.Children.OfType(Of Border)().Single(Function(border) TypeOf border.Child Is TextBlock)
        Using source = PreviewSurface.Attach(host, 980, 150)
            Try
                For Each dark In {False, True}
                    host.Width = 980
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    check(rows.All(Function(row) row.Height = 42 AndAlso row.ColumnDefinitions.Count = 6 AndAlso Grid.GetColumnSpan(DirectCast(row.FindName("LabTitle"), TextBlock)) = 2 AndAlso
                        Not badge(row).IsHitTestVisible AndAlso badge(row).Padding = New Thickness(3, 1, 3, 1) AndAlso badge(row).CornerRadius = New CornerRadius(3) AndAlso
                        DirectCast(badge(row).Child, TextBlock).FontSize = 11), "UVMC source label uses CE badge metrics while keeping PCL title and row dimensions: " & dark)
                    check(rows.All(Function(row) badge(row).ActualWidth > 50 AndAlso info(row).TranslatePoint(New Point(), row).X >= badge(row).TranslatePoint(New Point(badge(row).ActualWidth, 0), row).X + 3.9 AndAlso
                        info(row).TranslatePoint(New Point(), row).Y >= DirectCast(row.FindName("LabTitle"), TextBlock).TranslatePoint(New Point(0, DirectCast(row.FindName("LabTitle"), TextBlock).ActualHeight), row).Y),
                        "source badge and lore occupy separate measured columns without overlapping the title: " & dark)
                    Dim expectedBack = If(dark, Color.FromArgb(24, 255, 255, 255), Color.FromArgb(17, 0, 0, 0))
                    Dim expectedInk = If(dark, Color.FromRgb(178, 190, 205), Color.FromRgb(134, 134, 134))
                    check(rows.All(Function(row) DirectCast(badge(row).Background, SolidColorBrush).Color = expectedBack AndAlso DirectCast(DirectCast(badge(row).Child, TextBlock).Foreground, SolidColorBrush).Color = expectedInk AndAlso
                        row.InfoTag = "UVMC 特供下载" AndAlso row.Info.StartsWith("2013 | 这个秘密计划了两年的更新将游戏推向了一个新高度！", StringComparison.Ordinal)) AndAlso
                        rows.Select(Function(row) row.Title).SequenceEqual({"2.0 (Blue)", "2.0 (Red)", "2.0 (Purple)"}), "source label follows light/dark resources and preserves three variant names and 2013 lore: " & dark)
                    SaveControlVisual(host, IO.Path.Combine(directory, "uvmc-2.0-" & If(dark, "dark", "light") & ".png"))
                    host.Width = 250 : layout()
                    check(rows.All(Function(row) info(row).ActualWidth > 0 AndAlso info(row).TextTrimming = TextTrimming.CharacterEllipsis AndAlso info(row).TextWrapping = TextWrapping.NoWrap AndAlso
                        info(row).TranslatePoint(New Point(info(row).ActualWidth, 0), row).X <= row.ActualWidth AndAlso badge(row).ActualWidth < row.ActualWidth), "narrow version rows constrain lore with ellipsis and keep the source badge visible: " & dark)
                Next
                host.Width = 980 : layout()
                first.Selected = True : PauseSelectionClock(first.SelectionTransition)
                SetSelectionHover(first, True) : PauseSelectionClock(first.HoverTransition)
                Dim selection = first.SelectionTransition, hover = first.HoverTransition, transform = first.RenderTransform, oldBadge = badge(first)
                first.InfoTag = "临时来源" : first.InfoTag = "UVMC 特供下载" : layout()
                check(badge(first) Is oldBadge AndAlso first.ColumnDefinitions.Count = 6 AndAlso first.SelectionTransition Is selection AndAlso first.HoverTransition Is hover AndAlso first.RenderTransform Is transform,
                    "replacing source label text retains its visual and the live row selection/hover clocks")
                first.InfoTag = "" : layout()
                check(first.ColumnDefinitions.Count = 5 AndAlso Not first.Children.Contains(oldBadge) AndAlso Grid.GetColumn(info(first)) = 3 AndAlso Grid.GetColumnSpan(DirectCast(first.FindName("LabTitle"), TextBlock)) = 1 AndAlso
                    info(first).Margin = New Thickness(4, 0, 0, 0) AndAlso first.Tag Is originalIdentity, "clearing a source label restores the original PCL column layout without changing version identity")
                first.InfoTag = Nothing : first.InfoTag = "UVMC 特供下载" : first.Info = "" : layout()
                check(first.ColumnDefinitions.Count = 6 AndAlso badge(first).ActualHeight > 0 AndAlso info(first).Visibility = Visibility.Collapsed AndAlso first.InfoTag = "UVMC 特供下载" AndAlso first.Tag Is originalIdentity,
                    "null labels are idempotent and a source label remains visible with an empty info value")
            Finally
                first.Selected = False : SetSelectionHover(first, False)
                RestoreAppearance(savedAppearance)
            End Try
        End Using
    End Sub
End Class
