Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyClippedLazyRows(check As Action(Of Boolean, String))
        Dim created = 0, fresh = 0
        Dim body As New StackPanel()
        Dim factory As Func(Of PclListItem) = Function()
                                                    created += 1
                                                    Return New PclListItem With {.Title = "可见版本", .Height = 42}
                                                End Function
        For index = 0 To 99
            body.Children.Add(New PclLazyRow(factory))
        Next
        Dim clip As New Border With {.Height = 42, .ClipToBounds = True, .Child = body}
        Dim card As New PclCard With {.Title = "可见区域", .Collapsible = True, .BodyMargin = New Thickness(0, 40, 0, 0), .Body = clip}
        Dim viewer As New PclScrollViewer With {.Content = card}
        Dim host As New Border With {.Width = 500, .Height = 400, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(500, 400)) : host.Arrange(New Rect(0, 0, 500, 400))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 400)
            layout()
            check(created = 1 AndAlso TypeOf body.Children(0) Is PclListItem AndAlso TypeOf body.Children(1) Is PclLazyRow,
                "lazy rows inside the scroll viewport remain deferred when an intermediate ancestor clips them")
            clip.IsEnabled = False : clip.Height = 84 : layout()
            check(created = 2 AndAlso TypeOf body.Children(1) Is PclListItem,
                "exposing another clipped row initializes it even when ordinary visible content is disabled")
            clip.Height = 42 : clip.IsEnabled = True : layout()
            check(created = 2 AndAlso body.Children.OfType(Of PclListItem)().Count() = 2,
                "shrinking a clip retains visited rows without eagerly materializing the next boundary row")
            viewer.Content = Nothing : layout() : viewer.Content = card : layout()
            check(created = 2, "reattaching a clipped catalogue does not repeat initialized row factories")
            card.Collapsed = True
            Dim folding = card.HeightTransition : folding.Controller.Pause() : layout()
            card.RefreshBody(Function()
                                 Dim replacement As New StackPanel()
                                 For index = 0 To 1
                                     replacement.Children.Add(New PclLazyRow(Function()
                                                                                 fresh += 1
                                                                                 Return New PclListItem With {.Title = "新版本", .Height = 42}
                                                                             End Function))
                                 Next
                                 Return New Border With {.Height = 42, .ClipToBounds = True, .Child = replacement}
                             End Function)
            body.Children.Insert(0, New PclLazyRow(factory)) : layout()
            check(card.HasDeferredBodyRefresh AndAlso created = 2 AndAlso fresh = 0,
                "retiring catalogue rows cannot initialize stale controls while a replacement factory is deferred")
            card.Collapsed = False : FinishDeferredCard(card, layout) : layout()
            check(created = 2 AndAlso fresh = 1 AndAlso Not card.HasDeferredBodyRefresh,
                "reopening a refreshed catalogue initializes only its new visible row")
        End Using
    End Sub
End Class
