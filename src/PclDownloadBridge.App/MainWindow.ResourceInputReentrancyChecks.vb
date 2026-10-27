Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceInputReentrancyUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 850, .Height = 600}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceInputReentrancyAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Resource input reentrancy checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-input-reentrancy.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Sub SettleResourceWorldsForInputChecks()
        If Not PumpMotionUntil(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading, 5000) Then Throw New TimeoutException("Nested resource input did not settle.")
    End Sub
    Private Sub SelectResourceInstanceForInputChecks(name As String)
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(item) DirectCast(item.Tag, InstalledInstance).Name = name)
        SettleResourceWorldsForInputChecks()
    End Sub
    Private Sub SelectResourceWorldForInputChecks()
        ResourceTargetWorld.SelectedItem = ResourceTargetWorld.Items.OfType(Of ComboBoxItem)().Single(Function(item) DirectCast(item.Tag, InstalledWorld).Name = "后选存档")
    End Sub

    Private Async Function CheckResourceInputReentrancyAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareManagementFixture(directory)
                    FolderList.SelectedItem = folders(launcher)
                    ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
                    Dim target = CurrentInstallTarget(), instances = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                    For Each instance In instances.Where(Function(item) item.Name = "我的原版" OrElse item.Name = "Fabric 实例")
                        Dim world = Path.Combine(instance.RunDirectory, "saves", "后选存档")
                        IO.Directory.CreateDirectory(world) : File.WriteAllText(Path.Combine(world, "level.dat"), "preserved world marker")
                    Next
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), generation = _taskGeneration, calls = 0
                    _managementProvider = Function(binding, token)
                                              calls += 1 : Return Task.FromResult(instances)
                                          End Function
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout() : _motionPreview = animated
                    Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated
                    Dim data As New ModpackRelease With {.Kind = CommunityResourceKind.DataPack, .Name = "重入输入检查", .FileName = "world-input.zip", .GameVersions = {"1.20.1"}, .Size = 100, .Sha1 = New String("a"c, 40)}
                    Dim shader = data.Copy() : shader.Kind = CommunityResourceKind.Shader : shader.Name = "替换后的光影选择"
                    Dim prompt As Task(Of ResourceInstallationPlan) = Nothing
                    Dim open As Action = Sub()
                                             prompt = PromptResourceTargetAsync(data, target, ChangeFolderButton)
                                             SettleResourceWorldsForInputChecks() : SelectResourceInstanceForInputChecks("我的原版")
                                         End Sub
                    Dim close As Func(Of Task) = Async Function()
                                                     CompleteOnlinePrompt("no") : Await prompt
                                                     StopOnlinePromptMotion() : HideOnlinePrompt() : layout()
                                                 End Function

                    open()
                    Dim parent = _resourceTargetRequest, child = CancellationTokenSource.CreateLinkedTokenSource(parent.Token)
                    _resourceWorldRequest = child : _resourceWorldLoading = True : calls = 0
                    Dim replacement As Task(Of ResourceInstallationPlan) = Nothing, replacementParent As CancellationTokenSource = Nothing
                    Dim newOwner As TaskCompletionSource(Of String) = Nothing, newClock As Media.Animation.ClockGroup = Nothing
                    Dim choices As Object() = Nothing
                    Using registration = child.Token.Register(Sub()
                                                                 CompleteOnlinePrompt("no")
                                                                 replacement = PromptResourceTargetAsync(shader, target, ChangeFolderButton)
                                                                 SelectResourceInstanceForInputChecks("Fabric 实例")
                                                                 replacementParent = _resourceTargetRequest : newOwner = _onlinePrompt : newClock = _onlinePromptMotion
                                                                 If newClock IsNot Nothing Then PauseSelectionClock(newClock)
                                                                 choices = ResourceTargetInstance.Items.Cast(Of Object)().ToArray()
                                                             End Sub)
                        RefreshResourceTargets(Nothing, Nothing)
                    End Using
                    SettleResourceWorldsForInputChecks() : layout()
                    check(_resourceTargetPrompt AndAlso _resourceTargetRequest Is replacementParent AndAlso Not replacementParent.IsCancellationRequested AndAlso _onlinePrompt Is newOwner,
                        "refresh cancellation replacement retains its new prompt and parent" & label)
                    check(calls = 1 AndAlso ResourceTargetInstance.Items.Cast(Of Object)().SequenceEqual(choices),
                        "an old refresh cannot rescan or rebuild a replacement picker's choices" & label)
                    check(DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance).Name = "Fabric 实例" AndAlso _resourceTargetRelease.Kind = CommunityResourceKind.Shader,
                        "an old refresh cannot restore its previous instance over the replacement selection" & label)
                    check(_onlinePromptMotion Is newClock AndAlso PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso OnlinePackConfirm.IsEnabled,
                        "replacement refresh preserves its current animation input gate and validation" & label)
                    Await prompt : CompleteOnlinePrompt("no") : Await replacement
                    child.Dispose() : StopOnlinePromptMotion() : HideOnlinePrompt()

                    open() : parent = _resourceTargetRequest : child = CancellationTokenSource.CreateLinkedTokenSource(parent.Token)
                    _resourceWorldRequest = child : _resourceWorldLoading = True : calls = 0
                    Dim selectedWorld As Object = Nothing
                    Using registration = child.Token.Register(Sub()
                                                                 RefreshResourceTargets(Nothing, Nothing)
                                                                 SettleResourceWorldsForInputChecks()
                                                                 SelectResourceInstanceForInputChecks("Fabric 实例") : SelectResourceWorldForInputChecks()
                                                                 selectedWorld = ResourceTargetWorld.SelectedItem
                                                                 choices = ResourceTargetInstance.Items.Cast(Of Object)().ToArray()
                                                             End Sub)
                        RefreshResourceTargets(Nothing, Nothing)
                    End Using
                    SettleResourceWorldsForInputChecks() : layout()
                    check(_resourceTargetRequest Is parent AndAlso Not parent.IsCancellationRequested, "nested same-picker refresh retains its live parent" & label)
                    check(calls = 1 AndAlso ResourceTargetInstance.Items.Cast(Of Object)().SequenceEqual(choices),
                        "the newest refresh wins even when its parent request is unchanged" & label)
                    check(ResourceTargetWorld.SelectedItem Is selectedWorld AndAlso selectedWorld IsNot Nothing AndAlso
                        DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance).Name = "Fabric 实例" AndAlso OnlinePackConfirm.IsEnabled,
                        "a late outer refresh preserves the newer explicit instance and world" & label)
                    child.Dispose() : Await close()

                    open() : parent = _resourceTargetRequest : child = CancellationTokenSource.CreateLinkedTokenSource(parent.Token)
                    _resourceWorldRequest = child : _resourceWorldLoading = True : calls = 0
                    Dim worlds As Object() = Nothing
                    Using registration = child.Token.Register(Sub()
                                                                 SelectResourceInstanceForInputChecks("Fabric 实例") : SelectResourceWorldForInputChecks()
                                                                 selectedWorld = ResourceTargetWorld.SelectedItem
                                                                 worlds = ResourceTargetWorld.Items.Cast(Of Object)().ToArray()
                                                             End Sub)
                        ResourceTargetInstanceChanged(Nothing, Nothing)
                    End Using
                    SettleResourceWorldsForInputChecks() : layout()
                    check(_resourceTargetRequest Is parent AndAlso Not parent.IsCancellationRequested, "nested instance selection retains its live picker parent" & label)
                    check(DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance).Name = "Fabric 实例",
                        "the newest instance selection remains visible after cancellation reentry" & label)
                    check(ResourceTargetWorld.SelectedItem Is selectedWorld AndAlso selectedWorld IsNot Nothing AndAlso ResourceTargetWorld.Items.Cast(Of Object)().SequenceEqual(worlds) AndAlso OnlinePackConfirm.IsEnabled,
                        "an obsolete instance event cannot clear the world selected by its nested replacement" & label)
                    check(_resourceWorldRequest Is Nothing AndAlso Not _resourceWorldLoading AndAlso calls = 0,
                        "a retired instance event leaves no duplicate world reader or catalogue request" & label)
                    child.Dispose() : Await close()
                    Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    check(Not _busy AndAlso _taskGeneration = generation AndAlso finalFiles.Count = files.Count AndAlso
                        files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                        "refresh and selection reentry preserve launcher game and world bytes without starting installation" & label)
                    _motionPreview = False
                Next
            Next
        Next
    End Function
End Class
