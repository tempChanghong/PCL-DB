Imports System.ComponentModel
Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Sub CompletePreferenceRead(gate As TaskCompletionSource(Of LauncherDirectoryResult), mode As Integer, value As LauncherDirectoryResult)
        Select Case mode
            Case 0 : gate.SetResult(value)
            Case 1 : gate.SetException(New IOException("controlled retired read"))
            Case 2 : gate.SetCanceled()
            Case Else : gate.SetException(New InvalidOperationException("controlled retired failure"))
        End Select
    End Sub

    Private Shared Function AwaitPreferenceRestore(work As Task(Of List(Of String)), ByRef failure As Exception) As List(Of String)
        If Not PumpMotionUntil(Function() work.IsCompleted, 10000) Then Throw New TimeoutException("Preference lifetime fixture did not settle.")
        Try
            Return work.GetAwaiter().GetResult()
        Catch ex As Exception
            failure = ex
            Return Nothing
        End Try
    End Function

    Private Sub VerifyRetiredPreferenceRecovery(check As Action(Of Boolean, String), root As String)
        Dim game = Path.Combine(root, "lifetime-game")
        Directory.CreateDirectory(game)
        For mode = 0 To 3
            Dim reads As New List(Of String), owner = ScalarOwner(root, game, reads)
            Try
                Dim settings = ScalarRecoverySettings(root, game)
                Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                owner._launcherDirectoryLoader = Function(binding)
                                                    reads.Add(binding.Id)
                                                    Return gate.Task
                                                End Function
                Dim restore = owner.RestorePreferenceValuesAsync(settings)
                Dim closing As New CancelEventArgs
                owner.WindowClosing(owner, closing)
                Dim status = owner.StatusText.Text, selected = owner._selectedFolder, baseline = owner._preferenceBaseline
                CompletePreferenceRead(gate, mode, ScalarDirectory(owner._launcherState.Bindings(0), game))
                Dim failure As Exception = Nothing, result = AwaitPreferenceRestore(restore, failure)
                check(owner._closing AndAlso Not closing.Cancel AndAlso failure Is Nothing AndAlso result Is Nothing AndAlso
                    owner._preferenceBaseline Is baseline AndAlso baseline Is Nothing,
                    "closed startup retires without creating a preference baseline: " & mode)
                owner._restoringSettings = False
                check(reads.Count = 1 AndAlso owner._selectedFolder Is selected AndAlso owner.StatusText.Text = status AndAlso Not owner.DownloadSettingsCard.IsEnabled,
                    "late startup completion or failure cannot update closed targets status or settings input: " & mode)
                Dim state = owner._launcherState, theme = owner._appearance.Theme
                result = owner.RestorePreferenceValuesAsync(settings).GetAwaiter().GetResult()
                check(result Is Nothing AndAlso reads.Count = 1 AndAlso owner._launcherState Is state AndAlso owner._appearance.Theme = theme,
                    "an already closing window rejects another restore before applying profile models: " & mode)
            Finally
                owner._restoringSettings = False : owner.Close()
            End Try
        Next

        For mode = 0 To 3
            Dim reads As New List(Of String), owner = ScalarOwner(root, game, reads)
            Try
                Dim settings = ScalarRecoverySettings(root, game)
                Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                owner._launcherDirectoryLoader = Function(binding)
                                                    reads.Add(binding.Id)
                                                    Return gate.Task
                                                End Function
                Dim older = owner.RestorePreferenceValuesAsync(settings)
                Dim newer = DirectCast(settings.DeepClone(), JObject)
                newer("externalLaunchers")("targetBindingId") = "PclCe"
                newer("speed") = "2.5"
                owner._launcherDirectoryLoader = Function(binding)
                                                    reads.Add(binding.Id)
                                                    Return Task.FromResult(ScalarDirectory(binding, game))
                                                End Function
                owner.RestorePreferenceValuesAsync(newer).GetAwaiter().GetResult()
                owner._restoringSettings = False
                Dim baseline = owner._preferenceBaseline, selected = owner._selectedFolder, status = owner.StatusText.Text
                owner.SpeedLimitBox.Text = "3.5"
                CompletePreferenceRead(gate, mode, ScalarDirectory(owner._launcherState.Bindings(0), game))
                Dim failure As Exception = Nothing, result = AwaitPreferenceRestore(older, failure)
                check(failure Is Nothing AndAlso result Is Nothing AndAlso owner._preferenceBaseline Is baseline AndAlso CStr(baseline("speed")) = "2.5",
                    "superseded startup cannot recapture or fail the newer profile baseline: " & mode)
                check(reads.Count = 4 AndAlso owner._selectedFolder Is selected AndAlso owner.CurrentInstallTarget().BindingId = "PclCe" AndAlso
                    owner.SpeedLimitBox.Text = "3.5" AndAlso owner.StatusText.Text = status AndAlso owner.DownloadSettingsCard.IsEnabled,
                    "late old startup results preserve the newer target edits status and input state: " & mode)
                Dim file = Path.Combine(root, "lifetime-newer-" & mode & ".json")
                DownloadSettingsFile.Save(file, newer)
                owner._preferenceBaselinePath = Path.GetFullPath(file)
                owner.SavePreferenceValues(file, Nothing, 3.5)
                Dim saved = JObject.Parse(IO.File.ReadAllText(file))
                check(CStr(saved("speed")) = "3.5" AndAlso CStr(saved("externalLaunchers")("targetBindingId")) = "PclCe",
                    "an edit made after the newer restore remains a real save delta after old work finishes: " & mode)
            Finally
                owner._restoringSettings = False : owner.Close()
            End Try
        Next

        Dim normalReads As New List(Of String), normal = ScalarOwner(root, game, normalReads)
        Try
            Dim result = normal.RestorePreferenceValuesAsync(ScalarRecoverySettings(root, game)).GetAwaiter().GetResult()
            check(result IsNot Nothing AndAlso result.Count = 0 AndAlso normalReads.Count = 3 AndAlso normal._preferenceBaseline IsNot Nothing,
                "current successful startup still discovers all launchers and captures its baseline")
            normal._restoringSettings = False
            check(normal.DownloadSettingsCard.IsEnabled AndAlso normal.CurrentInstallTarget().BindingId = "Hmcl",
                "current successful startup still releases input with its original native target")
        Finally
            normal._restoringSettings = False : normal.Close()
        End Try
        For mode = 1 To 3
            Dim reads As New List(Of String), owner = ScalarOwner(root, game, reads)
            Try
                Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                owner._launcherDirectoryLoader = Function(binding) gate.Task
                Dim restore = owner.RestorePreferenceValuesAsync(ScalarRecoverySettings(root, game))
                CompletePreferenceRead(gate, mode, Nothing)
                Dim failure As Exception = Nothing
                AwaitPreferenceRestore(restore, failure)
                check(failure IsNot Nothing AndAlso owner._preferenceBaseline Is Nothing AndAlso
                    ((mode = 1 AndAlso TypeOf failure Is IOException) OrElse (mode = 2 AndAlso TypeOf failure Is OperationCanceledException) OrElse
                    (mode = 3 AndAlso TypeOf failure Is InvalidOperationException)),
                    "current startup errors and cancellation still propagate to the existing failure handler: " & mode)
            Finally
                owner._restoringSettings = False : owner.Close()
            End Try
        Next

        Dim vetoReads As New List(Of String), veto = ScalarOwner(root, game, vetoReads)
        Try
            Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            veto._launcherDirectoryLoader = Async Function(binding)
                                               Await gate.Task
                                               Return ScalarDirectory(binding, game)
                                           End Function
            Dim restore = veto.RestorePreferenceValuesAsync(ScalarRecoverySettings(root, game))
            veto._busy = True
            Dim args As New CancelEventArgs
            veto.WindowClosing(veto, args)
            check(args.Cancel AndAlso Not veto._closing AndAlso Not restore.IsCompleted,
                "a vetoed close while a task stops does not retire a still-live startup")
            veto._busy = False
            gate.SetResult(True)
            Dim failure As Exception = Nothing, result = AwaitPreferenceRestore(restore, failure)
            veto._restoringSettings = False
            check(failure Is Nothing AndAlso result IsNot Nothing AndAlso veto._preferenceBaseline IsNot Nothing AndAlso veto.CurrentInstallTarget().BindingId = "Hmcl",
                "startup can finish normally after the close request was deferred")
        Finally
            veto._busy = False : veto._restoringSettings = False : veto.Close()
        End Try
    End Sub
End Class
