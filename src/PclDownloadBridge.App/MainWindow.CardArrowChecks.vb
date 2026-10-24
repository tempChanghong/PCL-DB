Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyCardArrowMotion(directory As String, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson()
        Dim card As New PclCard With {.Title = "版本列表", .Collapsible = True, .Body = New Border With {.Height = 140}}
        Dim arrow = card.Children.OfType(Of Grid)().Single().Children.OfType(Of Shapes.Path)().Single()
        Dim rotation = DirectCast(arrow.RenderTransform, RotateTransform)
        Dim host As New Border With {.Width = 420, .Height = 240, .Padding = New Thickness(20), .Child = card}, layout = ControlLayout(host)
        Try
            Using surface = PreviewSurface.Attach(host, 420, 240)
                layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "card-arrow-initial.json"), New JObject From {{"collapsed", card.Collapsed}, {"angle", rotation.Angle}, {"animated", rotation.HasAnimatedProperties}}.ToString())
                check(Not card.Collapsed AndAlso rotation.Angle = 180 AndAlso Not rotation.HasAnimatedProperties, "a default expanded card starts with PCL's upward arrow before its first fold")
                card.PauseArrowForChecks = True
                For Each dark In {False, True}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    For Each arrowRight In {False, True}
                        card.Visibility = Visibility.Hidden : card.Collapsed = True : card.SwapLogoRight = arrowRight : card.Visibility = Visibility.Visible : layout()
                        Dim label = "/dark=" & dark & "/right=" & arrowRight, endpoint = If(arrowRight, 270.0, 0.0), body = card.Body
                        check(rotation.Angle = endpoint AndAlso card.ArrowTransition Is Nothing AndAlso Not rotation.HasAnimatedProperties,
                            "initially collapsed cards preserve PCL's downward or rightward arrow" & label)
                        card.Collapsed = False
                        Dim opening = card.ArrowTransition, height = card.HeightTransition
                        SeekControl(height, 50)
                        Dim animation = DirectCast(opening.Timeline, Animation.DoubleAnimation)
                        check(animation.Duration.TimeSpan.TotalMilliseconds = 250 AndAlso Math.Abs(animation.EasingFunction.Ease(0.5) - 0.96875) < 0.000001,
                            "real card arrows retain PCL's 250ms ExtraStrong rotation" & label)
                        SeekControl(opening, 100)
                        Dim expected = endpoint + (180 - endpoint) * (1 - Math.Pow(1 - 100 / 250.0, 5))
                        check(Math.Abs(rotation.Angle - expected) < 0.000001 AndAlso card.Body Is body AndAlso card.HeightTransition Is height,
                            "card rotation follows the source curve while preserving the separate height clock and body" & label)
                        card.Collapsed = False : card.SwapLogoRight = arrowRight
                        check(card.ArrowTransition Is opening AndAlso card.HeightTransition Is height AndAlso Math.Abs(rotation.Angle - expected) < 0.000001,
                            "reapplying accepted fold and arrow orientation does not interrupt a running rotation" & label)
                        Dim displayed = rotation.Angle
                        card.Collapsed = True
                        Dim closing = card.ArrowTransition, closingHeight = card.HeightTransition
                        check(closing IsNot opening AndAlso closingHeight IsNot height AndAlso Math.Abs(rotation.Angle - displayed) < 0.000001,
                            "rapid fold reversal starts new independent arrow and height owners at their displayed values" & label)
                        opening.Controller.SkipToFill() : layout()
                        check(card.ArrowTransition Is closing AndAlso card.HeightTransition Is closingHeight AndAlso Math.Abs(rotation.Angle - displayed) < 0.000001,
                            "a replaced rotation cannot finish its newer fold or overwrite its angle" & label)
                        closing.Controller.Resume()
                        Dim settled = PumpMotionUntil(Function() card.ArrowTransition Is Nothing AndAlso card.HeightTransition Is Nothing, timeoutMilliseconds:=1000)
                        check(settled AndAlso rotation.Angle = endpoint AndAlso Not rotation.HasAnimatedProperties AndAlso card.Height = 40 AndAlso card.Body Is body,
                            "natural fold completion clears the arrow clock and reaches the original collapsed endpoint" & label)
                        SaveControlVisual(host, IO.Path.Combine(directory, "card-arrow-" & If(dark, "dark", "light") & If(arrowRight, "-right", "-down") & ".png"))
                    Next
                Next
                For Each boundary In {"hide", "unload", "orientation"}
                    card.Collapsed = False
                    Dim retired = card.ArrowTransition, body = card.Body
                    SeekControl(retired, 45)
                    Select Case boundary
                        Case "hide" : card.Visibility = Visibility.Hidden
                        Case "unload" : host.Child = Nothing
                        Case "orientation" : card.SwapLogoRight = Not card.SwapLogoRight
                    End Select
                    layout()
                    check(card.ArrowTransition Is Nothing AndAlso Not rotation.HasAnimatedProperties AndAlso rotation.Angle = 180,
                        "interrupted card rotation settles its accepted expanded state: " & boundary)
                    card.Visibility = Visibility.Visible : host.Child = card : layout() : retired.Controller.SkipToFill() : layout()
                    check(card.ArrowTransition Is Nothing AndAlso rotation.Angle = 180 AndAlso Not card.Collapsed AndAlso card.Body Is body,
                        "a retired rotation stays inert after its card returns: " & boundary)
                    card.Collapsed = True
                    Dim fresh = card.ArrowTransition
                    check(fresh IsNot Nothing AndAlso fresh IsNot retired, "restored cards accept a fresh fold rotation: " & boundary)
                    fresh.Controller.Resume()
                    If Not PumpMotionUntil(Function() card.ArrowTransition Is Nothing AndAlso card.HeightTransition Is Nothing, timeoutMilliseconds:=1000) Then Throw New InvalidOperationException("Restored card did not finish folding.")
                Next
            End Using
        Finally
            host.Child = Nothing : card.PauseArrowForChecks = False
            RestoreAppearance(appearance)
        End Try
    End Sub

    Private Sub VerifyDetailCardArrow(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim motion = _motionPreview, paused = _categoryMotion.PauseForChecks, appearance = _appearance.ToJson()
        Dim card = PackVersionCards.Children.OfType(Of PclCard)().First(), body = card.Body
        Dim header = card.Children.OfType(Of Grid)().Single(), rotation = DirectCast(header.Children.OfType(Of Shapes.Path)().Single().RenderTransform, RotateTransform)
        Dim row = DirectCast(body, StackPanel).Children(0), project = _packProject, session = _packSearchSession, generation = _taskGeneration, cancellation = _cancellation
        Try
            _motionPreview = False : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
            card.PauseArrowForChecks = True
            ControlKey(header, Key.Enter, True)
            Dim closing = card.ArrowTransition
            SeekControl(card.HeightTransition, 50) : SeekControl(closing, 65)
            check(card.Collapsed AndAlso closing IsNot Nothing AndAlso card.HeightTransition IsNot Nothing,
                "the real detail header Enter owns both a card fold and its separate arrow rotation")
            _motionPreview = True : _categoryMotion.PauseForChecks = True
            BackToPackSearch(Nothing, Nothing) : layout()
            Dim exiting = _categoryMotion.Transition
            check(exiting IsNot Nothing AndAlso _packMode = PackBrowserMode.Search AndAlso card.ArrowTransition Is Nothing AndAlso Not rotation.HasAnimatedProperties AndAlso
                rotation.Angle = If(card.SwapLogoRight, 270, 0) AndAlso card.Body Is body,
                "returning to search releases the hidden detail arrow while retaining its collapsed card and rows")
            PauseSelectionClock(exiting) : exiting.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(175), Animation.TimeSeekOrigin.BeginTime) : layout()
            Dim searchRow = PackProjectRows.Children.OfType(Of PclResourceItem)().First()
            ControlKey(searchRow, Key.Enter, True) : ControlKey(searchRow, Key.Enter, False) : layout()
            Dim returning = _categoryMotion.Transition
            closing.Controller.SkipToFill() : layout()
            check(returning IsNot Nothing AndAlso returning IsNot exiting AndAlso _categoryMotion.Transition Is returning AndAlso Not DownloadViewHost.IsHitTestVisible AndAlso
                PackVersionCards.Children(0) Is card AndAlso card.Body Is body AndAlso DirectCast(body, StackPanel).Children(0) Is row,
                "rapid detail return keeps one page owner and its cached card despite obsolete arrow callbacks")
            _categoryMotion.Finish() : layout()
            ControlKey(header, Key.Enter, True)
            Dim opening = card.ArrowTransition
            SeekControl(card.HeightTransition, 50) : SeekControl(opening, 125)
            check(Not card.Collapsed AndAlso opening IsNot Nothing AndAlso _packDetailRequest Is Nothing AndAlso _packProject Is project AndAlso card.Body Is body,
                "the returned header expands its accepted cached versions without starting a detail request")
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
            check(card.ArrowTransition Is opening AndAlso _packSearchSession Is session AndAlso _taskGeneration = generation,
                "theme refresh updates detail colors without interrupting its accepted card rotation or search identity")
            SaveControlVisual(host, IO.Path.Combine(directory, "card-arrow-detail.png"))
            opening.Controller.Resume() : card.HeightTransition.Controller.Resume()
            Dim settled = PumpMotionUntil(Function() card.ArrowTransition Is Nothing AndAlso card.HeightTransition Is Nothing, timeoutMilliseconds:=1000)
            check(settled AndAlso rotation.Angle = 180 AndAlso Not rotation.HasAnimatedProperties AndAlso card.Body Is body AndAlso _packProject Is project AndAlso
                _taskGeneration = generation AndAlso _cancellation Is cancellation AndAlso Not _busy AndAlso Not _choosingOnline,
                "detail fold completion releases its arrow while preserving project, rows and installation cancellation ownership")
        Finally
            card.PauseArrowForChecks = False
            _motionPreview = False : _categoryMotion.Finish() : FinishPackContentMotion()
            card.ArrowTransition?.Controller.Resume()
            RestoreAppearance(appearance)
            _motionPreview = motion : _categoryMotion.PauseForChecks = paused
        End Try
    End Sub
End Class
