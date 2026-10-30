Imports System.Windows
Imports System.Windows.Data
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyPageRight card choreography decorates PageDownloadInstall's independent
    ' horizontal transition. Page opacity, input gates, card bodies, fold height,
    ' press transforms and scrolling remain owned by their existing controls.
    Friend NotInheritable Class PclPageCardMotion
        Private ReadOnly _host As FrameworkElement
        Private ReadOnly _units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement))
        Private ReadOnly _items As New List(Of Entry)
        Private _transition As ClockGroup
        Private _generation As Long

        Private NotInheritable Class LocalValue
            Friend Value As Object
            Friend Binding As BindingBase
            Friend Expression As Boolean
        End Class

        Private NotInheritable Class Entry
            Friend Target As FrameworkElement
            Friend OriginalTransform As Transform
            Friend Group As TransformGroup
            Friend Offset As TranslateTransform
            Friend Opacity As Double
            Friend OpacityLocal As LocalValue
            Friend HitLocal As LocalValue
            Friend Hit As Boolean
            Friend GateOwned As Boolean
            Friend Incoming As Boolean
            Friend StartOpacity As Double
            Friend StartY As Double
            Friend Delay As Integer
            Friend RevealAt As Double
        End Class

        Friend Property PauseForChecks As Boolean
        Friend Event Settled As EventHandler

        Friend Sub New(host As FrameworkElement, units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement)))
            If host Is Nothing Then Throw New ArgumentNullException(NameOf(host))
            If units Is Nothing Then Throw New ArgumentNullException(NameOf(units))
            _host = host : _units = units
            AddHandler host.Unloaded, Sub() Finish()
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible Then Finish()
            AddHandler host.SizeChanged, Sub() If HasMotion Then Finish()
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _transition
            End Get
        End Property

        Friend ReadOnly Property HasMotion As Boolean
            Get
                Return _transition IsNot Nothing
            End Get
        End Property

        Friend ReadOnly Property ItemCount As Integer
            Get
                Return _items.Count
            End Get
        End Property

        Friend ReadOnly Property IncomingTargets As IEnumerable(Of FrameworkElement)
            Get
                Return _items.Where(Function(item) item.Incoming).Select(Function(item) item.Target).ToArray()
            End Get
        End Property

        Friend ReadOnly Property OutgoingTargets As IEnumerable(Of FrameworkElement)
            Get
                Return _items.Where(Function(item) Not item.Incoming).Select(Function(item) item.Target).ToArray()
            End Get
        End Property

        Friend Function DelayFor(target As FrameworkElement) As Integer?
            Dim item = _items.FirstOrDefault(Function(candidate) candidate.Target Is target)
            If item Is Nothing Then Return Nothing
            Return item.Delay
        End Function

        Friend Sub Change(outgoing As FrameworkElement, incoming As FrameworkElement)
            If outgoing Is incoming Then Return
            _generation += 1
            Dim generation = _generation
            If Not _host.IsLoaded OrElse Not _host.IsVisible OrElse _host.ActualWidth <= 0 OrElse _host.ActualHeight <= 0 Then
                StopOwnedMotion(False) : Return
            End If
            Try
                _host.UpdateLayout()
                If generation <> _generation Then Return
                Dim exiting = VisibleUnits(outgoing)
                If generation <> _generation Then Return
                Dim entering = VisibleUnits(incoming)
                If generation <> _generation Then Return
                ' Sample all effective values before removing the old clocks.
                ' Original normal state belongs to an Entry throughout reversals.
                For Each item In _items.ToArray()
                    item.StartOpacity = item.Target.Opacity
                    item.StartY = item.Offset.Y
                    If generation <> _generation Then Return
                Next
                If Not StopClock(generation) Then Return
                Dim previous = _items.ToDictionary(Function(item) item.Target)
                Dim selected As New List(Of Entry)
                Dim append As Action(Of FrameworkElement, Boolean) =
                    Sub(target, isIncoming)
                        Dim item As Entry = Nothing
                        If previous.TryGetValue(target, item) Then
                            previous.Remove(target)
                            If item.Target.RenderTransform IsNot item.Group Then
                                If Not Release(item, generation) Then Return
                                _items.Remove(item)
                                item = Nothing
                            End If
                        End If
                        If item Is Nothing Then
                            item = CreateEntry(target)
                            If generation <> _generation Then Return
                            item.StartOpacity = If(isIncoming, 0, item.Opacity)
                            item.StartY = If(isIncoming, -16, 0)
                            ' Register provisional ownership before this observable
                            ' write, so a reentrant Finish can restore the wrapper.
                            _items.Add(item)
                            target.SetCurrentValue(UIElement.RenderTransformProperty, item.Group)
                            If generation <> _generation Then Return
                        End If
                        item.Incoming = isIncoming
                        selected.Add(item)
                    End Sub
                For Each target In exiting
                    append(target, False)
                    If generation <> _generation Then Return
                Next
                For Each target In entering
                    If Not selected.Any(Function(item) item.Target Is target) Then append(target, True)
                    If generation <> _generation Then Return
                Next
                For Each item In previous.Values.ToArray()
                    If Not Release(item, generation) Then Return
                Next
                _items.Clear() : _items.AddRange(selected)
                If _items.Count = 0 Then Return

                Dim plan As New ParallelTimeline()
                Dim bindings As New List(Of Action(Of AnimationClock))
                Dim add As Action(Of AnimationTimeline, Action(Of AnimationClock)) =
                    Sub(animation, bind)
                        plan.Children.Add(animation) : bindings.Add(bind)
                    End Sub
                Dim enterIndex = 0, exitIndex = 0, duration = 260
                For Each item In _items.ToArray()
                    Dim entry = item
                    Dim delay = If(entry.Incoming, 100 + 25 * enterIndex, 15 * exitIndex)
                    entry.Delay = delay
                    If entry.Incoming Then
                        enterIndex += 1
                    Else
                        exitIndex += 1
                    End If
                    entry.RevealAt = If(entry.Incoming, delay + 100, Double.PositiveInfinity)
                    entry.GateOwned = True
                    entry.Target.SetCurrentValue(UIElement.IsHitTestVisibleProperty, False)
                    If generation <> _generation Then Return
                    ' Clock BeginTime can remain inactive until the next render;
                    ' hold this sampled frame in the base values in the meantime.
                    entry.Target.SetCurrentValue(UIElement.OpacityProperty, entry.StartOpacity)
                    If generation <> _generation Then Return
                    entry.Offset.Y = entry.StartY
                    If generation <> _generation Then Return
                    ' Keep alpha active from time zero, holding its sampled
                    ' origin throughout the stagger. A delayed BeginTime would
                    ' expose the original bound/base alpha on inactive ticks.
                    Dim alpha = Global.PclDownloadBridge.Presentation.PageMotion.CardOpacity(entry.Incoming, entry.StartOpacity, entry.Opacity, delay)
                    add(alpha, Sub(clock) entry.Target.ApplyAnimationClock(UIElement.OpacityProperty, clock))
                    If entry.Incoming Then
                        add(New PclPageCardYAnimation(entry.StartY) With {.BeginTime = TimeSpan.FromMilliseconds(delay)},
                            Sub(clock) entry.Offset.ApplyAnimationClock(TranslateTransform.YProperty, clock))
                        duration = Math.Max(duration, delay + 350)
                    Else
                        add(Global.PclDownloadBridge.Presentation.PageMotion.ExitY(entry.StartY, delay),
                            Sub(clock) entry.Offset.ApplyAnimationClock(TranslateTransform.YProperty, clock))
                        duration = Math.Max(duration, delay + 70)
                    End If
                Next
                plan.Duration = New Duration(TimeSpan.FromMilliseconds(duration))
                Dim group = DirectCast(plan.CreateClock(True), ClockGroup)
                If generation <> _generation Then
                    group.Controller.Remove()
                    Return
                End If
                If PauseForChecks Then group.Controller.Pause()
                _transition = group
                AddHandler group.CurrentTimeInvalidated,
                    Sub()
                        If generation <> _generation OrElse _transition IsNot group Then Return
                        Dim elapsed = group.CurrentTime.GetValueOrDefault().TotalMilliseconds
                        For Each item In _items.ToArray()
                            If item.GateOwned AndAlso elapsed >= item.RevealAt Then
                                If Not ReleaseGate(item, generation) Then Return
                            End If
                        Next
                    End Sub
                AddHandler group.Completed, Sub() If generation = _generation AndAlso _transition Is group Then Finish()
                For i = 0 To bindings.Count - 1
                    bindings(i)(DirectCast(group.Children(i), AnimationClock))
                    If generation <> _generation Then Return
                Next
                ' Applying an inactive clock invalidates SetCurrentValue's
                ' earlier value. Re-prime after attaching all delayed clocks,
                ' as PclSurfaceMotion does, so the first frame cannot flash.
                For Each item In _items.ToArray()
                    item.Target.SetCurrentValue(UIElement.OpacityProperty, item.StartOpacity)
                    If generation <> _generation Then Return
                Next
            Catch
                If generation = _generation Then StopOwnedMotion(False)
                Throw
            End Try
        End Sub

        Friend Sub Finish()
            StopOwnedMotion(True)
        End Sub

        Private Sub StopOwnedMotion(notify As Boolean)
            _generation += 1
            Dim generation = _generation
            Dim owned = _transition IsNot Nothing OrElse _items.Count > 0
            If Not StopClock(generation) Then Return
            For Each item In _items.ToArray()
                If Not Release(item, generation) Then Return
            Next
            _items.Clear()
            If notify AndAlso owned Then RaiseEvent Settled(Me, EventArgs.Empty)
        End Sub

        Private Function VisibleUnits(surface As FrameworkElement) As List(Of FrameworkElement)
            If surface Is Nothing OrElse Not surface.IsVisible Then Return New List(Of FrameworkElement)()
            Return _units(surface).Where(Function(unit) unit IsNot Nothing AndAlso unit IsNot surface AndAlso unit IsNot _host AndAlso
                unit.IsVisible AndAlso Not PclExitSnapshot.VisibleBounds(unit, _host).IsEmpty).Distinct().Take(16).ToList()
        End Function

        Private Shared Function CreateEntry(target As FrameworkElement) As Entry
            Dim item As New Entry With {.Target = target, .OriginalTransform = target.RenderTransform,
                .Opacity = target.Opacity, .OpacityLocal = Capture(target, UIElement.OpacityProperty),
                .Hit = target.IsHitTestVisible, .HitLocal = Capture(target, UIElement.IsHitTestVisibleProperty),
                .Offset = New TranslateTransform(), .Group = New TransformGroup()}
            If item.OriginalTransform IsNot Nothing Then item.Group.Children.Add(item.OriginalTransform)
            item.Group.Children.Add(item.Offset)
            Return item
        End Function

        Private Function StopClock(generation As Long) As Boolean
            Dim previous = _transition
            _transition = Nothing
            previous?.Controller.Remove()
            If generation <> _generation Then Return False
            For Each item In _items.ToArray()
                item.Target.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
                If generation <> _generation Then Return False
                item.Offset.ApplyAnimationClock(TranslateTransform.YProperty, Nothing)
                If generation <> _generation Then Return False
            Next
            Return True
        End Function

        Private Function Release(item As Entry, generation As Long) As Boolean
            item.Target.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
            If generation <> _generation Then Return False
            item.Offset.ApplyAnimationClock(TranslateTransform.YProperty, Nothing)
            If generation <> _generation Then Return False
            Restore(item.Target, UIElement.OpacityProperty, item.OpacityLocal, item.Opacity)
            If generation <> _generation Then Return False
            If item.Target.RenderTransform Is item.Group Then item.Target.SetCurrentValue(UIElement.RenderTransformProperty, item.OriginalTransform)
            If generation <> _generation Then Return False
            Return ReleaseGate(item, generation)
        End Function

        Private Function ReleaseGate(item As Entry, generation As Long) As Boolean
            If Not item.GateOwned Then Return generation = _generation
            item.GateOwned = False
            Restore(item.Target, UIElement.IsHitTestVisibleProperty, item.HitLocal, item.Hit)
            Return generation = _generation
        End Function

        Private Shared Function Capture(target As DependencyObject, [property] As DependencyProperty) As LocalValue
            Return New LocalValue With {.Value = target.ReadLocalValue([property]), .Binding = BindingOperations.GetBindingBase(target, [property]),
                .Expression = DependencyPropertyHelper.GetValueSource(target, [property]).IsExpression}
        End Function

        Private Shared Sub Restore(target As DependencyObject, [property] As DependencyProperty, saved As LocalValue, effective As Object)
            Dim binding = BindingOperations.GetBindingBase(target, [property])
            If saved.Binding IsNot Nothing Then
                ' Reevaluate the original binding so an update received while the
                ' card was gated is retained; never replace a newly assigned one.
                If binding Is saved.Binding Then BindingOperations.SetBinding(target, [property], saved.Binding)
            ElseIf binding Is Nothing Then
                If saved.Value Is DependencyProperty.UnsetValue Then
                    target.ClearValue([property])
                ElseIf saved.Expression Then
                    target.SetCurrentValue([property], effective)
                Else
                    target.SetCurrentValue([property], saved.Value)
                End If
            End If
        End Sub
    End Class
End Namespace
