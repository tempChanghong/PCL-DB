Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementPublicationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "management-publication.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementPublicationAsync(directory, ControlLayout(host), check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Management publication checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementPublicationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each removed In {False, True}
                For Each rootEntry In {False, True}
                    For scenario = 0 To 3
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False
                        Dim folders = PrepareLocalModFixture(Path.Combine(directory, "native-" & launcher & "-" & removed & "-" & rootEntry & "-" & scenario)), game = folders(0).Location
                        LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "publication_shared")
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                        SetLocalModIsolationForChecks(native, True)
                        Dim realProvider As Func(Of LauncherInstallTarget, CancellationToken, Task(Of List(Of InstalledInstance))) = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        _managementProvider = realProvider
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + scenario) Mod 2 = 0}}) : _motionPreview = True
                        _surfaceMotion.PauseForChecks = True : _managementMotion.PauseForChecks = True
                        If rootEntry Then
                            ShowSettings(Nothing, Nothing) : settle() : ShowManagement(Nothing, Nothing) : layout()
                        End If
                        Dim oldInstance = _localModInstance, taskGeneration = _taskGeneration
                        If Not removed Then SetLocalModIsolationForChecks(_managedInstance, False)
                        Dim accepted = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                        If removed Then accepted = accepted.Where(Function(item) item.Name <> native.Name).ToList()
                        Dim protectedFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(game))
                        Dim tokens(2) As CancellationToken, sources = Enumerable.Range(0, 3).Select(Function(index) New CancellationTokenSource()).ToArray()
                        For index = 0 To 2 : tokens(index) = sources(index).Token : Next
                        _localModUpdatesRequest = sources(0) : _localModFolderRequest = sources(1) : _localModRequest = sources(2)
                        _localModScanning = True : _localModReadSucceeded = True : LocalModStatus.Text = "old Mod read"
                        Dim chosen = scenario, expectedMain As FrameworkElement = ManagementPage, expectedRows = accepted, expectedInstance As InstalledInstance = Nothing
                        Dim expectedSurfaceClock As ClockGroup = Nothing, expectedChildClock As ClockGroup = Nothing, nested As Task = Task.CompletedTask
                        Dim newRows = accepted.ToList(), status As String = Nothing, escaped = False
                        Dim label = "/launcher=" & target.Kind.ToString() & "/removed=" & removed & "/root-entry=" & rootEntry & "/case=" & scenario
                        _managementProvider = Function(binding, token) Task.FromResult(accepted)
                        Using registration = tokens(scenario Mod 3).Register(Sub()
                                                                                 If chosen = 2 Then Throw New IOException("Controlled publication cancellation failure")
                                                                                 If chosen = 1 Then
                                                                                     _managementProvider = Function(binding, token) Task.FromResult(newRows)
                                                                                     nested = ScanManagementAsync(True)
                                                                                 Else
                                                                                     ShowSettings(Nothing, Nothing)
                                                                                 End If
                                                                                 expectedMain = ActiveSurface() : expectedRows = _managementInstances : expectedInstance = _managedInstance
                                                                                 expectedSurfaceClock = _surfaceMotion.Transition : expectedChildClock = _managementMotion.Transition
                                                                                 status = LocalModStatus.Text
                                                                                 If chosen = 3 Then Throw New IOException("Controlled superseded publication failure")
                                                                             End Sub)
                            Try : Await RefreshManagementAsync() : Await nested : Catch : escaped = True : End Try
                            layout()
                            Dim feedback = If(removed, ManagementStatus.Text, LocalModStatus.Text)
                            check(Not escaped AndAlso _managementInstances Is expectedRows AndAlso ActiveSurface() Is expectedMain AndAlso
                                If(chosen = 2, feedback.Contains("Controlled publication"), _managedInstance Is expectedInstance AndAlso _surfaceMotion.Transition Is expectedSurfaceClock AndAlso _managementMotion.Transition Is expectedChildClock AndAlso LocalModStatus.Text = status),
                                "publishing native instance changes retains a newer scan, page, feedback and animation while exposing owned cancellation failures" & label)
                            settle()
                            check(tokens.All(Function(token) token.IsCancellationRequested) AndAlso _localModUpdatesRequest Is Nothing AndAlso _localModFolderRequest Is Nothing AndAlso _localModRequest Is Nothing AndAlso
                                Not _localModReadSucceeded AndAlso Not _localModScanning AndAlso _managementRequest Is Nothing AndAlso Not _managementScanning AndAlso
                                ActiveSurface() Is expectedMain AndAlso expectedMain.Opacity = 1 AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso _taskGeneration = taskGeneration,
                                "all retired Mod readers and clocks settle without restoring stale context or covering the accepted page" & label)
                        End Using
                        For Each source In sources : source.Dispose() : Next
                        ' Restore any orphaned baseline-only surface opacity before the
                        ' independent retry observation; the two failure checks are saved.
                        For Each page In New FrameworkElement() {DownloadPage, SettingsPage, AboutPage, ManagementPage}
                            page.BeginAnimation(UIElement.OpacityProperty, Nothing) : page.SetCurrentValue(UIElement.OpacityProperty, 1.0)
                        Next
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False : _managementProvider = realProvider
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        If LocalModsPage.IsVisible Then BackFromLocalMods() : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        check(_localModReadSucceeded AndAlso SameManagementTarget(_localModInstance.Target, target) AndAlso
                            SameGameRoot(_localModInstance.RunDirectory, If(removed, oldInstance.RunDirectory, game)) AndAlso _taskGeneration = taskGeneration AndAlso
                            protectedFiles.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "retry rereads the native isolation and shared Mods without changing existing files or starting a download" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
