Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourcePromptLifecycleUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckResourcePromptLifecycleAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Resource prompt lifecycle checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-prompt-lifecycle.json"), observations.ToString())
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

    Private Async Function CheckResourcePromptLifecycleAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareManagementFixture(directory)
                    FolderList.SelectedItem = folders(launcher)
                    ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
                    Dim target = CurrentInstallTarget(), instances = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), generation = _taskGeneration
                    _managementProvider = Function(binding, token) Task.FromResult(instances)
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout() : _motionPreview = animated
                    Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated
                    Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.Shader, .Name = "归属检查", .FileName = "original.zip",
                        .GameVersions = {"1.20.1"}, .Size = 100, .Sha1 = New String("a"c, 40)}
                    Dim later = release.Copy() : later.Name = "后续选择" : later.FileName = "later.zip"

                    Dim namePrompt = PromptOnlinePackAsync("输入版本名称", "命名状态保留检查。", target.Root, "fresh-name-" & Guid.NewGuid().ToString("N"), False, CancellationToken.None)
                    layout()
                    Dim nameOwner = _onlinePrompt
                    Dim rejected = PromptResourceTargetAsync(release, target, ChangeFolderButton)
                    check(rejected.IsFaulted AndAlso TypeOf rejected.Exception.GetBaseException() Is InvalidOperationException,
                        "an existing name prompt rejects another picker" & label)
                    ValidateOnlinePackName()
                    File.WriteAllText(Path.Combine(directory, "resource-name-state-" & launcher & "-" & dark & "-" & animated & ".json"), New JObject From {
                        {"root", target.Root}, {"sameOwner", _onlinePrompt Is nameOwner}, {"resourceFlag", _resourceTargetPrompt},
                        {"inputVisible", OnlinePackName.IsVisible}, {"confirmEnabled", OnlinePackConfirm.IsEnabled}, {"problem", OnlinePackName.ValidationMessage}}.ToString())
                    check(_onlinePrompt Is nameOwner AndAlso Not _resourceTargetPrompt AndAlso OnlinePackName.IsVisible AndAlso OnlinePackConfirm.IsEnabled,
                        "a rejected picker cannot mutate an existing name prompt or its validation" & label)
                    CompleteOnlinePrompt("no") : Await namePrompt : StopOnlinePromptMotion() : HideOnlinePrompt()

                    Dim originalPrompt = PromptResourceTargetAsync(release, target, ChangeFolderButton), originalOwner = _onlinePrompt, parent = _resourceTargetRequest
                    Dim choices = ResourceTargetInstance.Items.Cast(Of Object)().ToArray(), location = ResourceTargetLocation.Text
                    rejected = PromptResourceTargetAsync(later, target, ChangeFolderButton)
                    check(rejected.IsFaulted AndAlso TypeOf rejected.Exception.GetBaseException() Is InvalidOperationException,
                        "an existing resource picker rejects another picker" & label)
                    check(_onlinePrompt Is originalOwner AndAlso _resourceTargetRequest Is parent AndAlso _resourceTargetRelease.FileName = release.FileName AndAlso
                        ResourceTargetInstance.Items.Cast(Of Object)().SequenceEqual(choices) AndAlso ResourceTargetLocation.Text = location,
                        "rejecting a second picker preserves the original request choices and resource" & label)
                    CompleteOnlinePrompt("no") : originalOwner.TrySetResult("no") : Await originalPrompt
                    StopOnlinePromptMotion() : HideOnlinePrompt()

                    originalPrompt = PromptResourceTargetAsync(release, target, ChangeFolderButton) : originalOwner = _onlinePrompt : parent = _resourceTargetRequest
                    Dim parentToken = parent.Token
                    Dim child = CancellationTokenSource.CreateLinkedTokenSource(parentToken)
                    _resourceWorldRequest = child : _resourceWorldLoading = True
                    Dim detached = False, parentDetached = False, worldDetached = False
                    Dim replacement As Task(Of ResourceInstallationPlan) = Nothing, replacementOwner As TaskCompletionSource(Of String) = Nothing
                    Dim replacementParent As CancellationTokenSource = Nothing, retired As Media.Animation.ClockGroup = Nothing, replacementClock As Media.Animation.ClockGroup = Nothing
                    Using registration = child.Token.Register(Sub()
                                                                 detached = _onlinePrompt Is Nothing
                                                                 parentDetached = _resourceTargetRequest IsNot parent
                                                                 worldDetached = _resourceWorldRequest Is Nothing AndAlso Not _resourceWorldLoading
                                                                 retired = _onlinePromptMotion
                                                                 replacement = PromptResourceTargetAsync(later, target, ChangeFolderButton)
                                                                 replacementOwner = _onlinePrompt : replacementParent = _resourceTargetRequest
                                                                 replacementClock = _onlinePromptMotion
                                                                 If replacementClock IsNot Nothing Then PauseSelectionClock(replacementClock)
                                                             End Sub)
                        CompleteOnlinePrompt("no")
                    End Using
                    check(detached AndAlso parentDetached AndAlso worldDetached, "resource close retires its prompt parent and world before cancellation callbacks" & label)
                    check(parentToken.IsCancellationRequested AndAlso child.IsCancellationRequested, "resource close cancels the captured old requests" & label)
                    check(replacement IsNot Nothing AndAlso Not replacement.IsFaulted AndAlso replacementOwner IsNot originalOwner AndAlso
                        _onlinePrompt Is replacementOwner AndAlso _resourceTargetPrompt AndAlso Not replacementParent.IsCancellationRequested,
                        "a cancellation callback may open a live replacement without the old close cancelling it" & label)
                    originalOwner.TrySetResult("no") : Await originalPrompt
                    If retired IsNot Nothing Then retired.Controller.SkipToFill()
                    layout()
                    check(replacement IsNot Nothing AndAlso Not replacement.IsCompleted AndAlso _onlinePrompt Is replacementOwner AndAlso _onlinePromptMotion Is replacementClock AndAlso
                        PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso _resourceTargetRelease.FileName = later.FileName AndAlso OnlinePackConfirm.IsEnabled,
                        "retired resource exit and finally cannot close disable or complete a replacement picker" & label)
                    If replacement IsNot Nothing AndAlso Not replacement.IsFaulted Then
                        CompleteOnlinePrompt("no") : replacementOwner.TrySetResult("no") : Await replacement
                    ElseIf replacement IsNot Nothing Then
                        Dim observed = replacement.Exception
                    End If
                    child.Dispose() : StopOnlinePromptMotion() : HideOnlinePrompt() : _resourceTargetPrompt = False

                    originalPrompt = PromptResourceTargetAsync(release, target, ChangeFolderButton) : originalOwner = _onlinePrompt : parent = _resourceTargetRequest
                    parentToken = parent.Token : child = CancellationTokenSource.CreateLinkedTokenSource(parentToken)
                    _resourceWorldRequest = child : _resourceWorldLoading = True
                    Dim cancellationFault = False
                    Using registration = child.Token.Register(Sub() Throw New InvalidOperationException("Controlled cancellation notification failure"))
                        Try
                            CompleteOnlinePrompt("no")
                        Catch ex As AggregateException
                            cancellationFault = True
                        End Try
                    End Using
                    check(cancellationFault AndAlso parentToken.IsCancellationRequested AndAlso _resourceTargetRequest Is Nothing AndAlso _resourceWorldRequest Is Nothing,
                        "a failed child cancellation notification still retires and cancels its parent" & label)
                    check(originalOwner.Task.IsCompleted AndAlso _onlinePrompt Is Nothing,
                        "a failed cancellation notification still completes only the old prompt result" & label)
                    originalOwner.TrySetResult("no") : Await originalPrompt
                    CompleteOnlinePrompt("no") : child.Dispose() : StopOnlinePromptMotion() : HideOnlinePrompt()

                    Dim held As New HeldDependencyContext, context = SynchronizationContext.Current
                    SynchronizationContext.SetSynchronizationContext(held)
                    Try
                        originalPrompt = PromptResourceTargetAsync(release, target, ChangeFolderButton)
                    Finally
                        SynchronizationContext.SetSynchronizationContext(context)
                    End Try
                    Dim acceptedExit = _onlinePromptClosed.Task
                    ConfirmResourceTargetAsync()
                    Await WaitPackUiAsync(Function() held.Count > 0)
                    If _onlinePromptMotion IsNot Nothing Then _onlinePromptMotion.Controller.SkipToFill()
                    Await WaitPackUiAsync(Function() acceptedExit.IsCompleted)
                    replacement = PromptResourceTargetAsync(later, target, ChangeFolderButton)
                    ConfirmResourceTargetAsync()
                    Dim secondPlan = Await replacement
                    held.Drain() : Dim firstPlan = Await originalPrompt
                    check(firstPlan IsNot Nothing AndAlso firstPlan.Release.FileName = release.FileName AndAlso firstPlan.Destination.EndsWith("shaderpacks" & Path.DirectorySeparatorChar & release.FileName, StringComparison.Ordinal),
                        "a delayed accepted picker returns its own plan rather than a later confirmation" & label)
                    check(secondPlan IsNot Nothing AndAlso secondPlan.Release.FileName = later.FileName AndAlso SameManagementTarget(secondPlan.LauncherTarget, target),
                        "a later accepted picker retains its independently selected native target" & label)
                    check(_onlinePrompt Is Nothing AndAlso _resourceTargetRequest Is Nothing, "old resource finally cannot revive or retain a completed request" & label)
                    StopOnlinePromptMotion() : HideOnlinePrompt()
                    Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    check(Not _busy AndAlso _taskGeneration = generation AndAlso finalFiles.Count = files.Count AndAlso
                        files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                        "resource prompt replacement and delayed confirmation preserve all game launcher and save bytes without downloads" & label)
                    _motionPreview = False
                Next
            Next
        Next
    End Function
End Class
