Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyCardGestureOwnership(directory As String, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson(), observations As New JArray
        Dim body As New Border With {.Height = 600}
        Dim card As New PclCard With {.Title = "版本列表", .Collapsible = True, .Collapsed = True, .Body = body}
        Dim header = card.Children.OfType(Of Grid)().Single(), peer As New Button With {.Content = "焦点目标"}
        Dim offset As New TranslateTransform() : card.RenderTransform = offset
        Dim panel As New StackPanel() : panel.Children.Add(peer) : panel.Children.Add(card)
        Dim host As New Border With {.Width = 500, .Height = 400, .Child = panel}, layout = ControlLayout(host)
        Dim pressed As Func(Of Boolean) = Function() CBool(GetType(PclCard).GetField("_headerPressed", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).GetValue(card))
        Dim folds = 0 : AddHandler card.FoldChanged, Sub() folds += 1
        Try
            Using surface = PreviewSurface.Attach(host, 500, 400)
                layout()
                For Each dark In {False, True}
                    For Each interruption In {"focus", "capture", "theme", "programmatic-fold", "keyboard-fold"}
                        RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                        card.Collapsed = True : FinishDeferredCard(card, layout)
                        card.Collapsed = False
                        Dim opening = card.HeightTransition : opening.Controller.Pause() : layout()
                        opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40), Animation.TimeSeekOrigin.BeginTime) : layout()
                        offset.Y += Mouse.GetPosition(header).Y - 20 : layout()
                        PointerEvent(header, UIElement.MouseLeftButtonDownEvent)
                        Dim started = pressed()
                        Select Case interruption
                            Case "focus"
                                header.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, header, peer) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                            Case "capture"
                                header.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                            Case "theme"
                                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not dark}})
                            Case "programmatic-fold"
                                card.Collapsed = True
                            Case "keyboard-fold"
                                ControlKey(header, Key.Enter, True)
                        End Select
                        Dim owned = card.HeightTransition, expected = card.Collapsed, before = folds
                        owned.Controller.Pause() : layout()
                        Dim cancelled = Not pressed()
                        PointerEvent(header, UIElement.MouseLeftButtonUpEvent) : layout()
                        Dim retained = card.Collapsed = expected AndAlso folds = before AndAlso card.HeightTransition Is owned AndAlso card.Body Is body
                        observations.Add(New JObject From {{"dark", dark}, {"interruption", interruption}, {"started", started}, {"cancelled", cancelled}, {"retainedFoldAndClock", retained}})
                        IO.File.WriteAllText(IO.Path.Combine(directory, "card-gesture-states.json"), observations.ToString())
                        check(started AndAlso cancelled, "card header cancels its pending press after " & interruption & "/dark=" & dark)
                        check(retained, "abandoned card release preserves its fold, content and running height clock after " & interruption & "/dark=" & dark)
                        PointerEvent(header, UIElement.MouseLeftButtonDownEvent) : PointerEvent(header, UIElement.MouseLeftButtonUpEvent)
                        Dim fresh = card.HeightTransition : fresh.Controller.Pause() : layout()
                        owned.Controller.SkipToFill() : layout()
                        check(card.Collapsed <> expected AndAlso folds = before + 1 AndAlso card.HeightTransition Is fresh AndAlso card.Body Is body AndAlso
                            DirectCast(card.Children.OfType(Of Grid)().Single().Children.OfType(Of Shapes.Path)().Single().RenderTransform, RotateTransform).HasAnimatedProperties,
                            "a fresh card gesture owns one fold and its height/arrow animations after " & interruption & "/dark=" & dark)
                        FinishDeferredCard(card, layout)
                    Next
                Next
            End Using
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub

    Private Sub VerifyDetailCardGestureOwnership(directory As String, layout As Action, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson(), generation = _taskGeneration, session = _packSearchSession, project = _packProject
        Dim all = DirectCast(PackVersionFilters.Children(0), PclRadioButton)
        Dim filter = PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) button.Text = "1.20")
        SelectPackVersionFilter(filter) : layout()
        Dim card = DirectCast(PackVersionCards.Children(0), PclCard), body = DirectCast(card.Body, StackPanel)
        Dim release = DirectCast(DirectCast(body.Children(0), PclListItem).Tag, ModpackRelease)
        Dim header = card.Children.OfType(Of Grid)().Single(), transform = card.RenderTransform
        Dim offset As New TranslateTransform() : card.RenderTransform = offset
        Dim observations As New JArray
        Try
            For Each dark In {False, True}
                For Each interruption In {"focus", "capture", "theme"}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    card.Collapsed = True : FinishDeferredCard(card, layout)
                    card.Collapsed = False
                    Dim opening = card.HeightTransition : opening.Controller.Pause() : layout()
                    opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40), Animation.TimeSeekOrigin.BeginTime) : layout()
                    offset.Y += Mouse.GetPosition(header).Y - 20 : layout()
                    PointerEvent(header, UIElement.MouseLeftButtonDownEvent)
                    Dim started = CBool(GetType(PclCard).GetField("_headerPressed", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).GetValue(card))
                    Select Case interruption
                        Case "focus"
                            header.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, header, filter) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                        Case "capture"
                            header.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                        Case "theme"
                            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not dark}})
                    End Select
                    PointerEvent(header, UIElement.MouseLeftButtonUpEvent) : layout()
                    Dim retained = Not card.Collapsed AndAlso card.HeightTransition Is opening AndAlso card.Body Is body
                    check(started AndAlso retained AndAlso _packGameFilter = "1.20" AndAlso filter.Checked AndAlso Not all.Checked,
                        "the real detail card retains its filter, rows and opening after an abandoned header press: " & interruption & "/dark=" & dark)
                    PointerEvent(header, UIElement.MouseLeftButtonDownEvent) : PointerEvent(header, UIElement.MouseLeftButtonUpEvent)
                    Dim closing = card.HeightTransition : closing?.Controller.Pause() : layout()
                    opening.Controller.SkipToFill() : layout()
                    check(card.Collapsed AndAlso closing IsNot Nothing AndAlso card.HeightTransition Is closing AndAlso card.Body Is body AndAlso DirectCast(body.Children(0), PclListItem).Tag Is release AndAlso
                        _packSearchSession Is session AndAlso _packProject Is project AndAlso _taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline,
                        "a fresh detail header gesture folds once while preserving archive and installation ownership: " & interruption & "/dark=" & dark)
                    observations.Add(New JObject From {{"dark", dark}, {"interruption", interruption}, {"retainedOpening", retained}, {"releaseId", release.Id}, {"taskUnchanged", _taskGeneration = generation}})
                    FinishDeferredCard(card, layout)
                Next
            Next
            IO.File.WriteAllText(IO.Path.Combine(directory, "detail-card-gesture-states.json"), observations.ToString())
        Finally
            card.RenderTransform = transform
            card.Collapsed = False : FinishDeferredCard(card, layout)
            RestoreAppearance(appearance)
            SelectPackVersionFilter(all) : layout()
        End Try
    End Sub
End Class
