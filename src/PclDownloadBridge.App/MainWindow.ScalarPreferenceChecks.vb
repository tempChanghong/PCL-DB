Imports System.Globalization
Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Function ScalarRecoverySettings(root As String, game As String) As JObject
        Dim state As New LauncherBindingsState With {.TargetRoot = game, .TargetBindingId = "Hmcl"}
        For Each kind As ExternalLauncherKind In [Enum].GetValues(GetType(ExternalLauncherKind))
            Dim binding As New ExternalLauncherBinding With {.Id = kind.ToString(), .Kind = kind, .Workspace = Path.Combine(root, kind.ToString())}
            Directory.CreateDirectory(binding.Workspace)
            state.Bindings.Add(binding) : state.SelectedRows(kind) = binding.Id
        Next
        Return New JObject From {{"externalLaunchers", state.ToJson()}, {"gameRoot", game}, {"pclBase", Path.Combine(root, "pcl-source")},
            {"connectionsIndex", 2}, {"speed", "1.5"}, {"reuse", False}, {"java", "java-fixture.exe"},
            {"source", 2}, {"versionSource", 1}, {"defaultIsolation", 3}, {"communityResources", New JObject From {{"source", 2}}},
            {"appearance", New JObject From {{"theme", "deepblue"}, {"darkMode", True}}}, {"developer", New JObject From {{"enabled", True}}},
            {"future", New JObject From {{"keep", True}}}}
    End Function

    Private Shared Function ScalarDirectory(binding As ExternalLauncherBinding, game As String) As LauncherDirectoryResult
        Dim result As New LauncherDirectoryResult With {.Succeeded = True}
        result.Folders.Add(
            New GameFolder With {.Location = game, .Name = binding.Id, .Origin = "scalar fixture", .LauncherBindingId = binding.Id,
                .LauncherKind = binding.Kind, .LauncherWorkspace = binding.Workspace, .IsSelected = binding.Kind = ExternalLauncherKind.Hmcl})
        Return result
    End Function

    Private Shared Function ScalarOwner(root As String, game As String, reads As List(Of String)) As MainWindow
        Dim owner As New MainWindow(True)
        owner._pclBase = Path.Combine(root, "fallback") : owner._restoringSettings = True
        owner._launcherDirectoryLoader = Function(binding)
                                            reads.Add(binding.Id)
                                            Return Task.FromResult(ScalarDirectory(binding, game))
                                        End Function
        Return owner
    End Function

    Private Shared Function ScalarValuesMatch(owner As MainWindow, pcl As String, connections As Integer, speed As String, reuse As Boolean, java As String) As Boolean
        Return owner._pclBase = pcl AndAlso owner.ConnectionCount() = connections AndAlso owner.SpeedLimitBox.Text = speed AndAlso
            owner.ReuseBox.IsChecked.GetValueOrDefault() = reuse AndAlso owner.JavaPathBox.Text = java
    End Function

    Private Sub VerifyScalarPreferenceRecovery(check As Action(Of Boolean, String), root As String)
        Dim game = Path.Combine(root, "scalar-game")
        Directory.CreateDirectory(game)
        Dim invalid As New List(Of KeyValuePair(Of String, JToken))
        For Each key In {"pclBase", "connectionsIndex", "speed", "reuse", "java"}
            invalid.Add(New KeyValuePair(Of String, JToken)(key, New JObject From {{"bad", True}}))
            invalid.Add(New KeyValuePair(Of String, JToken)(key, New JArray("bad")))
        Next
        For Each value As JToken In {New JValue("broken"), New JValue(Long.MaxValue), JValue.CreateNull(), New JValue("999999999999999999999999999999999999")}
            invalid.Add(New KeyValuePair(Of String, JToken)("connectionsIndex", value))
        Next
        invalid.Add(New KeyValuePair(Of String, JToken)("reuse", New JValue("invalid-bool")))
        invalid.Add(New KeyValuePair(Of String, JToken)("reuse", JValue.CreateNull()))
        Dim labels As New Dictionary(Of String, String) From {{"pclBase", "PCL 位置"}, {"connectionsIndex", "下载线程数"}, {"speed", "下载速度"}, {"reuse", "复用文件"}, {"java", "Java 路径"}}
        Dim index = 0
        For Each sample In invalid
            Dim reads As New List(Of String), owner = ScalarOwner(root, game, reads)
            Try
                Dim settings = ScalarRecoverySettings(root, game)
                settings(sample.Key) = sample.Value.DeepClone()
                Dim original = settings.ToString(), warnings As List(Of String) = Nothing, failure As Exception = Nothing
                Try
                    warnings = owner.RestorePreferenceValuesAsync(settings).GetAwaiter().GetResult()
                Catch ex As Exception
                    failure = ex
                End Try
                Dim label = sample.Key & "/" & sample.Value.Type & "/" & index
                check(failure Is Nothing AndAlso owner._preferenceBaseline IsNot Nothing AndAlso reads.Count = 3 AndAlso
                    owner._selectedFolder IsNot Nothing AndAlso owner.CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl AndAlso
                    owner.CurrentInstallTarget().Root = game,
                    "one invalid scalar does not stop restoring native targets and the preference baseline: " & label)
                check(warnings.SequenceEqual({labels(sample.Key)}) AndAlso ScalarValuesMatch(owner,
                    Path.Combine(root, If(sample.Key = "pclBase", "fallback", "pcl-source")), If(sample.Key = "connectionsIndex", 16, 32),
                    If(sample.Key = "speed", "0", 1.5.ToString(CultureInfo.CurrentCulture)), sample.Key = "reuse", If(sample.Key = "java", "", "java-fixture.exe")) AndAlso
                    CInt(SelectedSource(owner.SourceBox)) = 2 AndAlso CInt(SelectedSource(owner.VersionSourceBox)) = 1 AndAlso owner.SelectedIsolationStrategy().GetValueOrDefault() = 3 AndAlso
                    owner._appearance.Theme = "deepblue" AndAlso owner._appearance.DarkMode AndAlso owner._developer.Enabled AndAlso owner._communityPreferences.Source = 2,
                    "scalar recovery falls back only the failed field and keeps valid appearance source isolation and developer settings: " & label)
                Dim file = Path.Combine(root, "scalar-" & index & ".json")
                DownloadSettingsFile.Save(file, settings)
                owner._preferenceBaselinePath = Path.GetFullPath(file)
                owner.SourceBox.SelectedIndex = Array.IndexOf(SourceChoices, SourcePreference.Automatic)
                Dim limit = If(sample.Key = "speed", 0, 1.5)
                owner.SavePreferenceValues(file, Nothing, limit)
                Dim saved = JObject.Parse(IO.File.ReadAllText(file))
                check(JToken.DeepEquals(saved(sample.Key), sample.Value) AndAlso CInt(saved("source")) = CInt(SourcePreference.Automatic) AndAlso
                    CBool(saved("future")("keep")) AndAlso settings.ToString() = original,
                    "an unrelated auto-save retains the original bad scalar and never mutates the loaded profile: " & label)
                owner.SavePreferenceValues(file, Nothing, limit, True)
                saved = JObject.Parse(IO.File.ReadAllText(file))
                check(JToken.DeepEquals(saved(sample.Key), owner.BuildSettings(limit)(sample.Key)) AndAlso CBool(saved("future")("keep")),
                    "explicit reviewed saves can repair a recovered scalar without losing unknown settings: " & label)
            Finally
                owner._restoringSettings = False : owner.Close()
            End Try
            index += 1
        Next

        Dim controls = {
            New KeyValuePair(Of String, JToken)("", JValue.CreateNull()),
            New KeyValuePair(Of String, JToken)("connectionsIndex", New JValue("-1")),
            New KeyValuePair(Of String, JToken)("connectionsIndex", New JValue("99")),
            New KeyValuePair(Of String, JToken)("connectionsIndex", New JValue("3")),
            New KeyValuePair(Of String, JToken)("reuse", New JValue(0)),
            New KeyValuePair(Of String, JToken)("reuse", New JValue("true")),
            New KeyValuePair(Of String, JToken)("speed", New JValue("not numeric")),
            New KeyValuePair(Of String, JToken)("speed", New JValue("2.5")),
            New KeyValuePair(Of String, JToken)("pclBase", JValue.CreateNull()),
            New KeyValuePair(Of String, JToken)("java", JValue.CreateNull())}
        For Each sample In controls
            Dim reads As New List(Of String), owner = ScalarOwner(root, game, reads)
            Try
                Dim settings = ScalarRecoverySettings(root, game), missing = sample.Key.Length = 0
                If missing Then
                    For Each key In labels.Keys : settings.Remove(key) : Next
                Else
                    settings(sample.Key) = sample.Value.DeepClone()
                End If
                Dim warnings = owner.RestorePreferenceValuesAsync(settings).GetAwaiter().GetResult()
                check(warnings.Count = 0 AndAlso reads.Count = 3 AndAlso owner.CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl,
                    "missing or convertible legacy scalar preferences restore without false recovery warnings: " & sample.Key & "/" & sample.Value.ToString())
                Dim threads = If(missing, 16, If(sample.Key = "connectionsIndex", If(CStr(sample.Value) = "-1", 8, 64), 32))
                Dim speed = If(missing, "0", If(sample.Key = "speed", If(CStr(sample.Value) = "not numeric", "not numeric", 2.5.ToString(CultureInfo.CurrentCulture)), 1.5.ToString(CultureInfo.CurrentCulture)))
                Dim reuse = If(missing, True, sample.Key = "reuse" AndAlso sample.Value.Type = JTokenType.String)
                check(ScalarValuesMatch(owner, Path.Combine(root, If(missing OrElse sample.Key = "pclBase", "fallback", "pcl-source")), threads, speed, reuse,
                    If(missing OrElse sample.Key = "java", "", "java-fixture.exe")) AndAlso
                    (sample.Key <> "speed" OrElse CStr(sample.Value) <> "not numeric" OrElse CStr(owner._preferenceBaseline("speed")) = "not numeric"),
                    "scalar recovery retains legacy coercion clamping and editable invalid speed text: " & sample.Key & "/" & sample.Value.ToString())
            Finally
                owner._restoringSettings = False : owner.Close()
            End Try
        Next

        Dim pendingReads As New List(Of String), pending = ScalarOwner(root, game, pendingReads)
        Try
            Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            pending._launcherDirectoryLoader = Async Function(binding)
                                                  Await gate.Task
                                                  Return ScalarDirectory(binding, game)
                                              End Function
            Dim settings = ScalarRecoverySettings(root, game)
            settings("speed") = New JObject
            Dim restore = pending.RestorePreferenceValuesAsync(settings)
            check(Not restore.IsCompleted AndAlso pending._preferenceBaseline Is Nothing AndAlso Not pending.DownloadSettingsCard.IsEnabled,
                "recovering a scalar keeps the startup input gate and defers its baseline until directory discovery completes")
            gate.SetResult(True)
            If Not PumpMotionUntil(Function() restore.IsCompleted, 10000) Then Throw New TimeoutException("Scalar restore did not settle.")
            Dim warnings = restore.GetAwaiter().GetResult()
            pending._restoringSettings = False
            check(warnings.SequenceEqual({"下载速度"}) AndAlso pending._preferenceBaseline IsNot Nothing AndAlso pending.DownloadSettingsCard.IsEnabled AndAlso
                pending.CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl,
                "completed scalar recovery preserves the target baseline and releases settings input normally")
        Finally
            pending._restoringSettings = False : pending.Close()
        End Try
        Dim failedReads As New List(Of String), failedOwner = ScalarOwner(root, game, failedReads)
        Try
            failedOwner._launcherDirectoryLoader = Function(binding) Task.FromException(Of LauncherDirectoryResult)(New IOException("controlled scalar fixture I/O failure"))
            Dim failed = False
            Try
                failedOwner.RestorePreferenceValuesAsync(ScalarRecoverySettings(root, game)).GetAwaiter().GetResult()
            Catch ex As IOException
                failed = True
            End Try
            check(failed AndAlso failedOwner._preferenceBaseline Is Nothing,
                "scalar recovery never absorbs directory I/O failures or captures a partially restored baseline")
        Finally
            failedOwner._restoringSettings = False : failedOwner.Close()
        End Try
    End Sub
End Class
