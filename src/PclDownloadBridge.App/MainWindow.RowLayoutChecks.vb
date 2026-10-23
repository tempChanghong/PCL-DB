Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyRowLayout(directory As String, check As Action(Of Boolean, String))
        Const square As String = "M 0,0 L 12,0 L 12,12 L 0,12 Z"
        Const block As String = "pack://application:,,,/PclDownloadBridge;component/Images/Blocks/Grass.png"
        Dim row As New PclListItem With {.Title = "原版游戏", .Height = 42}
        Dim explicitRow As New PclListItem With {.Title = "显式字号", .Height = 42, .FontSize = 12}
        Dim sidebar As New PclListItem With {.Title = "整合包", .Height = 36, .IsSidebar = True, .Logo = square}
        Dim samples As New StackPanel()
        samples.Children.Add(row) : samples.Children.Add(explicitRow) : samples.Children.Add(sidebar)
        Dim host As New Border With {.Width = 390, .Height = 180, .Padding = New Thickness(12), .Child = samples}
        host.SetResourceReference(Border.BackgroundProperty, "WindowSurfaceBrush")
        Dim layout = ControlLayout(host)
        Dim title As Func(Of PclListItem, TextBlock) = Function(item) DirectCast(item.FindName("LabTitle"), TextBlock)
        Dim column As Func(Of PclListItem, String, Double) = Function(item, name) DirectCast(item.FindName(name), ColumnDefinition).Width.Value
        Dim rowLogo As Func(Of PclListItem, FrameworkElement) = Function(item) item.Children.OfType(Of FrameworkElement)().FirstOrDefault(Function(child) Grid.GetColumn(child) = 2)
        Dim saved = _appearance.ToJson()
        Using source = PreviewSurface.Attach(host, 390, 180)
            Try
                layout()
                check(row.FontSize = 14 AndAlso title(row).FontSize = 14 AndAlso explicitRow.FontSize = 12 AndAlso title(explicitRow).FontSize = 12,
                    "list rows use PCL's 14px default while retaining explicit title-size overrides")
                check(column(row, "ColumnCheck") = 2 AndAlso column(row, "ColumnLogo") = 4 AndAlso title(row).Margin = New Thickness(4, 0, 0, 2),
                    "a full-height row without an icon retains PCL's four-pixel logo gutter and title baseline")
                row.Height = 36 : layout()
                check(column(row, "ColumnCheck") = 4 AndAlso column(row, "ColumnLogo") = 0 AndAlso title(row).Margin = New Thickness(4, 0, 0, 0),
                    "a compact row without an icon uses zero logo gutter and no title-bottom offset")
                row.Info = "先设置副文本" : row.Height = 42 : layout()
                Dim beforeClear = title(row).Margin
                row.Info = "" : layout()
                row.Height = 36 : row.Info = "后设置副文本" : layout()
                check(beforeClear = New Thickness(4, 0, 0, 2) AndAlso title(row).Margin = New Thickness(4, 0, 0, 0),
                    "title baseline follows declared row height rather than the presence or assignment order of info text")

                row.Info = "" : row.Logo = square : layout()
                Dim vector = DirectCast(rowLogo(row), Shapes.Path)
                check(column(row, "ColumnLogo") = 34 AndAlso vector.Margin = New Thickness(6, 8, 4, 8) AndAlso Double.IsNaN(vector.Width),
                    "compact vector rows use the source 34px column and asymmetric 6/4px side margins")
                row.Height = 42 : layout()
                check(column(row, "ColumnLogo") = 38 AndAlso vector.Margin = New Thickness(8, 8, 6, 8) AndAlso title(row).Margin.Bottom = 2,
                    "raising a vector row from 36 to 42 updates both icon-column and title metrics")
                row.Height = Double.NaN : layout()
                check(Double.IsNaN(row.Height) AndAlso column(row, "ColumnLogo") = 38 AndAlso column(row, "ColumnCheck") = 2 AndAlso vector.Margin = New Thickness(8, 8, 6, 8),
                    "Auto height follows PCL's declared-Height comparison and retains full-height metrics")
                row.OutlineLogo = True
                Dim outlineRetained = True
                For Each rowHeight As Double In {36.0, 42.0, Double.NaN}
                    row.Height = rowHeight : layout()
                    outlineRetained = outlineRetained AndAlso vector.Width = 18 AndAlso vector.Height = 18 AndAlso vector.Margin = New Thickness(0) AndAlso vector.Fill Is Nothing AndAlso vector.Stroke IsNot Nothing
                Next
                check(outlineRetained, "outline icons retain their deliberate 18px size and zero margin through compact, tall and Auto layouts")
                row.OutlineLogo = False : layout()
                check(vector.Stroke Is Nothing AndAlso vector.Fill IsNot Nothing AndAlso Double.IsNaN(vector.Width) AndAlso Double.IsNaN(vector.Height) AndAlso vector.Margin = New Thickness(8, 8, 6, 8),
                    "leaving outline mode restores the ordinary vector fill, natural size and source margins")

                row.Logo = block : row.Height = 36 : layout()
                Dim bitmap = DirectCast(rowLogo(row), Image)
                Dim compactBitmap = column(row, "ColumnLogo") = 34 AndAlso bitmap.Margin = New Thickness(4, 5, 3, 5)
                row.Height = 42 : layout()
                check(compactBitmap AndAlso column(row, "ColumnLogo") = 38 AndAlso bitmap.Margin = New Thickness(4, 5, 3, 5) AndAlso bitmap.Source IsNot Nothing,
                    "bitmap icons retain PCL's 4/5/3/5px margins while their column follows the row height")
                row.Logo = "" : layout()
                check(rowLogo(row) Is Nothing AndAlso row.Logo = "" AndAlso column(row, "ColumnLogo") = 4 AndAlso title(row).Margin.Bottom = 2,
                    "removing an icon clears its visual and restores the full-height empty gutter")
                sidebar.Height = 42 : layout()
                Dim fullSidebar = column(sidebar, "ColumnCheck") = 6 AndAlso column(sidebar, "ColumnLogo") = 38 AndAlso title(sidebar).Margin.Bottom = 2
                sidebar.Height = 36 : layout()
                check(fullSidebar AndAlso column(sidebar, "ColumnCheck") = 6 AndAlso column(sidebar, "ColumnLogo") = 34 AndAlso title(sidebar).Margin.Bottom = 0,
                    "sidebar rows retain the six-pixel marker column across compact and full-height icon layouts")
                row.Title = New String("长"c, 100) : row.Width = 280 : layout()
                check(title(row).TextTrimming = TextTrimming.CharacterEllipsis AndAlso title(row).ActualWidth > 0 AndAlso title(row).ActualWidth <= row.ActualWidth AndAlso row.ActualWidth = 280,
                    "long 14px row titles remain constrained and retain character ellipsis at narrow widths")

                row.Selected = True
                Dim selection = row.SelectionTransition : PauseSelectionClock(selection)
                SetSelectionHover(row, True)
                Dim hover = row.HoverTransition : PauseSelectionClock(hover)
                Dim transform = row.RenderTransform
                row.Info = "刷新行度量" : row.Width = 300 : layout()
                row.Info = "" : layout()
                check(row.SelectionTransition Is selection AndAlso row.HoverTransition Is hover AndAlso row.RenderTransform Is transform AndAlso selection.IsPaused AndAlso hover.IsPaused,
                    "info and width metric refreshes retain the live selection and hover clocks and their owned transform")

                row.Selected = False : SetSelectionHover(row, False)
                row.Title = "1.20.1" : row.Info = "正式版 · 测试行度量" : row.Width = Double.NaN : row.Logo = block
                Dim themesMatch = True
                For Each darkMode As Boolean In {False, True}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", darkMode}}) : layout()
                    Dim ink = DirectCast(row.Foreground, SolidColorBrush).Color
                    themesMatch = themesMatch AndAlso ink = PclAnimation.Brush("ColorBrush1").Color AndAlso title(row).FontSize = 14 AndAlso
                        title(row).Margin.Bottom = 2 AndAlso column(row, "ColumnLogo") = 38 AndAlso row.HoverTransition Is Nothing AndAlso row.SelectionTransition Is Nothing
                    SaveControlVisual(host, IO.Path.Combine(directory, "row-layout-" & If(darkMode, "dark", "blue") & ".png"))
                Next
                check(themesMatch, "light and dark palette replacement preserve the source row metrics while settling old clocks")
            Finally
                RestoreAppearance(saved)
            End Try
        End Using
    End Sub
End Class
