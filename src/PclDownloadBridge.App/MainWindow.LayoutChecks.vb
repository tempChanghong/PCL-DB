Imports System.Windows
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyPclDetails(check As Action(Of Boolean, String))
        SelectVersion(_versions(0))
        Dim component = _componentViews(ComponentKind.NeoForge).Card
        Dim arrow = component.Children.OfType(Of Grid)().Single().Children.OfType(Of Shapes.Path)().Single()
        check(DirectCast(arrow.RenderTransform, RotateTransform).Angle = 270, "collapsed component arrow faces right like PCL SwapLogoRight")
        component.Collapsed = False
        check(DirectCast(arrow.RenderTransform, RotateTransform).Angle = 180, "expanded component arrow retains PCL upward direction")
        Dim category = DirectCast(VersionCards.Children(1), PclCard)
        category.Collapsed = True
        Dim categoryArrow = category.Children.OfType(Of Grid)().Single().Children.OfType(Of Shapes.Path)().Single()
        check(DirectCast(categoryArrow.RenderTransform, RotateTransform).Angle = 0, "version category keeps the default downward collapsed arrow")
        check(component.Children.Cast(Of UIElement)().Any(Function(child) child.GetType().Name = "PclDropShadow") AndAlso component.Children.OfType(Of Border)().Single().Effect Is Nothing, "cards render PCL gradient shadow instead of a generic blur effect")
        ResetTaskView("PCL 对齐样本", True)
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载资源", .StageFraction = 0.129, .Total = 100, .Completed = 12})
        check(_taskRows("files").State.Text = "12%", "task phase percentage floors its progress like the PCL runtime")
        FinishTaskView(False, "样本失败")
        Dim failed = _taskRows("files")
        Dim expected = Geometry.Parse("F1 M2.5,0 L0,2.5 7.5,10 0,17.5 2.5,20 10,12.5 17.5,20 20,17.5 12.5,10 20,2.5 17.5,0 10,7.5 2.5,0Z")
        check(failed.State.Visibility = Visibility.Collapsed AndAlso failed.Icon.Visibility = Visibility.Visible AndAlso failed.Icon.Width = 15 AndAlso failed.Icon.Height = 15 AndAlso failed.Icon.Data.ToString() = expected.ToString(), "failed phase uses the original PCL vector geometry rather than a font glyph")
        Dim row = DirectCast(TaskPhases.Children(0), Grid)
        check(row.ColumnDefinitions(0).Width.Value = 50 AndAlso TaskPhases.Margin.Left = -11, "task row labels align to PCL runtime card inset and 50 DIP state column")
        SpeedLimitBox.Text = "无效速度"
        check(DirectCast(SpeedLimitBox.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrushRedBack").Color, "invalid input renders the PCL red background as well as the error border")
        SpeedLimitBox.Text = "0"
        ResetTaskView("处理器进度样本", True)
        RenderTaskProgress(New TransferProgress With {.Phase = "processors", .Stage = "处理器 2/5", .StageFraction = 0.376})
        check(_taskRows("processors").State.Text = "37%", "known processor progress remains numeric even without a download file count")
        RenderTaskProgress(New TransferProgress With {.Phase = "processors", .Stage = "处理器输出", .StageFraction = -1})
        check(_taskRows("processors").State.Text = "…", "processor output without measurable progress stays indeterminate")
        ResetTaskView("并行阶段", True)
        Dim states As New Dictionary(Of String, Double) From {{"metadata", 1}, {"files", 0.5}, {"components", 1}, {"processors", 1}, {"commit", 0}}
        RenderTaskProgress(New TransferProgress With {.Phase = "processors", .Stage = "组件安装完成", .StageFraction = 1, .PhaseFractions = states})
        check(Not _taskRows("files").Complete AndAlso _taskRows("files").State.Text = "50%" AndAlso _taskRows("components").Complete, "parallel task rows preserve unfinished resource downloads after components complete")
        check(Math.Abs(TaskProgress.Value - 0.66) < 0.001, "parallel overall progress sums branch weights instead of jumping to the latest phase")
        states("files") = 0.6
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载资源", .StageFraction = 0.6, .PhaseFractions = states})
        check(_taskRows("processors").Complete AndAlso Not _taskRows("files").Complete AndAlso TaskProgress.Value > 0.66, "late resource progress does not reopen a completed processor phase")
        states("components") = 0.5 : states("processors") = 0
        RenderTaskProgress(New TransferProgress With {.Phase = "components", .Stage = "下载组件支持库", .StageFraction = 0.5, .PhaseFractions = states,
            .Total = 100, .Completed = 55, .PhaseTotal = 10, .PhaseCompleted = 5})
        check(_taskRows("components").Label.Text = "下载组件支持库（5/10）" AndAlso TaskRemaining.Text = "45", "parallel phase file counts remain distinct from the total remaining files")
        check(InstallBack.LogoScale = 0.87 AndAlso InstallBack.Width = 26 AndAlso InstallBack.Opacity = 0.6, "install back button uses the PCL icon scale, size and opacity")
        Dim backPath = DirectCast(InstallBack.FindName("Path"), Shapes.Path)
        check(DirectCast(backPath.Fill, SolidColorBrush).Color = Color.FromArgb(160, 0, 0, 0) AndAlso backPath.Margin.Left = 5, "black icon theme uses PCL alpha and original five DIP path inset")
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        Dim clear = _componentViews(ComponentKind.Fabric).Clear
        check(clear.Width = 30 AndAlso clear.Height = 30 AndAlso clear.LogoScale = 0.5 AndAlso _componentViews(ComponentKind.Fabric).Card.HeaderContentMargin.Right = 32, "component clear icon aligns to the original 30 DIP control, half scale logo and right inset")
        clear.RaiseEvent(New MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) With {.RoutedEvent = Mouse.MouseDownEvent})
        clear.RaiseEvent(New MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) With {.RoutedEvent = Mouse.MouseUpEvent})
        check(Not _componentSelection.Has(ComponentKind.Fabric) AndAlso Not _componentSelection.Has(ComponentKind.FabricApi), "original icon structure still clears the component and its dependencies")
    End Sub

    Private Sub VerifyPageAlignment(check As Action(Of Boolean, String))
        SwitchMainPage(True)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim arrange As Action(Of Double, Double) = Sub(width, height)
                                                       content.Measure(New Size(width, height))
                                                       content.Arrange(New Rect(0, 0, width, height))
                                                       content.UpdateLayout()
                                                   End Sub
        arrange(850, 500)
        Dim folderX = FolderList.TranslatePoint(New Point(), SettingsContent).X
        Dim sourceX = SourceBox.TranslatePoint(New Point(), SettingsContent).X
        check(Math.Abs(folderX - sourceX) < 0.5 AndAlso sourceX > 100, "setting cards share the same label column and control alignment")
        check(Math.Abs(SourceBox.ActualHeight - 28) < 0.5 AndAlso DownloadSettingsGrid.RowDefinitions(1).ActualHeight = 7, "PCL settings render 28 DIP fields with 7 DIP row gaps")
        Dim initialHeight = DownloadSettingsCard.ActualHeight
        SpeedLimitBox.Text = "无效输入"
        arrange(850, 500)
        check(DownloadSettingsCard.ActualHeight > initialHeight AndAlso SpeedLimitBox.ActualHeight > 28, "inline validation grows its row instead of overlapping the next setting")
        SpeedLimitBox.Text = "0"
        arrange(850, 500)
        check(Math.Abs(DownloadSettingsCard.ActualHeight - initialHeight) < 0.5, "corrected input restores compact settings height")
        For Each count In ConnectionLevels
            ConnectionsSlider.Value = count
        Next
        check(ConnectionCount() = 64 AndAlso ConnectionValue.Text = "64" AndAlso Array.IndexOf(ConnectionLevels, ConnectionCount()) = 3, "thread slider retains the legacy saved index for 64 connections")
        ConnectionsSlider.Value = 8
        Slider.IncreaseSmall.Execute(Nothing, ConnectionsSlider)
        check(ConnectionCount() = 16 AndAlso ConnectionValue.Text = "16", "slider keyboard command steps to the next supported connection count")
        Dim scrolled = False
        Dim parent = DirectCast(ConnectionsSlider.Parent, UIElement)
        AddHandler parent.MouseWheel, Sub() scrolled = True
        ConnectionsSlider.RaiseEvent(New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent})
        check(ConnectionCount() = 16 AndAlso scrolled, "wheel over the thread slider scrolls settings without changing its value")
        Dim peer = UIElementAutomationPeer.CreatePeerForElement(ConnectionsSlider)
        Dim range = DirectCast(peer.GetPattern(PatternInterface.RangeValue), IRangeValueProvider)
        check(range.Minimum = 8 AndAlso range.Maximum = 64 AndAlso range.Value = 16, "slider accessibility exposes actual connection counts")
        ConnectionsSlider.Value = 64
        check(Not InstanceName.HasBackground AndAlso DirectCast(InstanceName.Background, SolidColorBrush).Color.A = 0 AndAlso InstanceName.MaxLength = 70, "install name uses the PCL transparent input and 70 character limit")
        Dim glyph As GlyphTypeface = Nothing
        Dim primaryFont As New FontFamily(SourceBox.FontFamily.BaseUri, SourceBox.FontFamily.Source.Split(","c)(0))
        Dim typeface As New Typeface(primaryFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
        check(typeface.TryGetGlyphTypeface(glyph) AndAlso glyph.FontUri.AbsoluteUri.EndsWith("/resources/font.ttf", StringComparison.OrdinalIgnoreCase), "embedded PCL English resolves from input styles stored in Controls")
        arrange(810, 470)
        Dim right = JavaPathBox.TranslatePoint(New Point(JavaPathBox.ActualWidth, 0), DownloadSettingsGrid).X
        check(right < DownloadSettingsGrid.ActualWidth AndAlso JavaPathBox.ActualWidth > 200, "minimum window keeps Java input and chooser in separate columns")
        ResetTaskView("新任务", True)
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载资源", .Total = 50, .Completed = 20, .StageFraction = 0.4, .NetworkBytes = 1024, .ActiveConnections = 12, .Source = "fixture.invalid"})
        check(TaskStats.ColumnDefinitions.Count = 4 AndAlso TaskConnections.Text = "12", "four PCL task statistics display live active connections")
        ResetTaskView("下一次任务", False)
        check(TaskProgress.Value = 0 AndAlso TaskConnections.Text = "—" AndAlso TransferSource.Text = "—" AndAlso TransferStats.Text = "暂无传输数据", "new task clears the previous progress and transfer details")
        FinishTaskView(True, "完成")
        check(TaskConnections.Text = "0" AndAlso TaskPercent.Text = "100.00 %", "finished task clears active connections and displays completed progress")
    End Sub
End Class
