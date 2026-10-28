Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Function VerifyNavigationUi(artifactDirectory As String, Optional checks As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("UI checks require isolated preview mode.")
        checks = If(checks, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     checks.Add(label)
                                                 End Sub
        Dim fixture As New MainWindow(True)
        fixture.VerifyPageNavigation(check, artifactDirectory)
        VerifyTitleNavigation(check, artifactDirectory, False)
        VerifyDownloadFocusOwnership(check)
        VerifyManagementUi(artifactDirectory, check)
        VerifyRepairRecoveryUi(artifactDirectory, checks)
        VerifyModContextRecoveryUi(artifactDirectory, checks)
        VerifyModWriteHandoffUi(artifactDirectory, checks)
        VerifyModWriteStartUi(artifactDirectory, checks)
        VerifyLocalModReadStartUi(artifactDirectory, checks)
        VerifyForeignModWriteUi(artifactDirectory, checks)
        VerifyModWriteRefreshUi(artifactDirectory, checks)
        VerifyModEntryRefreshUi(artifactDirectory, checks)
        VerifyTaskManagementRefreshUi(artifactDirectory, checks)
        VerifyManagementFolderUi(artifactDirectory, checks)
        VerifyManagementRetirementUi(artifactDirectory, checks)
        VerifyManagementIntentUi(artifactDirectory, checks)
        VerifyManagementSelectionUi(artifactDirectory, checks)
        VerifyLocalModFolderUi(artifactDirectory, checks)
        VerifyLocalModFolderIntentUi(artifactDirectory, checks)
        VerifyLocalModSelectionUi(artifactDirectory, checks)
        VerifyLocalModUpdateLifecycleUi(artifactDirectory, checks)
        VerifyLocalModPromptUi(artifactDirectory, checks)
        VerifyLocalModTransferUi(artifactDirectory, checks)
        VerifyResourcePromptLifecycleUi(artifactDirectory, checks)
        VerifyResourceInputReentrancyUi(artifactDirectory, checks)
        VerifyRepairNativeUi(artifactDirectory, checks)
        VerifyLocalModUi(artifactDirectory, check)
        Dim cancellationChecks As New List(Of String)
        VerifyManagementCancellationUi(artifactDirectory, cancellationChecks)
        checks.AddRange(cancellationChecks)
        VerifyLocalModFollowupUi(artifactDirectory, check)
        VerifyLocalModInteractionUi(artifactDirectory, check)
        VerifyLocalModStyleUi(artifactDirectory, check)
        VerifyLocalModWarningUi(artifactDirectory, check)
        VerifyLocalModCancelUi(artifactDirectory, check)
        VerifyLocalModRowUi(artifactDirectory, check)
        VerifyLocalModRowDiagnosticsUi(artifactDirectory, check)
        Return checks
    End Function

    Private Sub VerifyPageNavigation(check As Action(Of Boolean, String), artifactDirectory As String)
        For Each entering In {True, False}
            Dim plan = PclPageSwitch.TransitionPlan(0.7, -12, 0.3, 21, entering)
            Dim parts = plan.Children.Cast(Of DoubleAnimation)().ToArray()
            check(plan.Duration.TimeSpan.TotalMilliseconds = 260 AndAlso parts.Length = 4,
                "page navigation retains PCL's four-property 260ms plan: " & entering)
            check(parts.Select(Function(a) a.BeginTime.Value.TotalMilliseconds).SequenceEqual({10.0, 10.0, 100.0, 100.0}) AndAlso
                parts.Select(Function(a) a.Duration.TimeSpan.TotalMilliseconds).SequenceEqual({70.0, 90.0, 70.0, 160.0}),
                "page navigation retains the outgoing and incoming source delays and durations: " & entering)
            check(parts(0).From.Value = 0.7 AndAlso parts(0).To.Value = 0 AndAlso parts(1).From.Value = -12 AndAlso parts(1).To.Value = If(entering, -50, 50) AndAlso
                parts(2).From.Value = 0.3 AndAlso parts(2).To.Value = 1 AndAlso parts(3).From.Value = 21 AndAlso parts(3).To.Value = 0,
                "page navigation starts at interrupted values and retains the source direction: " & entering)
            Dim clone = plan.Clone() : clone.Freeze()
            check(parts.Take(3).All(Function(a) a.EasingFunction Is Nothing) AndAlso
                DirectCast(clone.Children(3), DoubleAnimation).EasingFunction.Ease(0.5) = 0.96875,
                "only the incoming horizontal motion uses PCL ExtraStrong easing and survives freezing: " & entering)
        Next

        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        Dim source As Interop.HwndSource = Nothing
        Dim observations As New JArray
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, time)
                                                        clock.Controller.Pause() : layout()
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Try
            check(_pageSwitch.Transition Is Nothing AndAlso VersionPage.Opacity = 1 AndAlso PageX(InstallPage) = 40,
                "a cold version browser retains PCL's forty-DIP initial install offset without an animation")
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 600) : layout()
            Dim category = VersionCards.Children.OfType(Of PclCard)().Single(Function(card) card.Title.StartsWith("正式版", StringComparison.Ordinal))
            category.Collapsed = False
            Dim returnRow = BrowserRows("正式版").First()
            _keyboardVersionReturn = New WeakReference(Of PclListItem)(returnRow)
            Dim entry = _versions.Single(Function(item) item.Id = "1.20.1")
            SelectVersion(entry)
            Dim opening = _pageSwitch.Transition
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "page-switch-initial.json"), New JObject From {
                {"clock", opening IsNot Nothing}, {"hostLoaded", DownloadViewHost.IsLoaded}, {"hostVisible", DownloadViewHost.IsVisible},
                {"versions", VersionPage.Visibility.ToString()}, {"install", InstallPage.Visibility.ToString()},
                {"hostInput", DownloadViewHost.IsHitTestVisible}, {"versionsInput", VersionPage.IsHitTestVisible}, {"installInput", InstallPage.IsHitTestVisible}}.ToString())
            check(opening IsNot Nothing AndAlso VersionPage.Visibility = Visibility.Visible AndAlso InstallPage.Visibility = Visibility.Visible AndAlso
                Not DownloadViewHost.IsHitTestVisible AndAlso Not VersionPage.IsHitTestVisible AndAlso Not InstallPage.IsHitTestVisible AndAlso
                CBool(InstallPage.ReadLocalValue(IsHitTestVisibleProperty)),
                "entering a loaded install page animates both pages and gates pointer input until they settle")
            check(DownloadPage.Opacity = 1 AndAlso InstallScroll.VerticalOffset = 0 AndAlso DownloadViewHost.ClipToBounds,
                "install navigation keeps the shared toolbar opaque resets the install viewport and clips horizontal motion")
            seek(opening, 45)
            CheckPageSample(check, observations, "enter-outgoing", opening, True)
            Dim oldOpacity = VersionPage.Opacity, oldX = PageX(VersionPage)
            BackToVersions(Me, New RoutedEventArgs())
            Dim returning = _pageSwitch.Transition
            returning.Controller.Pause()
            Dim reversePlan = DirectCast(returning.Timeline, ParallelTimeline)
            check(returning IsNot opening AndAlso DirectCast(reversePlan.Children(2), DoubleAnimation).From.Value = oldOpacity AndAlso
                DirectCast(reversePlan.Children(3), DoubleAnimation).From.Value = oldX,
                "an immediate return captures the actual interrupted browser opacity and position")
            opening.Controller.SkipToFill() : layout()
            check(_pageSwitch.Transition Is returning AndAlso _downloadView = DownloadView.Versions AndAlso Not DownloadViewHost.IsHitTestVisible,
                "a superseded entry clock cannot finish a newer return or reenable pointer input")
            FocusDownloadPage()
            check(_focusAfterPageSwitch, "keyboard return focus waits for the incoming page to finish moving")
            seek(returning, 145)
            CheckPageSample(check, observations, "return-incoming", returning, False)
            seek(returning, 261)
            check(_pageSwitch.Transition Is Nothing AndAlso VersionPage.Visibility = Visibility.Visible AndAlso InstallPage.Visibility = Visibility.Collapsed AndAlso
                DownloadViewHost.IsHitTestVisible AndAlso VersionPage.Opacity = 1 AndAlso PageX(VersionPage) = 0 AndAlso PageX(InstallPage) = 50 AndAlso Not _focusAfterPageSwitch,
                "completed return releases clocks restores browser input and consumes the pending keyboard focus")
            check(_selectedEntry Is entry AndAlso category.Body Is returnRow.Parent,
                "returning preserves the selected base version and existing browser rows")
            SelectVersion(entry)
            opening = _pageSwitch.Transition
            seek(opening, 180)
            CheckPageSample(check, observations, "enter-incoming", opening, True)
            check(VersionPage.Visibility = Visibility.Collapsed AndAlso InstallPage.Visibility = Visibility.Visible AndAlso Not DownloadViewHost.IsHitTestVisible,
                "entry hides its outgoing browser after one hundred milliseconds while the incoming motion continues")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "page-switch-middle.png"))
            seek(opening, 261)
            check(_pageSwitch.Transition Is Nothing AndAlso InstallPage.Opacity = 1 AndAlso PageX(InstallPage) = 0 AndAlso DownloadViewHost.IsHitTestVisible,
                "completed entry releases opacity and translation clocks with an interactive install page")
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            InstanceName.Text = "保留页面切换方案"
            Dim view = _componentViews(ComponentKind.Fabric), body = view.Card.Body
            Dim selection = _componentSelection.Item(ComponentKind.Fabric)
            SetDownloadView(DownloadView.Versions)
            returning = _pageSwitch.Transition : seek(returning, 40)
            SetDownloadView(DownloadView.Install)
            opening = _pageSwitch.Transition : opening.Controller.Pause()
            Dim retainedClock = _pageSwitch.Transition
            AcceptComponentList(ComponentKind.NeoForge, _selectedEntry, _componentGeneration, _componentEntries(ComponentKind.NeoForge))
            SetDownloadView(DownloadView.Install)
            check(_pageSwitch.Transition Is retainedClock AndAlso view.Card.Body Is body AndAlso InstanceName.Text = "保留页面切换方案" AndAlso
                _componentSelection.Item(ComponentKind.Fabric) Is selection AndAlso _componentSelection.Has(ComponentKind.FabricApi),
                "same-page updates and unrelated component refreshes retain the current animation name rows and dependencies")
            returning.Controller.SkipToFill() : layout()
            check(_pageSwitch.Transition Is opening AndAlso _downloadView = DownloadView.Install,
                "a superseded return clock cannot collapse a newly selected install page")
            FocusDownloadPage()
            SwitchMainPage(True) : layout()
            check(_pageSwitch.Transition Is Nothing AndAlso Not _focusAfterPageSwitch AndAlso SettingsPage.Visibility = Visibility.Visible AndAlso
                InstallPage.Visibility = Visibility.Visible AndAlso InstallPage.Opacity = 1 AndAlso DownloadViewHost.IsHitTestVisible,
                "opening settings settles the owned install motion and discards its pending focus")
            SwitchMainPage(False) : layout()
            check(_pageSwitch.Transition Is Nothing AndAlso _componentSelection.Item(ComponentKind.Fabric) Is selection AndAlso InstanceName.Text = "保留页面切换方案",
                "a settings round trip retains the exact install plan without restarting its page transition")
            SetDownloadView(DownloadView.Versions)
            returning = _pageSwitch.Transition : returning.Controller.Pause()
            ResetTaskView("页面切换验证", True)
            RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "测试下载", .StageFraction = 0.42, .Total = 100, .Completed = 42})
            Dim taskValue = TaskProgress.Value, taskText = TaskTitle.Text
            ShowProgress(Me, New RoutedEventArgs())
            returning.Controller.SkipToFill() : layout()
            check(_pageSwitch.Transition Is Nothing AndAlso DownloadProgress.Visibility = Visibility.Visible AndAlso VersionPage.Visibility = Visibility.Collapsed AndAlso
                InstallPage.Visibility = Visibility.Collapsed AndAlso _categoryMotion.Transition Is Nothing AndAlso DownloadViewHost.IsHitTestVisible,
                "ordinary static preview task navigation removes horizontal ownership and settles both hidden installation panels without a page clock")
            SwitchMainPage(True) : SwitchMainPage(False) : layout()
            check(VersionPage.Visibility = Visibility.Collapsed AndAlso InstallPage.Visibility = Visibility.Collapsed AndAlso DownloadProgress.Visibility = Visibility.Visible,
                "a settings round trip from task progress cannot resurrect an installation panel")
            ResumeInstallation(Me, New RoutedEventArgs()) : layout()
            check(_pageSwitch.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso VersionPage.Visibility = Visibility.Visible AndAlso InstallPage.Visibility = Visibility.Collapsed AndAlso ProgressShortcut.Visibility = Visibility.Visible AndAlso
                TaskProgress.Value = taskValue AndAlso TaskTitle.Text = taskText AndAlso _componentSelection.Item(ComponentKind.Fabric) Is selection,
                "task back restores its actual browser origin even with an older selected version and preserves task and component choices")
            SetDownloadView(DownloadView.Install) : _pageSwitch.Finish()
            SetDownloadView(DownloadView.Versions)
            returning = _pageSwitch.Transition : returning.Controller.Pause()
            source.RootVisual = Nothing : FlushControlLayout()
            check(_pageSwitch.Transition Is Nothing AndAlso VersionPage.Opacity = 1 AndAlso PageX(VersionPage) = 0 AndAlso DownloadViewHost.IsHitTestVisible,
                "unloading an interrupted return releases every owned clock and restores its final input state")
            SetDownloadView(DownloadView.Install)
            check(_pageSwitch.Transition Is Nothing AndAlso InstallPage.Opacity = 1 AndAlso PageX(InstallPage) = 0 AndAlso VersionPage.Visibility = Visibility.Collapsed,
                "detached navigation settles the requested page without a live animation")
            source.RootVisual = host : layout()
            check(_pageSwitch.Transition Is Nothing AndAlso InstallPage.Visibility = Visibility.Visible AndAlso _componentSelection.Item(ComponentKind.Fabric) Is selection AndAlso view.Card.Body Is body,
                "reattaching the install page retains its plan and rows without reviving a stale transition")
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "page-switch-install.png"))
            SetDownloadView(DownloadView.Versions)
            returning = _pageSwitch.Transition : seek(returning, 261)
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "page-switch-versions.png"))
        Finally
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "page-switch-motion.json"), observations.ToString())
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
        End Try
    End Sub

    Private Shared Function PageX(panel As FrameworkElement) As Double
        Return DirectCast(panel.RenderTransform, TranslateTransform).X
    End Function

    Private Sub FinishPageSwitchForCheck(layout As Action)
        Dim clock = _pageSwitch.Transition
        If clock Is Nothing Then Return
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(261), TimeSeekOrigin.BeginTime)
        layout()
    End Sub

    Private Sub CheckPageSample(check As Action(Of Boolean, String), observations As JArray, stage As String, clock As ClockGroup, entering As Boolean)
        Dim elapsed = clock.CurrentTime.Value.TotalMilliseconds
        Dim panels = If(entering, {VersionPage, InstallPage}, {InstallPage, VersionPage})
        Dim actual = {panels(0).Opacity, PageX(panels(0)), panels(1).Opacity, PageX(panels(1))}
        Dim plan = DirectCast(clock.Timeline, ParallelTimeline)
        Dim expected As New List(Of Double)
        For Each part As DoubleAnimation In plan.Children
            Dim progress = Math.Max(0, Math.Min(1, (elapsed - part.BeginTime.Value.TotalMilliseconds) / part.Duration.TimeSpan.TotalMilliseconds))
            If part.EasingFunction IsNot Nothing Then progress = part.EasingFunction.Ease(progress)
            expected.Add(part.From.Value + (part.To.Value - part.From.Value) * progress)
        Next
        observations.Add(New JObject From {{"stage", stage}, {"clockMs", elapsed}, {"actual", New JArray(actual)}, {"expected", New JArray(expected)}})
        check(actual.Zip(expected, Function(a, b) Math.Abs(a - b) < 0.01).All(Function(value) value),
            "real page opacity and translation follow the delayed source timeline: " & stage)
    End Sub
End Class
