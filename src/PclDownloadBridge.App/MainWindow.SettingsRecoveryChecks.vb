Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifySettingsRecoveryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 850, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim task = owner.CheckSettingsRecoveryAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() task.IsCompleted, 30000) Then Throw New TimeoutException("Settings recovery did not settle.")
                task.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "settings-recovery.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._restoringSettings = False : owner._closing = True : owner._launcherReadGeneration += 1
            owner.CancelManagementRequest() : owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckSettingsRecoveryAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim fixture = Path.Combine(directory, "settings-recovery-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(fixture)
        Dim binding As New ExternalLauncherBinding With {.Id = "pcl", .Kind = ExternalLauncherKind.Pcl, .Workspace = fixture}
        Dim state As New LauncherBindingsState : state.Bindings.Add(binding) : state.SelectedRows(binding.Kind) = binding.Id
        Dim settings As New JObject From {{"externalLaunchers", state.ToJson()}, {"appearance", New JObject From {{"theme", "deepblue"}}},
            {"communityResources", New JObject From {{"source", 0}}}}
        Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(item) gate.Task
        ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        _restoringSettings = True
        RestoreAppearance(settings("appearance")) : RestoreDeveloperSettings(Nothing) : RestoreSourcePreferences(settings)
        Dim restore = RestoreLauncherBindingsAsync(settings)
        layout()
        Dim hint = TryCast(FindName("SettingsRestoreHint"), PclHint)
        check(hint IsNot Nothing AndAlso hint.IsVisible AndAlso hint.Theme = PclHintTheme.Blue AndAlso hint.Text.Length > 0,
            "slow startup exposes a semantic PCL loading hint while directory restoration is pending")
        check(Not AppearanceCard.IsEnabled AndAlso Not DeveloperCard.IsEnabled AndAlso Not CommunitySettingsCard.IsEnabled,
            "startup cannot accept appearance developer or community edits whose handlers are suspended")
        check(Not GameFolderCard.IsEnabled AndAlso Not ExternalLauncherCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled,
            "startup keeps target isolation and transfer controls read-only until preferences are complete")
        DarkModeBox.Activate() : DeveloperEnabledBox.Activate() : layout()
        check(Not DarkModeBox.IsChecked.GetValueOrDefault() AndAlso Not _appearance.DarkMode AndAlso Not DeveloperEnabledBox.IsChecked.GetValueOrDefault() AndAlso Not _developer.Enabled,
            "pending startup cannot leave checkbox visuals disagreeing with effective preference models")
        check(Not CommunitySourceBox.Focus() AndAlso Not SpeedLimitBox.Focus(),
            "keyboard focus cannot enter ignored community or download setting inputs during startup")
        ResetAppearance(Nothing, Nothing)
        check(_appearance.Theme = "deepblue", "a queued appearance reset cannot discard the restored theme before startup completes")
        RestoreAppearance(settings("appearance"))
        SpeedLimitBox.Text = "invalid" : SpeedLimitBox.ValidationMessage = ""
        SaveSettings(Nothing, Nothing)
        check(SpeedLimitBox.ValidationMessage = "" AndAlso SettingsSaveFeedback.Text = "",
            "a queued save during restore cannot validate an intermediate profile or show misleading feedback")
        SpeedLimitBox.Text = "0"
        SetBusy(False) : layout()
        check(Not GameFolderCard.IsEnabled AndAlso Not ExternalLauncherCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled,
            "an unrelated task availability refresh cannot reopen settings while startup owns their gate")
        ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        check(Not AppearanceCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled AndAlso DownloadTab.IsEnabled AndAlso SettingsTab.IsEnabled,
            "rapid page changes preserve the startup gate while main navigation remains available")
        gate.SetResult(New LauncherDirectoryResult With {.Succeeded = True}) : Await restore
        CapturePreferenceBaseline() : _restoringSettings = False : layout()
        check(AppearanceCard.IsEnabled AndAlso CommunitySettingsCard.IsEnabled AndAlso DeveloperCard.IsEnabled AndAlso DownloadSettingsCard.IsEnabled AndAlso
            (hint Is Nothing OrElse hint.Visibility = Visibility.Collapsed), "completed startup releases its inputs and removes only its loading hint")
        DarkModeBox.Activate() : DeveloperEnabledBox.Activate() : CommunitySourceBox.SelectedIndex = 2 : layout()
        check(_appearance.DarkMode AndAlso DarkModeBox.IsChecked.GetValueOrDefault() AndAlso _developer.Enabled AndAlso _communityPreferences.Source = 2,
            "fresh preference edits apply normally after startup finishes")

        _restoringSettings = True : _busy = True : SetBusy(True) : _restoringSettings = False : layout()
        check(Not GameFolderCard.IsEnabled AndAlso Not ExternalLauncherCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled AndAlso AppearanceCard.IsEnabled,
            "startup completion cannot unlock active task targets and still allows live appearance changes")
        _busy = False : SetBusy(False) : layout()
        check(GameFolderCard.IsEnabled AndAlso ExternalLauncherCard.IsEnabled AndAlso DownloadSettingsCard.IsEnabled,
            "task completion releases transfer settings after the independent startup gate is gone")

        _restoringSettings = True
        _launcherDirectoryLoader = Function(item) Task.FromException(Of LauncherDirectoryResult)(New IOException("controlled discovery failure"))
        Dim failed = False
        Try
            Await RestoreLauncherBindingsAsync(settings)
        Catch ex As IOException
            failed = True
        Finally
            _restoringSettings = False
        End Try
        layout()
        check(failed AndAlso AppearanceCard.IsEnabled AndAlso DownloadSettingsCard.IsEnabled AndAlso (hint Is Nothing OrElse hint.Visibility = Visibility.Collapsed),
            "startup failure cleanup restores editable settings instead of trapping the user in a disabled page")
        RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : _restoringSettings = True : layout()
        check(Not DarkModeBox.IsEnabled AndAlso CBool(_appearance.ToJson()("darkMode")), "dark mode keeps its stored value while the startup input gate is active")
        _restoringSettings = False
    End Function
End Class
