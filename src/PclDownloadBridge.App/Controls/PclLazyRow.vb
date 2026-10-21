Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Input

Namespace Controls
    ' PCL MyVirtualizingElement + LazyLoadBehavior: replace a fixed-height placeholder
    ' on its first entrance into the enclosing ScrollViewer's viewport; do not recycle it.
    Friend NotInheritable Class PclLazyRow
        Inherits FrameworkElement

        Private _initializer As Func(Of PclListItem)
        Private _row As PclListItem
        Private _watching As Boolean

        Public Sub New(initializer As Func(Of PclListItem))
            If initializer Is Nothing Then Throw New ArgumentNullException(NameOf(initializer))
            _initializer = initializer
            Height = 42
            Focusable = True
            WatchLayout()
            AddHandler Loaded, Sub() If _row Is Nothing Then WatchLayout()
            AddHandler Unloaded, Sub() StopWatching()
        End Sub

        Private Sub WatchLayout()
            If _watching Then Return
            AddHandler LayoutUpdated, AddressOf CheckViewport
            _watching = True
        End Sub

        Private Sub StopWatching()
            If Not _watching Then Return
            RemoveHandler LayoutUpdated, AddressOf CheckViewport
            _watching = False
        End Sub

        Private Sub CheckViewport(sender As Object, e As EventArgs)
            If Not IsVisible OrElse RenderSize.Width <= 0 OrElse Parent Is Nothing Then Return
            Dim ancestor = VisualTreeHelper.GetParent(Me)
            While ancestor IsNot Nothing AndAlso Not TypeOf ancestor Is ScrollViewer
                Dim card = TryCast(ancestor, PclCard)
                If card IsNot Nothing AndAlso card.HasDeferredBodyRefresh Then Return
                ancestor = VisualTreeHelper.GetParent(ancestor)
            End While
            Dim scroll = TryCast(ancestor, ScrollViewer)
            If scroll Is Nothing OrElse scroll.ViewportWidth <= 0 OrElse scroll.ViewportHeight <= 0 Then Return
            Dim bounds = TransformToAncestor(scroll).TransformBounds(New Rect(New Point(), RenderSize))
            If Not New Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(bounds) Then Return
            ' Keep PCL's first-entry behavior, but respect the card's animated clip
            ' as well as the scroll viewport. A touching edge has no visible pixels.
            If Not PclExitSnapshot.VisibleBounds(Me, scroll).IsEmpty Then Materialize()
        End Sub

        Friend Function Materialize() As PclListItem
            Dim transferFocus = IsKeyboardFocused
            Dim panel = TryCast(Parent, Panel)
            If Parent IsNot Nothing AndAlso panel Is Nothing Then Throw New InvalidOperationException("延迟版本行的父级必须为 Panel。")
            If _row Is Nothing Then
                Dim row = _initializer.Invoke()
                If row Is Nothing Then Throw New InvalidOperationException("版本行生成器未返回控件。")
                _row = row
                _initializer = Nothing
            End If
            If panel IsNot Nothing Then
                Dim index = panel.Children.IndexOf(Me)
                panel.Children.RemoveAt(index)
                panel.Children.Insert(index, _row)
            End If
            StopWatching()
            If transferFocus Then
                panel?.UpdateLayout()
                _row.Focus()
            End If
            Return _row
        End Function
        Protected Overrides Sub OnGotKeyboardFocus(e As KeyboardFocusChangedEventArgs)
            MyBase.OnGotKeyboardFocus(e)
            If e.NewFocus IsNot Me Then Return
            Dim row = Materialize()
            TryCast(row.Parent, UIElement)?.UpdateLayout()
            row.BringIntoView()
            row.Focus()
        End Sub
    End Class
End Namespace
