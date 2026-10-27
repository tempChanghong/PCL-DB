Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyWorldRetirementUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckWorldRetirementAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("World retirement checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "world-retirement.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckWorldRetirementAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareManagementFixture(directory)
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                SetLocalModIsolationForChecks(native, True)
                native = InstalledInstanceCatalog.Read(target, native.Name, CancellationToken.None)
                Dim world = Path.Combine(native.RunDirectory, "saves", "存档保留检查")
                IO.Directory.CreateDirectory(world) : File.WriteAllText(Path.Combine(world, "level.dat"), "unchanged world marker")
                Dim metadata = Path.Combine(native.InstancePath, native.Name & ".json"), originalMetadata = File.ReadAllBytes(metadata)
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(target.Root), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                Dim padded = JObject.Parse(File.ReadAllText(metadata))
                ' Keep the real worker read pending until its Await has captured
                ' the held dispatcher context; this remains below JSON limits.
                padded("retirementFixturePadding") = New String("x"c, 1024 * 1024)
                Dim stableMetadata = padded.ToString()
                padded("retirementFixtureChanged") = True
                Dim changedMetadata = padded.ToString(), taskGeneration = _taskGeneration
                Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.DataPack, .Name = "存档退役检查", .FileName = "world-fixture.zip", .GameVersions = {"1.20.1"}, .Size = 100, .Sha1 = New String("a"c, 40)}
                Dim shader = release.Copy() : shader.Kind = CommunityResourceKind.Shader
                Dim prompt As Task(Of ResourceInstallationPlan) = Nothing, heldRequest As CancellationTokenSource = Nothing
                Dim openData As Func(Of Task) =
                    Async Function()
                        File.WriteAllText(metadata, stableMetadata)
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        prompt = PromptResourceTargetAsync(release, target, ChangeFolderButton)
                        Await WaitPackUiAsync(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading)
                        ResourceTargetInstance.SelectedItem = Nothing : layout()
                    End Function
                Dim holdFault As Func(Of Task(Of HeldDependencyContext)) =
                    Async Function()
                        Dim item = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = native.Name)
                        File.WriteAllText(metadata, changedMetadata)
                        Dim capturedContext As New HeldDependencyContext, context = SynchronizationContext.Current
                        SynchronizationContext.SetSynchronizationContext(capturedContext)
                        Try
                            ResourceTargetInstance.SelectedItem = item
                            heldRequest = _resourceWorldRequest
                        Finally
                            SynchronizationContext.SetSynchronizationContext(context)
                        End Try
                        Await WaitPackUiAsync(Function() capturedContext.Count > 0)
                        Return capturedContext
                    End Function
                Dim close As Func(Of Task) =
                    Async Function()
                        CompleteOnlinePrompt("no") : Await prompt
                        StopOnlinePromptMotion() : HideOnlinePrompt() : layout()
                    End Function
                ShowDownload(Nothing, Nothing) : settle()
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}})
                Dim label = "/launcher=" & target.Kind.ToString() & "/animated-dark=" & dark

                Await openData()
                Dim held = Await holdFault(), firstRequest = heldRequest
                ResourceTargetInstance.SelectedItem = Nothing : layout()
                check(firstRequest IsNot Nothing AndAlso firstRequest.IsCancellationRequested AndAlso _resourceWorldRequest Is Nothing AndAlso Not _resourceWorldLoading,
                    "deselecting an instance detaches its pending world request before cancellation can resume it" & label)
                held.Drain() : Await Task.Yield() : held.Drain() : layout()
                check(_resourceTargetPrompt AndAlso ResourceTargetInstance.SelectedItem Is Nothing AndAlso String.IsNullOrEmpty(_resourceTargetProblem) AndAlso
                    OnlinePackNameHint.Text.Contains("请选择安装目标") AndAlso Not OnlinePackConfirm.IsEnabled AndAlso ResourceTargetRefresh.IsEnabled,
                    "a queued world read fault cannot replace the current empty-instance selection feedback" & label)
                Await close()

                Await openData() : held = Await holdFault()
                Dim secondRequest = heldRequest
                Await close()
                check(secondRequest.IsCancellationRequested AndAlso _resourceWorldRequest Is Nothing AndAlso Not _resourceWorldLoading,
                    "closing the data pack picker retires its world owner before a replacement prompt is opened" & label)
                Dim replacement = PromptResourceTargetAsync(shader, target, ChangeFolderButton)
                Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
                Dim parent = _resourceTargetRequest, clock = _onlinePromptMotion, instanceCount = ResourceTargetInstance.Items.Count
                held.Drain() : Await Task.Yield() : held.Drain() : layout()
                check(_resourceTargetRequest Is parent AndAlso Not parent.IsCancellationRequested AndAlso _resourceTargetRelease.Kind = CommunityResourceKind.Shader AndAlso
                    _resourceTargetPrompt AndAlso Not _resourceTargetLoading AndAlso Not _resourceWorldLoading AndAlso String.IsNullOrEmpty(_resourceTargetProblem) AndAlso OnlinePackConfirm.IsEnabled AndAlso
                    ResourceTargetInstance.Items.Count = instanceCount AndAlso _onlinePromptMotion Is clock AndAlso ResourceTargetWorldRow.Visibility = Visibility.Collapsed,
                    "an old world fault cannot disable a new shader picker or replace its choices prompt clock and validation" & label)
                SaveControlVisual(host, Path.Combine(directory, "world-retirement-" & target.Kind.ToString() & If(dark, "-dark.png", "-light.png")))
                CompleteOnlinePrompt("no") : Await replacement : StopOnlinePromptMotion() : HideOnlinePrompt()

                Await openData()
                ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = native.Name)
                Await WaitPackUiAsync(Function() Not _resourceWorldLoading) : layout()
                ResourceTargetWorld.SelectedItem = ResourceTargetWorld.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledWorld).Name = "存档保留检查")
                ConfirmResourceTargetAsync()
                Dim plan = Await prompt
                StopOnlinePromptMotion() : HideOnlinePrompt() : layout()
                check(plan IsNot Nothing AndAlso plan.InstanceName = native.Name AndAlso SameManagementTarget(plan.LauncherTarget, target) AndAlso
                    plan.Destination = Path.Combine(world, "datapacks", release.FileName) AndAlso Not _resourceTargetPrompt AndAlso _resourceWorldRequest Is Nothing,
                    "retry after world cancellation accepts only the explicitly selected native instance and world" & label)
                File.WriteAllBytes(metadata, originalMetadata)
                check(protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    IO.Directory.GetFiles(Path.GetDirectoryName(target.Root), "*", SearchOption.AllDirectories).Length = protectedFiles.Count AndAlso
                    firstRequest.IsCancellationRequested AndAlso secondRequest.IsCancellationRequested AndAlso Not _busy AndAlso _taskGeneration = taskGeneration AndAlso PageSurface.IsEnabled,
                    "world cancellation replacement and retry preserve every launcher game and save file without starting a download" & label)
                _motionPreview = False
            Next
        Next
    End Function
End Class
