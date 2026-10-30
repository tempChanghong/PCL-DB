Imports System.ComponentModel
Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyControlMotionPolicyBridgeUi(directory As String, passed As List(Of String))
        Dim observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        Try
            VerifyControlMotionPolicyContract(check)
            VerifyControlMotionPolicyLifecycle(check)
        Finally
            System.IO.Directory.CreateDirectory(directory)
            File.WriteAllText(Path.Combine(directory, "control-motion-policy-bridge.json"), observations.ToString())
        End Try
        If observations.Count <> 40 Then Throw New InvalidOperationException("Expected forty control motion policy observations, got " & observations.Count & ".")
        Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
        If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
    End Sub

    Private Shared Sub VerifyControlMotionPolicyContract(check As Action(Of Boolean, String))
        Dim policyType = GetType(PclControlMotionPolicy), flags = BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic
        check(policyType.IsSealed AndAlso policyType.IsNotPublic AndAlso policyType.Assembly Is GetType(MainWindow).Assembly,
            "the motion policy facade remains sealed and internal in the original App assembly")
        Dim constructor = policyType.GetConstructors(flags).Single(), parameters = constructor.GetParameters()
        check(constructor.IsAssembly AndAlso parameters.Length = 3 AndAlso parameters(0).ParameterType Is GetType(FrameworkElement) AndAlso
            parameters(1).ParameterType Is GetType(Action) AndAlso parameters(2).ParameterType Is GetType(Boolean) AndAlso
            parameters(2).IsOptional AndAlso CBool(parameters(2).DefaultValue),
            "the motion policy constructor retains its optional interaction requirement and original parameter types")
        Dim readOnlyProperties = {"Interactive", "CanAnimate"}.Select(Function(name) policyType.GetProperty(name, flags)).ToArray()
        check(readOnlyProperties.All(Function(item) item IsNot Nothing AndAlso item.PropertyType Is GetType(Boolean) AndAlso
            item.GetGetMethod(True).IsAssembly AndAlso Not item.CanWrite),
            "motion eligibility remains exposed through the original internal Boolean getters")
        Dim pause = policyType.GetProperty("PauseForChecks", flags), enabled = policyType.GetProperty("EnabledForChecks", flags)
        check(pause.PropertyType Is GetType(Boolean) AndAlso enabled.PropertyType Is GetType(Boolean?) AndAlso
            {pause, enabled}.All(Function(item) item.GetGetMethod(True).IsAssembly AndAlso item.GetSetMethod(True).IsAssembly),
            "the motion policy retains its Boolean and nullable Boolean check properties and internal accessors")
        check(Not GetType(IDisposable).IsAssignableFrom(policyType), "the policy facade does not introduce a new disposal contract")
    End Sub

    ' Fixed PCL2/CE gate control animation on attachment and input state, and
    ' retire page animation synchronously on force exit. Exercise our matching
    ' local policy without changing Windows settings or invoking other owners.
    Private Sub VerifyControlMotionPolicyLifecycle(check As Action(Of Boolean, String))
        Dim previous = SynchronizationContext.Current, focus = LocalModCheckGetNativeFocus()
        Dim originalPreference = SystemParameters.ClientAreaAnimation, uiThread = Thread.CurrentThread.ManagedThreadId
        Dim target As New Border With {.Width = 160, .Height = 40}, host As New Border With {.Width = 240, .Height = 100, .Child = target}
        Dim layout = ControlLayout(host), policy As PclControlMotionPolicy = Nothing, notifications = 0, premature = False, allOnUi = True
        Dim reenter As Action = Nothing, settleFailure As Exception = Nothing
        Dim settle As Action = Sub()
                                   notifications += 1
                                   allOnUi = allOnUi AndAlso Thread.CurrentThread.ManagedThreadId = uiThread
                                   If policy Is Nothing Then premature = True
                                   Dim nested = reenter : reenter = Nothing
                                   nested?.Invoke()
                                   If settleFailure IsNot Nothing Then Throw settleFailure
                               End Sub
        policy = New PclControlMotionPolicy(target, settle)
        Dim changed = ControlMotionPolicyChangedForChecks(policy)
        check(notifications = 0 AndAlso Not premature AndAlso Not policy.CanAnimate AndAlso Not policy.Interactive,
            "constructing an unattached policy does not settle or grant animation eligibility")
        check(ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
            "an unattached policy does not subscribe to the static Windows preference event")
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
            Using surface = PreviewSurface.Attach(host, 240, 100)
                layout()
                check(target.IsLoaded AndAlso target.IsVisible AndAlso policy.Interactive AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "the first Loaded attaches exactly one preference subscription and enables interaction")
                policy.EnabledForChecks = True
                check(policy.CanAnimate AndAlso Not premature, "the deterministic animation override works after the original policy field is assigned")
                Dim before = notifications
                policy.PauseForChecks = True
                check(policy.PauseForChecks AndAlso notifications = before, "the pause check property roundtrips without settling its owner")
                changed(Nothing, New PropertyChangedEventArgs("UnrelatedPreference"))
                check(notifications = before, "an unrelated Windows preference does not settle a loaded policy")
                changed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                check(notifications = before + 1 AndAlso allOnUi, "a UI-thread animation preference change settles synchronously on its owning dispatcher")
                before = notifications : changed(Nothing, New PropertyChangedEventArgs(Nothing))
                check(notifications = before + 1, "a null preference name retains its all-properties notification semantics")
                before = notifications : changed(Nothing, New PropertyChangedEventArgs(""))
                check(notifications = before + 1, "an empty preference name retains its all-properties notification semantics")
                target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                check(ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "repeated Loaded notifications do not duplicate the static preference subscription")
                before = notifications
                For Each subscribed In ControlMotionPolicySubscriptionsForChecks(changed)
                    subscribed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                Next
                check(notifications = before + 1, "the actual static event invocation list contains one notification for this policy only")
                before = notifications
                Using Dispatcher.DisableProcessing()
                    InvokeControlMotionPolicyWorkerForChecks(changed)
                    check(notifications = before, "a background preference notification queues without settling on the worker thread")
                End Using
                If Not PumpMotionUntil(Function() notifications = before + 1, 5000) Then Throw New TimeoutException("A queued motion preference notification did not settle.")
                check(notifications = before + 1 AndAlso allOnUi, "the queued background notification settles exactly once on the UI thread")

                Using Dispatcher.DisableProcessing()
                    InvokeControlMotionPolicyWorkerForChecks(changed)
                    target.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, target))
                    before = notifications
                End Using
                DrainControlMotionPolicyQueueForChecks()
                check(notifications = before, "a queued preference notification is suppressed after its Unloaded notification")
                check(Not policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "Unloaded retires preference ownership before its settle callback returns")
                changed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                check(notifications = before, "a retired policy ignores a direct late UI-thread preference callback")
                target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                check(notifications = before + 1 AndAlso policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "reloading restores exactly one subscription and synchronous animation eligibility")
                Using Dispatcher.DisableProcessing()
                    InvokeControlMotionPolicyWorkerForChecks(changed)
                    target.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, target))
                    target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                    before = notifications
                End Using
                DrainControlMotionPolicyQueueForChecks()
                check(notifications = before + 1 AndAlso policy.CanAnimate,
                    "a queued callback retains execution-time watching semantics after an unload and reload")

                host.Child = Nothing : layout()
                check(Not target.IsLoaded AndAlso Not policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "actual visual-tree removal releases the policy's static subscription")
                host.Child = target : layout()
                check(target.IsLoaded AndAlso policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "actual visual-tree reattachment restores one live policy subscription")
                before = notifications
                reenter = Sub() target.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, target))
                changed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                check(notifications = before + 2 AndAlso Not policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "synchronous settle reentry can unload without an outer callback restoring retired ownership")
                before = notifications : changed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                check(notifications = before AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "subsequent callbacks cannot revive ownership retired by synchronous settle reentry")

                Dim expected As New InvalidOperationException("controlled-motion-policy-settle"), caught As Exception = Nothing
                settleFailure = expected
                Try
                    policy.EnabledForChecks = False
                Catch ex As InvalidOperationException When ex Is expected
                    caught = ex
                End Try
                check(caught Is expected AndAlso policy.EnabledForChecks.HasValue AndAlso Not policy.EnabledForChecks.Value AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "a failing override callback preserves the new override and prior subscription state")
                caught = Nothing
                Try
                    target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                Catch ex As InvalidOperationException When ex Is expected
                    caught = ex
                End Try
                check(caught Is expected AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "Loaded publishes its preference subscription before a settle callback throws")
                caught = Nothing
                Try
                    target.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, target))
                Catch ex As InvalidOperationException When ex Is expected
                    caught = ex
                End Try
                check(caught Is expected AndAlso Not policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 0,
                    "Unloaded retires its preference subscription before a settle callback throws")
                settleFailure = Nothing : policy.EnabledForChecks = True
                target.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, target))
                check(policy.CanAnimate AndAlso ControlMotionPolicySubscriptionsForChecks(changed).Length = 1,
                    "a policy remains reusable after controlled settle callback failures")
                host.Child = Nothing : layout()
                check(ControlMotionPolicySubscriptionsForChecks(changed).Length = 0 AndAlso SystemParameters.ClientAreaAnimation = originalPreference,
                    "policy lifecycle checks leave no static subscription or changed Windows animation preference")
            End Using

            Dim lateTarget As New Border With {.Width = 160, .Height = 40}, latePolicy As PclControlMotionPolicy = Nothing, lateNotifications = 0, latePremature = False
            host.Child = lateTarget
            Using surface = PreviewSurface.Attach(host, 240, 100)
                layout()
                check(lateTarget.IsLoaded AndAlso latePolicy Is Nothing, "the late-policy fixture completes Loaded before constructing its policy")
                latePolicy = New PclControlMotionPolicy(lateTarget, Sub()
                                                                      lateNotifications += 1
                                                                      If latePolicy Is Nothing Then latePremature = True
                                                                  End Sub, requiresInteraction:=False)
                Dim lateChanged = ControlMotionPolicyChangedForChecks(latePolicy)
                Try
                    check(Not latePremature AndAlso lateNotifications = 0 AndAlso ControlMotionPolicySubscriptionsForChecks(lateChanged).Length = 1,
                        "a policy constructed after Loaded subscribes immediately without an early owner callback")
                    latePolicy.EnabledForChecks = True
                    lateTarget.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, lateTarget))
                    check(ControlMotionPolicySubscriptionsForChecks(lateChanged).Length = 1 AndAlso lateNotifications = 2,
                        "a late-constructed policy does not double-subscribe when Loaded is delivered again")
                    lateTarget.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, lateTarget))
                    check(ControlMotionPolicySubscriptionsForChecks(lateChanged).Length = 0 AndAlso Not latePolicy.CanAnimate,
                        "a late-constructed policy releases its subscription on Unloaded")
                    lateTarget.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, lateTarget))
                    lateTarget.IsEnabled = False
                    check(latePolicy.CanAnimate AndAlso Not latePolicy.Interactive,
                        "a decorative policy retains animation eligibility when interaction is disabled")
                    lateTarget.IsHitTestVisible = False
                    check(latePolicy.CanAnimate AndAlso Not latePolicy.Interactive,
                        "a decorative policy retains animation eligibility behind an input gate")
                    lateTarget.Visibility = Visibility.Collapsed
                    check(Not latePolicy.CanAnimate, "a decorative policy still respects owner visibility")
                    host.Child = Nothing : layout()
                    check(ControlMotionPolicySubscriptionsForChecks(lateChanged).Length = 0 AndAlso Not lateTarget.IsLoaded,
                        "the late-policy fixture releases every static subscription on actual removal")
                Finally
                    lateTarget.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, lateTarget))
                End Try
            End Using
        Finally
            settleFailure = Nothing : reenter = Nothing
            target.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, target))
            host.Child = Nothing
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Shared Function ControlMotionPolicyChangedForChecks(policy As PclControlMotionPolicy) As PropertyChangedEventHandler
        Dim flags = BindingFlags.Instance Or BindingFlags.NonPublic, implementation As Object = policy
        Dim facade = policy.GetType().GetField("_implementation", flags)
        If facade IsNot Nothing Then implementation = facade.GetValue(policy)
        Dim field = implementation.GetType().GetField("_changed", flags)
        If field Is Nothing Then Throw New InvalidOperationException("The policy preference handler is unavailable to the isolated lifecycle probe.")
        Return DirectCast(field.GetValue(implementation), PropertyChangedEventHandler)
    End Function

    Private Shared Function ControlMotionPolicySubscriptionsForChecks(changed As PropertyChangedEventHandler) As PropertyChangedEventHandler()
        Dim field = GetType(SystemParameters).GetField("StaticPropertyChanged", BindingFlags.Static Or BindingFlags.NonPublic)
        If field Is Nothing Then Throw New InvalidOperationException("The Framework static preference event is unavailable to the isolated lifecycle probe.")
        Dim subscribed = DirectCast(field.GetValue(Nothing), PropertyChangedEventHandler)
        If subscribed Is Nothing Then Return New PropertyChangedEventHandler() {}
        ' Inspect the actual event, but never call another window's subscription.
        Return subscribed.GetInvocationList().OfType(Of PropertyChangedEventHandler)().Where(Function(item) item.Equals(changed)).ToArray()
    End Function

    Private Shared Sub InvokeControlMotionPolicyWorkerForChecks(changed As PropertyChangedEventHandler)
        Dim failure As Exception = Nothing
        Dim worker As New Thread(Sub()
                                     Try
                                         changed(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
                                     Catch ex As Exception
                                         failure = ex
                                     End Try
                                 End Sub) With {.IsBackground = True}
        worker.Start()
        If Not worker.Join(5000) Then Throw New TimeoutException("The isolated preference notification worker did not return.")
        If failure IsNot Nothing Then Throw New InvalidOperationException("The isolated preference notification worker failed.", failure)
    End Sub

    Private Sub DrainControlMotionPolicyQueueForChecks()
        Dim drained = False
        Dispatcher.BeginInvoke(New Action(Sub() drained = True))
        If Not PumpMotionUntil(Function() drained, 5000) Then Throw New TimeoutException("The motion preference notification queue did not drain.")
    End Sub
End Class
