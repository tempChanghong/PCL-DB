Imports System.ComponentModel
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Media
Imports System.Windows.Input
Imports System.Threading
Imports System.Windows.Threading
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Sub VerifyMotionFinalization(directory As String, check As Action(Of Boolean, String))
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Me.Content = Nothing : host.Child = content
        Dim context = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
        Using source = PreviewSurface.Attach(host, 850, 600)
            Try
                layout() : _motionPreview = True
                _surfaceMotion.PauseForChecks = True
                SwitchMainPage(True) : _surfaceMotion.Finish() : layout()
                SwitchMainPage(False)
                Dim incoming = _surfaceMotion.Transition
                incoming.Controller.Pause() : layout()
                Dim retired = VersionCards.Children.OfType(Of PclCard)().ToArray()
                Dim gate = ListGate(Of VersionEntry)()
                _versionProvider = Function(policy, token) gate.Task
                Dim loading = LoadVersionsAsync(True)
                Dim fresh = _versions.ToList()
                fresh.Insert(0, New VersionEntry With {.Id = "motion-new-catalogue", .Kind = "release", .Released = DateTimeOffset.Now})
                gate.SetResult(fresh)
                check(PumpMotionUntil(Function() loading.IsCompleted), "a controlled manifest result returns on the UI dispatcher during page entry")
                loading.GetAwaiter().GetResult()
                Dim refreshed = _surfaceMotion.Transition
                refreshed?.Controller.Pause() : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "catalogue-accept-state.json"), New JObject From {
                    {"pausedFromCreation", _surfaceMotion.PauseForChecks},
                    {"replaced", refreshed IsNot incoming}, {"dataAccepted", _versions Is fresh}, {"ghostLayers", PageGhost.Children.Count},
                    {"clockOwned", _surfaceMotion.Transition Is refreshed}, {"time", If(refreshed?.CurrentTime?.TotalMilliseconds, -1)},
                    {"retiredClean", retired.All(Function(card) Not card.HasAnimatedProperties AndAlso Not TypeOf card.RenderTransform Is TransformGroup)}}.ToString())
                check(refreshed IsNot incoming AndAlso _versions Is fresh AndAlso PageGhost.Children.Count = 1 AndAlso retired.All(Function(card) Not card.HasAnimatedProperties AndAlso Not TypeOf card.RenderTransform Is TransformGroup),
                    "accepted catalogue content releases detached card clocks and enters its own fresh animation group")
                incoming.Controller.SkipToFill() : layout()
                check(_surfaceMotion.Transition Is refreshed, "the old catalogue entry cannot finish its replacement")
                Dim frames = IO.Path.Combine(directory, "catalogue-motion-frames")
                IO.Directory.CreateDirectory(frames)
                For frame = 0 To 29
                    If _surfaceMotion.Transition Is refreshed Then
                        refreshed.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(frame * (1000.0 / 30)), TimeSeekOrigin.BeginTime)
                        layout()
                    End If
                    SaveControlVisual(host, IO.Path.Combine(frames, frame.ToString("0000") & ".png"))
                Next

                Dim oldGate = ListGate(Of VersionEntry)(), newGate = ListGate(Of VersionEntry)(), calls = 0
                _versionProvider = Function(policy, token)
                                       calls += 1
                                       Return If(calls = 1, oldGate.Task, newGate.Task)
                                   End Function
                Dim oldLoad = LoadVersionsAsync(True), newLoad = LoadVersionsAsync(True)
                Dim newest = fresh.ToList()
                newGate.SetResult(newest)
                check(PumpMotionUntil(Function() newLoad.IsCompleted), "a restarted catalogue request completes independently of its cancelled predecessor")
                newLoad.GetAwaiter().GetResult()
                refreshed = _surfaceMotion.Transition : refreshed.Controller.Pause() : layout()
                oldGate.SetResult(fresh)
                check(PumpMotionUntil(Function() oldLoad.IsCompleted), "an ignored-cancellation catalogue response drains without blocking the dispatcher")
                oldLoad.GetAwaiter().GetResult()
                check(_versions Is newest AndAlso _surfaceMotion.Transition Is refreshed, "a stale catalogue response cannot replace data or restart the current animation")

                SwitchMainPage(True)
                Dim settingsClock = _surfaceMotion.Transition : settingsClock.Controller.Pause() : layout()
                _versionProvider = Function(policy, token) Task.FromResult(_versions.ToList())
                LoadVersionsAsync(True).GetAwaiter().GetResult()
                check(_surfaceMotion.Transition Is settingsClock AndAlso SettingsPage.IsVisible,
                    "a hidden catalogue refresh preserves the visible settings entry group")
                Dim bar = SettingsPage.VerticalBar
                Dim originalBar = DirectCast(bar.RenderTransform, TransformGroup).Children(0)
                bar.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.PageDown) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent})
                check(bar.RenderTransform Is originalBar AndAlso _surfaceMotion.Transition Is settingsClock,
                    "scrollbar keyboard input settles only its horizontal entry transform")
                SwitchMainPage(False) : _surfaceMotion.Finish() : layout()
                SwitchMainPage(True)
                settingsClock = _surfaceMotion.Transition : settingsClock.Controller.Pause() : layout()
                bar = SettingsPage.VerticalBar
                originalBar = DirectCast(bar.RenderTransform, TransformGroup).Children(0)
                bar.RaiseEvent(New TouchEventArgs(New MotionTouchDevice(source), Environment.TickCount) With {.RoutedEvent = UIElement.PreviewTouchDownEvent})
                check(bar.RenderTransform Is originalBar AndAlso _surfaceMotion.Transition Is settingsClock,
                    "synthetic touch routing settles the scrollbar without cancelling card entry")

                _surfaceMotion.Finish()
                SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
                FinishPageSwitchForCheck(layout)
                SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
                InstanceName.Text = "动画收尾保留方案"
                SwitchMainPage(True) : _surfaceMotion.Finish() : SwitchMainPage(False)
                Dim componentClock = _surfaceMotion.Transition : componentClock.Controller.Pause() : layout()
                Dim selected = _componentSelection.Item(ComponentKind.Fabric)
                Dim replacement As New ComponentEntry With {.Kind = selected.Kind, .Minecraft = selected.Minecraft, .Version = selected.Version,
                    .Url = "https://example.org/motion-refreshed", .Data = New JObject()}
                AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {replacement})
                check(_surfaceMotion.Transition Is componentClock AndAlso _componentSelection.Item(ComponentKind.Fabric) Is replacement AndAlso InstanceName.Text = "动画收尾保留方案",
                    "component metadata rebinding preserves page entry and the exact installation selection")
                _surfaceMotion.Finish()
                BackToVersions(Me, New RoutedEventArgs())
                _pageSwitch.Transition.Controller.Pause() : layout()
                _focusAfterPageSwitch = True
                UpdateVersions()
                check(_pageSwitch.Transition Is Nothing AndAlso _pendingSurfaceFocus IsNot Nothing AndAlso _surfaceMotion.Transition IsNot Nothing,
                    "catalogue replacement settles the return animation and defers its keyboard focus to fresh rows")
                _surfaceMotion.Finish()
                check(_pendingSurfaceFocus Is Nothing AndAlso Not _focusAfterPageSwitch,
                    "fresh content settling consumes the transferred focus request once")
                _motionPreview = False
                Dim focused = False
                _pendingSurfaceFocus = Sub() focused = True
                UpdateVersions()
                check(_surfaceMotion.Transition Is Nothing AndAlso focused AndAlso _pendingSurfaceFocus Is Nothing,
                    "immediate catalogue replacement consumes pending focus without leaving an invisible animation gate")
                _motionPreview = True
                Dim savedVersions = _versions
                _versions = New List(Of VersionEntry)()
                UpdateVersions()
                check(_surfaceMotion.ItemCount = 1 AndAlso EmptyVersions.Visibility = Visibility.Visible AndAlso TypeOf EmptyVersions.RenderTransform Is TransformGroup,
                    "an empty catalogue uses the same source entry motion for its visible status text")
                _versions = savedVersions : UpdateVersions() : _surfaceMotion.Finish()
                SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
                Dim interrupted = _pageSwitch.Transition : interrupted.Controller.Pause() : layout()
                interrupted.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(120), TimeSeekOrigin.BeginTime) : layout()
                SaveControlVisual(host, IO.Path.Combine(directory, "inner-exit-before.png"))
                SwitchMainPage(True)
                Dim outer = _surfaceMotion.Transition : outer.Controller.Pause() : layout()
                outer.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
                SaveControlVisual(host, IO.Path.Combine(directory, "inner-exit-zero.png"))
                check(_pageSwitch.Transition Is Nothing AndAlso PageGhost.Children.Count = 1 AndAlso MotionImageDifference(
                    IO.Path.Combine(directory, "inner-exit-before.png"), IO.Path.Combine(directory, "inner-exit-zero.png"), 70) < 4,
                    "leaving during install-page motion freezes the displayed composite before settling its inner clock")
                _surfaceMotion.Finish()
                BackToVersions(Me, New RoutedEventArgs())
                SwitchMainPage(False) : _surfaceMotion.Finish() : layout()
                SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
                Dim clock = _pageSwitch.Transition
                clock.Controller.Pause() : layout()
                WindowClosing(Me, New CancelEventArgs())
                check(_pageSwitch.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso DownloadViewHost.IsHitTestVisible,
                    "closing explicitly releases the install-page clock before native visual unloading")
                BackToVersions(Me, New RoutedEventArgs())
                SwitchMainPage(True)
                check(_pageSwitch.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing,
                    "closing state prevents a late navigation callback from starting a new animation")
            Finally
                _surfaceMotion.PauseForChecks = False
                _closing = False : _pendingSurfaceFocus = Nothing
                _versionProvider = Nothing
                _pageSwitch.Finish() : _surfaceMotion.Finish()
                source.RootVisual = Nothing : host.Child = Nothing : Me.Content = content
                SynchronizationContext.SetSynchronizationContext(context)
            End Try
        End Using
        VerifyInstallMotionLifetime(check)
    End Sub

    Private Shared Sub VerifyInstallMotionLifetime(check As Action(Of Boolean, String))
        Dim page As New Grid(), versions As New Grid(), install As New Grid()
        page.Children.Add(versions) : page.Children.Add(install)
        Dim host As New Border With {.Width = 500, .Height = 300, .Child = page}
        Dim enabled = False
        Dim motion As New PclPageSwitch(page, versions, install, Function() enabled)
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 300)
            layout()
            motion.SelectInstall(True, True)
            check(motion.Transition Is Nothing AndAlso install.Opacity = 1 AndAlso versions.Visibility = Visibility.Collapsed AndAlso page.IsHitTestVisible,
                "disabled page motion selects the install page immediately with its final input state")
            enabled = True : motion.SelectInstall(False, True)
            Dim old = motion.Transition : old.Controller.Pause() : layout()
            page.Visibility = Visibility.Collapsed : layout()
            check(motion.Transition Is Nothing AndAlso versions.Opacity = 1 AndAlso page.IsHitTestVisible,
                "hiding the install navigation host releases its clock without waiting for Unloaded")
            page.Visibility = Visibility.Visible : layout()
            check(motion.Transition Is Nothing, "showing a settled host cannot revive a hidden page transition")
            motion.SelectInstall(True, True)
            old = motion.Transition : old.Controller.Pause() : layout()
            host.Width = 520 : layout()
            check(motion.Transition Is Nothing AndAlso install.Opacity = 1 AndAlso page.IsHitTestVisible,
                "resizing the install navigation host settles its independent horizontal animation")
            motion.SelectInstall(False, True)
            Dim current = motion.Transition : current.Controller.Pause() : layout()
            old.Controller.SkipToFill() : layout()
            check(motion.Transition Is current, "a hidden or resized install clock cannot finish a newer navigation")
            source.RootVisual = Nothing : FlushControlLayout()
            check(motion.Transition Is Nothing AndAlso page.IsHitTestVisible, "unloading the final install navigation releases its input gate")
        End Using
    End Sub

    Private NotInheritable Class MotionTouchDevice
        Inherits TouchDevice
        Friend Sub New(source As PresentationSource)
            MyBase.New(913)
            SetActiveSource(source)
        End Sub
        Public Overrides Function GetTouchPoint(relativeTo As IInputElement) As TouchPoint
            Return New TouchPoint(Me, New Point(), New Rect(0, 0, 1, 1), TouchAction.Down)
        End Function
        Public Overrides Function GetIntermediateTouchPoints(relativeTo As IInputElement) As TouchPointCollection
            Dim points As New TouchPointCollection()
            points.Add(GetTouchPoint(relativeTo))
            Return points
        End Function
    End Class
End Class
