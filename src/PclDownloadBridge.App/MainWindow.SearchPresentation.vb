Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _searchClearMotion As PclMotionSlot
    Private _searchInputPolicy As PclControlMotionPolicy

    Private Sub InitializeSearchPresentation()
        _searchClearMotion = New PclMotionSlot(PackSearchClear, OpacityProperty)
        _searchInputPolicy = New PclControlMotionPolicy(PackSearchSurface, Sub() RefreshSearchClear(False))
        PclTheme.Watch(PackSearchSurface, Sub() RefreshSearchClear(False))
        AddHandler PackSearchText.TextChanged, Sub() RefreshSearchClear(True)
        RefreshSearchClear(False)
    End Sub

    Private Sub RefreshSearchClear(animate As Boolean)
        If _searchClearMotion Is Nothing Then Return
        Dim hasText = PackSearchText.Text.Length > 0
        ' The empty control is never a keyboard stop or a hit target, even during fade-out.
        PackSearchClear.IsEnabled = hasText
        PackSearchClear.IsHitTestVisible = hasText AndAlso _searchInputPolicy.Interactive
        Dim target = If(hasText, 1.0, 0.0)
        If _searchClearMotion.Clock Is Nothing AndAlso PackSearchClear.Opacity = target Then Return
        If animate AndAlso _searchInputPolicy.CanAnimate AndAlso Not PclTheme.Changing Then
            _searchClearMotion.Animate(New DoubleAnimation(target, TimeSpan.FromMilliseconds(90)), target, _searchInputPolicy.PauseForChecks)
        Else
            _searchClearMotion.SetValue(target)
        End If
    End Sub

    Private Sub ClearPackSearchText(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline OrElse _closing OrElse _packMode <> PackBrowserMode.Search OrElse
            Not _searchInputPolicy.Interactive OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible Then Return
        ' Only edit the form. Accepted queries, filters and results remain owned by the search session.
        PackSearchText.SetCurrentValue(TextBox.TextProperty, "")
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(PackSearchText), PackSearchText)
        PackSearchText.Focus()
    End Sub

    Private Function CanUsePackSearchInput(sender As Object) As Boolean
        Dim source = TryCast(sender, UIElement)
        Return Not _busy AndAlso Not _choosingOnline AndAlso Not _closing AndAlso _downloadView = DownloadView.Modpacks AndAlso
            _packMode = PackBrowserMode.Search AndAlso _searchInputPolicy.Interactive AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
            source IsNot Nothing AndAlso source.IsEnabled AndAlso source.IsVisible AndAlso source.IsHitTestVisible
    End Function

    Private Sub SubmitPackSearch(sender As Object, e As RoutedEventArgs)
        If CanUsePackSearchInput(sender) Then RunPackSearch(sender, e)
    End Sub

    Private Sub ResetPackSearchFromUi(sender As Object, e As RoutedEventArgs)
        If CanUsePackSearchInput(sender) Then ResetPackSearch(sender, e)
    End Sub
End Class
