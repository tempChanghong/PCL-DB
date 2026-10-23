Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _versionLoading As PclLoadingPresentation
    Private _versionLoadingTimingForChecks As Boolean

    Private Sub InitializeVersionLoading()
        _versionLoading = New PclLoadingPresentation(VersionPage,
            Function() AllowPageMotion() AndAlso (Not _preview OrElse _versionLoadingTimingForChecks),
            Function() If(_categoryMotion.Transition IsNot Nothing, _categoryMotion, _surfaceMotion))
    End Sub

    Private Sub ClearVersionLoadingVisual()
        VersionLoadingCard.Visibility = Visibility.Collapsed
        VersionLoadingCard.Body = Nothing
    End Sub

    Private Sub PrepareVersionLoading()
        ChangeVersionContent(Sub()
                                 ClearVersionLoadingVisual()
                                 EmptyVersions.Visibility = Visibility.Collapsed
                             End Sub)
    End Sub

    Private Sub ShowVersionLoading()
        ChangeVersionContent(Sub()
                                 EmptyVersions.Visibility = Visibility.Collapsed
                                 VersionLoadingCard.Body = New PclLoadingView("正在加载")
                                 VersionLoadingCard.Visibility = Visibility.Visible
                             End Sub)
    End Sub

    Private Function CurrentVersionRequest(generation As Integer, request As System.Threading.CancellationTokenSource) As Boolean
        Return Not _closing AndAlso generation = _versionGeneration AndAlso _versionCancellation Is request
    End Function
End Class
