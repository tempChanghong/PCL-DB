Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _packDetailMotion As PclSurfaceMotion

    Private Function CanAnimatePackContent(mode As PackBrowserMode) As Boolean
        Return AllowPageMotion() AndAlso _downloadView = DownloadView.Modpacks AndAlso _packMode = mode AndAlso DownloadPage.IsVisible AndAlso _categoryMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing
    End Function
    Private Function HasPackContentMotion() As Boolean
        Return (_packResultMotion IsNot Nothing AndAlso _packResultMotion.Transition IsNot Nothing) OrElse (_packDetailMotion IsNot Nothing AndAlso _packDetailMotion.Transition IsNot Nothing)
    End Function
    Private Sub FinishPackContentMotion()
        _packResultMotion?.Finish() : _packDetailMotion?.Finish()
    End Sub

    ' PCL MyPageRight.PageLoaderState enters the accepted content after loading.
    ' An existing parent entry must capture its composited frame before replacing
    ' detached children; hidden responses only update their own cached controls.
    Private Sub ChangePackContent(mode As PackBrowserMode, update As Action)
        ' Cached responses may finish synchronously inside the parent's content
        ' transaction, before its entry clock exists. Let that parent collect the
        ' final cards; two controllers must never save each other's opacity zero.
        If _surfaceMotion.ApplyingContent OrElse _categoryMotion.ApplyingContent OrElse _packResultMotion.ApplyingContent OrElse _packDetailMotion.ApplyingContent Then
            FinishPackContentMotion() : update() : Return
        End If
        If _packMode <> mode OrElse _downloadView <> DownloadView.Modpacks OrElse Not DownloadPage.IsVisible Then
            update() : Return
        End If
        Dim replace As Action = Sub()
                                    FinishPackContentMotion()
                                    update()
                                End Sub
        If _surfaceMotion.Transition IsNot Nothing Then
            _surfaceMotion.Change(DownloadPage, DownloadPage, Sub()
                                                                _categoryMotion.Finish()
                                                                replace()
                                                            End Sub, contentRefresh:=True)
            If _surfaceMotion.Transition Is Nothing Then RestoreSurfaceFocus()
        ElseIf _categoryMotion.Transition IsNot Nothing Then
            _categoryMotion.Change(ModpackPage, ModpackPage, replace, contentRefresh:=True)
        ElseIf mode = PackBrowserMode.Search Then
            _packResultMotion.Change(PackSearchResultsSurface, PackSearchResultsSurface, update, contentRefresh:=True)
        Else
            _packDetailMotion.Change(PackDetailResultsSurface, PackDetailResultsSurface, update, contentRefresh:=True)
        End If
    End Sub
End Class
