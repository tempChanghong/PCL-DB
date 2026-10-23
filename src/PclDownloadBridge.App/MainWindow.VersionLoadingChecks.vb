Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyVersionLoading(check As Action(Of Boolean, String), directory As String)
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 600}, previous = SynchronizationContext.Current
        Dim appearance = _appearance.ToJson()
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
            owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
            Using source = PreviewSurface.Attach(host, 850, 600)
                Dim work = owner.VerifyVersionLoadingAsync(check, directory, host, ControlLayout(host))
                If Not PumpMotionUntil(Function() work.IsCompleted, 12000) Then Throw New TimeoutException("Version loading checks exceeded their deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._versionCancellation?.Cancel() : owner._versionLoading.Reset()
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(appearance)
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function VerifyVersionLoadingAsync(check As Action(Of Boolean, String), directory As String, host As Border, layout As Action) As Task
        Dim sample = _versions.ToList(), now As Double = 0
        Dim gates As New List(Of TaskCompletionSource(Of List(Of VersionEntry)))
        Dim createGate As Func(Of TaskCompletionSource(Of List(Of VersionEntry))) = Function()
                                                                                     Dim value = ListGate(Of VersionEntry)()
                                                                                     gates.Add(value) : Return value
                                                                                 End Function
        Dim settle As Action = Sub()
                                   _categoryMotion.Finish() : _surfaceMotion.Finish() : _pageSwitch.Finish() : layout()
                               End Sub
        Dim clear As Action = Sub()
                                  _versions = New List(Of VersionEntry)() : UpdateVersions() : settle()
                              End Sub
        Dim tick As Action = Sub()
                                 now += 400 : _versionLoading.Pulse() : layout()
                             End Sub
        Dim gate = createGate(), calls = 0
        Try
            _versionLoadingTimingForChecks = True : _motionPreview = True
            _surfaceMotion.PauseForChecks = True : _categoryMotion.PauseForChecks = True
            _versionLoading.NowForChecks = Function() now
            clear()
            _versionProvider = Function(policy, token)
                                   calls += 1 : Return gate.Task
                               End Function
            Dim work = LoadVersionsAsync(True)
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso VersionLoadingCard.Visibility = Visibility.Collapsed AndAlso EmptyVersions.Visibility = Visibility.Collapsed,
                "initial vanilla catalogue suppresses both placeholder text and pickaxe during PCL LoaderWait")
            now = 399 : _versionLoading.Pulse() : layout()
            check(VersionLoadingCard.Body Is Nothing, "vanilla loading does not create a pickaxe before the 400 ms boundary")
            gate.SetResult(sample) : Await work : settle()
            check(_versions Is sample AndAlso VersionCards.Children.Count > 0 AndAlso VersionLoadingCard.Body Is Nothing AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Idle,
                "a fast vanilla response enters the catalogue directly and removes the pending loading callback")

            Dim card = VersionCards.Children(0)
            gate = createGate() : work = LoadVersionsAsync(True) : tick()
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso VersionCards.Children(0) Is card AndAlso VersionLoadingCard.Body Is Nothing,
                "refreshing a usable vanilla catalogue retains existing rows without replacing them with a loading panel")
            gate.SetException(New IOException("cached refresh failure")) : Await work
            check(_versions Is sample AndAlso VersionCards.Children(0) Is card AndAlso StatusText.Text.Contains("上次") AndAlso _versionsDirty,
                "failed vanilla refresh keeps the accepted catalogue available and marks it for retry")

            clear() : gate = createGate() : work = LoadVersionsAsync(True) : tick()
            Dim entry = _surfaceMotion.Transition, loader = TryCast(VersionLoadingCard.Body, PclLoadingView)
            check(loader IsNot Nothing AndAlso VersionLoadingCard.IsVisible AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Entering AndAlso entry IsNot Nothing,
                "slow vanilla loading enters a centered PCL card through the existing page animation owner")
            gate.SetResult(sample)
            If Not PumpMotionUntil(Function() _versionLoading.CompletionPending) Then Throw New TimeoutException("Vanilla data did not reach entry wait.")
            now += 2000 : _versionLoading.Pulse()
            check(Not work.IsCompleted AndAlso _versions.Count = 0 AndAlso VersionLoadingCard.Body Is loader,
                "ready vanilla metadata cannot consume minimum stay while its real entry animation is unfinished")
            _surfaceMotion.Finish() : now += 399 : _versionLoading.Pulse()
            check(Not work.IsCompleted AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.MinimumStay,
                "vanilla minimum stay begins after entry and still owns the loading card at 399 ms")
            _titleNavigation.Finish() : layout() : SaveControlVisual(host, Path.Combine(directory, "vanilla-loader-stay.png"))
            now += 1 : _versionLoading.Pulse() : Await work : settle()
            check(_versions Is sample AndAlso VersionLoadingCard.Body Is Nothing AndAlso Not _versionLoading.TimerArmed AndAlso VersionCards.Children.Count > 0,
                "post-entry minimum stay publishes vanilla rows once and releases the pickaxe and timer")

            clear() : gate = createGate() : work = LoadVersionsAsync(True) : tick() : settle()
            gate.SetResult(New List(Of VersionEntry)()) : tick() : Await work : settle()
            check(EmptyVersions.IsVisible AndAlso EmptyVersions.Text.Contains("刷新") AndAlso VersionLoadingCard.Visibility = Visibility.Collapsed,
                "an accepted empty catalogue leaves a readable refresh prompt instead of a stuck loading card")

            gate = createGate() : work = LoadVersionsAsync(True)
            ShowSettings(Nothing, Nothing) : layout()
            Dim settingsEntry = _surfaceMotion.Transition, savedCalls = calls
            gate.SetResult(sample) : Await work
            check(SettingsPage.IsVisible AndAlso _surfaceMotion.Transition Is settingsEntry AndAlso _versions Is sample AndAlso VersionLoadingCard.Body Is Nothing,
                "a hidden vanilla response updates its cache without retargeting the visible settings animation")
            ShowDownload(Nothing, Nothing) : settle() : clear()
            gate = createGate() : work = LoadVersionsAsync(True) : tick() : settle()
            ShowSettings(Nothing, Nothing) : settle()
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.Hidden AndAlso VersionLoadingCard.Body Is Nothing,
                "leaving an unfinished vanilla loader suspends presentation and unloads its animated pickaxe")
            savedCalls = calls : ShowDownload(Nothing, Nothing) : layout()
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.Waiting AndAlso calls = savedCalls AndAlso VersionLoadingCard.Body Is Nothing,
                "returning to an unfinished vanilla request resumes LoaderWait without requesting the manifest twice")
            gate.SetResult(sample) : Await work : settle()

            clear() : gate = createGate() : work = LoadVersionsAsync(True)
            SetDownloadView(DownloadView.Modpacks) : layout()
            ShowVanillaDownloads(Nothing, Nothing) : layout() : tick()
            check(_categoryMotion.Transition IsNot Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Entering,
                "vanilla loading returning from modpacks retargets its category owner without a second root clock")
            gate.SetResult(sample)
            If Not PumpMotionUntil(Function() _versionLoading.CompletionPending) Then Throw New TimeoutException("Category loading wait did not accept data.")
            ShowSettings(Nothing, Nothing) : layout() : Await work
            check(_versions Is sample AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not _versionLoading.TimerArmed,
                "hiding a ready vanilla loader releases its forced-stay waiter and caches metadata immediately")
            ShowDownload(Nothing, Nothing) : settle()

            clear() : gate = createGate() : work = LoadVersionsAsync(True) : tick()
            gate.SetResult(sample)
            If Not PumpMotionUntil(Function() _versionLoading.CompletionPending) Then Throw New TimeoutException("Retired vanilla wait did not begin.")
            Dim oldWork = work, oldClock = _surfaceMotion.Transition
            gate = createGate() : work = LoadVersionsAsync(True) : layout()
            Dim replacement = New List(Of VersionEntry) From {sample(1)}
            oldClock.Controller.SkipToFill() : gate.SetResult(replacement) : Await work : Await oldWork : settle()
            check(_versions Is replacement AndAlso VersionLoadingCard.Body Is Nothing AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Idle,
                "restarting during vanilla entry releases the old waiter while retired data and clocks cannot replace newer results")

            clear() : gate = createGate() : work = LoadVersionsAsync(True) : tick()
            VersionSourceBox.SelectedIndex = If(VersionSourceBox.SelectedIndex = 2, 0, 2)
            gate.SetResult(sample) : Await work : settle()
            check(_versions.Count = 0 AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso VersionLoadingCard.Body Is Nothing AndAlso EmptyVersions.IsVisible,
                "changing version source cancels the old loader and rejects its late response without leaving a stale pickaxe")

            gate = createGate() : work = LoadVersionsAsync(True) : tick() : settle()
            _versionCancellation.Cancel() : gate.SetResult(sample) : Await work : settle()
            check(_versions.Count = 0 AndAlso _versionsDirty AndAlso EmptyVersions.Text.Contains("失败") AndAlso VersionLoadingCard.Body Is Nothing AndAlso Not _versionLoading.TimerArmed,
                "cancellation or timeout clears an initial vanilla loader and exposes the existing refresh recovery path")
            _versionProvider = Function(policy, token) Task.FromResult(sample)
            Await LoadVersionsAsync(True) : settle()
            check(_versions Is sample AndAlso Not _versionsDirty AndAlso EmptyVersions.Visibility = Visibility.Collapsed,
                "retry after an initial vanilla failure replaces the error with usable rows")

            clear() : gate = createGate() : _versionProvider = Function(policy, token) gate.Task
            work = LoadVersionsAsync(True) : tick() : settle() : gate.SetResult(sample)
            If Not PumpMotionUntil(Function() _versionLoading.CompletionPending) Then Throw New TimeoutException("Cancelable vanilla wait did not begin.")
            _versionCancellation.Cancel() : Await work : settle() : tick()
            check(_versions.Count = 0 AndAlso _versionsDirty AndAlso VersionLoadingCard.Body Is Nothing AndAlso EmptyVersions.Text.Contains("失败"),
                "cancellation after metadata is ready releases minimum stay and rechecks the token before publishing vanilla data")

            clear() : gate = createGate() : _versionProvider = Function(policy, token) gate.Task
            work = LoadVersionsAsync(True) : tick() : settle()
            gate.SetResult(sample)
            If Not PumpMotionUntil(Function() _versionLoading.CompletionPending) Then Throw New TimeoutException("Busy vanilla wait did not begin.")
            _busy = True : StatusText.Text = "安装状态必须保留" : tick() : Await work
            check(_versions.Count = 0 AndAlso _versionsDirty AndAlso StatusText.Text = "安装状态必须保留" AndAlso VersionLoadingCard.Body Is Nothing,
                "installation taking ownership during minimum stay prevents catalogue publication and preserves task status")
            _busy = False : settle()

            _motionPreview = False : gate = createGate() : work = LoadVersionsAsync(True)
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.Loading AndAlso Not _versionLoading.TimerArmed AndAlso VersionLoadingCard.Body IsNot Nothing,
                "disabled page motion displays vanilla progress immediately without artificial timing waits")
            gate.SetResult(sample) : Await work : _motionPreview = True : settle()

            clear() : _versionProvider = Function(policy, token) Task.FromResult(sample)
            Dim supplements = createGate()
            _supplementalProvider = Function(policy, token) supplements.Task
            work = LoadVersionsAsync(True) : layout()
            check(_versions Is sample AndAlso Not _loading AndAlso VersionLoadingCard.Body Is Nothing AndAlso VersionCards.Children.Count > 0 AndAlso Not work.IsCompleted,
                "optional supplemental metadata never prolongs the primary vanilla loading presentation")
            supplements.SetException(New IOException("supplements unavailable")) : Await work : settle()
            check(_versions Is sample AndAlso Not _versionsDirty AndAlso VersionLoadingCard.Body Is Nothing AndAlso StatusText.Text.Contains("补充清单暂不可用"),
                "supplemental failure preserves normal vanilla rows and cannot revive a completed loader")
            _supplementalProvider = Nothing

            clear() : RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
            gate = createGate() : _versionProvider = Function(policy, token) gate.Task
            work = LoadVersionsAsync(True) : tick()
            _versionLoading.NowForChecks = Nothing : settle()
            check(_appearance.DarkMode AndAlso VersionLoadingCard.IsVisible AndAlso VersionLoadingCard.Body IsNot Nothing AndAlso EmptyVersions.Visibility = Visibility.Collapsed,
                "the centered vanilla loader remains a single readable card under the shared dark theme")
            SaveControlVisual(host, Path.Combine(directory, "vanilla-loader-dark.png"))
            check(_versionLoading.Phase = PclLoadingPresentation.Phases.MinimumStay AndAlso _versionLoading.TimerArmed,
                "vanilla close regression owns an actual dispatcher minimum-stay timer before closing")
            Dim taskGeneration = _taskGeneration, installation = _cancellation
            WindowClosing(Me, New CancelEventArgs()) : gate.SetResult(sample) : Await work : layout()
            check(_closing AndAlso _versionCancellation Is Nothing AndAlso _versionLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso
                VersionLoadingCard.Body Is Nothing AndAlso Not _versionLoading.TimerArmed AndAlso _versions.Count = 0,
                "window closing removes vanilla loading clocks and ignores a late successful manifest")
            check(_taskGeneration = taskGeneration AndAlso _cancellation Is installation,
                "vanilla presentation ownership never replaces the separate installation generation or cancellation source")
        Finally
            _busy = False : _versionProvider = Nothing : _supplementalProvider = Nothing
            _versionCancellation?.Cancel() : _versionLoading.Reset()
            For Each pending In gates : pending.TrySetResult(sample) : Next
        End Try
    End Function
End Class
