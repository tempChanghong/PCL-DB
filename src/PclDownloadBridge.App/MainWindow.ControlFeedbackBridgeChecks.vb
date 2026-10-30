Imports System.IO
Imports System.Reflection
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyControlFeedbackBridgeUi(directory As String, passed As List(Of String))
        ' Baseline-compatible bridge checks. PCL 5ef81e4 MyButton/MyIconButton,
        ' MyListItem and MyLocalModItem own independent feedback clocks; PCL-CE
        ' 915ac297 preserves StateLast and the delayed invisible backing reset.
        Dim before = passed.Count
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        VerifyControlFeedbackContracts(check)
        VerifyControlFeedbackClocks(check)
        If passed.Count - before <> 42 Then Throw New InvalidOperationException("Control feedback bridge must report exactly 42 observations.")
        IO.Directory.CreateDirectory(directory)
        File.WriteAllText(Path.Combine(directory, "control-feedback-bridge.json"), New JObject From {
            {"Count", 42}, {"Checks", New JArray(passed.Skip(before))}, {"WindowsConfigurationChanged", False}}.ToString())
    End Sub

    Private Shared Sub VerifyControlFeedbackContracts(check As Action(Of Boolean, String))
        Dim app = GetType(MainWindow).Assembly, instance = BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic
        Dim statics = BindingFlags.Static Or BindingFlags.NonPublic
        check(GetType(PclPressKind).Assembly Is app AndAlso [Enum].GetUnderlyingType(GetType(PclPressKind)) Is GetType(Integer) AndAlso
            [Enum].GetNames(GetType(PclPressKind)).SequenceEqual({"ButtonDown", "ButtonUp", "ButtonLeave", "IconDown", "IconUp", "IconLeave"}) AndAlso
            [Enum].GetValues(GetType(PclPressKind)).Cast(Of PclPressKind)().Select(Function(value) CInt(value)).SequenceEqual(Enumerable.Range(0, 6)),
            "control feedback retains the original App press enum names and numeric values")
        For Each kind In {GetType(PclPressAnimation), GetType(PclSelectedHeightAnimation), GetType(PclRadioColorAnimation)}
            Dim expectedBase = If(kind Is GetType(PclRadioColorAnimation), GetType(ColorAnimationBase), GetType(DoubleAnimationBase))
            check(kind.Assembly Is app AndAlso kind.IsSealed AndAlso kind.BaseType Is expectedBase,
                "control feedback retains the original VB WPF animation identity: " & kind.Name)
        Next
        Dim pressOrigin = FeedbackProperty(GetType(PclPressAnimation), "OriginProperty"), pressKind = FeedbackProperty(GetType(PclPressAnimation), "KindProperty")
        check(pressOrigin.OwnerType Is GetType(PclPressAnimation) AndAlso pressOrigin.Name = "Origin" AndAlso pressOrigin.PropertyType Is GetType(Double) AndAlso
            CDbl(pressOrigin.DefaultMetadata.DefaultValue) = 1 AndAlso pressKind.OwnerType Is GetType(PclPressAnimation) AndAlso pressKind.Name = "Kind" AndAlso
            pressKind.PropertyType Is GetType(PclPressKind) AndAlso DirectCast(pressKind.DefaultMetadata.DefaultValue, PclPressKind) = PclPressKind.ButtonDown,
            "control feedback retains the press dependency-property owners types and defaults")
        Dim selectedOrigin = FeedbackProperty(GetType(PclSelectedHeightAnimation), "OriginProperty"), selectedTarget = FeedbackProperty(GetType(PclSelectedHeightAnimation), "TargetProperty")
        check(selectedOrigin.OwnerType Is GetType(PclSelectedHeightAnimation) AndAlso selectedTarget.OwnerType Is GetType(PclSelectedHeightAnimation) AndAlso
            selectedOrigin.Name = "Origin" AndAlso selectedTarget.Name = "Target" AndAlso CDbl(selectedOrigin.DefaultMetadata.DefaultValue) = 0 AndAlso CDbl(selectedTarget.DefaultMetadata.DefaultValue) = 0,
            "control feedback retains the selection dependency-property owners names and defaults")
        Dim colorOrigin = FeedbackProperty(GetType(PclRadioColorAnimation), "OriginProperty"), colorTarget = FeedbackProperty(GetType(PclRadioColorAnimation), "DestinationProperty")
        check(colorOrigin.OwnerType Is GetType(PclRadioColorAnimation) AndAlso colorTarget.OwnerType Is GetType(PclRadioColorAnimation) AndAlso
            colorOrigin.Name = "Origin" AndAlso colorTarget.Name = "Destination" AndAlso colorOrigin.PropertyType Is GetType(Color) AndAlso colorTarget.PropertyType Is GetType(Color) AndAlso
            DirectCast(colorOrigin.DefaultMetadata.DefaultValue, Color) = Colors.Transparent AndAlso DirectCast(colorTarget.DefaultMetadata.DefaultValue, Color) = Colors.Transparent,
            "control feedback retains the color dependency-property owners types and defaults")
        Dim originals As Freezable() = {New PclPressAnimation(0.873, PclPressKind.IconUp), New PclSelectedHeightAnimation(7, 32), New PclRadioColorAnimation(Colors.Red, Colors.Blue, 120)}
        check(originals.All(Function(original)
                                Dim clone = original.Clone()
                                clone.Freeze()
                                Dim first = DirectCast(original, Timeline), second = DirectCast(clone, Timeline)
                                Return clone.IsFrozen AndAlso clone.GetType() Is original.GetType() AndAlso first.Duration = second.Duration AndAlso
                                    If(TypeOf original Is PclPressAnimation, CDbl(clone.GetValue(pressOrigin)) = 0.873 AndAlso DirectCast(clone.GetValue(pressKind), PclPressKind) = PclPressKind.IconUp,
                                       If(TypeOf original Is PclSelectedHeightAnimation, CDbl(clone.GetValue(selectedOrigin)) = 7 AndAlso CDbl(clone.GetValue(selectedTarget)) = 32,
                                          DirectCast(clone.GetValue(colorOrigin), Color) = Colors.Red AndAlso DirectCast(clone.GetValue(colorTarget), Color) = Colors.Blue))
                            End Function), "control feedback cloning and freezing preserve every original animation value and duration")
        Dim facades = {
            New With {.Kind = GetType(PclPressMotion), .Arguments = New Type() {GetType(FrameworkElement), GetType(ScaleTransform), GetType(Action), GetType(Func(Of Boolean)), GetType(Func(Of Boolean))}, .Clock = GetType(AnimationClock)},
            New With {.Kind = GetType(PclResourceHoverMotion), .Arguments = New Type() {GetType(Border)}, .Clock = GetType(ClockGroup)},
            New With {.Kind = GetType(PclListHoverMotion), .Arguments = New Type() {GetType(Border), GetType(ScaleTransform)}, .Clock = GetType(ClockGroup)},
            New With {.Kind = GetType(PclListSelectionMotion), .Arguments = New Type() {GetType(PclListItem), GetType(Border)}, .Clock = GetType(ClockGroup)},
            New With {.Kind = GetType(PclLocalModSelectionMotion), .Arguments = New Type() {GetType(Border), GetType(TextBlock)}, .Clock = GetType(ClockGroup)}}
        For Each facade In facades
            Dim constructor = facade.Kind.GetConstructor(instance, Nothing, facade.Arguments, Nothing), transition = facade.Kind.GetProperty("Transition", instance)
            check(facade.Kind.Assembly Is app AndAlso facade.Kind.IsSealed AndAlso constructor IsNot Nothing AndAlso constructor.IsAssembly AndAlso transition IsNot Nothing AndAlso
                transition.PropertyType Is facade.Clock AndAlso transition.CanRead AndAlso Not transition.CanWrite AndAlso transition.GetGetMethod(True).IsAssembly,
                "control feedback retains the App facade constructor and read-only Transition contract: " & facade.Kind.Name)
        Next
        check(FeedbackMethod(GetType(PclPressMotion), "Start", GetType(PclPressKind)) AndAlso FeedbackMethod(GetType(PclPressMotion), "Cancel"),
            "control feedback retains the original press Start and Cancel signatures")
        check(FeedbackMethod(GetType(PclResourceHoverMotion), "Update", GetType(Boolean), GetType(Boolean), GetType(Color), GetType(Boolean), GetType(Boolean)) AndAlso
            FeedbackMethod(GetType(PclResourceHoverMotion), "RefreshPalette", GetType(Color)), "control feedback retains the original resource hover signatures")
        check(FeedbackMethod(GetType(PclListHoverMotion), "Update", GetType(Boolean), GetType(Boolean), GetType(Boolean), GetType(Color), GetType(Boolean)) AndAlso
            FeedbackMethod(GetType(PclListHoverMotion), "RefreshPalette", GetType(Color)), "control feedback retains the original list hover signatures")
        check(FeedbackMethod(GetType(PclListSelectionMotion), "Update", GetType(Boolean), GetType(Boolean)) AndAlso FeedbackMethod(GetType(PclListSelectionMotion), "RefreshPalette"),
            "control feedback retains the original list selection signatures")
        check(FeedbackMethod(GetType(PclLocalModSelectionMotion), "Update", GetType(Boolean), GetType(String), GetType(Boolean), GetType(Boolean)) AndAlso
            FeedbackMethod(GetType(PclLocalModSelectionMotion), "RefreshPalette", GetType(String)), "control feedback retains the original local Mod selection signatures")
        Dim optionalPress = GetType(PclPressMotion).GetConstructors(instance).Single().GetParameters()
        check(optionalPress(3).IsOptional AndAlso optionalPress(4).IsOptional AndAlso optionalPress(3).DefaultValue Is Nothing AndAlso optionalPress(4).DefaultValue Is Nothing,
            "control feedback retains both optional Nothing press-policy delegates")
    End Sub

    Private Shared Function FeedbackProperty(kind As Type, name As String) As DependencyProperty
        Return DirectCast(kind.GetField(name, BindingFlags.NonPublic Or BindingFlags.Static).GetValue(Nothing), DependencyProperty)
    End Function
    Private Shared Function FeedbackMethod(kind As Type, name As String, ParamArray arguments As Type()) As Boolean
        Dim method = kind.GetMethod(name, BindingFlags.NonPublic Or BindingFlags.Instance, Nothing, arguments, Nothing)
        Return method IsNot Nothing AndAlso method.IsAssembly AndAlso method.ReturnType Is GetType(Void)
    End Function
    Private Shared Function FeedbackCompleted(clock As Clock) As EventHandler
        ' Capture the real WPF Completed delegate while the old clock still owns it.
        ' Remove/SkipToFill alone cannot prove the guard: Remove suppresses delivery.
        Dim store = GetType(Clock).GetField("_eventHandlersStore", BindingFlags.NonPublic Or BindingFlags.Instance)?.GetValue(clock)
        Dim key = GetType(Timeline).GetField("CompletedKey", BindingFlags.NonPublic Or BindingFlags.Static)?.GetValue(Nothing)
        If store Is Nothing OrElse key Is Nothing Then Throw New InvalidOperationException("The net48 clock Completed event storage is unavailable.")
        Dim accessor = store.GetType().GetMethod("Get", BindingFlags.Public Or BindingFlags.NonPublic Or BindingFlags.Instance)
        Dim handler = TryCast(accessor?.Invoke(store, {key}), EventHandler)
        If handler Is Nothing Then Throw New InvalidOperationException("Expected the owned clock's actual Completed handler.")
        Return handler
    End Function
    Private Shared Sub FeedbackSeek(clock As Clock, milliseconds As Double, layout As Action)
        clock.Controller.Pause()
        If Not PumpMotionUntil(Function() clock.IsPaused) Then Throw New InvalidOperationException("The feedback clock did not pause.")
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
        layout()
    End Sub

    Private Shared Sub VerifyControlFeedbackClocks(check As Action(Of Boolean, String))
        Dim blue = Colors.CornflowerBlue, orange = Colors.OrangeRed
        Dim scale As New ScaleTransform(1, 1), pressOwner As New Border With {.Height = 30, .RenderTransform = scale}
        Dim allow = True, cancellations = 0, cancelledWhileOwned = False, press As PclPressMotion = Nothing
        press = New PclPressMotion(pressOwner, scale, Sub()
                                                        cancellations += 1
                                                        cancelledWhileOwned = press.Transition IsNot Nothing
                                                    End Sub, Function() allow, Function() True)
        press.Start(PclPressKind.ButtonDown)
        check(press.Transition Is Nothing AndAlso cancellations = 0 AndAlso scale.ScaleX = 1 AndAlso scale.ScaleY = 1,
            "feedback press before Loaded resets geometry without synthesizing gesture cancellation")
        Dim resourceBack As New Border With {.Height = 30, .Opacity = 0, .RenderTransform = New ScaleTransform(0.8, 0.8), .Background = New SolidColorBrush(blue)}
        Dim resource As New PclResourceHoverMotion(resourceBack)
        Dim listBack As New Border With {.Height = 30, .Opacity = 0, .RenderTransform = New ScaleTransform(0.75, 0.75), .Background = New SolidColorBrush(blue)}
        Dim whole As New ScaleTransform(1, 1), listContainer As New Border With {.Child = listBack, .RenderTransform = whole}
        Dim hover As New PclListHoverMotion(listBack, whole)
        Dim row As New PclListItem With {.Title = "feedback bridge", .Height = 44}
        Dim marker As New Border With {.Width = 5, .Height = 0, .Opacity = 0, .VerticalAlignment = VerticalAlignment.Center}
        Dim selected As New PclListSelectionMotion(row, marker), selectedHost As New Grid With {.Height = 44}
        selectedHost.Children.Add(row) : selectedHost.Children.Add(marker)
        Dim modMarker As New Border With {.Width = 5, .Height = 0, .Opacity = 0, .VerticalAlignment = VerticalAlignment.Center}, title As New TextBlock With {.Text = "Mod feedback", .Foreground = PclAnimation.Brush("ColorBrush1")}
        Dim local As New PclLocalModSelectionMotion(modMarker, title), localHost As New Grid With {.Height = 44}
        localHost.Children.Add(title) : localHost.Children.Add(modMarker)
        Dim rows As New StackPanel()
        For Each item As UIElement In New UIElement() {pressOwner, resourceBack, listContainer, selectedHost, localHost} : rows.Children.Add(item) : Next
        Dim host As New Border With {.Width = 480, .Height = 240, .Child = rows}, layout = ControlLayout(host)
        Using source = PreviewSurface.Attach(host, 480, 240)
            layout()
            allow = False : Dim previousCancellations = cancellations
            press.Start(PclPressKind.ButtonDown)
            check(press.Transition Is Nothing AndAlso cancellations = previousCancellations AndAlso Not scale.HasAnimatedProperties,
                "feedback reduced-motion press clears its scale without invoking gesture cancellation")
            allow = True : press.Start(PclPressKind.ButtonDown)
            Dim down = press.Transition : FeedbackSeek(down, 80, layout)
            check(down.Timeline.GetType() Is GetType(PclPressAnimation) AndAlso down.Timeline.Duration.TimeSpan.TotalMilliseconds = 700 AndAlso
                scale.ScaleX = scale.ScaleY AndAlso scale.ScaleX < 0.96 AndAlso scale.ScaleX > 0.94,
                "feedback press attaches the original VB animation to both live scale axes")
            Dim origin = scale.ScaleX
            press.Start(PclPressKind.ButtonUp)
            Dim up = press.Transition, latePress = FeedbackCompleted(up)
            Dim preserved = Math.Abs(scale.ScaleX - origin) < 0.000001
            FeedbackSeek(up, 60, layout) : origin = scale.ScaleX
            press.Start(PclPressKind.IconDown) : Dim replacement = press.Transition
            latePress(up, EventArgs.Empty)
            check(preserved AndAlso Math.Abs(scale.ScaleX - origin) < 0.000001 AndAlso press.Transition Is replacement,
                "feedback rapid press reversal preserves displayed scale and rejects a delivered old Completed callback")
            previousCancellations = cancellations : pressOwner.IsHitTestVisible = False : layout()
            check(cancellations = previousCancellations + 1 AndAlso press.Transition Is Nothing AndAlso Not scale.HasAnimatedProperties AndAlso scale.ScaleX = 1 AndAlso scale.ScaleY = 1,
                "feedback input gating cancels the gesture and releases both owned scale properties")
            pressOwner.IsHitTestVisible = True : press.Start(PclPressKind.ButtonDown)
            cancelledWhileOwned = False : press.Cancel()
            check(cancelledWhileOwned AndAlso press.Transition Is Nothing AndAlso Not scale.HasAnimatedProperties AndAlso scale.ScaleX = 1,
                "feedback explicit cancellation notifies the gesture before retiring its owned clock")

            Dim resourceFill = resourceBack.Background
            resource.Update(False, False, blue, False, False)
            check(resource.Transition Is Nothing AndAlso resourceBack.Opacity = 0 AndAlso DirectCast(resourceBack.RenderTransform, ScaleTransform).ScaleX = 0.8 AndAlso Not resourceBack.HasAnimatedProperties,
                "feedback resource hover snaps to the original inactive geometry without empty clocks")
            resource.Update(True, False, blue, True, True)
            Dim resourceEntry = resource.Transition : FeedbackSeek(resourceEntry, 60, layout)
            check(resourceEntry.Children.Count = 3 AndAlso resourceEntry.Children(2).Timeline.GetType() Is GetType(PclRadioColorAnimation) AndAlso
                resourceEntry.Children(1).Timeline.Duration.TimeSpan.TotalMilliseconds = 144 AndAlso resourceBack.Opacity > 0 AndAlso resourceBack.Opacity < 1,
                "feedback resource entry preserves the App color factory and source backing-plate timing")
            Dim resourceHeight = DirectCast(resourceBack.RenderTransform, ScaleTransform).ScaleX, resourceAlpha = resourceBack.Opacity
            resource.RefreshPalette(orange)
            check(resource.Transition Is resourceEntry AndAlso resourceBack.Background Is resourceFill AndAlso resourceBack.Opacity = resourceAlpha AndAlso
                DirectCast(resourceBack.RenderTransform, ScaleTransform).ScaleX = resourceHeight AndAlso DirectCast(resourceFill, SolidColorBrush).Color = orange,
                "feedback resource palette refresh preserves the active geometry clock and owned brush")
            resource.Update(False, False, orange, True, True)
            Dim resourceExit = resource.Transition, lateResource = FeedbackCompleted(resourceExit)
            FeedbackSeek(resourceExit, 70, layout) : resourceAlpha = resourceBack.Opacity
            resource.Update(True, True, orange, True, True) : resourceEntry = resource.Transition
            preserved = resourceBack.Opacity = resourceAlpha : lateResource(resourceExit, EventArgs.Empty)
            preserved = preserved AndAlso resource.Transition Is resourceEntry
            resource.Update(False, False, orange, False, False)
            check(preserved AndAlso resource.Transition Is Nothing AndAlso Not resourceBack.HasAnimatedProperties AndAlso Not DirectCast(resourceBack.RenderTransform, ScaleTransform).HasAnimatedProperties AndAlso
                Not DirectCast(resourceBack.Background, SolidColorBrush).HasAnimatedProperties AndAlso resourceBack.Opacity = 0,
                "feedback resource reversal rejects delivered stale completion and a snap removes every animated property")

            hover.Update(False, False, False, blue, False)
            check(hover.Transition Is Nothing AndAlso whole.ScaleX = 1 AndAlso DirectCast(listBack.RenderTransform, ScaleTransform).ScaleX = 0.75 AndAlso listBack.Opacity = 0,
                "feedback list hover preserves its distinct inactive backing and whole-row geometry")
            hover.Update(True, True, False, blue, True)
            Dim listEntry = hover.Transition : FeedbackSeek(listEntry, 55, layout)
            check(listEntry.Children.Count = 4 AndAlso listEntry.Children(2).Timeline.Duration.TimeSpan.TotalMilliseconds = 108 AndAlso
                listEntry.Children(3).Timeline.GetType() Is GetType(ColorAnimation) AndAlso whole.ScaleX < 1 AndAlso whole.ScaleX = whole.ScaleY,
                "feedback list hover preserves the whole-row press and original WPF color timeline")
            Dim listFill = listBack.Background, listAlpha = listBack.Opacity, listScale = whole.ScaleX
            For index = 1 To 20 : hover.Update(True, True, False, blue, True) : Next
            hover.RefreshPalette(orange)
            check(hover.Transition Is listEntry AndAlso listBack.Background Is listFill AndAlso listBack.Opacity = listAlpha AndAlso whole.ScaleX = listScale AndAlso DirectCast(listFill, SolidColorBrush).Color = orange,
                "feedback repeated list state and palette-only updates retain the same brush and geometry clock")
            hover.Update(False, False, False, orange, True)
            Dim listExit = hover.Transition, lateList = FeedbackCompleted(listExit)
            FeedbackSeek(listExit, 65, layout) : listAlpha = listBack.Opacity
            hover.Update(True, False, False, orange, True) : listEntry = hover.Transition
            preserved = listBack.Opacity = listAlpha : lateList(listExit, EventArgs.Empty)
            preserved = preserved AndAlso hover.Transition Is listEntry
            hover.Update(False, False, True, orange, False)
            check(preserved AndAlso hover.Transition Is Nothing AndAlso Not whole.HasAnimatedProperties AndAlso Not listBack.HasAnimatedProperties AndAlso
                Not DirectCast(listBack.RenderTransform, ScaleTransform).HasAnimatedProperties AndAlso whole.ScaleX = 1 AndAlso DirectCast(listBack.RenderTransform, ScaleTransform).ScaleX = 1,
                "feedback list reversal rejects old completion and sidebar snap clears whole-row and backing clocks")

            selected.Update(False, False)
            check(selected.Transition Is Nothing AndAlso marker.Height = 0 AndAlso marker.Opacity = 0,
                "feedback list selection snaps its inactive marker without creating a clock")
            selected.Update(True, True)
            Dim selectionEntry = selected.Transition : FeedbackSeek(selectionEntry, 70, layout)
            check(selectionEntry.Children.Count = 3 AndAlso selectionEntry.Children(0).Timeline.GetType() Is GetType(PclSelectedHeightAnimation) AndAlso marker.Height > 0 AndAlso marker.Opacity > 0,
                "feedback list selection uses the original App height animation on the live marker")
            Dim markerHeight = marker.Height, markerAlpha = marker.Opacity
            selected.Update(True, True) : selected.RefreshPalette()
            check(selected.Transition Is selectionEntry AndAlso marker.Height = markerHeight AndAlso marker.Opacity = markerAlpha,
                "feedback identical list selection and palette refresh retain the existing marker clock")
            selected.Update(False, True)
            Dim selectionExit = selected.Transition, lateSelection = FeedbackCompleted(selectionExit)
            FeedbackSeek(selectionExit, 55, layout) : selected.Update(True, True) : selectionEntry = selected.Transition
            lateSelection(selectionExit, EventArgs.Empty) : preserved = selected.Transition Is selectionEntry
            selected.Update(True, False)
            check(preserved AndAlso selected.Transition Is Nothing AndAlso Double.IsNaN(marker.Height) AndAlso marker.VerticalAlignment = VerticalAlignment.Stretch AndAlso
                marker.Margin.Top = 6 AndAlso marker.Margin.Bottom = 6 AndAlso marker.Opacity = 1 AndAlso Not marker.HasAnimatedProperties,
                "feedback list reversal rejects old completion and selected snap restores Auto stretch geometry")

            local.Update(False, "ColorBrush1", False, False)
            check(local.Transition Is Nothing AndAlso modMarker.Height = 0 AndAlso modMarker.Opacity = 0,
                "feedback local Mod selection snaps its inactive marker independently of hover")
            local.Update(True, "ColorBrush3", True, True)
            Dim modEntry = local.Transition : FeedbackSeek(modEntry, 70, layout)
            check(modEntry.Children.Count = 3 AndAlso modEntry.Children(0).Timeline.GetType() Is GetType(PclSelectedHeightAnimation) AndAlso
                modEntry.Children(2).Timeline.GetType() Is GetType(PclRadioColorAnimation) AndAlso modEntry.IsPaused AndAlso modMarker.Height > 0,
                "feedback local Mod selection retains both original VB animation factories and controllable pause")
            markerHeight = modMarker.Height : markerAlpha = modMarker.Opacity
            local.Update(True, "ColorBrush2", True, True)
            check(local.Transition Is modEntry AndAlso modMarker.Height = markerHeight AndAlso modMarker.Opacity = markerAlpha AndAlso
                DirectCast(title.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color,
                "feedback local Mod metadata changes update title ink without restarting checked geometry")
            local.Update(False, "ColorBrush1", True, True)
            Dim modExit = local.Transition, lateMod = FeedbackCompleted(modExit)
            FeedbackSeek(modExit, 60, layout) : markerHeight = modMarker.Height : markerAlpha = modMarker.Opacity
            local.RefreshPalette("ColorBrush3")
            check(local.Transition Is modExit AndAlso modMarker.Height = markerHeight AndAlso modMarker.Opacity = markerAlpha AndAlso
                DirectCast(title.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                "feedback local Mod palette changes preserve a partly exited marker and its sampled opacity")
            local.Update(True, "ColorBrush2", True, True) : modEntry = local.Transition
            preserved = modMarker.Height = markerHeight AndAlso modMarker.Opacity = markerAlpha
            lateMod(modExit, EventArgs.Empty) : preserved = preserved AndAlso local.Transition Is modEntry
            local.Update(False, "ColorBrush1", False, False)
            check(preserved AndAlso local.Transition Is Nothing AndAlso modMarker.Height = 0 AndAlso modMarker.Opacity = 0 AndAlso Not modMarker.HasAnimatedProperties AndAlso
                Not DirectCast(title.Foreground, SolidColorBrush).HasAnimatedProperties,
                "feedback local Mod reversal rejects delivered stale completion and snap releases marker and ink clocks")
        End Using
    End Sub
End Class
