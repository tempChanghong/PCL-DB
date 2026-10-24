Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackProjectActionsAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim saved = _packProjectAction, savedWait = _packProjectActionWait, savedVerified = _packVerifiedProject, savedAppearance = _appearance.ToJson()
        Dim owner = _packProject, session = _packSearchSession, generation = _taskGeneration, cards = PackVersionCards.Children.Cast(Of Object)().ToArray()
        Dim previousMotion = _motionPreview, detailPaused = _packDetailMotion.PauseForChecks, surfacePaused = _surfaceMotion.PauseForChecks
        Dim gates As New List(Of TaskCompletionSource(Of Boolean)), calls = 0, waits = 0, added = 0
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label) : added += 1
                                                  End Sub
        Dim occupied As Action(Of String, String) = Sub(kind, value)
                                                       calls += 1
                                                       Throw New ExternalException("fixture clipboard is occupied", &H800401D0)
                                                   End Sub
        Dim hold As Func(Of TaskCompletionSource(Of Boolean)) = Function()
                                                                   Dim gate = PackCheckGate() : gates.Add(gate)
                                                                   _packProjectActionWait = Function() gate.Task
                                                                   Return gate
                                                               End Function
        Try
            _packVerifiedProject = New ModpackProject With {.Id = owner.Id, .Platform = owner.Platform, .Name = "已核对的项目名称", .Website = "https://modrinth.com/modpack/verified-project",
                .Summary = "用于验证项目操作的已接受元数据。", .GameVersions = {"1.20.1"}, .Updated = DateTimeOffset.UtcNow.AddDays(-2)}
            RenderPackProject(ActivePackProject()) : layout()
            Await VerifyProjectActionHandoffAsync(directory, layout, verify)
            _packProjectActionWait = Function()
                                         waits += 1 : Return Task.CompletedTask
                                     End Function
            _packProjectAction = occupied
            Dim escaped = False
            Try
                Await DispatchPackProjectActionAsync("copy")
            Catch
                escaped = True
            End Try
            verify(Not escaped, "an occupied clipboard is contained by the project copy action instead of escaping to the dispatcher")
            verify(calls = 4 AndAlso waits = 3, "clipboard contention has exactly four attempts and three asynchronous retry intervals like PCL")
            verify(PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.Text.Contains("复制失败") AndAlso Not PackProjectActionStatus.Text.Contains("成功"),
                "clipboard exhaustion gives a local failure message without claiming success")
            SaveControlVisual(host, Path.Combine(directory, "project-copy-failed.png"))

            calls = 0 : Dim copied As String = Nothing
            _packProjectAction = Sub(kind, value)
                                     calls += 1
                                     If calls < 3 Then Throw New ExternalException("transient clipboard contention")
                                     copied = value
                                 End Sub
            Await DispatchPackProjectActionAsync("copy")
            verify(calls = 3 AndAlso copied = "已核对的项目名称", "transient clipboard contention retries the accepted displayed project name rather than stale search metadata")
            verify(PackProjectActionStatus.Text = "已成功复制！", "a successful retry replaces the previous copy failure feedback")
            verify(_packSearchSession Is session AndAlso _packProject Is owner AndAlso _taskGeneration = generation AndAlso PackVersionCards.Children.Cast(Of Object)().SequenceEqual(cards),
                "external project actions preserve search ownership version cards and installation generation")

            calls = 0 : _packProjectAction = occupied
            Dim gate = hold(), pending = DispatchPackProjectActionAsync("copy")
            verify(Not pending.IsCompleted AndAlso calls = 1, "clipboard retry yields the UI dispatcher while the asynchronous interval is pending")
            _packProjectAction = Sub(kind, value)
                                     calls += 1 : copied = value
                                 End Sub
            gate.TrySetResult(True) : Await pending
            verify(calls = 2 AndAlso copied = "已核对的项目名称" AndAlso PackProjectActionStatus.Text = "已成功复制！", "a deferred clipboard retry completes without blocking other page work")

            Dim actions As New List(Of String)
            _packProjectAction = Sub(kind, value) actions.Add(kind & ":" & value)
            Await DispatchPackProjectActionAsync("link")
            verify(actions.SequenceEqual({"link:https://modrinth.com/modpack/verified-project"}) AndAlso PackProjectActionStatus.Text.Contains("默认浏览器"),
                "normal website actions use the accepted project URL and do not copy it unnecessarily")
            actions.Clear()
            _packProjectAction = Sub(kind, value)
                                     actions.Add(kind & ":" & value)
                                     If kind = "link" Then Throw New ComponentModel.Win32Exception("fixture has no default browser")
                                 End Sub
            Await DispatchPackProjectActionAsync("link")
            verify(actions.SequenceEqual({"link:https://modrinth.com/modpack/verified-project", "copy:https://modrinth.com/modpack/verified-project"}) AndAlso PackProjectActionStatus.Text.Contains("网址已复制"),
                "browser failure falls back to copying the same URL and reports what actually succeeded")
            calls = 0 : waits = 0 : _packProjectAction = occupied
            _packProjectActionWait = Function()
                                         waits += 1 : Return Task.CompletedTask
                                     End Function
            Await DispatchPackProjectActionAsync("link")
            verify(calls = 5 AndAlso waits = 3 AndAlso PackProjectActionStatus.Text.Contains("复制也失败") AndAlso PackProjectActionStatus.Text.Contains("https://modrinth.com/modpack/verified-project"),
                "browser and clipboard failures are bounded and expose a manual URL without a false copied claim")

            For Each state In {"gated", "disabled", "prompt"}
                calls = 0
                If state = "gated" Then PackProjectIntro.IsHitTestVisible = False
                If state = "disabled" Then PackProjectIntro.IsEnabled = False
                If state = "prompt" Then _choosingOnline = True
                Await DispatchPackProjectActionAsync("copy")
                verify(calls = 0, "project copy cannot run from inactive content: " & state)
                PackProjectIntro.IsHitTestVisible = True : PackProjectIntro.IsEnabled = True : _choosingOnline = False
            Next

            calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            _packProjectAction = Sub(kind, value) copied = value
            Await DispatchPackProjectActionAsync("link")
            Dim newerFeedback = PackProjectActionStatus.Text
            gate.TrySetResult(True) : Await pending
            verify(calls = 1 AndAlso PackProjectActionStatus.Text = newerFeedback, "a newer project action supersedes delayed retries and keeps its own feedback")

            _packProjectAction = occupied : calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            ChangePackMode(PackBrowserMode.Search) : layout()
            ChangePackMode(PackBrowserMode.Detail) : layout()
            gate.TrySetResult(True) : Await pending
            verify(calls = 1 AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed, "leaving and reopening the same project cannot resurrect a retired clipboard retry")

            calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            RenderPackProject(ActivePackProject())
            gate.TrySetResult(True) : Await pending
            verify(calls = 1 AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed, "replacing displayed project metadata retires previous action feedback and retries")
            calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            _closing = True : gate.TrySetResult(True) : Await pending : _closing = False
            verify(calls = 1 AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed, "closing prevents pending clipboard work from changing external data or status")

            calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            _packProject = New ModpackProject With {.Platform = owner.Platform, .Id = "different-action-owner", .Name = "另一个项目"}
            gate.TrySetResult(True) : Await pending
            verify(calls = 1 AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed, "a different accepted project cannot inherit the old project's retry or success message")
            _packProject = owner

            calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            _motionPreview = True : _packDetailMotion.PauseForChecks = True
            ChangePackContent(PackBrowserMode.Detail, Sub()
                                                    End Sub)
            Dim entry = _packDetailMotion.Transition
            PauseSelectionClock(entry) : entry.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(175), TimeSeekOrigin.BeginTime) : layout()
            verify(Not pending.IsCompleted AndAlso calls = 1 AndAlso entry Is _packDetailMotion.Transition, "details-card entry can advance while clipboard retry is awaiting its interval")
            _packProjectAction = Sub(kind, value) copied = value
            gate.TrySetResult(True) : Await pending : layout()
            verify(_packDetailMotion.Transition Is entry AndAlso PackProjectActionStatus.IsVisible, "clipboard completion leaves the actual details-card entry clock owned by its page")
            SaveControlVisual(host, Path.Combine(directory, "project-action-entry-175.png"))
            _packDetailMotion.Finish()

            _packProjectAction = occupied : calls = 0 : gate = hold() : pending = DispatchPackProjectActionAsync("copy")
            _surfaceMotion.PauseForChecks = True
            SwitchMainPage(True) : _surfaceMotion.Finish() : SwitchMainPage(False) : _surfaceMotion.Finish() : layout()
            gate.TrySetResult(True) : Await pending
            verify(calls = 1 AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed, "a root settings round trip retires the old clipboard operation even when the detail project is cached")
            _motionPreview = previousMotion

            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                _packProjectAction = Sub(kind, value) copied = value
                Await DispatchPackProjectActionAsync("copy") : layout()
                verify(PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.ActualWidth > 0 AndAlso PackProjectActionStatus.TextWrapping = TextWrapping.Wrap,
                    "project action feedback remains readable in dark mode " & dark)
                SaveControlVisual(host, Path.Combine(directory, "project-copy-" & If(dark, "dark", "blue") & ".png"))
            Next
        Finally
            RetirePackProjectAction()
            _packDetailMotion.Finish() : _surfaceMotion.Finish()
            _motionPreview = previousMotion : _packDetailMotion.PauseForChecks = detailPaused : _surfaceMotion.PauseForChecks = surfacePaused
            For Each gate In gates
                gate.TrySetResult(True)
            Next
            _closing = False : _choosingOnline = False
            PackProjectIntro.IsHitTestVisible = True : PackProjectIntro.IsEnabled = True
            _packProjectAction = saved : _packProjectActionWait = savedWait : _packVerifiedProject = savedVerified
            RenderPackProject(ActivePackProject()) : RestoreAppearance(savedAppearance) : layout()
        End Try
        File.WriteAllText(Path.Combine(directory, "project-action-count.json"), New JObject From {{"checks", added}, {"physicalClipboardChanged", False}, {"browserOpened", False}}.ToString())
    End Function
End Class
