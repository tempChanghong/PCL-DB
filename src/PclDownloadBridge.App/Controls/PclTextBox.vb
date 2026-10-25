Imports System.Windows
Imports System.Windows.Controls

Namespace Controls
    Public Class PclTextBox
        Inherits TextBox

        Public Shared ReadOnly HintTextProperty As DependencyProperty = DependencyProperty.Register("HintText", GetType(String), GetType(PclTextBox), New PropertyMetadata(""))
        Public Shared ReadOnly ShowValidateResultProperty As DependencyProperty = DependencyProperty.Register("ShowValidateResult", GetType(Boolean), GetType(PclTextBox), New PropertyMetadata(True))
        Public Property ShowValidateResult As Boolean
            Get
                Return CBool(GetValue(ShowValidateResultProperty))
            End Get
            Set(value As Boolean)
                SetValue(ShowValidateResultProperty, value)
            End Set
        End Property
        Public Shared ReadOnly HasBackgroundProperty As DependencyProperty = DependencyProperty.Register("HasBackground", GetType(Boolean), GetType(PclTextBox), New PropertyMetadata(True, Sub(d, e) DirectCast(d, PclTextBox).RefreshColor()))
        Public Shared ReadOnly ValidationMessageProperty As DependencyProperty = DependencyProperty.Register("ValidationMessage", GetType(String), GetType(PclTextBox), New PropertyMetadata("", Sub(d, e) DirectCast(d, PclTextBox).RefreshColor()))
        Public Property HintText As String
            Get
                Return CStr(GetValue(HintTextProperty))
            End Get
            Set(value As String)
                SetValue(HintTextProperty, value)
            End Set
        End Property
        Public Property HasBackground As Boolean
            Get
                Return CBool(GetValue(HasBackgroundProperty))
            End Get
            Set(value As Boolean)
                SetValue(HasBackgroundProperty, value)
            End Set
        End Property
        Public Property ValidationMessage As String
            Get
                Return CStr(GetValue(ValidationMessageProperty))
            End Get
            Set(value As String)
                SetValue(ValidationMessageProperty, value)
            End Set
        End Property

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor, AddressOf RefreshColor)
            AddHandler Loaded, Sub() RefreshColor()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler GotKeyboardFocus, Sub() RefreshColor()
            AddHandler LostKeyboardFocus, Sub() RefreshColor()
        End Sub

        Private Sub RefreshColor()
            Dim ink = If(Not IsEnabled, "ColorBrushGray5", If(ValidationMessage.Length > 0, "ColorBrushRedLight", If(IsKeyboardFocusWithin, "ColorBrush3", If(IsMouseOver, "ColorBrush4", "ColorBrushBg0"))))
            Dim fill = If(Not IsEnabled, "ColorBrushGray6", If(ValidationMessage.Length > 0, "ColorBrushRedBack", If(IsKeyboardFocusWithin OrElse IsMouseOver, "ColorBrush7", "ColorBrushHalfWhite")))
            Dim duration = If(Not IsEnabled OrElse ValidationMessage.Length > 0, 200, If(IsKeyboardFocusWithin, 10, 100))
            If Not HasBackground Then fill = "ColorBrushTransparent"
            If IsLoaded Then
                ColorTo(Me, BorderBrushProperty, Brush(ink).Color, duration)
                ColorTo(Me, BackgroundProperty, Brush(fill).Color, duration)
            Else
                SetResourceReference(BorderBrushProperty, ink)
                SetResourceReference(BackgroundProperty, fill)
            End If
        End Sub
    End Class
End Namespace
