Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModTransferUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim culture = CultureInfo.CurrentCulture, focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModTransferAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Mod transfer checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-mod-transfer.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            CultureInfo.CurrentCulture = culture
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckLocalModTransferAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(Path.Combine(directory, "mod-transfer"), layout)
        Dim protectedFiles = DirectoryFilesForModUpdateChecks(_localModInstance.Target.Root)
        Dim transfers As New JArray
        For Each dark In {False, True}
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(If(dark, "de-DE", "en-US"))
            For Each animated In {False, True}
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : layout()
                For index = 0 To 3
                    _managementMotion.Finish() : _surfaceMotion.Finish() : layout()
                    Dim speed = If(index < 2, 0.5, If(index = 2, 8.0, 0.0)), expected = CLng(speed * 1024 * 1024)
                    Dim entry = _localMods.Single(Function(item) item.FileName = "first.jar")
                    Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "transfer-mod"}
                    Dim current As New ModpackRelease With {.Kind = project.Kind, .Platform = project.Platform, .ProjectId = project.Id, .Id = "old", .FileName = entry.FileName,
                        .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {_localModInstance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = entry.Length, .Sha1 = New String("a"c, 40)}
                    Dim candidate = current.Copy() : candidate.Id = "new" : candidate.Published = current.Published.AddDays(1) : candidate.Sha1 = New String("b"c, 40)
                    Dim update As New LocalModUpdate(entry, project, current, candidate)
                    Dim row = DirectCast(_localModRowViews(entry), PclLocalModItem)
                    _localModUpdates(entry.FilePath) = update : ConfigureLocalModSelection(row, entry)
                    row.Checked = True
                    SpeedLimitBox.Text = speed.ToString(CultureInfo.CurrentCulture) : layout()
                    Dim owner = _localModInstance, generation = _localModOperationGeneration, connections = ConnectionCount(), sourcePolicy = Policy()
                    Dim calls = 0, actual As Long = -1, frozen As Boolean = False, tokenUsed As CancellationToken
                    Dim gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                    _localModUpdateInstaller = Async Function(instance, updates, cache, source, token, count, limit)
                                                  calls += 1 : actual = limit : tokenUsed = token
                                                  frozen = instance Is owner AndAlso updates.Count = 1 AndAlso updates(0) Is update AndAlso count = connections AndAlso
                                                      source.Preference = sourcePolicy.Preference AndAlso source.CommunityPreference.Equals(sourcePolicy.CommunityPreference) AndAlso Not String.IsNullOrWhiteSpace(cache)
                                                  Dim result = Await gate.Task
                                                  token.ThrowIfCancellationRequested() : Return result
                                              End Function
                    Dim label = "/dark=" & dark & "/motion=" & animated & "/case=" & index & "/MBps=" & speed.ToString(CultureInfo.InvariantCulture)
                    UpdateSelectedLocalMods(LocalModUpdateSelected, Nothing) : layout()
                    check(_onlinePrompt IsNot Nothing AndAlso PackNameOverlay.IsVisible AndAlso calls = 0 AndAlso Not _localModWriting,
                        "the update button requires confirmation before passing transfer options" & label)
                    CompleteOnlinePrompt("yes")
                    Dim closing = _onlinePromptMotion
                    If closing IsNot Nothing Then PauseSelectionClock(closing)
                    check(If(animated, closing IsNot Nothing AndAlso calls = 0 AndAlso Not PageSurface.IsEnabled,
                        closing Is Nothing AndAlso Not PackNameOverlay.IsVisible), "Mod transfer honors animated or immediate confirmation exit" & label)
                    closing?.Controller.SkipToFill()
                    Try
                        Await WaitPackUiAsync(Function() calls = 1)
                    Catch ex As TimeoutException
                        File.WriteAllText(Path.Combine(directory, "transfer-timeout.json"), New JObject From {{"case", label}, {"calls", calls},
                            {"prompt", _onlinePrompt IsNot Nothing}, {"overlay", PackNameOverlay.IsVisible}, {"motion", _onlinePromptMotion IsNot Nothing},
                            {"writing", _localModWriting}, {"selection", _localModSelection.Count}, {"generation", _localModGeneration}, {"operation", _localModOperationGeneration},
                            {"input", LocalModInputAllowed()}, {"read", _localModReadSucceeded}, {"status", LocalModStatus.Text}, {"validation", SpeedLimitBox.ValidationMessage}}.ToString())
                        Throw
                    End Try
                    layout()
                    transfers.Add(New JObject From {{"case", label}, {"expectedBytesPerSecond", expected}, {"actualBytesPerSecond", actual}, {"optionsFrozen", frozen}})
                    check(actual = expected AndAlso frozen AndAlso _localModWriting AndAlso _localModWriteRequest IsNot Nothing,
                        "Mod transfer converts MB per second to bytes per second and preserves the accepted instance and options" & label)
                    UpdateSelectedLocalMods(LocalModUpdateSelected, Nothing)
                    Dim duplicateBlocked = calls = 1 AndAlso _onlinePrompt Is Nothing
                    If index = 0 Then CancelLocalModOperation(Nothing, Nothing)
                    gate.SetResult(New LocalModActionResult(Path.Combine(owner.RunDirectory, "mods"), 0, 1))
                    Await WaitPackUiAsync(Function() Not _localModWriting AndAlso Not _localModScanning AndAlso _localModReadSucceeded)
                    _managementMotion.Finish() : layout()
                    check(duplicateBlocked AndAlso tokenUsed.IsCancellationRequested = (index = 0) AndAlso _localModOperationGeneration = generation + 1 AndAlso
                        _localModWriteRequest Is Nothing AndAlso Not PackNameOverlay.IsVisible AndAlso PageSurface.IsEnabled AndAlso LocalModInputAllowed() AndAlso LocalModImport.IsEnabled,
                        "Mod transfer cancellation or completion restores the list and permits a fresh retry" & label)
                Next
                Dim files = DirectoryFilesForModUpdateChecks(_localModInstance.Target.Root)
                check(files.Count = protectedFiles.Count AndAlso protectedFiles.All(Function(item) files.ContainsKey(item.Key) AndAlso files(item.Key).SequenceEqual(item.Value)),
                    "controlled Mod transfers preserve every fixture game and launcher configuration byte/dark=" & dark & "/motion=" & animated)
            Next
        Next
        File.WriteAllText(Path.Combine(directory, "local-mod-transfer-options.json"), transfers.ToString())
    End Function
End Class
