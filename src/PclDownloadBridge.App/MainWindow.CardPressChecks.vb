Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    ' Card headers share the action controls' cancellation contract. Exercise
    ' dependency-property inheritance and actual page navigation before release.
    Private Shared Function ObserveCardPressLifecycle(directory As String) As JObject
        Dim card As New PclCard With {.Title = "卡片按压交接", .Collapsible = True, .Collapsed = True,
            .Body = New Border With {.Height = 180}}
        Dim header = card.Children.OfType(Of Grid)().Single()
        Dim offset As New TranslateTransform() : card.RenderTransform = offset
        Dim container As New StackPanel() : container.Children.Add(card)
        Dim pages As New Grid(), versions As New Border()
        pages.Children.Add(versions) : pages.Children.Add(container)
        Dim host As New Border With {.Width = 500, .Height = 400, .Child = pages}
        Dim navigation As New PclPageSwitch(host, versions, container, Function() True)
        navigation.SelectInstall(True, False)
        Dim layout As Action = Sub()
                                   host.Measure(New Size(500, 400)) : host.Arrange(New Rect(0, 0, 500, 400))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim pressed As Func(Of Boolean) = Function() CBool(GetType(PclCard).GetField("_headerPressed", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).GetValue(card))
        Dim folds = 0
        AddHandler card.FoldChanged, Sub() folds += 1
        Dim result As New JObject From {{"gate", True}, {"hidden", True}, {"detached", True}}
        Dim cases As New JArray()
        Using source = PreviewSurface.Attach(host, 500, 400)
            layout()
            For Each interruption In {"page-switch-reversal", "ancestor-gate", "header-gate", "disabled", "noncollapsible", "hidden", "header-hidden", "detached", "pointer-leave"}
                card.Collapsed = True : FinishDeferredCard(card, layout) : folds = 0
                ' The hidden HWND lives at -32000. Translate this isolated fixture
                ' to place the synthetic pointer 20 DIP into its header, without
                ' moving the desktop cursor or bypassing the production hit area.
                offset.Y += Mouse.GetPosition(header).Y - 20 : layout()
                PointerEvent(header, UIElement.MouseLeftButtonDownEvent)
                Dim started = pressed()
                Select Case interruption
                    Case "page-switch-reversal"
                        navigation.SelectInstall(False, True)
                        navigation.Transition.Controller.Pause()
                    Case "ancestor-gate" : container.IsHitTestVisible = False
                    Case "header-gate" : header.IsHitTestVisible = False
                    Case "disabled" : container.IsEnabled = False
                    Case "noncollapsible" : card.Collapsible = False
                    Case "hidden" : container.Visibility = Visibility.Collapsed
                    Case "header-hidden" : card.ShowHeader = False
                    Case "detached" : container.Children.Remove(card)
                    Case "pointer-leave" : LeavePointer(header)
                End Select
                layout()
                Dim cancelled = Not pressed()
                Select Case interruption
                    Case "page-switch-reversal"
                        navigation.SelectInstall(True, True) : navigation.Finish()
                    Case "ancestor-gate" : container.IsHitTestVisible = True
                    Case "header-gate" : header.IsHitTestVisible = True
                    Case "disabled" : container.IsEnabled = True
                    Case "noncollapsible" : card.Collapsible = True
                    Case "hidden" : container.Visibility = Visibility.Visible
                    Case "header-hidden" : card.ShowHeader = True
                    Case "detached" : container.Children.Add(card)
                End Select
                layout() : PointerEvent(header, UIElement.MouseLeftButtonUpEvent)
                Dim ignoredRelease = card.Collapsed AndAlso folds = 0
                Dim staleFolds = folds
                card.Collapsed = True : FinishDeferredCard(card, layout) : folds = 0
                PointerEvent(header, UIElement.MouseLeftButtonDownEvent) : PointerEvent(header, UIElement.MouseLeftButtonUpEvent)
                Dim freshClick = Not card.Collapsed AndAlso folds = 1
                FinishDeferredCard(card, layout)
                PointerEvent(header, UIElement.MouseLeftButtonUpEvent)
                Dim singleClick = Not card.Collapsed AndAlso folds = 1
                cases.Add(New JObject From {{"case", interruption}, {"headerPointerY", Mouse.GetPosition(header).Y}, {"pressStarted", started}, {"pressCancelled", cancelled},
                    {"staleReleaseIgnored", ignoredRelease}, {"staleFolds", staleFolds}, {"freshClickWorks", freshClick}, {"singleClick", singleClick}})
                Dim group = If(interruption.Contains("hidden"), "hidden", If(interruption = "detached", "detached", "gate"))
                result(group) = CBool(result(group)) AndAlso started AndAlso cancelled AndAlso ignoredRelease AndAlso freshClick AndAlso singleClick
            Next
        End Using
        result("cases") = cases
        IO.File.WriteAllText(IO.Path.Combine(directory, "card-press-lifecycle.json"), result.ToString())
        Return result
    End Function
End Class
