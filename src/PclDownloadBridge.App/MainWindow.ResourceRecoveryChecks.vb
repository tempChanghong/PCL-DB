Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceTargetRecoveryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 810, .Height = 470}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 810, 470)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                layout()
                Dim task = owner.CheckResourceRecoveryAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() task.IsCompleted, 30000) Then Throw New TimeoutException("Resource recovery checks did not settle.")
                task.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-recovery.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelManagementRequest() : owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckResourceRecoveryAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim fixture = Path.Combine(directory, "resource-recovery-" & Guid.NewGuid().ToString("N")), game = Path.Combine(fixture, "game"), work = Path.Combine(fixture, "work")
        IO.Directory.CreateDirectory(Path.Combine(work, "PCL"))
        Dim config = Path.Combine(work, "PCL/Setup.ini") : File.WriteAllText(config, "LaunchArgumentIndieV2:0")
        Dim instancePath = Path.Combine(game, "versions/Shared") : IO.Directory.CreateDirectory(instancePath)
        Dim metadata = Path.Combine(instancePath, "Shared.json")
        File.WriteAllText(metadata, New JObject From {{"id", "Shared"}, {"clientVersion", "1.20.1"}, {"type", "release"}, {"mainClass", "net.minecraft.client.main.Main"},
            {"libraries", New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.0"}})}}.ToString())
        Dim oldConfig = File.ReadAllBytes(config), oldMetadata = File.ReadAllBytes(metadata)
        _pclBase = work : _selectedFolder = New GameFolder With {.Location = game, .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = work, .LauncherBindingId = "recovery-fixture", .Name = "Fixture"}
        _folders = New List(Of GameFolder) From {_selectedFolder} : FolderList.ItemsSource = _folders : FolderList.SelectedItem = _selectedFolder
        Dim target = CurrentInstallTarget(), instances = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
        Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Name = "恢复检查", .FileName = "recovery.jar", .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}, .Size = 100, .Sha1 = New String("a"c, 40)}
        _motionPreview = False
        For Each cancelledTask In {False, True}
            If cancelledTask Then
                _managementProvider = Function(binding, token) Task.FromCanceled(Of List(Of InstalledInstance))(New CancellationToken(True))
            Else
                _managementProvider = Function(binding, token) Task.FromException(Of List(Of InstalledInstance))(New OperationCanceledException("非当前请求的扫描中断"))
            End If
            Dim prompt = PromptResourceTargetAsync(release, target, ChangeFolderButton)
            Await Task.Delay(10) : layout()
            check(Not _resourceTargetLoading AndAlso ResourceTargetRefresh.IsEnabled AndAlso Not OnlinePackConfirm.IsEnabled AndAlso OnlinePackNameHint.IsVisible AndAlso OnlinePackNameHint.Theme = PclHintTheme.Red,
                "unowned scan cancellation becomes a retryable error/cancelled-task=" & cancelledTask)
            check(Not _resourceTargetRequest.IsCancellationRequested AndAlso Not String.IsNullOrWhiteSpace(_resourceTargetProblem),
                "unowned scan failure preserves the live picker owner and explains the failure/cancelled-task=" & cancelledTask)
            _managementProvider = Function(binding, token) Task.FromResult(instances)
            RefreshResourceTargets(Nothing, Nothing) : Await Task.Delay(10) : layout()
            check(Not _resourceTargetLoading AndAlso ResourceTargetInstance.Items.Count = 1 AndAlso ResourceTargetInstance.SelectedItem Is Nothing AndAlso Not OnlinePackConfirm.IsEnabled,
                "refresh recovers in the same picker without implicitly changing the install target/cancelled-task=" & cancelledTask)
            CompleteOnlinePrompt("no") : Await prompt
        Next
        _managementProvider = Function(binding, token) Task.FromResult(instances)
        Dim ready = PromptResourceTargetAsync(release, target, ChangeFolderButton)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
        check(OnlinePackConfirm.IsEnabled, "a fresh picker remains usable after scan failures")
        _resourceTargetValidating = True : ResourceTargetInstance.IsEnabled = False : ValidateResourceTarget() : layout()
        check(OnlinePackNameHint.IsVisible AndAlso OnlinePackNameHint.Theme = PclHintTheme.Blue AndAlso OnlinePackNameHint.Text.Contains("检查") AndAlso Not ResourceTargetRefresh.IsEnabled AndAlso Not OnlinePackConfirm.IsEnabled,
            "target validation displays a blue busy explanation while preventing duplicate confirmation")
        CancelOnlinePackName(Nothing, Nothing) : check((Await ready) Is Nothing AndAlso Not _resourceTargetPrompt, "target validation remains cancellable without creating an installation plan")

        Dim delayed As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(binding, token) delayed.Task
        Dim abandoned = PromptResourceTargetAsync(release, target, ChangeFolderButton)
        check(_resourceTargetLoading AndAlso OnlinePackNameHint.Theme = PclHintTheme.Blue AndAlso Not OnlinePackConfirm.IsEnabled, "an active scan starts with loading feedback and a closed confirmation gate")
        CancelOnlinePackName(Nothing, Nothing) : Await abandoned
        _managementProvider = Function(binding, token) Task.FromResult(instances)
        Dim replacement = PromptResourceTargetAsync(release, target, ChangeFolderButton), current = _resourceTargetRequest
        delayed.SetException(New OperationCanceledException("已退役的扫描中断"))
        Await Task.Delay(20) : layout()
        check(_resourceTargetRequest Is current AndAlso Not current.IsCancellationRequested AndAlso Not _resourceTargetLoading AndAlso ResourceTargetInstance.Items.Count = 1 AndAlso OnlinePackConfirm.IsEnabled AndAlso String.IsNullOrEmpty(_resourceTargetProblem),
            "a late cancelled scan cannot overwrite a replacement picker's ready state")
        CompleteOnlinePrompt("no") : Await replacement
        check(File.ReadAllBytes(config).SequenceEqual(oldConfig) AndAlso File.ReadAllBytes(metadata).SequenceEqual(oldMetadata) AndAlso Not File.Exists(Path.Combine(game, "mods/recovery.jar")) AndAlso Not _busy,
            "recovery and cancellation preserve launcher settings, existing metadata and resource files")
    End Function
End Class
