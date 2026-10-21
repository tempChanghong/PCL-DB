Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyComponentFoldRefresh(directory As String, check As Action(Of Boolean, String))
        SelectVersion(_versions.Single(Function(version) version.Id = "1.20.1"))
        Dim entry = _selectedEntry
        Dim releases = Enumerable.Range(0, 30).Select(Function(index) New ComponentEntry With {
            .Kind = ComponentKind.Fabric, .Minecraft = entry.Id, .Version = "0.16." & index, .Stable = True}).ToList()
        AcceptComponentList(ComponentKind.Fabric, entry, _componentGeneration, releases)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(850, 700)) : host.Arrange(New Rect(0, 0, 850, 700))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Me.Content = Nothing : host.Child = content
        Using source = PreviewSurface.Attach(host, 850, 700)
            Try
                layout() : FinishPageSwitchForCheck(layout)
                Dim card = _componentViews(ComponentKind.Fabric).Card
                card.Collapsed = False
                Dim opening = card.HeightTransition
                opening.Controller.Pause() : layout()
                opening.Controller.SeekAlignedToLastTick(opening.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime) : layout()
                Dim body = DirectCast(card.Body, StackPanel)
                Dim rows = MaterializeComponentRows(body).ToArray() : layout()
                PointerEvent(rows(0), Mouse.MouseDownEvent) : PointerEvent(rows(0), Mouse.MouseUpEvent)
                Dim closing = card.HeightTransition : closing.Controller.Pause() : layout()
                InstanceName.Text = "收起刷新保留名称"
                PointerEvent(rows(1), Mouse.MouseDownEvent)
                closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40), TimeSeekOrigin.BeginTime) : layout()
                Dim before = card.ActualHeight
                Dim replacement = releases.Take(3).Select(Function(version) New ComponentEntry With {
                    .Kind = version.Kind, .Minecraft = version.Minecraft, .Version = version.Version, .Stable = True,
                    .Url = "https://example.org/accepted-fold-refresh/" & version.Version}).ToList()
                AcceptComponentList(ComponentKind.Fabric, entry, _componentGeneration, replacement) : layout()
                Dim retained = card.Body Is body
                Dim selectedMarker = rows(0).Selected
                Dim blocked = Not rows(1).IsEnabled
                PointerEvent(rows(1), Mouse.MouseUpEvent)
                IO.File.WriteAllText(IO.Path.Combine(directory, "component-fold-refresh.json"), New JObject From {
                    {"retainedBody", retained}, {"selectedMarkerPreserved", selectedMarker}, {"retiredInputBlocked", blocked},
                    {"sameHeightClock", card.HeightTransition Is closing}, {"heightBefore", before}, {"heightAfter", card.ActualHeight},
                    {"acceptedCount", _componentEntries(ComponentKind.Fabric).Count}, {"selectedVersion", _componentSelection.Item(ComponentKind.Fabric)?.Version},
                    {"instanceName", InstanceName.Text}}.ToString())
                check(retained, "accepted component refresh keeps the visible retiring rows until the collapse finishes")
                check(selectedMarker AndAlso blocked, "retiring component rows preserve their displayed selection while rejecting old input")
                check(card.HeightTransition Is closing AndAlso Math.Abs(card.ActualHeight - before) < 0.01,
                    "refresh during collapse preserves the active source height clock and displayed geometry")
                check(_componentEntries(ComponentKind.Fabric) Is replacement AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement(0) AndAlso
                    InstanceName.Text = "收起刷新保留名称", "retired-row release cannot replace accepted metadata, selected component or edited instance name")
                Dim newest = replacement.Take(2).Select(Function(version) New ComponentEntry With {
                    .Kind = version.Kind, .Minecraft = version.Minecraft, .Version = version.Version, .Stable = True,
                    .Url = "https://example.org/newest-fold-refresh/" & version.Version}).ToList()
                AcceptComponentList(ComponentKind.Fabric, entry, _componentGeneration, newest) : layout()
                check(card.Body Is body AndAlso card.HasDeferredBodyRefresh AndAlso rows(0).Selected AndAlso card.HeightTransition Is closing AndAlso
                    _componentSelection.Item(ComponentKind.Fabric) Is newest(0), "a second accepted list replaces only deferred data while the old selected rows finish exiting")
                AcceptComponentList(ComponentKind.Fabric, entry, _componentGeneration - 1, releases) : layout()
                check(_componentEntries(ComponentKind.Fabric) Is newest AndAlso card.Body Is body AndAlso card.HeightTransition Is closing,
                    "a stale response cannot replace deferred data or restart the component collapse")
                Dim frames = IO.Path.Combine(directory, "fold-refresh-frames") : IO.Directory.CreateDirectory(frames)
                Dim duration = closing.Timeline.Duration.TimeSpan.TotalMilliseconds
                For index = 0 To 12
                    closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40 + (duration - 40) * index / 12 + If(index = 12, 1, 0)), TimeSeekOrigin.BeginTime)
                    layout() : SaveControlVisual(host, IO.Path.Combine(frames, index.ToString("D2") & ".png"))
                Next
                check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso card.Body Is Nothing AndAlso Not card.HasDeferredBodyRefresh AndAlso
                    rows.All(Function(row) Not row.IsLoaded AndAlso row.HoverTransition Is Nothing AndAlso row.SelectionTransition Is Nothing),
                    "fold completion unloads retired component rows and their clocks while keeping the newest factory deferred")
                card.Collapsed = False
                Dim refreshedOpening = card.HeightTransition : refreshedOpening.Controller.Pause() : layout()
                Dim freshBody = DirectCast(card.Body, StackPanel), freshRows = MaterializeComponentRows(freshBody).ToArray()
                layout()
                check(freshBody IsNot body AndAlso freshRows.Length = 2 AndAlso freshRows(0).Tag Is newest(0) AndAlso freshRows(0).Selected AndAlso
                    freshRows.All(Function(row) row.IsEnabled), "reopening creates enabled rows only from the newest accepted catalogue with the preserved selection")
                refreshedOpening.Controller.SeekAlignedToLastTick(refreshedOpening.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime) : layout()
                card.Collapsed = True
                Dim interrupted = card.HeightTransition : interrupted.Controller.Pause() : layout()
                interrupted.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40), TimeSeekOrigin.BeginTime) : layout()
                AcceptComponentList(ComponentKind.Fabric, entry, _componentGeneration, replacement) : layout()
                before = card.ActualHeight
                card.Collapsed = False
                Dim reversed = card.HeightTransition : reversed.Controller.Pause() : layout()
                Dim reversedBody = DirectCast(card.Body, StackPanel), reversedRows = MaterializeComponentRows(reversedBody).ToArray()
                check(reversed IsNot interrupted AndAlso Math.Abs(DirectCast(reversed.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - before) < 0.01 AndAlso
                    reversedBody IsNot freshBody AndAlso reversedRows.Length = 3 AndAlso reversedRows(0).Tag Is replacement(0),
                    "reopening before collapse completes builds the latest accepted rows from the displayed card height")
                interrupted.Controller.SkipToFill() : layout()
                check(card.HeightTransition Is reversed AndAlso card.Body Is reversedBody AndAlso reversedRows(0).Selected AndAlso
                    InstanceName.Text = "收起刷新保留名称", "an obsolete fold completion cannot clear a reopened component list or its edited name")
            Finally
                source.RootVisual = Nothing : host.Child = Nothing : Me.Content = content
            End Try
        End Using
        VerifyDeferredCardFactories(directory, check)
    End Sub

    Private Shared Sub FinishDeferredCard(card As PclCard, layout As Action)
        Dim clock = card.HeightTransition
        If clock Is Nothing Then Return
        clock.Controller.Pause() : layout()
        clock.Controller.SeekAlignedToLastTick(clock.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime) : layout()
    End Sub

    Private Shared Sub VerifyDeferredCardFactories(directory As String, check As Action(Of Boolean, String))
        Dim row As New PclListItem With {.Title = "旧版本行", .Height = 42}
        Dim retry As New PclButton With {.Text = "旧重试操作", .Height = 35}
        Dim oldBody As New StackPanel()
        oldBody.Children.Add(row) : oldBody.Children.Add(retry) : oldBody.Children.Add(New Border With {.Height = 1000})
        Dim card As New PclCard With {.Title = "延后刷新", .BodyMargin = New Thickness(0, 40, 0, 0), .Collapsible = True, .Body = oldBody}
        Dim viewer As New PclScrollViewer With {.Content = card}
        Dim host As New Border With {.Width = 500, .Height = 400, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(500, 400)) : host.Arrange(New Rect(0, 0, 500, 400))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 400)
            layout() : card.Collapsed = True
            Dim folding = card.HeightTransition : folding.Controller.Pause() : layout()
            folding.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40), TimeSeekOrigin.BeginTime) : layout()
            Dim actions = 0
            AddHandler row.Click, Sub() actions += 1
            AddHandler retry.Click, Sub() actions += 1
            PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(retry, Mouse.MouseDownEvent)
            Dim firstCalls = 0, latestCalls = 0
            card.RefreshBody(Function()
                                 firstCalls += 1
                                 Return New Border With {.Height = 200}
                             End Function)
            Dim lastGeneration = 0
            For generation = 1 To 10
                Dim accepted = generation
                card.RefreshBody(Function()
                                     latestCalls += 1 : lastGeneration = accepted
                                     Return New Border With {.Height = 400 + accepted, .Tag = accepted}
                                 End Function)
            Next
            layout()
            check(firstCalls = 0 AndAlso latestCalls = 0 AndAlso card.Body Is oldBody AndAlso card.HasDeferredBodyRefresh AndAlso oldBody.IsVisible,
                "eleven refreshes during collapse retain the visible body and do not execute deferred factories")
            PointerEvent(row, Mouse.MouseUpEvent) : PointerEvent(retry, Mouse.MouseUpEvent)
            row.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) With {.RoutedEvent = Keyboard.KeyDownEvent})
            retry.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) With {.RoutedEvent = Keyboard.KeyDownEvent})
            check(actions = 0 AndAlso Not row.IsEnabled AndAlso Not retry.IsEnabled,
                "retired row and retry controls reject the cancelled pointer release and synthetic keyboard activation")
            Dim factory = card.Builder, rejected = False
            Try
                card.RefreshBody(Nothing)
            Catch ex As ArgumentNullException
                rejected = ex.ParamName = "builder"
            End Try
            check(rejected AndAlso card.Builder Is factory AndAlso card.Body Is oldBody AndAlso card.HeightTransition Is folding,
                "an invalid refresh factory is rejected before mutating a pending body or its active fold")
            Dim before = card.ActualHeight
            card.Collapsed = False
            Dim opening = card.HeightTransition : opening.Controller.Pause() : layout()
            check(firstCalls = 0 AndAlso latestCalls = 1 AndAlso lastGeneration = 10 AndAlso CInt(card.Body.GetValue(FrameworkElement.TagProperty)) = 10 AndAlso
                card.Body.IsEnabled AndAlso Not card.HasDeferredBodyRefresh AndAlso Math.Abs(DirectCast(opening.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - before) < 0.01,
                "early reversal invokes only the latest factory once and restores inherited input at the displayed height")
            folding.Controller.SkipToFill() : layout()
            check(card.HeightTransition Is opening AndAlso CInt(card.Body.GetValue(FrameworkElement.TagProperty)) = 10,
                "a stale generic-card fold cannot discard its replacement body")
            FinishDeferredCard(card, layout)
            check(Double.IsNaN(card.Height) AndAlso Math.Abs(card.ActualHeight - 450) < 0.01 AndAlso Not card.ClipToBounds,
                "the latest deferred factory settles to its own measured expanded height")

            For Each interruption In {"hidden", "detached"}
                card.Collapsed = True
                folding = card.HeightTransition : folding.Controller.Pause() : layout()
                Dim calls = 0
                card.RefreshBody(Function()
                                     calls += 1
                                     Return New Border With {.Height = 500}
                                 End Function)
                Select Case interruption
                    Case "hidden" : viewer.Visibility = Visibility.Collapsed : layout()
                    Case Else : viewer.Content = Nothing : layout()
                End Select
                check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso card.Body Is Nothing AndAlso Not card.HasDeferredBodyRefresh AndAlso calls = 0,
                    "pending card " & interruption & " releases its retiring body without constructing hidden replacement content")
                Select Case interruption
                    Case "hidden" : viewer.Visibility = Visibility.Visible : layout()
                    Case Else : viewer.Content = card : layout()
                End Select
                folding.Controller.SkipToFill() : layout()
                check(card.Collapsed AndAlso card.Body Is Nothing AndAlso calls = 0,
                    "restoring a " & interruption & " card cannot revive old content or invoke the deferred factory")
                card.Collapsed = False : layout()
                check(calls = 1 AndAlso card.Body.IsEnabled AndAlso Not card.HasDeferredBodyRefresh,
                    "reopening after " & interruption & " builds enabled replacement content exactly once")
                FinishDeferredCard(card, layout)
            Next

            viewer.IsEnabled = False
            card.Collapsed = True
            folding = card.HeightTransition : folding.Controller.Pause() : layout()
            Dim disabledCalls = 0
            card.RefreshBody(Function()
                                 disabledCalls += 1
                                 Return New Border With {.Height = 300}
                             End Function)
            FinishDeferredCard(card, layout)
            check(card.Body Is Nothing AndAlso Not card.IsEnabled AndAlso disabledCalls = 0,
                "fold cleanup respects a disabled ancestor and leaves replacement construction deferred")
            card.Collapsed = False : layout()
            Dim ancestry As New JArray(), current As DependencyObject = card.Body
            While current IsNot Nothing
                Dim element = TryCast(current, FrameworkElement)
                ancestry.Add(New JObject From {{"type", current.GetType().Name}, {"enabled", If(element Is Nothing, CType(Nothing, JToken), New JValue(element.IsEnabled))},
                    {"visible", If(element Is Nothing, CType(Nothing, JToken), New JValue(element.IsVisible))}, {"loaded", If(element Is Nothing, CType(Nothing, JToken), New JValue(element.IsLoaded))}})
                current = System.Windows.Media.VisualTreeHelper.GetParent(current)
            End While
            IO.File.WriteAllText(IO.Path.Combine(directory, "disabled-retirement.json"), New JObject From {{"factoryCalls", disabledCalls}, {"ancestry", ancestry}}.ToString())
            check(disabledCalls = 1 AndAlso Not card.Body.IsEnabled, "new content continues to inherit disabled state after the private retirement gate is removed")
            viewer.IsEnabled = True : layout()
            check(card.Body.IsEnabled AndAlso Not card.HasDeferredBodyRefresh, "reenabling the ancestor restores normal content input without a leftover retirement gate")
            FinishDeferredCard(card, layout)
            card.Collapsed = True : FinishDeferredCard(card, layout)
            Dim settledCalls = 0
            card.RefreshBody(Function()
                                 settledCalls += 1
                                 Return New Border With {.Height = 200}
                             End Function)
            check(card.Body Is Nothing AndAlso Not card.HasDeferredBodyRefresh AndAlso settledCalls = 0,
                "refreshing a fully collapsed card still clears hidden content immediately and defers its factory")
            card.Collapsed = False : layout()
            check(settledCalls = 1 AndAlso card.Body.IsEnabled, "ordinary reopening retains the preexisting lazy-build behavior")
        End Using
    End Sub
End Class
