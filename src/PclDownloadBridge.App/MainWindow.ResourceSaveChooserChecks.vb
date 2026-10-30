Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceSaveChooserUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host), observations As New JObject
        Dim focus = LocalModCheckGetNativeFocus()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 960, 740)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceSaveChooserAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Resource Save As chooser checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-save-chooser.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
            LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceSaveChooserAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        _packCatalogFactory = Function() New ModpackCatalogClient(New CommunityFixtureHandler(), "", 0)
        Dim kinds = {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
        For index = 0 To kinds.Length - 1
            Dim kind = kinds(index)
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(Path.Combine(directory, "chooser-" & kind.ToString() & "-" & dark))
                FolderList.SelectedItem = folders(index Mod 3)
                Dim protectedRoot = Path.GetDirectoryName(folders(0).Location), files = DirectoryFilesForModUpdateChecks(protectedRoot)
                For scenario = 0 To 4
                    Dim chosen = scenario
                    _motionPreview = False : CancelResourceRequests() : _resourceBrowsers.Clear()
                    ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                    Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
                    Dim project As New ModpackProject With {.Kind = kind, .Platform = ModpackPlatform.Modrinth, .Id = "chooser-owner", .Name = "Save As owner"}
                    PrepareResourceBackDetail(project) : settle()
                    Dim row = DirectCast(DirectCast(PackVersionCards.Children.OfType(Of PclCard)().First().Body, StackPanel).Children.OfType(Of PclListItem)().First(), PclListItem)
                    Dim release = DirectCast(row.Tag, ModpackRelease)
                    release.FileName = If(kind = CommunityResourceKind.Mod, "chosen.jar", "chosen.zip")
                    Dim destination = Path.Combine(protectedRoot, "chooser-" & Guid.NewGuid().ToString("N") & Path.GetExtension(release.FileName))
                    Dim calls = 0, starts = 0, generation = _taskGeneration, savedRelease As ModpackRelease = Nothing, savedPath As String = Nothing
                    Dim expected As FrameworkElement = DownloadPage, clock As Media.Animation.ClockGroup = Nothing
                    _resourceSaveRunner = Function(value, path, progress, token)
                                              starts += 1 : savedRelease = value : savedPath = path
                                              Return Task.CompletedTask
                                          End Function
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = dark : _surfaceMotion.PauseForChecks = dark
                    _resourceSaveChooser = Function(value)
                                               calls += 1
                                               If chosen >= 1 AndAlso chosen <= 3 Then
                                                   ShowSettings(Nothing, Nothing)
                                                   If chosen >= 2 Then
                                                       settle() : ShowDownload(Nothing, Nothing) : settle()
                                                   End If
                                               End If
                                               expected = ActiveSurface() : clock = _surfaceMotion.Transition
                                               If chosen = 0 OrElse chosen = 1 OrElse chosen = 3 Then Throw New IOException("controlled current chooser failure")
                                               Return destination
                                           End Function
                    row.Activate() : Await Task.Yield() : layout()
                    Dim label = "/kind=" & kind.ToString() & "/dark-motion=" & dark & "/case=" & scenario
                    If scenario = 4 Then
                        check(calls = 1 AndAlso starts = 1 AndAlso savedRelease.Id = release.Id AndAlso savedRelease.ProjectId = project.Id AndAlso savedRelease.Kind = kind AndAlso
                            savedPath = destination AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _taskGeneration = generation + 1 AndAlso Not _busy,
                            "an owned Save As choice still starts the exact selected resource and destination" & label)
                        check(Not _choosingOnline AndAlso _downloadView = DownloadView.Progress AndAlso ActiveSurface() Is DownloadPage AndAlso Not File.Exists(destination),
                            "controlled successful Save As releases the chooser gate and reaches the ordinary task view without file writes" & label)
                    Else
                        check(calls = 1 AndAlso starts = 0 AndAlso Not _busy AndAlso _taskGeneration = generation AndAlso
                            If(scenario = 0, PackProjectActionStatus.Text.Contains("controlled current chooser failure") AndAlso PackProjectActionStatus.Visibility = Visibility.Visible,
                               PackProjectActionStatus.Text = "" AndAlso PackProjectActionStatus.Visibility = Visibility.Collapsed),
                            "retired Save As callbacks cannot start a task or publish feedback while current failures remain visible" & label)
                        Dim preserved = ActiveSurface() Is expected AndAlso _surfaceMotion.Transition Is clock
                        settle()
                        check(preserved AndAlso Not _choosingOnline AndAlso PageSurface.IsHitTestVisible AndAlso expected.Opacity = 1 AndAlso
                            _packProject Is project AndAlso _downloadView = DownloadView.Modpacks AndAlso Not File.Exists(destination),
                            "Save As retirement preserves the accepted page and animation and leaves input ready for retry" & label)
                    End If
                    If _taskGeneration <> generation Then Await ResumeResourceTaskForChecksAsync()
                    settle()
                Next
                Dim after = DirectoryFilesForModUpdateChecks(protectedRoot)
                check(after.Count = files.Count AndAlso files.All(Function(item) after.ContainsKey(item.Key) AndAlso after(item.Key).SequenceEqual(item.Value)),
                    "Save As current and retired choices preserve every native configuration and game file/kind=" & kind.ToString() & "/dark-motion=" & dark)
            Next
        Next
    End Function
End Class
