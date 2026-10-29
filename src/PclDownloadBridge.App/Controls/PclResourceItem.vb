Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Core

Namespace Controls
    ' PCL MyResourceItem.xaml at 5ef81e4: 64px row, 50px icon, 14/12px text.
    ' The resource row has its own backing-plate animation and gesture ownership.
    Public NotInheritable Class PclResourceItem
        Inherits Grid
        Public Event Click As RoutedEventHandler
        Private ReadOnly _back As Border
        Private ReadOnly _motion As PclResourceHoverMotion
        Private _mouseDown As Boolean
        Private _keyDown As Key = Key.None
        Private _canInteract As Boolean = True
        Private Shared ReadOnly StatisticGlyphs As New Lazy(Of Geometry())(AddressOf LoadStatisticGlyphs)
        Private Shared Function LoadStatisticGlyphs() As Geometry()
            Dim result As New List(Of Geometry)
            For Each resource In {"IconTitle设置", "IconTitle下载", "IconResourceUpdated", "IconResourceSource"}
                Dim glyph = Geometry.Parse(CStr(Application.Current.FindResource(resource)))
                glyph.Freeze() : result.Add(glyph)
            Next
            Return result.ToArray()
        End Function
        ' PCL MyResourceItem.CanInteraction keeps header tooltips available.
        Public Property CanInteract As Boolean
            Get
                Return _canInteract
            End Get
            Set(value As Boolean)
                If _canInteract = value Then Return
                _canInteract = value : Focusable = value : Cursor = If(value, Cursors.Hand, Cursors.Arrow)
                ResetPressed(False)
            End Set
        End Property
        Friend ReadOnly Property VersionLabel As TextBlock
        Friend ReadOnly Property DownloadLabel As TextBlock
        Friend ReadOnly Property UpdatedLabel As TextBlock
        Friend ReadOnly Property SourceLabel As TextBlock
        Friend ReadOnly Property StatisticsGrid As Grid
        Friend ReadOnly Property SummaryLabel As TextBlock
        Friend ReadOnly Property TitleLabel As TextBlock
        Friend ReadOnly Property SubtitleRun As Run
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property HoverTransition As Animation.ClockGroup
            Get
                Return _motion.Transition
            End Get
        End Property
        Public ReadOnly Property Icon As Image
        Public Sub New(project As ModpackProject, Optional knownVersions As IEnumerable(Of String) = Nothing)
            Height = 64 : Background = Brushes.Transparent : Cursor = Cursors.Hand : Focusable = True
            RenderTransformOrigin = New Point(0.5, 0.5) : RenderTransform = New ScaleTransform(1, 1)
            _back = New Border With {.CornerRadius = New CornerRadius(3), .BorderThickness = New Thickness(1), .SnapsToDevicePixels = True, .Background = PclAnimation.Brush("ColorBrush7"), .BorderBrush = PclAnimation.Brush("ColorBrush6"), .Opacity = 0, .RenderTransform = New ScaleTransform(0.8, 0.8), .RenderTransformOrigin = New Point(0.5, 0.5), .IsHitTestVisible = False}
            Children.Add(_back)
            _motion = New PclResourceHoverMotion(_back)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub() ResetPressed(False))
            Dim body As New Grid With {.Margin = New Thickness(7, 0, 7, 0), .IsHitTestVisible = False}
            body.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(50)})
            body.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(8)})
            body.ColumnDefinitions.Add(New ColumnDefinition())
            Icon = New Image With {.Width = 50, .Height = 50, .Stretch = Stretch.UniformToFill, .Clip = New RectangleGeometry(New Rect(0, 0, 50, 50), 6, 6), .Source = New BitmapImage(New Uri("pack://application:,,,/Images/Blocks/NoIcon.png"))}
            RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality)
            body.Children.Add(Icon)
            Dim text As New Grid With {.VerticalAlignment = VerticalAlignment.Center}
            For Each rowHeight In {17, 18, 16} : text.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(rowHeight)}) : Next
            Dim names = ResourceProjectNames.GetTitle(project)
            Dim title As New TextBlock With {.Text = names.Title, .FontSize = 14, .TextTrimming = TextTrimming.CharacterEllipsis}
            TitleLabel = title
            SubtitleRun = New Run(If(names.Subtitle = "", "", " | " & names.Subtitle)) With {.FontSize = 12}
            SubtitleRun.SetResourceReference(TextElement.ForegroundProperty, "PclResourceSubtitleInk")
            If names.Subtitle <> "" Then
                title.Inlines.Add(SubtitleRun)
            End If
            title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1") : text.Children.Add(title)
            Dim summary As New TextBlock With {.Text = If(project.Summary, "").Replace(vbCr, "").Replace(vbLf, ""), .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis, .ClipToBounds = True}
            SummaryLabel = summary
            Dim tags = If(project.Tags, New String() {}).Where(Function(value) Not String.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
            If tags.Length > 0 Then
                Dim description = summary.Text : summary.Text = ""
                For Each tagText In tags
                    Dim label As New TextBlock With {.Text = tagText, .FontSize = 11}
                    label.SetResourceReference(TextBlock.ForegroundProperty, "PclResourceTagInk")
                    Dim badge As New Border With {.Child = label, .Padding = New Thickness(3, 1, 3, 1), .CornerRadius = New CornerRadius(3), .Margin = New Thickness(0, 0, 3, 0), .SnapsToDevicePixels = True}
                    badge.SetResourceReference(Border.BackgroundProperty, "PclResourceTagBackground")
                    summary.Inlines.Add(New InlineUIContainer(badge) With {.BaselineAlignment = BaselineAlignment.TextBottom})
                Next
                summary.Inlines.Add(New Run(description))
            End If
            summary.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3") : SetRow(summary, 1) : text.Children.Add(summary)
            Dim details As New Grid With {.ClipToBounds = True}
            StatisticsGrid = details
            ' Source MyResourceItem: icon/text pairs separated by weighted gaps.
            For Each columnWidth In {GridLength.Auto, New GridLength(1.3, GridUnitType.Star), New GridLength(0.7, GridUnitType.Star),
                GridLength.Auto, New GridLength(55), New GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto,
                New GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, New GridLength(1.7, GridUnitType.Star)}
                details.ColumnDefinitions.Add(New ColumnDefinition With {.Width = columnWidth})
            Next
            details.ColumnDefinitions(7).MinWidth = 57
            Dim now = DateTimeOffset.UtcNow
            Dim loaderText = ModpackProjectMetadata.LoaderText(project.Loaders)
            Dim shortLoaders = ModpackProjectMetadata.CompactLoaderText(project.Loaders, project.GameVersions)
            Dim values = {If(shortLoaders = "", "", shortLoaders & " ") & ModpackProjectStatistics.VersionText(project.GameVersions, knownVersions), ModpackProjectStatistics.DownloadText(project.Downloads),
                ModpackReleaseStatistics.ShortRelativeTime(project.Updated, now), project.Platform.ToString()}
            Dim heights = {11.0, 10.5, 11.5, 11.5}
            Dim labels As New List(Of TextBlock)
            For i = 0 To values.Length - 1
                Dim resource = If(i = 0, "ColorBrushGray3", "ColorBrushGray4")
                Dim glyph As New Shapes.Path With {.Data = StatisticGlyphs.Value(i), .Stretch = Stretch.Uniform, .Height = heights(i), .VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 0, If(i = 2, 1, 0), 0)}
                glyph.SetResourceReference(Shapes.Path.FillProperty, resource) : SetColumn(glyph, i * 3) : details.Children.Add(glyph)
                Dim label As New TextBlock With {.Text = values(i), .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis, .VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(If(i = 0, 4, 5), 0, 2, 0)}
                label.SetResourceReference(TextBlock.ForegroundProperty, resource) : SetColumn(label, i * 3 + 1) : details.Children.Add(label) : labels.Add(label)
            Next
            VersionLabel = labels(0) : DownloadLabel = labels(1) : UpdatedLabel = labels(2) : SourceLabel = labels(3)
            ' Retain PCL's weighted gaps, but give the compact compatibility text
            ' its natural width when space permits instead of clipping it first.
            AddHandler details.SizeChanged, Sub()
                                                Dim natural = New FormattedText(VersionLabel.Text, Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                    New Typeface(VersionLabel.FontFamily, VersionLabel.FontStyle, VersionLabel.FontWeight, VersionLabel.FontStretch),
                                                    VersionLabel.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(VersionLabel).PixelsPerDip).WidthIncludingTrailingWhitespace + 6
                                                Dim minimum = Math.Min(natural, details.ActualWidth * 0.42)
                                                If Math.Abs(details.ColumnDefinitions(1).MinWidth - minimum) > 0.1 Then details.ColumnDefinitions(1).MinWidth = minimum
                                            End Sub
            SetRow(details, 2) : text.Children.Add(details) : SetColumn(text, 2) : body.Children.Add(text) : Children.Add(body)
            ToolTip = names.Title & If(names.Subtitle = "", "", " | " & names.Subtitle) & vbCrLf & project.Name & vbCrLf & project.Summary & vbCrLf &
                "Minecraft：" & If(project.GameVersions Is Nothing OrElse project.GameVersions.Length = 0, "未知", String.Join("、", project.GameVersions)) & vbCrLf &
                If(tags.Length = 0, "", "分类：" & String.Join("、", tags) & vbCrLf) &
                If(loaderText = "", "", "项目加载器标签：" & loaderText & "（各文件的支持情况以版本列表和包内清单为准）" & vbCrLf) &
                project.Platform.ToString() & " · 下载 " & project.Downloads.ToString("N0", Globalization.CultureInfo.InvariantCulture) & " 次" & vbCrLf &
                "更新于 " & project.Updated.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
            Automation.AutomationProperties.SetName(Me, names.Title & "，" & project.Name & "，" & project.Platform.ToString())
            AddHandler MouseEnter, Sub() Refresh()
            AddHandler MouseLeave, Sub()
                                       _mouseDown = False : ReleaseOwnCapture() : Refresh()
                                   End Sub
            AddHandler GotKeyboardFocus, Sub() Refresh()
            AddHandler LostKeyboardFocus, Sub() ResetPressed()
            AddHandler LostMouseCapture, Sub()
                                            _mouseDown = False : Refresh()
                                        End Sub
            PclTheme.Watch(Me, Sub()
                                   _back.BorderBrush = PclAnimation.Brush("ColorBrush6") : ResetPressed(False)
                               End Sub, Sub()
                                            _back.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6")
                                            Refresh(False, True)
                                        End Sub)
            Refresh(False)
        End Sub
        Private Sub ResetPressed(Optional animate As Boolean = True)
            _mouseDown = False : _keyDown = Key.None : ReleaseOwnCapture() : Refresh(animate)
        End Sub
        Private Sub ReleaseOwnCapture()
            If IsMouseCaptured Then ReleaseMouseCapture()
        End Sub
        Private Sub Refresh(Optional animate As Boolean = True, Optional paletteOnly As Boolean = False)
            Dim active = CanInteract AndAlso MotionPolicy.Interactive AndAlso (IsMouseOver OrElse IsKeyboardFocusWithin)
            Dim pressed = active AndAlso (_mouseDown OrElse _keyDown <> Key.None)
            Dim color = PclAnimation.Brush(If(active, If(pressed, "ColorBrush6", "ColorBrushBg1"), "ColorBrush7")).Color
            If paletteOnly Then _motion.RefreshPalette(color) : Return
            _motion.Update(active, pressed, color, animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing, MotionPolicy.PauseForChecks)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not CanInteract OrElse Not MotionPolicy.Interactive Then Return
            e.Handled = True
            If _keyDown <> Key.None Then Return
            Focus() : _mouseDown = True : CaptureMouse() : Refresh()
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim activate = CanInteract AndAlso _mouseDown AndAlso IsMouseOver AndAlso MotionPolicy.Interactive
            _mouseDown = False : ReleaseOwnCapture() : Refresh() : e.Handled = True
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not CanInteract OrElse Not MotionPolicy.Interactive OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            e.Handled = True
            If e.IsRepeat OrElse _mouseDown OrElse _keyDown <> Key.None Then Return
            _keyDown = e.Key : Refresh()
        End Sub
        Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
            MyBase.OnKeyUp(e)
            If e.Key <> Key.Enter AndAlso e.Key <> Key.Space Then Return
            e.Handled = True
            If _keyDown <> e.Key Then Return
            Dim activate = CanInteract AndAlso MotionPolicy.Interactive
            _keyDown = Key.None : Refresh()
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
    End Class
End Namespace
