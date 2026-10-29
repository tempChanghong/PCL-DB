Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Input
Imports System.Windows.Media

Namespace Controls
    ' PCL MyComboBox / MyComboBoxItem: retain WPF selection, keyboard and accessibility behavior.
    Public Class PclComboBox
        Inherits ComboBox

        Private _popupPanel As FrameworkElement
        Private _popupOpacityOwner As Window
        Private _popupOpacityBinding As BindingExpressionBase

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor, AddressOf RefreshColor)
            AddHandler Loaded, Sub()
                                   SynchronizePopupOpacity()
                                   RefreshColor()
                               End Sub
            AddHandler Unloaded, Sub() ReleasePopupOpacityBinding()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler GotKeyboardFocus, Sub() RefreshColor()
            AddHandler LostKeyboardFocus, Sub() RefreshColor()
            AddHandler DropDownOpened, Sub()
                                           SynchronizePopupOpacity()
                                           RefreshColor()
                                       End Sub
            AddHandler DropDownClosed, Sub() RefreshColor()
        End Sub

        Public Overrides Sub OnApplyTemplate()
            ReleasePopupOpacityBinding()
            MyBase.OnApplyTemplate()
            SynchronizePopupOpacity()
        End Sub

        Private Sub ReleasePopupOpacityBinding()
            If _popupPanel IsNot Nothing AndAlso _popupOpacityBinding IsNot Nothing AndAlso
                BindingOperations.GetBindingExpressionBase(_popupPanel, OpacityProperty) Is _popupOpacityBinding Then
                ' A popup can still be fading out after its owner is unloaded.
                ' Keep its displayed opacity while releasing the retired window.
                Dim opacity = _popupPanel.Opacity
                BindingOperations.ClearBinding(_popupPanel, OpacityProperty)
                _popupPanel.SetCurrentValue(OpacityProperty, opacity)
            End If
            _popupPanel = Nothing : _popupOpacityOwner = Nothing : _popupOpacityBinding = Nothing
        End Sub

        Private Sub SynchronizePopupOpacity()
            Dim panel = TryCast(Template?.FindName("PanPopup", Me), FrameworkElement)
            Dim owner = Window.GetWindow(Me)
            If panel IsNot _popupPanel OrElse owner IsNot _popupOpacityOwner OrElse
                (panel IsNot Nothing AndAlso BindingOperations.GetBindingExpressionBase(panel, OpacityProperty) IsNot _popupOpacityBinding) Then
                ReleasePopupOpacityBinding()
            End If
            If panel Is Nothing OrElse owner Is Nothing OrElse _popupOpacityBinding IsNot Nothing Then Return
            ' PCL synchronizes its popup with FrmMain.Opacity when opening it.
            ' Resolve the real owner instead: previews may have no window, and a
            ' selector can move between independent windows. Respect an explicit
            ' caller binding/animation and never change theme or hover clocks.
            ' HasAnimatedProperties is already true before the animation's first
            ' tick, when the opacity value source may not yet report IsAnimated.
            If BindingOperations.IsDataBound(panel, OpacityProperty) OrElse panel.HasAnimatedProperties OrElse DependencyPropertyHelper.GetValueSource(panel, OpacityProperty).IsAnimated Then Return
            _popupPanel = panel : _popupOpacityOwner = owner
            _popupOpacityBinding = BindingOperations.SetBinding(panel, OpacityProperty,
                New Binding(NameOf(Window.Opacity)) With {.Source = owner, .Mode = BindingMode.OneWay})
        End Sub

        Protected Overrides Function GetContainerForItemOverride() As DependencyObject
            Return New PclComboBoxItem()
        End Function

        Protected Overrides Function IsItemItsOwnContainerOverride(item As Object) As Boolean
            Return TypeOf item Is ComboBoxItem
        End Function

        Private Sub RefreshColor()
            Dim ink = If(Not IsEnabled, "ColorBrushGray5", If(IsDropDownOpen OrElse IsKeyboardFocusWithin, "ColorBrush3", If(IsMouseOver, "ColorBrush4", "ColorBrushBg0")))
            Dim fill = If(Not IsEnabled, "ColorBrushGray6", If(IsDropDownOpen OrElse IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush7", "ColorBrushHalfWhite"))
            Dim duration = If(Not IsEnabled, 200, If(IsDropDownOpen, 10, 100))
            If IsLoaded Then
                ColorTo(Me, ForegroundProperty, Brush(ink).Color, duration)
                ColorTo(Me, BackgroundProperty, Brush(fill).Color, duration)
            Else
                SetResourceReference(ForegroundProperty, ink)
                SetResourceReference(BackgroundProperty, fill)
            End If
        End Sub

        Protected Overrides Sub OnPreviewMouseWheel(e As MouseWheelEventArgs)
            ' A closed selector inside the settings scroller must not silently change its value.
            If Not IsDropDownOpen Then
                e.Handled = True
                Dim parent = TryCast(Me.Parent, UIElement)
                parent?.RaiseEvent(New MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) With {.RoutedEvent = Mouse.MouseWheelEvent, .Source = Me})
                Return
            End If
            MyBase.OnPreviewMouseWheel(e)
        End Sub
    End Class

    Public Class PclComboBoxItem
        Inherits ComboBoxItem

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor, AddressOf RefreshColor)
            AddHandler Loaded, Sub() RefreshColor()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler Selected, Sub() RefreshColor()
            AddHandler Unselected, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler IsKeyboardFocusWithinChanged, Sub() RefreshColor()
        End Sub

        Private Sub RefreshColor()
            Dim key = If(IsSelected, "ColorBrush6", If(IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush8", "ColorBrushTransparent"))
            If IsLoaded Then
                ColorTo(Me, BackgroundProperty, Brush(key).Color, If(IsSelected OrElse IsMouseOver OrElse IsKeyboardFocusWithin, 100, 300))
            Else
                SetResourceReference(BackgroundProperty, key)
            End If
            Opacity = If(IsEnabled, 1, 0.4)
        End Sub
    End Class
End Namespace
