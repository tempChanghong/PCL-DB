Imports System.ComponentModel
Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyBeta9SwitchReentryUi(directory As String, passed As List(Of String))
        Dim before = passed.Count
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Try
            VerifyBeta9SwitchDelegates(check)
            VerifyBeta9SwitchWrites(check)
            VerifyBeta9SwitchClocks(check)
            VerifyBeta9SwitchRetiredError(check)
            VerifyBeta9SwitchCardProperties(check)
            If passed.Count - before <> 20 Then Throw New InvalidOperationException("Beta 9 switch reentry must report 20 checks.")
        Finally
            DirectoryCreateBeta9Switch(directory)
            File.WriteAllText(Path.Combine(directory, "beta9-switch-reentry.json"), New JObject From {
                {"Count", passed.Count - before}, {"Checks", New JArray(passed.Skip(before))}, {"WindowsConfigurationChanged", False}}.ToString())
        End Try
    End Sub

    Public Sub VerifyBeta9SwitchClockReentryUi(directory As String, passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        VerifyBeta9SwitchClocks(check)
    End Sub

    Public Sub VerifyBeta9SwitchCardReentryUi(directory As String, passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        VerifyBeta9SwitchCardProperties(check)
    End Sub

    Private Shared Sub DirectoryCreateBeta9Switch(directory As String)
        IO.Directory.CreateDirectory(directory)
    End Sub

    Private NotInheritable Class Beta9SwitchFixture
        Implements IDisposable
        Friend ReadOnly Versions As New Canvas()
        Friend ReadOnly Install As New Canvas()
        Friend ReadOnly Host As Border
        Friend ReadOnly Motion As PclPageSwitch
        Friend ReadOnly Layout As Action
        Friend Enabled As Func(Of Boolean) = Function() True
        Friend CardsEnabled As Func(Of Boolean) = Function() True
        Friend Settled As Integer
        Private ReadOnly _source As HwndSource

        Friend Sub New(cards As Boolean)
            Dim panel As New Grid()
            panel.Children.Add(Versions) : panel.Children.Add(Install)
            Dim oldCard As New Border With {.Width = 380, .Height = 50, .Background = Brushes.LightBlue}
            Dim newCard As New Border With {.Width = 380, .Height = 50, .Background = Brushes.White}
            Versions.Children.Add(oldCard) : Install.Children.Add(newCard)
            Host = New Border With {.Width = 460, .Height = 220, .Child = panel}
            Layout = MainWindow.ControlLayout(Host)
            Motion = New PclPageSwitch(Host, Versions, Install, Function() Enabled(),
                If(cards, Function(root As FrameworkElement) If(root Is Versions, New FrameworkElement() {oldCard}, New FrameworkElement() {newCard}), Nothing),
                Function() CardsEnabled())
            If cards Then Motion.CardMotion.PauseForChecks = True
            AddHandler Motion.Settled, Sub() Settled += 1
            _source = PreviewSurface.Attach(Host, 460, 220)
            Layout()
        End Sub

        Friend Function IsSettled(installSelected As Boolean) As Boolean
            Return Motion.Transition Is Nothing AndAlso Not Motion.HasMotion AndAlso Host.IsHitTestVisible AndAlso
                Versions.Visibility = If(installSelected, Visibility.Collapsed, Visibility.Visible) AndAlso
                Install.Visibility = If(installSelected, Visibility.Visible, Visibility.Collapsed) AndAlso
                Versions.Opacity = If(installSelected, 0, 1) AndAlso Install.Opacity = If(installSelected, 1, 0)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                Motion.Finish()
            Finally
                _source.Dispose()
            End Try
        End Sub
    End Class

    Private Shared Sub VerifyBeta9SwitchDelegates(check As Action(Of Boolean, String))
        For Each cards In {False, True}
            For Each result In {True, False}
                For mode = 0 To 2
                    Dim selectedMode = mode
                    Using fixture As New Beta9SwitchFixture(cards)
                        Dim armed = True, calls = 0, accepted As ClockGroup = Nothing, acceptedCards As ClockGroup = Nothing
                        Dim callback As Func(Of Boolean) = Function()
                                                               If Not armed Then Return True
                                                               armed = False : calls += 1
                                                               If selectedMode = 0 Then
                                                                   fixture.Motion.SelectInstall(False, False)
                                                               ElseIf selectedMode = 1 Then
                                                                   fixture.Motion.Finish()
                                                               Else
                                                                   fixture.Motion.SelectInstall(False, True)
                                                                   accepted = fixture.Motion.Transition
                                                                   accepted.Controller.Pause()
                                                                   If cards Then acceptedCards = fixture.Motion.CardMotion.Transition
                                                               End If
                                                               Return result
                                                           End Function
                        If cards Then
                            fixture.CardsEnabled = callback
                        Else
                            fixture.Enabled = callback
                        End If
                        fixture.Motion.SelectInstall(True, True)
                        Dim ok As Boolean
                        If mode < 2 Then
                            ok = fixture.IsSettled(mode = 1) AndAlso fixture.Settled = 1
                        Else
                            ok = accepted IsNot Nothing AndAlso fixture.Motion.Transition Is accepted AndAlso Not fixture.Host.IsHitTestVisible AndAlso fixture.Settled = 0 AndAlso
                                (Not cards OrElse fixture.Motion.CardMotion.Transition Is acceptedCards)
                        End If
                        check(ok AndAlso calls = 1, "beta9 switch retires " & If(cards, "card", "page") & " enable caller after accepted " & {"return", "finish", "animated return"}(mode) & " with result " & result)
                    End Using
                Next
            Next
        Next
    End Sub

    Private Shared Sub VerifyBeta9SwitchWrites(check As Action(Of Boolean, String))
        For scenario = 0 To 2
            Dim selectedScenario = scenario
            Using fixture As New Beta9SwitchFixture(False)
                If scenario <> 1 Then fixture.Motion.SelectInstall(True, False)
                fixture.Settled = 0
                Dim propertyOwner As DependencyObject = If(scenario = 1, DirectCast(fixture.Host, DependencyObject), fixture.Versions)
                Dim dp = If(scenario = 0, UIElement.VisibilityProperty, UIElement.IsHitTestVisibleProperty)
                Dim descriptor = DependencyPropertyDescriptor.FromProperty(dp, propertyOwner.GetType()), armed = True
                Dim handler As EventHandler = Sub(sender, args)
                                                 If Not armed Then Return
                                                 Dim ready = If(selectedScenario = 0, fixture.Versions.Visibility = Visibility.Visible,
                                                     If(selectedScenario = 1, Not fixture.Host.IsHitTestVisible, fixture.Versions.IsHitTestVisible))
                                                 If Not ready Then Return
                                                 armed = False
                                                 If selectedScenario = 1 Then
                                                     fixture.Motion.Finish()
                                                 Else
                                                     fixture.Motion.SelectInstall(True, False)
                                                 End If
                                             End Sub
                descriptor.AddValueChanged(propertyOwner, handler)
                Try
                    fixture.Motion.SelectInstall(scenario = 1, True)
                    check(Not armed AndAlso fixture.IsSettled(True) AndAlso fixture.Settled = 1,
                        "beta9 switch preserves a new accepted state from synchronous page property write " & scenario)
                Finally
                    descriptor.RemoveValueChanged(propertyOwner, handler)
                End Try
            End Using
        Next
    End Sub

    Private Shared Sub VerifyBeta9SwitchClocks(check As Action(Of Boolean, String))
        For Each animatedReplacement In {True, False}
                Using fixture As New Beta9SwitchFixture(False)
                    Dim vx = DirectCast(fixture.Versions.RenderTransform, TranslateTransform), ix = DirectCast(fixture.Install.RenderTransform, TranslateTransform)
                    fixture.Versions.Opacity = 0.8 : fixture.Install.Opacity = 0.2
                    Dim engine As New Global.PclDownloadBridge.Presentation.PageSwitchMotion(fixture.Versions, fixture.Install, vx, ix, Sub() Return)
                    Dim accepted As ClockGroup = Nothing, external As AnimationClock = Nothing, armed = True
                    Dim descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, fixture.Versions.GetType())
                    engine.Start(fixture.Versions, fixture.Install, vx, ix, True)
                    FeedbackSeek(engine.Transition, 55, fixture.Layout)
                    Dim handler As EventHandler = Sub(sender, args)
                                                     If Not armed Then Return
                                                     armed = False
                                                     engine.Stop()
                                                     If animatedReplacement Then
                                                         engine.Start(fixture.Install, fixture.Versions, ix, vx, False)
                                                         accepted = engine.Transition
                                                         accepted.Controller.Pause()
                                                     Else
                                                         external = DirectCast(New DoubleAnimation(0.9, 0.9, TimeSpan.FromSeconds(5)).CreateClock(True), AnimationClock)
                                                         fixture.Install.ApplyAnimationClock(UIElement.OpacityProperty, external)
                                                         external.Controller.Pause()
                                                     End If
                                                 End Sub
                    descriptor.AddValueChanged(fixture.Versions, handler)
                    Try
                        engine.Stop()
                        Dim ok As Boolean
                        If animatedReplacement Then
                            If accepted IsNot Nothing Then FeedbackSeek(accepted, 55, fixture.Layout)
                            ok = engine.Transition Is accepted AndAlso accepted IsNot Nothing AndAlso
                                Math.Abs(fixture.Install.Opacity - 0.2 * (1 - 45.0 / 70)) < 0.000001 AndAlso Math.Abs(ix.X - 45) < 0.000001
                        Else
                            If external IsNot Nothing Then FeedbackSeek(external, 50, fixture.Layout)
                            ok = engine.Transition Is Nothing AndAlso fixture.Install.HasAnimatedProperties AndAlso fixture.Install.Opacity = 0.9
                        End If
                        check(Not armed AndAlso ok, "beta9 switch clock detach retires after synchronous " & If(animatedReplacement, "replacement clock", "stop and external channel"))
                    Finally
                        descriptor.RemoveValueChanged(fixture.Versions, handler)
                        engine.Stop()
                        external?.Controller.Remove()
                    End Try
                End Using
        Next
    End Sub

    Private Shared Sub VerifyBeta9SwitchRetiredError(check As Action(Of Boolean, String))
        Using fixture As New Beta9SwitchFixture(True)
            Dim armed = True, accepted As ClockGroup = Nothing, failure As New InvalidOperationException("accepted page callback failure"), observed As Exception = Nothing
            fixture.CardsEnabled = Function()
                                       If Not armed Then Return True
                                       armed = False
                                       fixture.Motion.SelectInstall(False, True)
                                       accepted = fixture.Motion.Transition
                                       accepted.Controller.Pause()
                                       Throw failure
                                   End Function
            Try
                fixture.Motion.SelectInstall(True, True)
            Catch ex As InvalidOperationException
                observed = ex
            End Try
            check(observed Is failure AndAlso accepted IsNot Nothing AndAlso fixture.Motion.Transition Is accepted AndAlso Not fixture.Host.IsHitTestVisible,
                "beta9 switch retired card-enable exception preserves the new accepted clock while retaining the original exception")
        End Using
    End Sub

    Private Shared Sub VerifyBeta9SwitchCardProperties(check As Action(Of Boolean, String))
        For Each useTransform In {False, True}
            Using fixture As New Beta9SwitchFixture(True)
                Dim card = DirectCast(fixture.Install.Children(0), Border)
                Dim original As New ScaleTransform(0.97, 0.97)
                card.RenderTransform = original
                card.Tag = 0.8 : card.IsHitTestVisible = True
                Dim binding As New Binding("Tag") With {.RelativeSource = New RelativeSource(RelativeSourceMode.Self), .Mode = BindingMode.OneWay}
                BindingOperations.SetBinding(card, OpacityProperty, binding)
                Dim dp = If(useTransform, RenderTransformProperty, OpacityProperty)
                Dim descriptor = DependencyPropertyDescriptor.FromProperty(dp, card.GetType()), armed = True, triggered = 0, failure As Exception = Nothing
                Dim handler As EventHandler = Sub(sender, args)
                                                 If Not armed Then Return
                                                 armed = False : triggered += 1
                                                 card.Tag = 0.65
                                                 fixture.Motion.SelectInstall(False, False)
                                             End Sub
                descriptor.AddValueChanged(card, handler)
                Try
                    Try
                        fixture.Motion.SelectInstall(True, True)
                    Catch ex As Exception
                        failure = ex
                    End Try
                    check(triggered = 1 AndAlso failure Is Nothing AndAlso fixture.IsSettled(False) AndAlso fixture.Motion.CardMotion.ItemCount = 0 AndAlso
                        card.RenderTransform Is original AndAlso card.Opacity = 0.65 AndAlso BindingOperations.GetBindingBase(card, OpacityProperty) Is binding AndAlso
                        CBool(card.ReadLocalValue(IsHitTestVisibleProperty)),
                        "beta9 switch card " & If(useTransform, "transform", "alpha") & " callback preserves accepted return binding local gate and original transform without old clocks")
                Finally
                    descriptor.RemoveValueChanged(card, handler)
                End Try
            End Using
        Next
    End Sub
End Class
