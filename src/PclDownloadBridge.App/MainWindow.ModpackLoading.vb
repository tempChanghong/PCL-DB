Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _packSearchLoading As PclLoadingPresentation
    Private _packDetailLoading As PclLoadingPresentation
    ' Offline UI previews are immediate by default; the timing regression suite
    ' explicitly enables the same request-owned controller used in the real window.
    Private _packLoadingTimingForChecks As Boolean

    Private Function CanTimePackLoading() As Boolean
        Return AllowPageMotion() AndAlso (Not _preview OrElse _packLoadingTimingForChecks)
    End Function
    Private Function PackEntryMotion(mode As PackBrowserMode) As PclSurfaceMotion
        If _surfaceMotion.Transition IsNot Nothing Then Return _surfaceMotion
        If _categoryMotion.Transition IsNot Nothing Then Return _categoryMotion
        Return If(mode = PackBrowserMode.Search, _packResultMotion, _packDetailMotion)
    End Function
    Private Sub PreparePackSearchLoading()
        ChangePackResults(Sub()
                              PackSearchHint.Visibility = Visibility.Collapsed : PackPagingCard.Visibility = Visibility.Collapsed
                              PackProjectRows.Children.Clear() : PackResultsCard.Visibility = Visibility.Collapsed
                          End Sub)
    End Sub
    Private Sub PreparePackDetailLoading()
        ChangePackContent(PackBrowserMode.Detail, Sub()
                                                     PackVersionCards.Children.Clear() : PackVersionFilters.Children.Clear()
                                                     PackVersionFilterCard.Visibility = Visibility.Collapsed
                                                     PackDetailHint.Visibility = Visibility.Collapsed : PackDetailRetry.Visibility = Visibility.Collapsed
                                                 End Sub)
    End Sub
End Class
