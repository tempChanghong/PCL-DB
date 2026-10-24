Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyExternalLauncherUi(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyExternalLauncherWorkflowAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 15000) Then Throw New TimeoutException("External launcher UI did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner._launcherReadGeneration += 1
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub

    Private Async Function VerifyExternalLauncherWorkflowAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim added = 0
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label) : added += 1
                                                  End Sub
        Dim root = Path.Combine(directory, "launcher-ui-" & Guid.NewGuid().ToString("N")), game = Path.Combine(root, ".minecraft")
        Dim pcl = Path.Combine(root, "PCL"), hmcl = Path.Combine(root, "HMCL"), ce = Path.Combine(root, "CE")
        For Each folder In {game, pcl, hmcl, ce, Path.Combine(pcl, "PCL"), Path.Combine(ce, "PCL")}
            IO.Directory.CreateDirectory(folder)
        Next
        Dim pclConfig = Path.Combine(pcl, "PCL/Setup.ini"), hmclConfig = Path.Combine(hmcl, "hmcl.json"), ceConfig = Path.Combine(ce, "PCL/config.v1.yml")
        File.WriteAllText(pclConfig, "LaunchFolderSelect:" & game)
        File.WriteAllText(hmclConfig, New JObject From {{"last", "同目录"}, {"configurations", New JObject From {{"同目录", New JObject From {{"gameDir", game}}}}}}.ToString())
        File.WriteAllText(ceConfig, "LaunchFolderSelect: '" & game.Replace("'", "''") & "'")
        Dim before = {pclConfig, hmclConfig, ceConfig}.ToDictionary(Function(filePath) filePath, Function(filePath) File.ReadAllBytes(filePath))
        _launcherDirectoryLoader = Function(binding) Task.FromResult(New LauncherDirectoryReader().Read(binding, False))
        _pclBase = pcl : SwitchMainPage(True) : layout()
        Dim legacy As New JObject From {{"pclBase", pcl}, {"gameRoot", game}, {"unrelatedFutureValue", 19}}
        Await RestoreLauncherBindingsAsync(legacy) : layout()
        verify(ExternalLauncherCard.Parent Is SettingsContent AndAlso ExternalLauncherCard IsNot GameFolderCard AndAlso PclBindingList.Items.Count = 1,
            "external launcher bindings occupy an independent PCL-shaped settings card")
        verify(_selectedFolder.Location = game AndAlso _selectedFolder.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.Pcl AndAlso CurrentInstallTarget().PclDirectory = pcl,
            "legacy saved directory migrates as a PCL target with its original source base")
        Dim preferences = Path.Combine(root, "preferences.json")
        DownloadSettingsFile.Save(preferences, legacy)
        CapturePreferenceBaseline() : _preferenceBaselinePath = Path.GetFullPath(preferences)
        SavePreferenceValues(preferences, Path.Combine(root, "absent.json"), 0)
        Dim migrated = JObject.Parse(File.ReadAllText(preferences))
        verify(migrated("externalLaunchers") IsNot Nothing AndAlso CInt(migrated("unrelatedFutureValue")) = 19 AndAlso CStr(migrated("gameRoot")) = game,
            "migration is saved even with an unchanged baseline while retaining unrelated preferences")

        Dim h As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Hmcl, .Workspace = hmcl, .ConfigPath = hmclConfig, .SharedConfigPath = Path.Combine(hmcl, "shared.json")}
        Dim c As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.PclCe, .Workspace = ce}
        AddLauncherBinding(h) : Await ReadLauncherBindingAsync(h)
        AddLauncherBinding(c) : Await ReadLauncherBindingAsync(c) : layout()
        verify(_folders.Where(Function(folder) SameGameRoot(folder.Location, game)).Count() = 3, "one physical directory remains selectable under three launcher identities")
        verify(_selectedFolder.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.Pcl, "reading other launchers does not replace an already chosen installation target")
        Dim second = h.Copy() : second.Id = "second-hmcl" : second.ConfigPath = Nothing
        AddLauncherBinding(second)
        verify(HmclBindingList.Items.Count = 2 AndAlso CeBindingList.Items.Count = 1, "multiple copies and explicit config bindings coexist independently")
        HmclBindingList.SelectedItem = h
        Dim hFolder = _folders.Single(Function(folder) folder.LauncherBindingId = h.Id AndAlso SameGameRoot(folder.Location, game))
        FolderList.SelectedItem = hFolder : layout()
        verify(CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl AndAlso CurrentInstallTarget().PclDirectory Is Nothing AndAlso SelectedPath.Text = game,
            "selecting an HMCL directory binds the launcher source and root together")
        verify(IsolationBox.IsEnabled AndAlso IsolationHint.Text.Contains("原生隔离"), "foreign targets expose the same global preference and follow their native defaults")
        verify(ExternalInstallProblem(pack:=True) Is Nothing, "foreign targets no longer gate the local or online modpack entry before native installation")
        RepairBox.IsChecked = True
        verify(ExternalInstallProblem() Is Nothing AndAlso CurrentInstallTarget().ConfigPath = hmclConfig, "foreign repair entry uses its explicit configuration source instead of the first-batch restriction")
        RepairBox.IsChecked = False
        Dim frozen = CurrentInstallTarget()
        CeBindingList.SelectedItem = c : Await ReadLauncherBindingAsync(c)
        verify(_selectedFolder Is hFolder AndAlso frozen.Kind = ExternalLauncherKind.Hmcl AndAlso frozen.Workspace = hmcl, "refresh of an overlapping CE root preserves the chosen HMCL target and its snapshot")
        Dim saved = BuildSettings(0)
        Dim restored = LauncherBindingsState.Restore(saved, pcl)
        verify(restored.Bindings.Count = 4 AndAlso restored.TargetBindingId = h.Id AndAlso restored.TargetRoot = game,
            "binding preferences retain multiple sources and the exact same-root target identity")
        DownloadSettingsFile.SaveChanges(preferences, Path.Combine(root, "absent.json"), New JObject From {{"externalLaunchers", New JObject From {{"bindings", New JObject From {{"parallel-window", New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Pcl, .Workspace = ce}.ToJson()}}}}}})
        SavePreferenceValues(preferences, Path.Combine(root, "absent.json"), 0)
        Dim concurrent = JObject.Parse(File.ReadAllText(preferences))
        verify(DirectCast(concurrent("externalLaunchers")("bindings"), JObject).Count = 5 AndAlso CInt(concurrent("unrelatedFutureValue")) = 19,
            "saving new bindings merges a different window's binding and unrelated settings instead of replacing them")

        _launcherDirectoryLoader = Function(binding) Task.FromResult(New LauncherDirectoryResult With {.Succeeded = False})
        Await ReadLauncherBindingAsync(h)
        verify(_selectedFolder Is hFolder AndAlso _launcherFolders(h.Id).Contains(hFolder), "a failed refresh preserves the last accepted directories and active target")
        Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(binding) gate.Task
        Dim old = ReadLauncherBindingAsync(h)
        _launcherDirectoryLoader = Function(binding) Task.FromResult(New LauncherDirectoryReader().Read(binding, False))
        Await ReadLauncherBindingAsync(h)
        Dim accepted = _launcherFolders(h.Id)
        gate.TrySetResult(New LauncherDirectoryResult With {.Succeeded = True}) : Await old
        verify(_launcherFolders(h.Id) Is accepted AndAlso CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl, "an older asynchronous directory result cannot replace a newer accepted refresh")
        hFolder = _selectedFolder
        gate = New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(binding) gate.Task
        old = ReadLauncherBindingAsync(h)
        _busy = True : SetBusy(True) : _busy = False : SetBusy(False)
        gate.TrySetResult(New LauncherDirectoryResult With {.Succeeded = True}) : Await old
        verify(_launcherFolders(h.Id) Is accepted AndAlso _selectedFolder Is hFolder, "a discovery begun before a download cannot apply after the busy interval ends")

        Dim install As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously), installRoot As String = Nothing
        _installRunner = Function(entry, destination, name, components, mode, strategy, token)
                             installRoot = destination : Return install.Task
                         End Function
        _componentProvider = Function(policy, kind, mc, token) Task.FromResult(New List(Of ComponentEntry)())
        ShowDownload(Nothing, Nothing)
        SelectVersion(BrowserEntry("1.20.1", "release", "2023-06-12T12:00:00Z"))
        Await RefreshComponentListsAsync()
        _pageSwitch.Finish() : _surfaceMotion.Finish() : _categoryMotion.Finish()
        InstanceName.Text = "Launcher-Frozen-Task" : RepairBox.IsChecked = False : layout()
        StartInstall(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _busy AndAlso installRoot IsNot Nothing)
        verify(_taskInstallTarget.Kind = ExternalLauncherKind.Hmcl AndAlso _taskInstallTarget.Root = game AndAlso _taskInstallTarget.PclDirectory Is Nothing,
            "installation freezes the selected HMCL root and never captures another PCL directory")
        verify(Not ExternalLauncherCard.IsEnabled AndAlso Not GameFolderCard.IsEnabled, "active download disables both launcher bindings and directory selection")
        Dim readCalls = 0
        _launcherDirectoryLoader = Function(binding)
                                       readCalls += 1 : Return Task.FromResult(New LauncherDirectoryResult())
                                   End Function
        Await ReadLauncherBindingAsync(h)
        verify(readCalls = 0, "a stale reload action during download cannot begin another directory read")
        Dim count = _launcherState.Bindings.Count
        AddLauncherBinding(New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Hmcl, .Workspace = root})
        FolderList.SelectedItem = _folders.First(Function(folder) folder.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.Pcl)
        verify(_launcherState.Bindings.Count = count AndAlso _selectedFolder Is hFolder AndAlso _taskInstallTarget.Root = installRoot,
            "queued binding or folder changes during download cannot retarget the installation")
        Dim taskWorkspace = _taskInstallTarget.Workspace
        hFolder.LauncherWorkspace = root : hFolder.LauncherConfigPath = root : _pclBase = root
        verify(_taskInstallTarget.Workspace = taskWorkspace AndAlso _taskInstallTarget.Root = game AndAlso _taskInstallTarget.ConfigPath = hmclConfig AndAlso _taskInstallTarget.SharedConfigPath = h.SharedConfigPath, "task context including native configuration paths is immutable even if live preference objects change")
        hFolder.LauncherWorkspace = hmcl : hFolder.LauncherConfigPath = hmclConfig : _pclBase = pcl
        CancelInstall(Nothing, Nothing) : install.TrySetCanceled()
        Await WaitPackUiAsync(Function() Not _busy) : layout()
        verify(ExternalLauncherCard.IsEnabled AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso _selectedFolder Is hFolder,
            "cancelled task re-enables bindings without losing its selected source")
        verify(before.All(Function(item) item.Value.SequenceEqual(File.ReadAllBytes(item.Key))), "UI binding migration and cancellation leave all launcher configurations byte-for-byte unchanged")

        SwitchMainPage(True) : layout()
        ExternalLauncherCard.BringIntoView() : layout()
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : host.Width = 810 : layout()
            verify(ExternalLauncherCard.ActualWidth > 500 AndAlso PclBindingList.ActualWidth >= 100 AndAlso HmclBindingList.IsVisible AndAlso CeBindingList.IsVisible,
                "external launcher rows fit the minimum settings width in dark mode " & dark)
            SaveControlVisual(host, Path.Combine(directory, "external-launchers-" & If(dark, "dark", "blue") & ".png"))
        Next
        _motionPreview = True : _surfaceMotion.PauseForChecks = True
        SwitchMainPage(False) : _surfaceMotion.Finish() : SwitchMainPage(True) : layout()
        Dim entryClock = _surfaceMotion.Transition
        Dim beforeCount = _launcherState.Bindings.Count
        AddLauncherBinding(New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Pcl, .Workspace = root})
        verify(entryClock IsNot Nothing AndAlso _launcherState.Bindings.Count = beforeCount, "root animation input gating also rejects direct stale launcher actions")
        PauseSelectionClock(entryClock) : entryClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(245), TimeSeekOrigin.BeginTime) : layout()
        verify(_surfaceMotion.Transition Is entryClock AndAlso ExternalLauncherCard.IsHitTestVisible AndAlso ExternalLauncherCard.Opacity > 0 AndAlso ExternalLauncherCard.Opacity < 1,
            "launcher settings enter through the existing PCL page clock with a visible staggered card fade")
        SaveControlVisual(host, Path.Combine(directory, "external-launchers-entry-245.png"))
        _surfaceMotion.Finish()
        Dim future As New JObject From {{"format", 2}, {"opaqueFutureData", "keep"}}
        Await RestoreLauncherBindingsAsync(New JObject From {{"externalLaunchers", future}, {"gameRoot", game}})
        verify(_selectedFolder Is Nothing AndAlso _launcherState.ReadOnlyFormat AndAlso JToken.DeepEquals(_launcherState.ToJson(), future),
            "unknown saved binding formats are preserved without guessing a launcher for the old root")
        File.WriteAllText(Path.Combine(directory, "external-launcher-ui-count.json"), New JObject From {{"checks", added}, {"configurationBytesPreserved", True}, {"realGameLaunched", False}}.ToString())
    End Function
End Class
