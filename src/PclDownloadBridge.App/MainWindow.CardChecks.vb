Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Public Function VerifyCardUi(artifactDirectory As String, Optional checks As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("UI checks require isolated preview mode.")
        checks = If(checks, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     checks.Add(label)
                                                 End Sub
        VerifyInstallViewport(check, artifactDirectory)
        VerifyCardMotion(check, artifactDirectory)
        VerifyComponentSummary(check, artifactDirectory)
        VerifyVersionFormatUi(check, artifactDirectory)
        VerifyLoaderBoundaryUi(check, artifactDirectory)
        Return checks
    End Function

    Private Sub VerifyInstallViewport(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 470}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Try
            SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
            Dim catalogue = Enumerable.Range(0, 120).Select(Function(index) New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "0.16." & index}).ToList()
            AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, catalogue)
            _componentViews(ComponentKind.Fabric).Card.Collapsed = False
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 470)
            layout()
            Dim scroll = InstallPage.Children.OfType(Of ScrollViewer)().Single()
            scroll.ScrollToEnd() : layout()
            Dim oldOffset = scroll.VerticalOffset
            check(oldOffset > 4000, "the install viewport fixture starts deep inside a real long loader catalogue")
            SelectVersion(_versions.Single(Function(item) item.Id = "1.20.6"))
            layout()
            Dim observation As New JObject From {{"oldOffset", oldOffset}, {"newOffset", scroll.VerticalOffset}, {"scrollableHeight", scroll.ScrollableHeight}, {"name", InstanceName.Text}}
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "install-viewport.json"), observation.ToString())
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "install-viewport.png"))
            check(scroll.VerticalOffset = 0 AndAlso InstanceName.Text = "1.20.6", "selecting another Minecraft version starts its install options at the top like PCL PrepareSelectPage")
            check(_componentViews.Values.All(Function(view) view.Card.Collapsed) AndAlso _componentSelection.Entries.Count = 0, "entering a new version resets component folds and choices together with its viewport")
            _componentViews(ComponentKind.Fabric).Card.Collapsed = False
            Dim card = _componentViews(ComponentKind.Fabric).Card
            card.BeginAnimation(HeightProperty, Nothing) : card.Height = Double.NaN
            layout()
            scroll.ScrollToEnd() : layout()
            Dim retainedOffset = scroll.VerticalOffset
            SwitchMainPage(True) : layout()
            SwitchMainPage(False) : layout()
            check(scroll.VerticalOffset = retainedOffset AndAlso Not card.Collapsed, "a settings round trip preserves the current install viewport and expanded component")
        Finally
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
        End Try
    End Sub

    Private Shared Function SampleCardMotion(animation As DoubleAnimationUsingKeyFrames, milliseconds As Double) As Double
        Dim previousTime As Double = 0, previousValue = animation.KeyFrames(0).Value
        For Each frame As DoubleKeyFrame In animation.KeyFrames
            Dim time = frame.KeyTime.TimeSpan.TotalMilliseconds
            If milliseconds <= time AndAlso time > previousTime Then Return frame.InterpolateValue(previousValue, Math.Max(0, (milliseconds - previousTime) / (time - previousTime)))
            previousTime = time : previousValue = frame.Value
        Next
        Return previousValue
    End Function

    Private Sub VerifyCardMotion(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim distances As Double() = {0, 1, -1, 799.9, -799.9, 800, -800, 800.1, -800.1, 1000, -1000, 3000, -3000, 3000.1, -3000.1, 50000, -50000}
        Dim durations As Integer() = {150, 150, 150, 150, 150, 150, 150, 363, 250, 412, 250, 912, 250, 700, 250, 700, 250}
        Dim metrics As New JArray
        For index = 0 To distances.Length - 1
            Dim origin = If(distances(index) < 0, 40 - distances(index), 40)
            Dim animation = PclCardMotion.HeightTransition(origin, origin + distances(index))
            metrics.Add(New JObject From {{"delta", distances(index)}, {"durationMs", animation.Duration.TimeSpan.TotalMilliseconds}, {"frames", animation.KeyFrames.Count}})
            check(animation.Duration.TimeSpan.TotalMilliseconds = durations(index), "card height duration follows PCL short medium long and reverse boundaries: " & distances(index).ToString(Globalization.CultureInfo.InvariantCulture))
        Next
        Dim shortMotion = PclCardMotion.HeightTransition(40, 840)
        check(Math.Abs(SampleCardMotion(shortMotion, 75) - 815) < 0.00001, "short card motion uses PCL ExtraStrong fifth-power easing rather than fourth power")
        Dim longMotion = PclCardMotion.HeightTransition(40, 5040)
        check(Math.Abs(SampleCardMotion(longMotion, 300) - 1540) < 0.00001, "long expansion reaches its linear-to-easing boundary at 300ms")
        check(Math.Abs(SampleCardMotion(longMotion, 500) - 3290) < 0.00001, "long card motion preserves PCL's linear fallback when the last leg has a greater average speed")
        Dim reverseMotion = PclCardMotion.HeightTransition(5040, 40)
        check(Math.Abs(SampleCardMotion(reverseMotion, 100) - 240) < 0.00001 AndAlso SampleCardMotion(reverseMotion, 250) = 40, "long collapse uses a hundred-millisecond linear leg and a 150ms finish")
        Dim medium = PclCardMotion.HeightTransition(40, 1040)
        check(SampleCardMotion(medium, 212) = 890 AndAlso SampleCardMotion(medium, 412) = 1040, "medium expansion preserves AaHeight integer timing and the 150-DIP easing distance")
        check(Math.Abs(SampleCardMotion(medium, 312) - 1016.3157894736842) < 0.00001, "medium card deceleration follows PCL's initial-speed ratio curve")
        Dim clone = longMotion.Clone()
        clone.Freeze()
        check(Math.Abs(SampleCardMotion(clone, 500) - SampleCardMotion(longMotion, 500)) < 0.00001, "WPF clone and freeze retain the initial-speed easing parameters")
        Dim ease As New PclInitialSpeedEase(100, 0.1, 100)
        check(ease.Ease(0.5) = 0.5, "an initial speed below average falls back to linear just like PCL")
        Dim arrow = PclCardMotion.ArrowTransition(270, 180)
        check(arrow.Duration.TimeSpan.TotalMilliseconds = 250 AndAlso Math.Abs(arrow.EasingFunction.Ease(0.5) - 0.96875) < 0.00001, "card arrows use the original 250ms ExtraStrong easing")
        IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "card-motion-plans.json"), metrics.ToString())
        VerifyLiveCardMotion(check, artifactDirectory)
    End Sub

    Private Sub VerifyLiveCardMotion(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim body As New StackPanel
        For index = 1 To 100
            body.Children.Add(New TextBlock With {.Height = 50, .Text = "动画验证 · 示例版本 " & index, .VerticalAlignment = VerticalAlignment.Center})
        Next
        Dim card As New PclCard With {.Title = "长列表动画验证", .Collapsible = True, .Collapsed = True, .SwapLogoRight = True, .BodyMargin = New Thickness(0, 40, 0, 0), .Body = body}
        Dim scroll As New ScrollViewer With {.Content = card, .VerticalScrollBarVisibility = ScrollBarVisibility.Auto}
        Dim host As New Border With {.Width = 500, .Height = 500, .Child = scroll}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(500, 500)) : host.Arrange(New Rect(0, 0, 500, 500))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 500)
            layout()
            card.Collapsed = False
            Dim opening = card.HeightTransition
            check(opening IsNot Nothing AndAlso opening.Timeline.Duration.TimeSpan.TotalMilliseconds = 700, "a loaded long card actually runs the source-aligned expansion timeline")
            opening.Controller.Pause()
            layout()
            opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(300), TimeSeekOrigin.BeginTime)
            layout()
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "live-card-motion.json"), New JObject From {
                {"height", card.Height}, {"actualHeight", card.ActualHeight}, {"desiredHeight", card.DesiredSize.Height}, {"clip", card.ClipToBounds},
                {"bodyRetained", card.Body Is body}, {"timeMs", If(opening.CurrentTime.HasValue, opening.CurrentTime.Value.TotalMilliseconds, -1)},
                {"clockState", opening.CurrentState.ToString()}, {"clockPaused", opening.IsPaused},
                {"frames", New JArray(DirectCast(opening.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Select(Function(frame) New JObject From {{"timeMs", frame.KeyTime.TimeSpan.TotalMilliseconds}, {"value", frame.Value}}))}}.ToString())
            Dim sampledHeight = SampleCardMotion(DirectCast(opening.Timeline, DoubleAnimationUsingKeyFrames), opening.CurrentTime.Value.TotalMilliseconds)
            check(Math.Abs(card.ActualHeight - sampledHeight) < 0.01 AndAlso card.ActualHeight > 40 AndAlso card.ActualHeight < 5040 AndAlso card.ClipToBounds AndAlso card.Body Is body,
                "the real WPF expansion clock follows its sampled timeline without rebuilding rows")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "card-motion-middle.png"))
            card.Collapsed = True
            Dim closing = card.HeightTransition
            check(closing IsNot opening AndAlso closing.Timeline.Duration.TimeSpan.TotalMilliseconds = 250, "reversing a running long expansion starts a bounded collapse from its current height")
            closing.Controller.Pause()
            layout()
            closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(50), TimeSeekOrigin.BeginTime)
            layout()
            opening.Controller.SkipToFill()
            layout()
            check(card.HeightTransition Is closing AndAlso card.Collapsed AndAlso card.ClipToBounds AndAlso card.Height > 40, "a replaced animation clock cannot finish or reset its successor")
            closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(251), TimeSeekOrigin.BeginTime)
            layout()
            check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso Not card.ClipToBounds, "a completed collapse releases its clock restores forty-DIP height and clears clipping")
            card.Collapsed = False
            card.HeightTransition.Controller.Pause()
            source.RootVisual = Nothing
            FlushControlLayout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso Not card.ClipToBounds AndAlso card.Body Is body, "unloading during expansion releases the clock while preserving its expanded content")
            source.RootVisual = host
            layout()
            check(Not card.Collapsed AndAlso card.ActualHeight >= 5040 AndAlso card.Body Is body AndAlso body.Children.Count = 100, "reattaching an expanded card restores auto height with the same rows")
            source.RootVisual = Nothing
            Dim fixedCard As New PclCard With {.Title = "固定高度卡片", .Height = 123}
            scroll.Content = fixedCard : source.RootVisual = host : layout()
            source.RootVisual = Nothing : FlushControlLayout()
            check(fixedCard.Height = 123, "unloading a nonanimated card preserves its explicitly configured height")
        End Using
    End Sub
End Class
