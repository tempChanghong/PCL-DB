Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifySettingsFormatConflicts(check As Action(Of Boolean, String), root As String)
        Dim unknownFormats As JToken() = {
            New JObject From {{"format", 999}, {"bindings", New JObject}, {"future", New JArray("keep")}},
            New JObject From {{"bindings", New JObject}}, New JValue("opaque"), New JArray("future", New JObject), JValue.CreateNull(),
            New JObject From {{"format", "1"}, {"bindings", New JObject}}, New JObject From {{"format", 1}, {"bindings", New JArray}},
            New JObject From {{"format", True}, {"bindings", New JObject}}, New JObject From {{"format", 1.0}, {"bindings", New JObject}},
            New JObject From {{"format", 1}}}
        Dim originalRoot = Path.Combine(root, "format-original"), editedRoot = Path.Combine(root, "format-edited")
        Dim binding As New ExternalLauncherBinding With {.Id = "format-pcl", .Workspace = root}
        Dim seed As New LauncherBindingsState()
        seed.Bindings.Add(binding)
        For index = 0 To unknownFormats.Length - 1
            Dim owner As New MainWindow(True), aware As New MainWindow(True)
            Try
                Dim path = IO.Path.Combine(root, "format-change-" & index & ".json")
                owner._launcherState = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", seed.ToJson()}}, root)
                owner._selectedFolder = New GameFolder With {.Location = originalRoot, .LauncherBindingId = binding.Id,
                    .LauncherKind = binding.Kind, .LauncherWorkspace = root}
                owner.SavePreferenceValues(path, Nothing, 0, True)
                Dim initial = JObject.Parse(File.ReadAllText(path)), baseline = owner._preferenceBaseline.ToString()
                Dim future = DirectCast(initial.DeepClone(), JObject)
                future("externalLaunchers") = unknownFormats(index).DeepClone()
                future("gameRoot") = "future-root" : future("futureSetting") = "keep"
                DownloadSettingsFile.Save(path, future)
                Dim bytes = File.ReadAllBytes(path), stamp = File.GetLastWriteTimeUtc(path)
                owner._selectedFolder.Location = editedRoot
                Dim rejected = False, message = ""
                Try
                    owner.SavePreferenceValues(path, Nothing, 1.25)
                Catch ex As InvalidDataException
                    rejected = True : message = ex.Message
                End Try
                check(rejected AndAlso message.Contains("重新打开") AndAlso File.ReadAllBytes(path).SequenceEqual(bytes) AndAlso
                    File.GetLastWriteTimeUtc(path) = stamp AndAlso owner._preferenceBaseline.ToString() = baseline,
                    "automatic target save rejects a newer opaque format without publishing or advancing its baseline: " & index)
                rejected = False
                Try
                    owner.SavePreferenceValues(path, Nothing, 1.25, True)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                check(rejected AndAlso File.ReadAllBytes(path).SequenceEqual(bytes) AndAlso owner._preferenceBaseline.ToString() = baseline AndAlso
                    Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
                    "explicit settings save cannot downgrade a newer valid opaque format: " & index)

                owner.SaveAppearanceValues(path, Nothing, True)
                DownloadSettingsFile.SaveChanges(path, Nothing, New JObject From {{"speed", "3.5"}})
                Dim saved = JObject.Parse(File.ReadAllText(path))
                check(JToken.DeepEquals(saved("externalLaunchers"), unknownFormats(index)) AndAlso CStr(saved("gameRoot")) = "future-root" AndAlso
                    CStr(saved("speed")) = "3.5" AndAlso CStr(saved("futureSetting")) = "keep",
                    "independent appearance and speed saves preserve a concurrently upgraded binding subtree: " & index)

                aware._launcherState = LauncherBindingsState.Restore(saved, root)
                aware._selectedFolder = New GameFolder With {.Location = originalRoot}
                aware.SavePreferenceValues(path, Nothing, 0, True)
                saved = JObject.Parse(File.ReadAllText(path))
                check(aware._launcherState.ReadOnlyFormat AndAlso JToken.DeepEquals(saved("externalLaunchers"), unknownFormats(index)) AndAlso
                    CStr(saved("gameRoot")) = originalRoot AndAlso CStr(saved("futureSetting")) = "keep",
                    "a window already aware of an opaque format can save unrelated and manual directory preferences: " & index)

                DownloadSettingsFile.Save(path, initial)
                owner.SavePreferenceValues(path, Nothing, 1.25)
                saved = JObject.Parse(File.ReadAllText(path))
                check(CStr(saved("gameRoot")) = editedRoot AndAlso CStr(saved("externalLaunchers")("targetRoot")) = editedRoot AndAlso
                    CStr(saved("externalLaunchers")("targetBindingId")) = binding.Id AndAlso CStr(saved("speed")) = "1.25",
                    "a rejected window can retry its complete target when the known format is restored: " & index)
            Finally
                owner.Close() : aware.Close()
            End Try
        Next

        Dim legacyOwner As New MainWindow(True)
        Try
            Dim path = IO.Path.Combine(root, "format-legacy.json")
            legacyOwner._launcherState = LauncherBindingsState.Restore(New JObject From {{"pclBase", root}, {"gameRoot", originalRoot}}, root)
            legacyOwner._selectedFolder = New GameFolder With {.Location = originalRoot, .LauncherBindingId = "legacy-pcl", .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = root}
            legacyOwner._launcherMigrationPending = True
            Dim future As New JObject From {{"externalLaunchers", unknownFormats(0).DeepClone()}, {"futureSetting", True}}
            DownloadSettingsFile.Save(path, future)
            Dim before = File.ReadAllBytes(path), rejected = False
            Try
                legacyOwner.SavePreferenceValues(path, Nothing, 0)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            check(rejected AndAlso legacyOwner._launcherMigrationPending AndAlso legacyOwner._preferenceBaseline Is Nothing AndAlso File.ReadAllBytes(path).SequenceEqual(before),
                "pending legacy migration cannot replace a format upgraded after startup")
            DownloadSettingsFile.Save(path, New JObject From {{"pclBase", root}, {"gameRoot", originalRoot}})
            legacyOwner.SavePreferenceValues(path, Nothing, 0)
            check(Not legacyOwner._launcherMigrationPending AndAlso CInt(JObject.Parse(File.ReadAllText(path))("externalLaunchers")("format")) = 1,
                "a blocked migration retains its pending state and can retry after compatibility is restored")
        Finally
            legacyOwner.Close()
        End Try

        Dim gateTarget = Path.Combine(root, "format-gate.json")
        DownloadSettingsFile.Save(gateTarget, New JObject From {{"externalLaunchers", seed.ToJson()}})
        Dim gateFactory = GetType(DownloadSettingsFile).GetMethod("GateName", Reflection.BindingFlags.Static Or Reflection.BindingFlags.NonPublic)
        Dim gateName = CStr(gateFactory.Invoke(Nothing, New Object() {gateTarget}))
        Dim futureBytes As Byte() = Nothing, work As Task(Of Boolean) = Nothing
        Using gate As New Mutex(False, gateName), started As New ManualResetEventSlim(False)
            gate.WaitOne()
            Try
                work = Task.Run(Function()
                                    started.Set()
                                    Try
                                        DownloadSettingsFile.SaveChanges(gateTarget, Nothing, New JObject From {{"externalLaunchers", New JObject From {{"targetRoot", editedRoot}}}})
                                        Return False
                                    Catch ex As InvalidDataException
                                        Return True
                                    End Try
                                End Function)
                If Not started.Wait(TimeSpan.FromSeconds(5)) Then Throw New TimeoutException("Format gate fixture did not start.")
                DownloadSettingsFile.Save(gateTarget, New JObject From {{"externalLaunchers", unknownFormats(0).DeepClone()}})
                futureBytes = File.ReadAllBytes(gateTarget)
            Finally
                gate.ReleaseMutex()
            End Try
            If Not work.Wait(TimeSpan.FromSeconds(5)) Then Throw New TimeoutException("Format gate fixture did not finish.")
            check(work.GetAwaiter().GetResult() AndAlso File.ReadAllBytes(gateTarget).SequenceEqual(futureBytes),
                "a waiting writer checks the latest format only after acquiring the settings gate")
        End Using
        DownloadSettingsFile.SaveChanges(gateTarget, Nothing, New JObject From {{"source", 2}})
        check(CInt(JObject.Parse(File.ReadAllText(gateTarget))("source")) = 2 AndAlso Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
            "a rejected format write releases its gate for independent saves and leaves no temporary publication")
    End Sub
End Class
