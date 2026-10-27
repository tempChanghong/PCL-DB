Imports System.IO
Imports System.Windows
Imports System.Windows.Automation
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Core

Namespace Controls
    ' PCL 5ef81e4 MyLocalModItem: local files have their own 44-DIP row,
    ' typography, disabled state and backing-plate/action motion.
    Public NotInheritable Class PclLocalModItem
        Inherits Grid

        Private Shared ReadOnly ActionInsetProperty As DependencyProperty = DependencyProperty.Register("ActionInset", GetType(Double), GetType(PclLocalModItem),
            New PropertyMetadata(4.0, Sub(sender, args)
                                         Dim item = DirectCast(sender, PclLocalModItem)
                                         If item._padding IsNot Nothing Then item._padding.Width = New GridLength(Math.Max(0, CDbl(args.NewValue)))
                                         item.CompressTitle()
                                     End Sub))
        Private ReadOnly _back As Border, _padding As ColumnDefinition
        Private ReadOnly _hover As PclResourceHoverMotion, _actionOpacity As PclMotionSlot, _actionInset As PclMotionSlot
        Private ReadOnly _titleGrid As Grid, _titleColumn As ColumnDefinition, _versionColumn As ColumnDefinition, _extendColumn As ColumnDefinition
        Private _lastActive As Boolean?, _pressed As Boolean, _touchRevealed As Boolean, _titleCompression As Integer
        Private _checked As Boolean, _hasUpdate As Boolean, _rowPress As Point?
        Private ReadOnly _selectionMark As Border
        Private ReadOnly _selectionMotion As PclLocalModSelectionMotion
        Public Event CheckedChanged As EventHandler
        Public Property Checked As Boolean
            Get
                Return _checked
            End Get
            Set(value As Boolean)
                If value = _checked Then Return
                _checked = value
                AutomationProperties.SetItemStatus(Me, If(value, "已选择", "未选择"))
                RefreshAppearance(True) : RaiseEvent CheckedChanged(Me, EventArgs.Empty)
            End Set
        End Property
        Public Property HasUpdate As Boolean
            Get
                Return _hasUpdate
            End Get
            Set(value As Boolean)
                If value = _hasUpdate Then Return
                _hasUpdate = value : RefreshAppearance(True)
            End Set
        End Property
        Friend Sub SetUpdateInfo(update As LocalModUpdate)
            Dim entry = DirectCast(Tag, LocalModEntry)
            HasUpdate = update IsNot Nothing AndAlso update.HasUpdate
            Dim text = BuildToolTip(entry)
            If HasUpdate Then text &= vbCrLf & "可更新至 " & Bounded(Flat(update.Release.Name), 200) & vbCrLf & "来源：" & update.Project.Platform.ToString()
            ToolTip = Bounded(text, 4608)
        End Sub
        Friend ReadOnly Property TitleLabel As TextBlock
        Friend ReadOnly Property VersionLabel As TextBlock
        Friend ReadOnly Property InfoLabel As TextBlock
        Friend ReadOnly Property Icon As Image
        Friend ReadOnly Property DisabledBadge As Image
        Friend ReadOnly Property ToggleButton As PclIconButton
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property TitleCompression As Integer
            Get
                Return _titleCompression
            End Get
        End Property
        Friend ReadOnly Property HoverTransition As ClockGroup
            Get
                Return _hover.Transition
            End Get
        End Property
        Friend ReadOnly Property ActionOpacityTransition As AnimationClock
            Get
                Return _actionOpacity.Clock
            End Get
        End Property
        Friend ReadOnly Property ActionInsetTransition As AnimationClock
            Get
                Return _actionInset.Clock
            End Get
        End Property
        Friend ReadOnly Property ActionInset As Double
            Get
                Return CDbl(GetValue(ActionInsetProperty))
            End Get
        End Property

        Public Sub New(entry As LocalModEntry, Optional fileNameFirst As Boolean = False)
            If entry Is Nothing Then Throw New ArgumentNullException(NameOf(entry))
            Tag = entry : Height = 44 : Background = Brushes.Transparent : SnapsToDevicePixels = True : Focusable = True
            AutomationProperties.SetName(Me, entry.DisplayName)
            AutomationProperties.SetHelpText(Me, "按空格选择或取消选择；右侧按钮控制启用状态。")
            For Each columnSize As GridLength In {New GridLength(6), New GridLength(34), New GridLength(7), GridLength.Auto, New GridLength(1, GridUnitType.Star), New GridLength(4)}
                ColumnDefinitions.Add(New ColumnDefinition With {.Width = columnSize})
            Next
            _padding = ColumnDefinitions(5)
            _selectionMark = New Border With {.Width = 5, .Height = 0, .Opacity = 0, .CornerRadius = New CornerRadius(2), .Margin = New Thickness(-3, 0, 0, 0), .VerticalAlignment = VerticalAlignment.Center, .HorizontalAlignment = HorizontalAlignment.Left, .IsHitTestVisible = False}
            _selectionMark.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
            SetRowSpan(_selectionMark, 4) : SetColumnSpan(_selectionMark, 6) : Panel.SetZIndex(_selectionMark, 2) : Children.Add(_selectionMark)
            For Each rowSize As GridLength In {New GridLength(1, GridUnitType.Star), New GridLength(17), New GridLength(18), New GridLength(1, GridUnitType.Star)}
                RowDefinitions.Add(New RowDefinition With {.Height = rowSize})
            Next
            _back = New Border With {.CornerRadius = New CornerRadius(3), .BorderThickness = New Thickness(1), .Background = Brush("ColorBrush7"),
                .BorderBrush = Brush("ColorBrush6"), .Opacity = 0, .RenderTransform = New ScaleTransform(0.8, 0.8), .RenderTransformOrigin = New Point(0.5, 0.5), .IsHitTestVisible = False}
            SetColumnSpan(_back, 6) : SetRowSpan(_back, 4) : Children.Add(_back)
            _hover = New PclResourceHoverMotion(_back)
            Icon = New Image With {.Width = 34, .Height = 34, .Stretch = Stretch.Uniform, .IsHitTestVisible = False,
                .Source = ModIcon(entry)}
            RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality)
            Dim iconFrame As New Border With {.Width = 34, .Height = 34, .Child = Icon, .IsHitTestVisible = False,
                .Clip = New RectangleGeometry(New Rect(0, 0, 34, 34), 6, 6), .VerticalAlignment = VerticalAlignment.Center}
            SetColumn(iconFrame, 1) : SetRow(iconFrame, 1) : SetRowSpan(iconFrame, 2) : Children.Add(iconFrame)
            DisabledBadge = New Image With {.Width = 20, .Height = 20, .Margin = New Thickness(0, 0, -5, -3), .IsHitTestVisible = False,
                .HorizontalAlignment = HorizontalAlignment.Right, .VerticalAlignment = VerticalAlignment.Bottom,
                .Source = New BitmapImage(New Uri("pack://application:,,,/Images/Icons/Disabled.png")), .Visibility = If(entry.Enabled, Visibility.Collapsed, Visibility.Visible)}
            RenderOptions.SetBitmapScalingMode(DisabledBadge, BitmapScalingMode.HighQuality)
            SetColumn(DisabledBadge, 1) : SetRow(DisabledBadge, 1) : SetRowSpan(DisabledBadge, 2) : Children.Add(DisabledBadge)
            _titleGrid = New Grid With {.HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Bottom, .Margin = New Thickness(0, 1, 0, 0), .IsHitTestVisible = False, .ClipToBounds = True}
            _titleColumn = New ColumnDefinition With {.Width = GridLength.Auto}
            _versionColumn = New ColumnDefinition With {.Width = GridLength.Auto}
            _extendColumn = New ColumnDefinition()
            _titleGrid.ColumnDefinitions.Add(_titleColumn) : _titleGrid.ColumnDefinitions.Add(_versionColumn) : _titleGrid.ColumnDefinitions.Add(_extendColumn)
            TitleLabel = New TextBlock With {.Text = Bounded(If(fileNameFirst, Path.GetFileNameWithoutExtension(entry.EnabledName), entry.DisplayName), 200), .FontSize = 14, .TextTrimming = TextTrimming.CharacterEllipsis,
                .VerticalAlignment = VerticalAlignment.Bottom, .TextDecorations = If(entry.Enabled, Nothing, TextDecorations.Strikethrough)}
            TitleLabel.SetResourceReference(TextBlock.ForegroundProperty, If(entry.Enabled, "ColorBrush1", "ColorBrushGray4"))
            _titleGrid.Children.Add(TitleLabel)
            _selectionMotion = New PclLocalModSelectionMotion(_selectionMark, TitleLabel)
            VersionLabel = New TextBlock With {.Text = If(String.IsNullOrEmpty(entry.Version), "", "  |  " & Bounded(Flat(entry.Version), 200)), .FontSize = 12,
                .TextTrimming = TextTrimming.CharacterEllipsis, .VerticalAlignment = VerticalAlignment.Bottom, .Opacity = 0.4,
                .Visibility = If(fileNameFirst OrElse String.IsNullOrEmpty(entry.Version), Visibility.Collapsed, Visibility.Visible)}
            VersionLabel.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray1")
            SetColumn(VersionLabel, 1) : _titleGrid.Children.Add(VersionLabel)
            SetColumn(_titleGrid, 3) : SetColumnSpan(_titleGrid, 2) : SetRow(_titleGrid, 1) : Children.Add(_titleGrid)
            Dim description = Path.GetFileNameWithoutExtension(entry.EnabledName)
            If fileNameFirst Then description = entry.DisplayName & If(String.IsNullOrWhiteSpace(entry.Version), "", " (" & entry.Version & ")")
            If Not String.IsNullOrWhiteSpace(entry.Description) Then description &= ": " & Flat(entry.Description)
            InfoLabel = New TextBlock With {.Text = Bounded(Flat(If(entry.Problem, description)), 512), .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis,
                .VerticalAlignment = VerticalAlignment.Bottom, .Margin = New Thickness(0, 0, 3, 1), .IsHitTestVisible = False}
            InfoLabel.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
            SetColumn(InfoLabel, 4) : SetRow(InfoLabel, 2) : Children.Add(InfoLabel)
            ToggleButton = New PclIconButton With {.Width = 25, .Height = 25, .Margin = New Thickness(0, 0, 5, 0), .HorizontalAlignment = HorizontalAlignment.Right,
                .VerticalAlignment = VerticalAlignment.Center, .Tag = entry, .IsEnabled = entry.CanToggle, .Opacity = 0,
                .ToolTip = If(entry.Enabled, "禁用", "启用") & " " & entry.FileName,
                .Logo = If(entry.Enabled,
                    "M508 990.4c-261.6 0-474.4-212-474.4-474.4S246.4 41.6 508 41.6s474.4 212 474.4 474.4S769.6 990.4 508 990.4zM508 136.8c-209.6 0-379.2 169.6-379.2 379.2 0 209.6 169.6 379.2 379.2 379.2s379.2-169.6 379.2-379.2C887.2 306.4 717.6 136.8 508 136.8zM697.6 563.2 318.4 563.2c-26.4 0-47.2-21.6-47.2-47.2 0-26.4 21.6-47.2 47.2-47.2l379.2 0c26.4 0 47.2 21.6 47.2 47.2C744.8 542.4 724 563.2 697.6 563.2z",
                    "M512 0a512 512 0 1 0 512 512A512 512 0 0 0 512 0z m0 921.6a409.6 409.6 0 1 1 409.6-409.6 409.6 409.6 0 0 1-409.6 409.6z M716.8 339.968l-256 253.44L328.192 460.8A51.2 51.2 0 0 0 256 532.992l168.448 168.96a51.2 51.2 0 0 0 72.704 0l289.28-289.792A51.2 51.2 0 0 0 716.8 339.968z")}
            AutomationProperties.SetName(ToggleButton, If(entry.Enabled, "禁用", "启用") & " " & entry.DisplayName)
            SetColumnSpan(ToggleButton, 6) : SetRowSpan(ToggleButton, 4) : Children.Add(ToggleButton)
            _actionOpacity = New PclMotionSlot(ToggleButton, UIElement.OpacityProperty)
            _actionInset = New PclMotionSlot(Me, ActionInsetProperty)
            MotionPolicy = New PclControlMotionPolicy(Me, AddressOf SettleInteraction)
            ToolTip = BuildToolTip(entry)
            AddHandler _titleGrid.SizeChanged, Sub() CompressTitle()
            AddHandler SizeChanged, Sub() CompressTitle()
            AddHandler Loaded, Sub() CompressTitle()
            AddHandler MouseEnter, Sub() RefreshAppearance(True)
            AddHandler MouseLeave, Sub()
                                      _pressed = False : _touchRevealed = False : _rowPress = Nothing
                                      RefreshAppearance(True)
                                  End Sub
            AddHandler IsKeyboardFocusWithinChanged, Sub()
                                                         If Not IsKeyboardFocusWithin Then _pressed = False
                                                         RefreshAppearance(True)
                                                     End Sub
            AddHandler PreviewMouseLeftButtonDown, Sub(sender, args)
                                                       If MotionPolicy.Interactive Then _pressed = True : RefreshAppearance(True)
                                                       If MotionPolicy.Interactive AndAlso Not ToggleButton.IsMouseOver Then _rowPress = args.GetPosition(Me)
                                                   End Sub
            AddHandler PreviewMouseLeftButtonUp, Sub(sender, args)
                                                     _pressed = False : RefreshAppearance(True)
                                                     Dim start = _rowPress : _rowPress = Nothing
                                                     If start.HasValue AndAlso MotionPolicy.Interactive AndAlso Not ToggleButton.IsMouseOver AndAlso
                                                         (args.GetPosition(Me) - start.Value).Length <= 5 Then
                                                         Focus() : Checked = Not Checked : args.Handled = True
                                                     End If
                                                 End Sub
            AddHandler PreviewKeyDown, Sub(sender, args)
                                           If ToggleButton.IsKeyboardFocusWithin AndAlso MotionPolicy.Interactive AndAlso (args.Key = Key.Space OrElse args.Key = Key.Enter) Then _pressed = True : RefreshAppearance(True)
                                       End Sub
            AddHandler PreviewKeyUp, Sub()
                                         _pressed = False : RefreshAppearance(True)
                                     End Sub
            AddHandler KeyDown, Sub(sender, args)
                                    If IsKeyboardFocused AndAlso MotionPolicy.Interactive AndAlso Not args.IsRepeat AndAlso (args.Key = Key.Space OrElse args.Key = Key.Enter) Then
                                        Checked = Not Checked : args.Handled = True
                                    End If
                                End Sub
            AddHandler PreviewTouchDown, Sub(sender, args)
                                            If Not MotionPolicy.Interactive OrElse _touchRevealed Then Return
                                            _touchRevealed = True : RefreshAppearance(False)
                                            ' The first touch reveals the action without
                                            ' promoting an invisible button into a file write.
                                            args.Handled = True
                                        End Sub
            PclTheme.Watch(Me, Sub()
                                  _back.BorderBrush = Brush("ColorBrush6")
                                  RefreshAppearance(False)
                              End Sub, Sub()
                                           _back.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6")
                                           RefreshAppearance(False, True)
                                       End Sub)
        End Sub
        Private Shared Function ModIcon(entry As LocalModEntry) As ImageSource
            Dim bytes = entry.IconData
            If bytes IsNot Nothing Then
                Try
                    Using input As New MemoryStream(bytes, False)
                        Dim bitmap As New BitmapImage()
                        bitmap.BeginInit() : bitmap.CacheOption = BitmapCacheOption.OnLoad
                        If entry.IconWidth >= entry.IconHeight Then
                            bitmap.DecodePixelWidth = Math.Min(68, entry.IconWidth)
                        Else
                            bitmap.DecodePixelHeight = Math.Min(68, entry.IconHeight)
                        End If
                        bitmap.StreamSource = input : bitmap.EndInit() : bitmap.Freeze()
                        Return bitmap
                    End Using
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is FormatException
                    ' Optional artwork never disables a valid local Mod row.
                End Try
            End If
            Return New BitmapImage(New Uri("pack://application:,,,/Images/Blocks/NoIcon.png"))
        End Function

        Private Sub SettleInteraction()
            If MotionPolicy IsNot Nothing AndAlso Not MotionPolicy.Interactive Then _pressed = False : _touchRevealed = False : _rowPress = Nothing
            RefreshAppearance(False)
        End Sub
        Private Sub RefreshAppearance(animate As Boolean, Optional paletteOnly As Boolean = False)
            If MotionPolicy Is Nothing Then Return
            Dim interactive = MotionPolicy.Interactive, active = interactive AndAlso (IsMouseOver OrElse IsKeyboardFocusWithin OrElse _touchRevealed)
            Dim plate = _checked OrElse active
            Dim moving = animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing
            Dim titleKey = If(_checked, If(DirectCast(Tag, LocalModEntry).Enabled, "ColorBrush2", "ColorBrush5"), If(_hasUpdate, "ColorBrush3", If(DirectCast(Tag, LocalModEntry).Enabled, "ColorBrush1", "ColorBrushGray4")))
            If paletteOnly Then
                _selectionMotion.RefreshPalette(titleKey)
                _hover.RefreshPalette(Brush(If(_pressed AndAlso interactive, "ColorBrush6", If(plate, "ColorBrushBg1", "ColorBrush7"))).Color)
                Return
            End If
            ' MyLocalModItem.Checked uses IsVisibleInForm: selecting a large
            ' catalogue must not create marker/color clocks below the viewport.
            Dim root = TryCast(PresentationSource.FromVisual(Me)?.RootVisual, FrameworkElement)
            Dim selectionMoving = moving AndAlso root IsNot Nothing AndAlso Not PclExitSnapshot.VisibleBounds(Me, root).IsEmpty
            _selectionMotion.Update(_checked, titleKey, selectionMoving, MotionPolicy.PauseForChecks)
            _hover.Update(plate, interactive AndAlso _pressed, Brush(If(_pressed AndAlso interactive, "ColorBrush6", If(plate, "ColorBrushBg1", "ColorBrush7"))).Color, moving, MotionPolicy.PauseForChecks)
            If moving AndAlso _lastActive.HasValue AndAlso _lastActive.Value = active Then Return
            _lastActive = active
            Dim opacity = If(active, 1.0, 0.0), inset = If(active, 30.0, 4.0)
            If moving Then
                _actionOpacity.Animate(New DoubleAnimation(ToggleButton.Opacity, opacity, TimeSpan.FromMilliseconds(If(active, 84, 72))) With {
                    .BeginTime = TimeSpan.FromMilliseconds(If(active, 36, 0))}, opacity, MotionPolicy.PauseForChecks)
                _actionInset.Animate(New DoubleAnimation(ActionInset, inset, TimeSpan.FromMilliseconds(If(active, 36, 72))) With {
                    .BeginTime = TimeSpan.FromMilliseconds(If(active, 84, 0))}, inset, MotionPolicy.PauseForChecks)
            Else
                _actionOpacity.SetValue(opacity) : _actionInset.SetValue(inset)
            End If
        End Sub
        Private Sub CompressTitle()
            If _titleGrid Is Nothing OrElse ActualWidth <= 0 Then Return
            ' Auto title/version columns can arrange beyond their Grid's measured
            ' desired size. Bound the actual PCL text region before compressing.
            Dim available = Math.Max(0, ActualWidth - 47 - ActionInset)
            _titleGrid.Width = available
            Dim titleWidth = NaturalWidth(TitleLabel), versionWidth = If(VersionLabel.Visibility = Visibility.Visible, NaturalWidth(VersionLabel), 0)
            Dim level = If(titleWidth + versionWidth <= available + 0.5, 0, If(titleWidth <= available, 1, 2))
            _titleCompression = level
            _titleColumn.Width = If(level = 2, New GridLength(1, GridUnitType.Star), GridLength.Auto)
            _versionColumn.Width = If(level = 0, GridLength.Auto, If(level = 1, New GridLength(1, GridUnitType.Star), New GridLength(0)))
            _extendColumn.Width = If(level = 0, New GridLength(1, GridUnitType.Star), New GridLength(0))
        End Sub
        Private Shared Function NaturalWidth(label As TextBlock) As Double
            Dim probe As New TextBlock With {.Text = label.Text, .FontFamily = label.FontFamily, .FontSize = label.FontSize, .FontWeight = label.FontWeight, .FontStyle = label.FontStyle, .FontStretch = label.FontStretch}
            probe.Measure(New Size(Double.PositiveInfinity, Double.PositiveInfinity))
            Return probe.DesiredSize.Width
        End Function
        Private Shared Function BuildToolTip(entry As LocalModEntry) As String
            Dim lines As New List(Of String) From {entry.FileName, "物理状态：" & If(entry.Enabled, "已启用", "已禁用")}
            If Not String.IsNullOrEmpty(entry.Version) Then lines.Add("已读版本：" & entry.Version)
            lines.Add("声明加载器：" & entry.LoaderSummary)
            If Not String.IsNullOrEmpty(entry.Problem) Then lines.Add("问题：" & entry.Problem)
            lines.AddRange(entry.Warnings.Select(Function(warning) "警告：" & warning))
            If Not String.IsNullOrEmpty(entry.Description) Then lines.Add(entry.Description)
            Return Bounded(String.Join(vbCrLf, lines), 4096)
        End Function
        Private Shared Function Flat(value As String) As String
            Return If(value, "").Replace(vbCr, "").Replace(vbLf, "")
        End Function
        Private Shared Function Bounded(value As String, maximum As Integer) As String
            value = If(value, "")
            If value.Length <= maximum Then Return value
            Dim count = maximum - 1
            If count > 0 AndAlso Char.IsHighSurrogate(value(count - 1)) Then count -= 1
            Return value.Substring(0, count) & "…"
        End Function
    End Class
End Namespace
