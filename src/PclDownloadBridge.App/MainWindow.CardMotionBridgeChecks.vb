Imports System.IO
Imports System.Reflection
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Markup
Imports System.Windows.Media.Animation
Imports Microsoft.VisualBasic.CompilerServices
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyCardMotionBridgeUi(directory As String, passed As List(Of String))
        ' PCL 5ef81e4 MyCard.StartHeightAnimation and PCL-CE 915ac297 MyCard:
        ' preserve the 800-DIP boundary, integer leg timing and VB WPF identities.
        ' Exercise the accepted PclCard surface, not a replacement engine's fields.
        Dim before = passed.Count, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        IO.Directory.CreateDirectory(directory)
        Try
            VerifyCardBridgeContracts(check)
            VerifyCardBridgeClocks(check, observations)
            If passed.Count - before <> 39 Then Throw New InvalidOperationException("Card motion bridge must report exactly 39 observations.")
        Finally
            observations("Count") = passed.Count - before
            observations("Checks") = New JArray(passed.Skip(before))
            observations("WindowsConfigurationChanged") = False
            File.WriteAllText(Path.Combine(directory, "card-motion-bridge.json"), observations.ToString())
        End Try
    End Sub

    Private Shared Sub VerifyCardBridgeContracts(check As Action(Of Boolean, String))
        Dim app = GetType(MainWindow).Assembly, instance = BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic
        Dim moduleType = app.GetType("PclDownloadBridge.Controls.PclCardMotion", True)
        check(moduleType.IsDefined(GetType(StandardModuleAttribute), False) AndAlso moduleType.Assembly Is app,
            "card bridge retains the original VB standard module in the App assembly")
        Dim height = moduleType.GetMethod("HeightTransition", BindingFlags.NonPublic Or BindingFlags.Static)
        check(height.IsAssembly AndAlso height.ReturnType Is GetType(DoubleAnimationUsingKeyFrames) AndAlso height.GetParameters().Select(Function(item) item.ParameterType).SequenceEqual({GetType(Double), GetType(Double)}),
            "card bridge retains the Friend Double height factory signature")
        Dim arrow = moduleType.GetMethod("ArrowTransition", BindingFlags.NonPublic Or BindingFlags.Static)
        check(arrow.IsAssembly AndAlso arrow.ReturnType Is GetType(DoubleAnimation) AndAlso arrow.GetParameters().Select(Function(item) item.ParameterType).SequenceEqual({GetType(Double), GetType(Double)}),
            "card bridge retains the Friend Double arrow factory signature")
        Dim kind = GetType(PclInitialSpeedEase), alpha = DirectCast(kind.GetField("AlphaProperty", BindingFlags.NonPublic Or BindingFlags.Static).GetValue(Nothing), DependencyProperty)
        check(kind.Assembly Is app AndAlso kind.IsSealed AndAlso kind.BaseType Is GetType(EasingFunctionBase),
            "card bridge retains the original VB InitialSpeedEase WPF type identity")
        check(alpha.OwnerType Is kind AndAlso alpha.Name = "Alpha" AndAlso alpha.PropertyType Is GetType(Double) AndAlso CDbl(alpha.DefaultMetadata.DefaultValue) = 0,
            "card bridge retains the Alpha dependency-property owner type name and default")
        Dim defaults As New PclInitialSpeedEase()
        check(defaults.EasingMode = EasingMode.EaseIn AndAlso defaults.Ease(-1) = 0 AndAlso defaults.Ease(0.5) = 0.5 AndAlso defaults.Ease(2) = 1,
            "card bridge preserves the original ease mode linear fallback and progress clamping")
        Dim ease As New PclInitialSpeedEase(4000, 0.2, 150), frozen = DirectCast(ease.Clone(), PclInitialSpeedEase)
        frozen.Freeze()
        check(frozen.IsFrozen AndAlso frozen.GetType() Is kind AndAlso frozen.EasingMode = EasingMode.EaseIn AndAlso
            CDbl(frozen.GetValue(alpha)) = CDbl(ease.GetValue(alpha)) AndAlso Math.Abs(frozen.Ease(0.5) - 16.0 / 19) < 0.0000001,
            "card bridge cloning and freezing retain the initial-speed ratio dependency value")
        Dim longPlan = PclCardMotion.HeightTransition(40, 5040), clone = longPlan.Clone()
        clone.Freeze()
        check(clone.IsFrozen AndAlso clone.Duration = longPlan.Duration AndAlso DirectCast(clone.KeyFrames(clone.KeyFrames.Count - 1), EasingDoubleKeyFrame).EasingFunction.GetType() Is kind AndAlso
            Math.Abs(SampleCardMotion(clone, 500) - SampleCardMotion(longPlan, 500)) < 0.0000001,
            "card bridge frozen height plans retain the original App easing factory and sampled values")
        Dim empty = kind.GetConstructor(Type.EmptyTypes)
        check(empty IsNot Nothing AndAlso empty.IsPublic, "card bridge retains the public parameterless WPF easing constructor")
        Dim configured = kind.GetConstructor(instance, Nothing, {GetType(Double), GetType(Double), GetType(Double)}, Nothing)
        check(configured IsNot Nothing AndAlso configured.IsAssembly, "card bridge retains the Friend three-Double easing constructor")
        Dim shortPlan = PclCardMotion.HeightTransition(40, 840)
        check(shortPlan.KeyFrames.Count = 2 AndAlso shortPlan.Duration.TimeSpan.TotalMilliseconds = 150 AndAlso shortPlan.KeyFrames(0).Value = 40 AndAlso
            shortPlan.KeyFrames(1).Value = 840 AndAlso Math.Abs(SampleCardMotion(shortPlan, 75) - 815) < 0.0000001,
            "card bridge keeps PCL's inclusive 800-DIP short-distance boundary and fifth-power midpoint")
        Dim medium = PclCardMotion.HeightTransition(40, 840.1)
        check(medium.KeyFrames.Count = 3 AndAlso medium.Duration.TimeSpan.TotalMilliseconds = 363 AndAlso medium.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 163 AndAlso
            Math.Abs(medium.KeyFrames(1).Value - 690.1) < 0.0000001,
            "card bridge preserves the first medium-distance integer timing beyond 800 DIP")
        Dim large = PclCardMotion.HeightTransition(40, 3040.1)
        check(large.KeyFrames.Count = 3 AndAlso large.Duration.TimeSpan.TotalMilliseconds = 700 AndAlso large.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 300,
            "card bridge preserves the long-distance branch immediately beyond 3000 DIP")
        Dim reverse = PclCardMotion.HeightTransition(1040, 40)
        check(reverse.Duration.TimeSpan.TotalMilliseconds = 250 AndAlso reverse.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 100 AndAlso reverse.KeyFrames(1).Value = 240 AndAlso reverse.KeyFrames(reverse.KeyFrames.Count - 1).Value = 40,
            "card bridge preserves the reverse linear leg and 150-ms easing finish")
        Dim rounded = PclCardMotion.HeightTransition(40, 1040)
        check(rounded.KeyFrames(1).KeyTime.TimeSpan.TotalMilliseconds = 212 AndAlso rounded.Duration.TimeSpan.TotalMilliseconds = 412,
            "card bridge preserves VB banker's rounding at the half-millisecond leg boundary")
        Dim rotation = PclCardMotion.ArrowTransition(270, 180)
        check(rotation.From.GetValueOrDefault() = 270 AndAlso rotation.To.GetValueOrDefault() = 180 AndAlso rotation.Duration.TimeSpan.TotalMilliseconds = 250 AndAlso
            Math.Abs(rotation.EasingFunction.Ease(0.5) - 0.96875) < 0.0000001,
            "card bridge preserves arrow endpoints duration and ExtraStrong easing independently of height")
        Dim transition = GetType(PclCard).GetProperty("HeightTransition", instance), body = GetType(PclCard).GetProperty("Body")
        check(GetType(PclCard).Assembly Is app AndAlso GetType(PclCard).IsPublic AndAlso GetType(PclCard).BaseType Is GetType(Grid) AndAlso
            transition.PropertyType Is GetType(AnimationClock) AndAlso transition.GetGetMethod(True).IsAssembly AndAlso Not transition.CanWrite AndAlso
            body.PropertyType Is GetType(UIElement) AndAlso body.CanWrite AndAlso DirectCast(Attribute.GetCustomAttribute(GetType(PclCard), GetType(ContentPropertyAttribute)), ContentPropertyAttribute).Name = "Body",
            "card bridge retains the public card content contract and Friend read-only height clock")
    End Sub

    Private Shared Function CardBridgeTarget(clock As AnimationClock) As Double
        Dim frames = DirectCast(clock.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames
        Return frames(frames.Count - 1).Value
    End Function
    Private Shared Function CardBridgeClock(card As PclCard, stage As String) As AnimationClock
        If card.HeightTransition Is Nothing Then Throw New InvalidOperationException("Card bridge fixture has no clock at " & stage &
            "; loaded=" & card.IsLoaded & "; visible=" & card.IsVisible & "; collapsed=" & card.Collapsed & "; width=" & card.ActualWidth &
            "; height=" & card.Height & "; actual=" & card.ActualHeight & "; desired=" & card.DesiredSize.Height)
        Return card.HeightTransition
    End Function
    Private Shared Function CardBridgeFinish(card As PclCard, layout As Action) As Boolean
        Dim clock = card.HeightTransition
        If clock IsNot Nothing Then FeedbackSeek(clock, clock.Timeline.Duration.TimeSpan.TotalMilliseconds + 1, layout)
        Return PumpMotionUntil(Function() card.HeightTransition Is Nothing, 1000)
    End Function
    Private Shared Sub VerifyCardBridgeClocks(check As Action(Of Boolean, String), observations As JObject)
        Dim builds = 0, original As Border = Nothing
        Dim card As New PclCard With {.Title = "Card bridge", .Collapsible = True, .Collapsed = True, .PauseArrowForChecks = True, .BodyMargin = New Thickness(0, 40, 0, 0)}
        card.Builder = Function()
                           builds += 1
                           original = New Border With {.Height = 1400}
                           Return original
                       End Function
        check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso card.Body Is Nothing AndAlso builds = 0,
            "card bridge before Loaded retains the collapsed height without building its body")
        Dim scroll As New ScrollViewer With {.Content = card, .VerticalScrollBarVisibility = ScrollBarVisibility.Auto, .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled}
        Dim host As New Border With {.Width = 520, .Height = 260, .Child = scroll}, layout = ControlLayout(host)
        Using source = PreviewSurface.Attach(host, 520, 260)
            layout()
            check(card.IsLoaded AndAlso card.HeightTransition Is Nothing AndAlso card.ActualHeight = 40 AndAlso builds = 0,
                "card bridge first collapsed layout remains lazy and creates no empty height animation")
            card.Collapsed = False
            Dim opening = card.HeightTransition, lateOpening = FeedbackCompleted(opening)
            check(opening IsNot Nothing AndAlso builds = 1 AndAlso card.Body Is original AndAlso card.ClipToBounds AndAlso CardBridgeTarget(opening) = 1440,
                "card bridge first expansion binds one body and owns its measured clipped height target")
            FeedbackSeek(opening, 80, layout)
            Dim sampled = SampleCardMotion(DirectCast(opening.Timeline, DoubleAnimationUsingKeyFrames), opening.CurrentTime.Value.TotalMilliseconds)
            observations("openingTimeMs") = opening.CurrentTime.Value.TotalMilliseconds
            observations("openingActualHeight") = card.ActualHeight
            observations("openingTarget") = CardBridgeTarget(opening)
            check(Math.Abs(card.Height - sampled) < 0.001 AndAlso Math.Abs(card.ActualHeight - sampled) < 0.001 AndAlso card.Body Is original,
                "card bridge live WPF height and arranged height follow the original keyframe plan")
            Dim displayed = card.ActualHeight
            card.Collapsed = True
            Dim closing = card.HeightTransition, lateClosing = FeedbackCompleted(closing)
            lateOpening(opening, EventArgs.Empty)
            check(card.HeightTransition Is closing AndAlso card.Collapsed AndAlso Math.Abs(card.Height - displayed) < 0.001 AndAlso card.ClipToBounds,
                "card bridge rapid collapse keeps displayed height and rejects a delivered old expansion completion")
            card.Collapsed = True
            check(card.HeightTransition Is closing AndAlso builds = 1 AndAlso card.Body Is original,
                "card bridge reapplying the accepted fold keeps its body builder and owned clock unchanged")
            FeedbackSeek(closing, 35, layout) : displayed = card.ActualHeight
            card.Collapsed = False : opening = card.HeightTransition
            check(opening IsNot closing AndAlso Math.Abs(card.Height - displayed) < 0.001 AndAlso builds = 1 AndAlso Not card.Collapsed,
                "card bridge quick reopen starts from the displayed collapse height without rebuilding content")
            lateClosing(closing, EventArgs.Empty)
            check(card.HeightTransition Is opening AndAlso Not card.Collapsed AndAlso CardBridgeTarget(opening) = 1440 AndAlso card.Body Is original,
                "card bridge a delivered retired collapse completion cannot settle the newer expanded owner")
            FeedbackSeek(opening, 45, layout)
            Dim nested = 0, reenter = True
            Dim reentrant As EventHandler = Sub()
                                               nested += 1
                                               If card.Collapsed AndAlso reenter Then reenter = False : card.Collapsed = False
                                           End Sub
            AddHandler card.FoldChanged, reentrant
            Try
                card.Collapsed = True
            Finally
                RemoveHandler card.FoldChanged, reentrant
            End Try
            check(nested = 2 AndAlso Not card.Collapsed AndAlso card.HeightTransition IsNot Nothing AndAlso card.HeightTransition IsNot opening AndAlso CardBridgeTarget(card.HeightTransition) = 1440,
                "card bridge synchronous FoldChanged reentry leaves ownership with the latest expansion")
            card.Visibility = Visibility.Hidden : layout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso Not card.ClipToBounds AndAlso card.Body Is original,
                "card bridge hiding a running expansion releases its height clock while retaining accepted content")
            card.Visibility = Visibility.Visible : layout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso card.Body Is original AndAlso builds = 1,
                "card bridge showing settled content restores Auto layout without an empty animation or rebuild")
            card.Collapsed = True : closing = card.HeightTransition : FeedbackSeek(closing, 25, layout)
            card.IsEnabled = False : card.IsHitTestVisible = False : layout()
            check(card.HeightTransition Is closing AndAlso card.Collapsed AndAlso card.ClipToBounds AndAlso card.Body Is original,
                "card bridge page input gates preserve an already accepted fold and its independent height clock")
            card.Collapsed = False
            check(card.HeightTransition IsNot Nothing AndAlso card.HeightTransition IsNot closing AndAlso Not card.Collapsed AndAlso card.ClipToBounds,
                "card bridge programmatic fold changes retain their animation while gesture input is gated")
            card.IsEnabled = True : card.IsHitTestVisible = True
            card.Collapsed = True : closing = card.HeightTransition : FeedbackSeek(closing, 25, layout)
            Dim refreshBuilds = 0, replacement As Border = Nothing
            card.RefreshBody(Function()
                                 refreshBuilds += 1
                                 replacement = New Border With {.Height = 420}
                                 Return replacement
                             End Function)
            check(card.HeightTransition Is closing AndAlso card.HasDeferredBodyRefresh AndAlso card.Body Is original AndAlso Not original.IsEnabled AndAlso refreshBuilds = 0,
                "card bridge a folded body refresh retains only its gated retiring content until height exit")
            Dim finished = CardBridgeFinish(card, layout)
            check(finished AndAlso card.Height = 40 AndAlso Not card.ClipToBounds AndAlso card.Body Is Nothing AndAlso Not card.HasDeferredBodyRefresh AndAlso refreshBuilds = 0,
                "card bridge fold completion discards retired content without eagerly invoking its replacement builder")
            card.Collapsed = False : layout()
            check(refreshBuilds = 1 AndAlso card.Body Is replacement AndAlso replacement.IsEnabled AndAlso Not card.HasDeferredBodyRefresh AndAlso card.HeightTransition IsNot Nothing,
                "card bridge reopening refreshed content builds once and clears its inherited retirement input gate")

            Dim text As New TextBlock With {.TextWrapping = TextWrapping.Wrap, .Text = String.Join(" ", Enumerable.Repeat("Minecraft 卡片宽度重排", 180))}
            Dim wrapping As New PclCard With {.Title = "Wrapping bridge", .Collapsible = True, .Collapsed = True, .PauseArrowForChecks = True, .BodyMargin = New Thickness(0, 40, 0, 0), .Body = text}
            ' Keep scrollbar availability fixed: this fixture changes the host width,
            ' not an additional width caused by the first expanding scrollbar.
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible
            scroll.Content = wrapping : layout()
            observations("wrapLoaded") = wrapping.IsLoaded : observations("wrapVisible") = wrapping.IsVisible
            observations("wrapInitialWidth") = wrapping.ActualWidth : observations("wrapInitialHeight") = wrapping.ActualHeight
            wrapping.Collapsed = False
            Dim wide = CardBridgeClock(wrapping, "initial wrapping expansion"), lateWide = FeedbackCompleted(wide)
            FeedbackSeek(wide, 40, layout)
            Dim wideTarget = CardBridgeTarget(wide)
            host.Width = 280
            host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height)) : host.UpdateLayout()
            ' Reserve the accepted retarget before a dispatcher pump can naturally
            ' finish its real clock while formatting the large wrapping text.
            Dim narrow = CardBridgeClock(wrapping, "narrow wrapping reflow"), narrowTarget = CardBridgeTarget(narrow)
            narrow.Controller.Pause() : layout()
            observations("wideTarget") = wideTarget : observations("narrowTarget") = narrowTarget
            check(narrow IsNot wide AndAlso narrowTarget > wideTarget AndAlso wrapping.Body Is text AndAlso wrapping.ClipToBounds,
                "card bridge width reflow remeasures accepted wrapping content and replaces its obsolete height target")
            lateWide(wide, EventArgs.Empty)
            check(wrapping.HeightTransition Is narrow AndAlso CardBridgeTarget(narrow) = narrowTarget AndAlso wrapping.Body Is text AndAlso wrapping.ClipToBounds,
                "card bridge a delivered pre-reflow completion cannot clear the new width-dependent owner")
            finished = CardBridgeFinish(wrapping, layout)
            check(finished AndAlso Double.IsNaN(wrapping.Height) AndAlso Not wrapping.ClipToBounds AndAlso wrapping.Body Is text AndAlso Math.Abs(wrapping.ActualHeight - narrowTarget) < 1,
                "card bridge reflow completion restores Auto height at the newly measured wrapping target")
            wrapping.Collapsed = True : FeedbackSeek(wrapping.HeightTransition, 30, layout) : wrapping.Collapsed = False
            source.RootVisual = Nothing : FlushControlLayout()
            check(wrapping.HeightTransition Is Nothing AndAlso Double.IsNaN(wrapping.Height) AndAlso Not wrapping.ClipToBounds AndAlso wrapping.Body Is text,
                "card bridge unloading a reversing card releases animated height while preserving the latest body")
            Dim tiny As New PclCard With {.Title = "Sub-DIP", .Collapsible = True, .Collapsed = True, .BodyMargin = New Thickness(0, 40, 0, 0), .Body = New Border With {.Height = 0.4}}
            scroll.Content = tiny : source.RootVisual = host : layout() : tiny.Collapsed = False : layout()
            check(tiny.HeightTransition Is Nothing AndAlso Double.IsNaN(tiny.Height) AndAlso Not tiny.ClipToBounds AndAlso tiny.Body IsNot Nothing,
                "card bridge sub-DIP height differences settle directly without allocating an empty clock")
            source.RootVisual = Nothing : FlushControlLayout()
            Dim fixedCard As New PclCard With {.Title = "Fixed bridge", .Height = 123}
            scroll.Content = fixedCard : source.RootVisual = host : layout()
            source.RootVisual = Nothing : FlushControlLayout()
            check(fixedCard.HeightTransition Is Nothing AndAlso fixedCard.Height = 123 AndAlso Not fixedCard.ClipToBounds,
                "card bridge an unloaded nonanimated card retains its explicit fixed height")
        End Using
    End Sub
End Class
