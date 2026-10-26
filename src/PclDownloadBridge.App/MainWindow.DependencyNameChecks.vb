Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyDependencyNamesUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckDependencyNamesAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Dependency names did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "dependency-names.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckDependencyNamesAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim requests As New JArray
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
            For Each dark In {False, True}
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}})
                ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : settle()
                Dim kind = If(dark, CommunityResourceKind.DataPack, CommunityResourceKind.Mod)
                Dim project As New ModpackProject With {.Platform = platform, .Kind = kind, .Id = If(platform = ModpackPlatform.Modrinth, "parent", "100"), .Name = "前置名称检查"}
                _resourceKind = kind : _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                ChangePackMode(PackBrowserMode.Detail) : RenderPackProject(project) : settle()
                Dim reset As Action = Sub()
                                          ResetResourceNames() : _resourceDependencyNames.Clear() : PackVersionCards.Children.Clear() : layout()
                                      End Sub
                Dim state As New DependencyNameState With {.Kind = kind, .Name = "共享前置 · " & platform.ToString()}
                _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNameHandler(state), "", 0)
                Dim fixedVersion As String = Nothing
                Dim add As Func(Of String, PclButton) =
                    Function(id)
                        Dim rows As New StackPanel, release As New ModpackRelease With {.Dependencies = {New ResourceDependency With {.ProjectId = id, .VersionId = fixedVersion}}}
                        AddResourceDependencies(rows, New List(Of ModpackRelease) From {release})
                        PackVersionCards.Children.Add(New PclCard With {.Title = "版本分组 " & PackVersionCards.Children.Count, .Body = rows})
                        layout()
                        Return rows.Children.OfType(Of WrapPanel)().Single().Children.OfType(Of PclButton)().Single()
                    End Function
                Dim key = platform.ToString() & ":123", label = "/platform=" & platform.ToString() & "/animated-dark=" & dark
                reset()
                Dim links = Enumerable.Range(1, 4).Select(Function(index)
                                                            fixedVersion = (9000 + index).ToString() : Return add("123")
                                                        End Function).ToArray()
                fixedVersion = Nothing
                Await WaitPackUiAsync(Function() state.Calls >= 1)
                Dim sharedQuery = state.Calls = 1
                state.Gate.SetResult(True)
                Await WaitPackUiAsync(Function() links.All(Function(button) button.Text = state.Name)) : settle()
                requests.Add(New JObject From {{"Platform", platform.ToString()}, {"AnimatedDark", dark}, {"Total", state.Calls}, {"Versions", state.VersionCalls}, {"Projects", state.Calls - state.VersionCalls}})
                check(sharedQuery AndAlso state.Calls = 1 AndAlso state.VersionCalls = 0 AndAlso _resourceNameSlots.CurrentCount = 4 AndAlso links.All(Function(button) button.IsEnabled AndAlso button.Tag IsNot Nothing) AndAlso
                    links.Select(Function(button) DirectCast(button.Tag, ResourceDependency).VersionId).SequenceEqual({"9001", "9002", "9003", "9004"}),
                    "repeated dependency labels across version groups share one pending project query and keep every link usable" & label)
                Dim before = state.Calls, cached = add("123")
                check(cached.Text = state.Name AndAlso state.Calls = before AndAlso _resourceDependencyNames(key) = state.Name,
                    "completed dependency names reuse the accepted platform cache without another query" & label)
                SaveControlVisual(host, Path.Combine(directory, "dependency-names-" & platform.ToString() & If(dark, "-dark.png", "-light.png")))

                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "恢复的前置", .Fail = True}
                state.Gate.SetResult(True)
                Dim failedLink = add("123")
                Await WaitPackUiAsync(Function() state.Calls > 0 AndAlso _resourceNameSlots.CurrentCount = 4)
                Dim failureRetained = failedLink.Text = "查看前置" AndAlso Not _resourceDependencyNames.ContainsKey(key)
                state = New DependencyNameState With {.Kind = kind, .Name = "恢复的前置"} : state.Gate.SetResult(True)
                LoadDependencyNameAsync(project, DirectCast(failedLink.Tag, ResourceDependency), failedLink, _resourceNamesRequest.Token)
                Await WaitPackUiAsync(Function() failedLink.Text = state.Name)
                check(failureRetained AndAlso state.Calls = 1 AndAlso _resourceNameSlots.CurrentCount = 4,
                    "a failed optional name is not cached and the same link can retry successfully without losing a slot" & label)

                ' Complete the real Core lookup, but hold its dispatcher continuation
                ' until cancellation. This reproduces the post-response cache race.
                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "已退役的前置"}
                Dim held As New HeldDependencyContext, context = SynchronizationContext.Current, retiredLink As PclButton
                Try
                    SynchronizationContext.SetSynchronizationContext(held) : retiredLink = add("123")
                Finally
                    SynchronizationContext.SetSynchronizationContext(context)
                End Try
                state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() held.Count > 0)
                ResetResourceNames() : held.Drain() : Await Task.Yield() : held.Drain()
                Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 4)
                Dim rejected = Not _resourceDependencyNames.ContainsKey(key) AndAlso retiredLink.Text = "查看前置"
                state = New DependencyNameState With {.Kind = kind, .Name = "当前前置"} : state.Gate.SetResult(True)
                Dim fresh = add("123") : Await WaitPackUiAsync(Function() fresh.Text <> "查看前置")
                check(rejected AndAlso fresh.Text = state.Name AndAlso state.Calls = 1 AndAlso _resourceDependencyNames(key) = state.Name,
                    "cancellation after a real response prevents its queued continuation from poisoning a new name cache" & label)

                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "旧等待"}
                Dim oldState = state, oldLink = add("123")
                ResetResourceNames()
                state = New DependencyNameState With {.Kind = kind, .Name = "新等待"}
                Dim newer = add("123")
                oldState.Gate.SetResult(True) : Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 3)
                Dim joined = add("123"), joinedOnce = state.Calls = 1
                state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() newer.Text <> "查看前置" AndAlso joined.Text <> "查看前置")
                check(joinedOnce AndAlso state.Calls = 1 AndAlso newer.Text = state.Name AndAlso joined.Text = state.Name AndAlso oldLink.Text = "查看前置" AndAlso _resourceNameSlots.CurrentCount = 4,
                    "cleanup from an older scope cannot remove the newer shared name query or duplicate its subscribers" & label)

                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "隐藏旧名称"}
                held = New HeldDependencyContext
                Try
                    SynchronizationContext.SetSynchronizationContext(held) : retiredLink = add("123")
                Finally
                    SynchronizationContext.SetSynchronizationContext(context)
                End Try
                state.Gate.SetResult(True) : Await WaitPackUiAsync(Function() held.Count > 0)
                ShowSettings(Nothing, Nothing) : settle() : held.Drain() : Await Task.Yield() : held.Drain()
                Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 4)
                rejected = Not _resourceDependencyNames.ContainsKey(key) AndAlso retiredLink.Text = "查看前置" AndAlso SettingsPage.IsVisible
                state = New DependencyNameState With {.Kind = kind, .Name = "返回后的前置"} : state.Gate.SetResult(True)
                ShowDownload(Nothing, Nothing) : settle() : ResetResourceNames()
                fresh = add("123") : Await WaitPackUiAsync(Function() fresh.Text <> "查看前置")
                check(rejected AndAlso fresh.Text = state.Name AndAlso state.Calls = 1 AndAlso PackDetailSurface.IsVisible,
                    "root-page departure retires queued names and returning can resolve the current label without reviving hidden content" & label)

                ' Seed this accepted value so a baseline failure above cannot mask
                ' the independent platform-key check.
                _resourceDependencyNames(key) = "返回后的前置"
                Dim other = If(platform = ModpackPlatform.Modrinth, ModpackPlatform.CurseForge, ModpackPlatform.Modrinth)
                Dim otherProject As New ModpackProject With {.Platform = other, .Kind = kind, .Id = If(other = ModpackPlatform.Modrinth, "parent", "100"), .Name = "另一平台"}
                _packProject = otherProject : _packVerifiedProject = otherProject : ResetResourceNames()
                state = New DependencyNameState With {.Kind = kind, .Name = "另一平台前置"} : state.Gate.SetResult(True)
                Dim otherLink = add("123") : Await WaitPackUiAsync(Function() otherLink.Text <> "查看前置")
                check(otherLink.Text = state.Name AndAlso _resourceDependencyNames(other.ToString() & ":123") = state.Name AndAlso _resourceDependencyNames(key) = "返回后的前置",
                    "equal dependency IDs on CurseForge and Modrinth keep separate accepted names" & label)
                _packProject = project : _packVerifiedProject = project

                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "仅版本 ID 前置"}
                fixedVersion = "9001"
                Dim versionLinks = Enumerable.Range(1, 4).Select(Function(index) add(Nothing)).ToArray()
                fixedVersion = Nothing : state.Gate.SetResult(True)
                If platform = ModpackPlatform.Modrinth Then
                    Await WaitPackUiAsync(Function() versionLinks.All(Function(button) button.Text = state.Name))
                    check(state.Calls = 2 AndAlso state.VersionCalls = 1 AndAlso _resourceDependencyNames(platform.ToString() & ":version:9001") = state.Name AndAlso
                        versionLinks.All(Function(button) DirectCast(button.Tag, ResourceDependency).ProjectId Is Nothing AndAlso DirectCast(button.Tag, ResourceDependency).VersionId = "9001"),
                        "version-only references retain their identity and use the validated version-to-project lookup with safe fallback" & label)
                Else
                    Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 4)
                    check(state.Calls = 0 AndAlso _resourceDependencyNames.Count = 0 AndAlso versionLinks.All(Function(button) button.Text = "查看前置" AndAlso button.IsEnabled),
                        "version-only references retain their identity and use the validated version-to-project lookup with safe fallback" & label)
                End If

                reset() : state = New DependencyNameState With {.Kind = kind, .Name = "有界查询"}
                Dim pending = Enumerable.Range(200, 6).Select(Function(id) add(id.ToString())).ToArray()
                Await WaitPackUiAsync(Function() state.Calls >= 4)
                Dim bounded = state.Calls = 4 AndAlso _resourceNameSlots.CurrentCount = 0
                ResetResourceNames() : state.Gate.SetResult(True)
                Await WaitPackUiAsync(Function() _resourceNameSlots.CurrentCount = 4)
                check(bounded AndAlso state.Calls = 4 AndAlso pending.All(Function(button) button.Text = "查看前置") AndAlso _resourceDependencyNames.Count = 0 AndAlso state.NonProjectCalls = 0 AndAlso state.VersionCalls = 0,
                    "different project names remain bounded to four queries and cancellation drains waiters without version or download requests" & label)
                reset() : settle()
            Next
        Next
        File.WriteAllText(Path.Combine(directory, "dependency-name-requests.json"), requests.ToString())
    End Function

    Private Class DependencyNameState
        Friend Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Kind As CommunityResourceKind, Name As String, Fail As Boolean, Calls As Integer, NonProjectCalls As Integer, VersionCalls As Integer
    End Class
    Private Class DependencyNameHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As DependencyNameState
        Friend Sub New(state As DependencyNameState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Interlocked.Increment(_state.Calls)
            Await _state.Gate.Task.ConfigureAwait(False)
            If _state.Fail Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("controlled optional name failure")}
            Dim address = request.RequestUri.AbsolutePath, id = address.Split("/"c).Last(), data As JObject
            If address.EndsWith("/version", StringComparison.Ordinal) OrElse address.Contains("/files") Then
                Interlocked.Increment(_state.NonProjectCalls) : Throw New IOException("Unexpected dependency list/file endpoint: " & address)
            ElseIf address.Contains("/version/") Then
                Interlocked.Increment(_state.VersionCalls)
                data = New JObject From {{"id", id}, {"project_id", "123"}, {"loaders", New JArray(If(_state.Kind = CommunityResourceKind.DataPack, "datapack", "fabric"))}}
            ElseIf address.Contains("/project/") Then
                data = CommunityProjectFixture(_state.Kind, id) : data("title") = _state.Name
            ElseIf address.Contains("/mods/") Then
                data = New JObject From {{"data", New JObject From {{"id", id}, {"classId", CommunityResources.ClassId(_state.Kind)}, {"name", _state.Name}, {"slug", "fixture"}, {"summary", "dependency name"}, {"downloadCount", 1}, {"dateModified", "2026-10-01T00:00:00Z"}}}}
            Else
                Interlocked.Increment(_state.NonProjectCalls) : Throw New IOException("Unexpected dependency endpoint: " & address)
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
    Private Class HeldDependencyContext
        Inherits SynchronizationContext
        Private ReadOnly _pending As New Queue(Of Tuple(Of SendOrPostCallback, Object))
        Friend ReadOnly Property Count As Integer
            Get
                SyncLock _pending
                    Return _pending.Count
                End SyncLock
            End Get
        End Property
        Public Overrides Sub Post(callback As SendOrPostCallback, state As Object)
            SyncLock _pending
                _pending.Enqueue(Tuple.Create(callback, state))
            End SyncLock
        End Sub
        Friend Sub Drain()
            Do
                Dim callback As Tuple(Of SendOrPostCallback, Object)
                SyncLock _pending
                    If _pending.Count = 0 Then Return
                    callback = _pending.Dequeue()
                End SyncLock
                callback.Item1.Invoke(callback.Item2)
            Loop
        End Sub
    End Class
End Class
