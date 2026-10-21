Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Controls
Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Private Sub VerifyComponentSummary(check As Action(Of Boolean, String), artifactDirectory As String)
        SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        Dim view = _componentViews(ComponentKind.Fabric)
        check(view.Summary.Visibility = Visibility.Visible AndAlso view.Icon.Visibility = Visibility.Visible AndAlso view.Clear.Visibility = Visibility.Visible,
            "a selected folded component exposes its version icon text and clear action")
        view.Card.Collapsed = False
        IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "component-summary-structure.json"), New JObject From {
            {"summaryVisibility", view.Summary.Visibility.ToString()}, {"iconVisibility", view.Icon.Visibility.ToString()},
            {"clearVisibility", view.Clear.Visibility.ToString()}, {"summaryAndClearShareParent", view.Summary.Parent Is view.Clear.Parent}}.ToString())
        check(view.Summary.Visibility = Visibility.Visible AndAlso view.Icon.Visibility = Visibility.Visible,
            "expanded component summaries retain their children for the original PCL parent opacity and translation animation")
        check(view.Summary.Parent Is view.Info AndAlso view.Icon.Parent Is view.Info AndAlso view.Clear.Parent IsNot view.Info,
            "only the version icon and text belong to the animated summary panel")
        VerifySummaryPlans(check)
        VerifyLiveSummary(check, artifactDirectory)
    End Sub

    Private Sub VerifySummaryPlans(check As Action(Of Boolean, String))
        Dim hiding = PclComponentSummary.TransitionPlan(1, 0, False)
        Dim showing = PclComponentSummary.TransitionPlan(0, 6, True)
        Dim hideOpacity = DirectCast(hiding.Children(0), DoubleAnimation), hideOffset = DirectCast(hiding.Children(1), DoubleAnimation)
        Dim showOpacity = DirectCast(showing.Children(0), DoubleAnimation), showOffset = DirectCast(showing.Children(1), DoubleAnimation)
        check(hiding.Duration.TimeSpan.TotalMilliseconds = 60 AndAlso hideOpacity.Duration.TimeSpan.TotalMilliseconds = 60 AndAlso hideOffset.Duration.TimeSpan.TotalMilliseconds = 60,
            "summary hide translates and fades together in the original sixty milliseconds")
        check(hideOpacity.From.Value = 1 AndAlso hideOpacity.To.Value = 0 AndAlso hideOffset.From.Value = 0 AndAlso hideOffset.To.Value = 6 AndAlso hideOpacity.EasingFunction Is Nothing AndAlso hideOffset.EasingFunction Is Nothing,
            "summary hide uses linear opacity and six-DIP downward translation")
        check(showing.Duration.TimeSpan.TotalMilliseconds = 150 AndAlso showOpacity.Duration.TimeSpan.TotalMilliseconds = 60 AndAlso showOffset.Duration.TimeSpan.TotalMilliseconds = 150,
            "summary show fades for sixty milliseconds while returning for 150 milliseconds")
        check(showOffset.From.Value = 6 AndAlso showOffset.To.Value = 0 AndAlso showOpacity.EasingFunction Is Nothing AndAlso Math.Abs(showOffset.EasingFunction.Ease(0.5) - 0.875) < 0.000001,
            "summary return uses the original default cubic fluent easing")
        Dim clone = showing.Clone()
        clone.Freeze()
        check(DirectCast(clone.Children(1), DoubleAnimation).EasingFunction.Ease(0.5) = showOffset.EasingFunction.Ease(0.5),
            "summary easing survives WPF cloning and freezing")
        Dim cold As New PclComponentSummary
        cold.SetShown(False)
        check(cold.Transition Is Nothing AndAlso cold.Opacity = 0 AndAlso cold.Offset = 6 AndAlso Not cold.IsHitTestVisible,
            "a never-loaded hidden summary starts at its final position without a detached animation")
        cold.SetShown(False)
        check(cold.Transition Is Nothing AndAlso cold.Opacity = 0 AndAlso cold.Offset = 6,
            "assigning the same hidden summary state does not restart an animation")
        cold.SetShown(True)
        check(cold.Transition Is Nothing AndAlso cold.Opacity = 1 AndAlso cold.Offset = 0 AndAlso cold.IsHitTestVisible,
            "a never-loaded summary can restore its visible final state directly")
    End Sub

    Private Shared Function SummaryValue(animation As DoubleAnimation, milliseconds As Double) As Double
        Dim fraction = Math.Max(0, Math.Min(1, milliseconds / animation.Duration.TimeSpan.TotalMilliseconds))
        If animation.EasingFunction IsNot Nothing Then fraction = animation.EasingFunction.Ease(fraction)
        Return animation.From.Value + (animation.To.Value - animation.From.Value) * fraction
    End Function

    Private Sub VerifyLiveSummary(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim source As Interop.HwndSource = Nothing
        Dim observations As New JArray
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Try
            Dim view = _componentViews(ComponentKind.Fabric)
            view.Card.Collapsed = True
            InstanceName.Text = "保留摘要动画方案"
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            layout()
            Dim clearPosition = view.Clear.TranslatePoint(New Point(), view.Card)
            Dim clearCardWidth = view.Card.ActualWidth
            view.Card.Collapsed = False
            Dim body = view.Card.Body, hiding = view.Info.Transition
            check(hiding IsNot Nothing AndAlso hiding.Timeline.Duration.TimeSpan.TotalMilliseconds = 60,
                "expanding a loaded component starts its actual summary fade clock")
            hiding.Controller.Pause() : layout()
            hiding.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(20), TimeSeekOrigin.BeginTime) : layout()
            RecordSummary(observations, "hide", view)
            Dim elapsed = If(hiding.CurrentTime.HasValue, hiding.CurrentTime.Value.TotalMilliseconds, 60)
            Dim hidePlan = DirectCast(hiding.Timeline, ParallelTimeline)
            check(Math.Abs(view.Info.Opacity - SummaryValue(DirectCast(hidePlan.Children(0), DoubleAnimation), elapsed)) < 0.001 AndAlso
                Math.Abs(view.Info.Offset - SummaryValue(DirectCast(hidePlan.Children(1), DoubleAnimation), elapsed)) < 0.001,
                "the actual summary fade follows its WPF clock opacity and translation values")
            host.Width = 820 : layout()
            Dim afterClearPosition = view.Clear.TranslatePoint(New Point(), view.Card)
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "component-summary-anchor.json"), New JObject From {
                {"beforeCardWidth", clearCardWidth}, {"afterCardWidth", view.Card.ActualWidth}, {"beforeX", clearPosition.X}, {"afterX", afterClearPosition.X},
                {"beforeY", clearPosition.Y}, {"afterY", afterClearPosition.Y},
                {"beforeRightInset", clearCardWidth - clearPosition.X - view.Clear.ActualWidth},
                {"afterRightInset", view.Card.ActualWidth - afterClearPosition.X - view.Clear.ActualWidth}}.ToString())
            Dim oldRightInset = clearCardWidth - clearPosition.X - view.Clear.ActualWidth
            Dim rightInset = view.Card.ActualWidth - afterClearPosition.X - view.Clear.ActualWidth
            check(view.Clear.Visibility = Visibility.Visible AndAlso view.Clear.Opacity = 1 AndAlso Math.Abs(rightInset - oldRightInset) < 0.01 AndAlso
                Math.Abs(rightInset - 32) < 0.01 AndAlso Math.Abs(afterClearPosition.Y - clearPosition.Y) < 0.01,
                "the clear action stays opaque and right-anchored during summary animation and window resizing")
            host.Width = 850 : layout()
            Dim current = view.Info.Transition
            Dim scrollOffset = InstallScroll.VerticalOffset
            AcceptComponentList(ComponentKind.NeoForge, _selectedEntry, _componentGeneration, _componentEntries(ComponentKind.NeoForge))
            check(view.Info.Transition Is current AndAlso view.Card.Body Is body AndAlso InstanceName.Text = "保留摘要动画方案" AndAlso _componentSelection.Has(ComponentKind.FabricApi) AndAlso InstallScroll.VerticalOffset = scrollOffset,
                "an unrelated catalogue refresh retains the summary clock rows custom name and dependency")
            view.Card.Collapsed = True
            Dim showing = view.Info.Transition
            check(showing IsNot Nothing AndAlso showing IsNot hiding AndAlso showing.Timeline.Duration.TimeSpan.TotalMilliseconds = 150,
                "closing the component reverses its current summary values into the original show timeline")
            showing.Controller.Pause() : layout()
            showing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(30), TimeSeekOrigin.BeginTime) : layout()
            elapsed = If(showing.CurrentTime.HasValue, showing.CurrentTime.Value.TotalMilliseconds, 150)
            Dim showPlan = DirectCast(showing.Timeline, ParallelTimeline)
            check(Math.Abs(view.Info.Opacity - SummaryValue(DirectCast(showPlan.Children(0), DoubleAnimation), elapsed)) < 0.001 AndAlso
                Math.Abs(view.Info.Offset - SummaryValue(DirectCast(showPlan.Children(1), DoubleAnimation), elapsed)) < 0.001,
                "the actual summary return preserves the cubic offset and independent opacity duration")
            RecordSummary(observations, "show", view)
            view.Card.Collapsed = False
            Dim replacement = view.Info.Transition
            replacement.Controller.Pause()
            showing.Controller.SkipToFill()
            layout()
            check(view.Info.Transition Is replacement AndAlso Not view.Info.IsHitTestVisible,
                "a replaced summary clock cannot show an expanded component's hidden information")
            replacement.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(61), TimeSeekOrigin.BeginTime) : layout()
            check(view.Info.Transition Is Nothing AndAlso view.Info.Opacity = 0 AndAlso view.Info.Offset = 6,
                "a completed summary hide releases its clocks and retains the six-DIP final position")
            RefreshComponentHeader(ComponentKind.Fabric)
            check(view.Info.Transition Is Nothing AndAlso view.Card.Body Is body,
                "refreshing an unchanged completed summary cannot recreate a fade or component rows")
            view.Card.Collapsed = True
            view.Info.Transition.Controller.Pause()
            source.RootVisual = Nothing : FlushControlLayout()
            check(view.Info.Transition Is Nothing AndAlso view.Info.Opacity = 1 AndAlso view.Info.Offset = 0 AndAlso view.Card.Body Is body,
                "unloading an in-flight summary settles the owned final state without discarding its rows")
            view.Card.Collapsed = False
            check(view.Info.Transition Is Nothing AndAlso view.Info.Opacity = 0 AndAlso view.Info.Offset = 6,
                "changing a detached component fold applies summary state without a live clock")
            source.RootVisual = host : layout()
            check(view.Info.Opacity = 0 AndAlso view.Info.Offset = 6 AndAlso view.Clear.Visibility = Visibility.Visible AndAlso view.Card.Body Is body,
                "reattaching an expanded component preserves its hidden summary and independent clear action")
            Dim dependency = _componentViews(ComponentKind.FabricApi)
            dependency.Card.Collapsed = False
            PointerEvent(dependency.Clear, Input.Mouse.MouseDownEvent) : PointerEvent(dependency.Clear, Input.Mouse.MouseUpEvent)
            check(Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso _componentSelection.Has(ComponentKind.Fabric) AndAlso dependency.Card.Collapsed AndAlso InstanceName.Text = "保留摘要动画方案",
                "clearing an expanded dependency during its fade preserves the parent loader and custom name")
            check(dependency.Clear.Visibility = Visibility.Collapsed AndAlso dependency.Info.IsHitTestVisible AndAlso _suppressedDependencies.Contains(ComponentKind.FabricApi),
                "dependency clear retains its explicit opt-out and restores the available summary")
            ClearComponent(ComponentKind.Fabric)
            check(_componentSelection.Entries.Count = 0 AndAlso view.Clear.Visibility = Visibility.Collapsed AndAlso view.Icon.Visibility = Visibility.Collapsed AndAlso view.Card.Collapsed,
                "clearing the parent retains the original dependency cleanup and folded header roles")
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            check(_componentSelection.Has(ComponentKind.Fabric) AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso InstanceName.Text = "保留摘要动画方案",
                "reselecting a loader does not override the user's dependency opt-out or name")
            RefreshComponents()
            check(view.Card.Body Is body AndAlso view.Summary.TextTrimming = TextTrimming.CharacterEllipsis AndAlso view.Card.HeaderContentMargin.Right = 32,
                "summary animation keeps visited rows ellipsis and the existing clear-button inset")
            view.Card.Collapsed = False
            view.Info.Transition.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(61), TimeSeekOrigin.BeginTime) : layout()
            FinishSummaryCard(view, layout)
            RecordSummary(observations, "expanded-final", view)
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "component-summary-expanded.png"))
            view.Card.Collapsed = True
            view.Info.Transition.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(151), TimeSeekOrigin.BeginTime) : layout()
            FinishSummaryCard(view, layout)
            RecordSummary(observations, "folded-final", view)
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "component-summary-folded.png"))
        Finally
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "component-summary-motion.json"), observations.ToString())
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
        End Try
    End Sub

    Private Shared Sub FinishSummaryCard(view As ComponentView, layout As Action)
        Dim heightClock = view.Card.HeightTransition
        If heightClock Is Nothing Then Return
        heightClock.Controller.SeekAlignedToLastTick(heightClock.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime)
        layout()
    End Sub

    Private Shared Sub RecordSummary(items As JArray, stage As String, view As ComponentView)
        Dim clock = view.Info.Transition
        items.Add(New JObject From {{"stage", stage}, {"opacity", view.Info.Opacity}, {"offset", view.Info.Offset},
            {"clockMs", If(clock IsNot Nothing AndAlso clock.CurrentTime.HasValue, clock.CurrentTime.Value.TotalMilliseconds, -1)},
            {"clearOpacity", view.Clear.Opacity}, {"clearVisibility", view.Clear.Visibility.ToString()}, {"folded", view.Card.Collapsed}})
    End Sub
End Class
