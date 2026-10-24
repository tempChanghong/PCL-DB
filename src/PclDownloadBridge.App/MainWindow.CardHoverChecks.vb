Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyCardHoverMotion(directory As String, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson()
        Dim card As New PclCard With {.Title = "版本列表", .Collapsible = True, .Body = New Border With {.Height = 110}}
        Dim header = card.Children.OfType(Of Grid)().Single(), title = header.Children.OfType(Of TextBlock)().Single(), arrow = header.Children.OfType(Of Shapes.Path)().Single()
        Dim shadow = card.Children.OfType(Of PclDropShadow)().Single()
        Dim host As New Border With {.Width = 400, .Height = 220, .Padding = New Thickness(20), .Child = card}, layout = ControlLayout(host)
        Dim clean As Func(Of Boolean) = Function() card.HoverTransitions.All(Function(clock) clock Is Nothing) AndAlso Not shadow.HasAnimatedProperties AndAlso
            Not DirectCast(title.Foreground, SolidColorBrush).HasAnimatedProperties AndAlso Not DirectCast(arrow.Fill, SolidColorBrush).HasAnimatedProperties
        Dim finish As Action = Sub()
                                   For Each clock In card.HoverTransitions.Where(Function(item) item IsNot Nothing) : clock.Controller.Resume() : Next
                                   If Not PumpMotionUntil(clean, timeoutMilliseconds:=1000) Then Throw New InvalidOperationException("Card hover did not release its completed clocks.")
                                   layout()
                               End Sub
        Try
            Using surface = PreviewSurface.Attach(host, 400, 220)
                layout() : card.HoverMotionPolicy.EnabledForChecks = True : card.HoverMotionPolicy.PauseForChecks = True : ControlPointer(card, True)
                check(shadow.HasAnimatedProperties AndAlso DirectCast(title.Foreground, SolidColorBrush).HasAnimatedProperties, "the card hover regression starts real title and shadow animations")
                card.IsEnabled = False : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "card-hover-disabled.json"), New JObject From {{"clean", clean()}, {"shadowAnimated", shadow.HasAnimatedProperties}, {"opacity", shadow.Opacity}}.ToString())
                check(clean(), "disabling a hovered card releases its title, arrow and shadow clocks")
                For Each dark In {False, True}
                    card.IsEnabled = True : ControlPointer(card, False)
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    Dim normal = PclAnimation.Brush("ColorBrush1").Color, accent = PclAnimation.Brush("ColorBrush2").Color
                    Dim idleShadow = DirectCast(Application.Current.FindResource("ColorObject1"), Color), activeShadow = DirectCast(Application.Current.FindResource("ColorObject4"), Color)
                    ControlPointer(card, True)
                    Dim entering = card.HoverTransitions
                    check(entering.All(Function(clock) clock IsNot Nothing AndAlso clock.Timeline.Duration.TimeSpan.TotalMilliseconds = 90), "card title, arrow and shadow retain PCL's shared 90ms hover timing: " & dark)
                    For Each clock In entering : SeekControl(clock, 45) : Next
                    check(CardHoverMidpoint(DirectCast(title.Foreground, SolidColorBrush).Color, normal, accent) AndAlso CardHoverMidpoint(DirectCast(arrow.Fill, SolidColorBrush).Color, normal, accent) AndAlso
                        CardHoverMidpoint(shadow.Color, idleShadow, activeShadow) AndAlso Math.Abs(shadow.Opacity - 0.235) < 0.00001,
                        "card hover samples PCL text, independent shadow color and opacity without changing fold height: " & dark)
                    SaveControlVisual(host, IO.Path.Combine(directory, "card-hover-" & If(dark, "dark", "light") & ".png"))
                    ControlPointer(card, True)
                    check(card.HoverTransitions.SequenceEqual(entering), "repeated card hover retains its accepted clocks instead of restarting the fade: " & dark)
                    Dim displayed = shadow.Color, opacity = shadow.Opacity
                    ControlPointer(card, False) : layout()
                    Dim leaving = card.HoverTransitions
                    check(shadow.Color = displayed AndAlso Math.Abs(shadow.Opacity - opacity) < 0.00001, "reversed card hover begins at its displayed color and opacity: " & dark)
                    For Each clock In entering : clock.Controller.SkipToFill() : Next
                    layout()
                    check(card.HoverTransitions.SequenceEqual(leaving), "obsolete card hover completions cannot replace the newer return clocks: " & dark)
                    finish()
                    check(clean(), "completed card fades release all four clocks and privately owned brush animations: " & dark)
                    check(shadow.Color = idleShadow AndAlso shadow.Opacity = 0.07 AndAlso DirectCast(title.Foreground, SolidColorBrush).Color = normal,
                        "idle card shadow uses PCL ColorObject1 rather than the adapted dark text color: " & dark)
                    ControlPointer(card, True) : RestoreAppearance(New JObject From {{"theme", "deepblue"}, {"darkMode", Not dark}}) : layout()
                    check(clean() AndAlso shadow.Color = DirectCast(Application.Current.FindResource("ColorObject4"), Color) AndAlso shadow.Opacity = 0.4,
                        "a palette change settles the current card highlight without retaining old-theme clocks: " & dark)
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    For Each boundary In {"enabled", "hit-test", "visibility", "unload", "preference"}
                        ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True)
                        Dim retired = card.HoverTransitions
                        Select Case boundary
                            Case "enabled" : card.IsEnabled = False
                            Case "hit-test" : card.IsHitTestVisible = False
                            Case "visibility" : card.Visibility = Visibility.Hidden
                            Case "unload" : host.Child = Nothing
                            Case "preference" : card.HoverMotionPolicy.EnabledForChecks = False
                        End Select
                        layout()
                        check(clean() AndAlso shadow.Opacity = If(boundary = "preference", 0.4, 0.07), "card hover settles immediately across lifecycle changes: " & boundary & "/dark=" & dark)
                        card.IsEnabled = True : card.IsHitTestVisible = True : card.Visibility = Visibility.Visible : host.Child = card : layout()
                        ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True)
                        Dim recovered = card.HoverTransitions
                        For Each clock In retired : clock.Controller.SkipToFill() : Next
                        layout()
                        check(recovered.All(Function(clock) clock IsNot Nothing) AndAlso card.HoverTransitions.SequenceEqual(recovered), "a fresh card hover survives obsolete lifecycle callbacks: " & boundary & "/dark=" & dark)
                        finish()
                    Next
                Next
            End Using
        Finally
            host.Child = Nothing : card.HoverMotionPolicy.EnabledForChecks = Nothing
            RestoreAppearance(appearance)
        End Try
    End Sub

    Private Shared Function CardHoverMidpoint(actual As Color, origin As Color, target As Color) As Boolean
        Return Math.Abs(actual.A - (CInt(origin.A) + target.A) / 2.0) <= 1 AndAlso Math.Abs(actual.R - (CInt(origin.R) + target.R) / 2.0) <= 1 AndAlso
            Math.Abs(actual.G - (CInt(origin.G) + target.G) / 2.0) <= 1 AndAlso Math.Abs(actual.B - (CInt(origin.B) + target.B) / 2.0) <= 1
    End Function

    Private Async Function VerifyDetailCardHoverAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim factory = _packCatalogFactory, motion = _motionPreview, paused = _surfaceMotion.PauseForChecks, calls = 0
        Dim generation = _taskGeneration, cancellation = _cancellation, session = _packSearchSession
        Dim card As PclCard = Nothing
        Try
            _motionPreview = False
            ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : ChangePackMode(PackBrowserMode.Search)
            _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Function(uri, token)
                                                                                                                              Interlocked.Increment(calls) : Return Task.CompletedTask
                                                                                                                          End Function}, "")
            OpenPackDetails(DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().First().Tag, ModpackProject))
            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
            card = PackVersionCards.Children.OfType(Of PclCard)().First()
            Dim project = _packProject, body = card.Body, row = DirectCast(body, StackPanel).Children(0), fetched = calls
            card.HoverMotionPolicy.EnabledForChecks = True : card.HoverMotionPolicy.PauseForChecks = True
            ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True)
            check(card.HoverTransitions.All(Function(clock) clock IsNot Nothing), "the real detail version card starts all PCL hover channels without changing its cached rows")
            PackDetailResultsHost.IsHitTestVisible = False : layout()
            check(card.HoverTransitions.All(Function(clock) clock Is Nothing) AndAlso _packSearchSession Is session AndAlso _packProject Is project AndAlso _taskGeneration = generation,
                "detail-result input gating settles card hover without changing search, project or installation ownership")
            PackDetailResultsHost.IsHitTestVisible = True : ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True)
            Dim retired = card.HoverTransitions
            check(retired.All(Function(clock) clock IsNot Nothing) AndAlso Not card.Collapsed AndAlso card.Body Is body,
                "a restored detail result accepts fresh hover on the same expanded card and body")
            card.Collapsed = True : layout()
            Dim folding = card.HeightTransition
            For Each clock In retired : clock.Controller.SkipToFill() : Next
            layout()
            check(folding IsNot Nothing AndAlso card.HeightTransition Is folding AndAlso card.Collapsed AndAlso DirectCast(row, PclListItem).Tag Is _packReleases(0),
                "retired hover callbacks cannot finish the separate card-fold animation or replace its release")
            card.Collapsed = False : FinishDeferredCard(card, layout)
            ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True) : retired = card.HoverTransitions
            _motionPreview = True : _surfaceMotion.PauseForChecks = True : ShowSettings(Nothing, Nothing) : layout()
            Dim settings = _surfaceMotion.Transition
            check(settings IsNot Nothing AndAlso SettingsPage.IsVisible AndAlso card.HoverTransitions.All(Function(clock) clock Is Nothing),
                "leaving details captures the current card frame and releases the hidden hover channels")
            ShowDownload(Nothing, Nothing) : layout()
            Dim returning = _surfaceMotion.Transition
            For Each clock In retired : clock.Controller.SkipToFill() : Next
            layout()
            check(returning IsNot Nothing AndAlso returning IsNot settings AndAlso _surfaceMotion.Transition Is returning AndAlso Not PageSurface.IsHitTestVisible AndAlso
                PackVersionCards.Children(0) Is card AndAlso card.Body Is body AndAlso DirectCast(body, StackPanel).Children(0) Is row,
                "rapid detail return retains one page clock and its cached card while obsolete hover callbacks stay inert")
            _surfaceMotion.Finish() : layout()
            ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = True : ControlPointer(card, True)
            check(card.HoverTransitions.All(Function(clock) clock IsNot Nothing) AndAlso calls = fetched AndAlso _packProject Is project,
                "returned details resume card hover without fetching versions again or replacing the accepted project")
            For Each clock In card.HoverTransitions : SeekControl(clock, 45) : Next
            SaveControlVisual(host, IO.Path.Combine(directory, "card-hover-detail.png"))
            card.HoverMotionPolicy.EnabledForChecks = False
            check(card.HoverTransitions.All(Function(clock) clock Is Nothing) AndAlso card.Body Is body AndAlso Not card.Collapsed AndAlso _packSearchSession Is session AndAlso
                _taskGeneration = generation AndAlso _cancellation Is cancellation AndAlso Not _busy AndAlso Not _choosingOnline,
                "static card hover preserves expanded details, search identity and installation cancellation ownership")
        Finally
            _packDetailRequest?.Cancel()
            If card IsNot Nothing Then ControlPointer(card, False) : card.HoverMotionPolicy.EnabledForChecks = Nothing : card.HoverMotionPolicy.PauseForChecks = False
            _motionPreview = False : _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion()
            _packCatalogFactory = factory : _motionPreview = motion : _surfaceMotion.PauseForChecks = paused
        End Try
    End Function
End Class
