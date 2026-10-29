Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Sub VerifySettingsDuplicateKeys(check As Action(Of Boolean, String), root As String)
        Dim samples = {
            "{""gameRoot"":""first"",""gameRoot"":""last""}",
            "{""source"":0,""source"":2}",
            "{""externalLaunchers"":{""format"":999,""format"":1,""bindings"":{}}}",
            "{""externalLaunchers"":{""format"":1,""bindings"":{""same"":{""workspace"":""first""},""same"":{""workspace"":""last""}}}}",
            "{""future"":{""keep"":true,""keep"":false}}",
            "{""future"": [{""id"":""first"",""id"":""last""}]}",
            "{""gameRoot"":""first"",""game\u0052oot"":""last""}",
            "{""外观"":{""主题"":1,""主题"":2}}",
            "{""source"":2,""source"":2}"}
        For index = 0 To samples.Length - 1
            Dim owner As New MainWindow(True)
            Try
                Dim current = Path.Combine(root, "duplicate-current-" & index & ".json")
                Dim legacy = Path.Combine(root, "duplicate-fallback-" & index & ".json")
                File.WriteAllText(current, samples(index), New UTF8Encoding(False))
                File.WriteAllText(legacy, "{""fallback"":true}")
                Dim before = File.ReadAllBytes(current), legacyBefore = File.ReadAllBytes(legacy), stamp = File.GetLastWriteTimeUtc(current)
                Dim rejected = False
                Try
                    DownloadSettingsFile.LoadCompatible(current, legacy)
                Catch ex As Newtonsoft.Json.JsonException
                    rejected = True
                End Try
                check(rejected AndAlso File.ReadAllBytes(current).SequenceEqual(before) AndAlso File.GetLastWriteTimeUtc(current) = stamp,
                    "duplicate current settings reject ambiguous fields without using obsolete fallback data: " & index)
                Dim baseline = owner._preferenceBaseline
                rejected = False
                Try
                    owner.SavePreferenceValues(current, legacy, 0)
                Catch ex As Newtonsoft.Json.JsonException
                    rejected = True
                End Try
                check(rejected AndAlso owner._preferenceBaseline Is baseline AndAlso File.ReadAllBytes(current).SequenceEqual(before) AndAlso
                    File.ReadAllBytes(legacy).SequenceEqual(legacyBefore) AndAlso File.GetLastWriteTimeUtc(current) = stamp,
                    "automatic settings saves preserve duplicate input bytes and the retry baseline: " & index)

                Dim badLegacy = Path.Combine(root, "duplicate-legacy-" & index & ".json")
                Dim absent = Path.Combine(root, "duplicate-migration-" & index, "settings.json")
                File.WriteAllBytes(badLegacy, before)
                rejected = False
                Try
                    DownloadSettingsFile.LoadCompatible(absent, badLegacy)
                Catch ex As Newtonsoft.Json.JsonException
                    rejected = True
                End Try
                check(rejected AndAlso Not Directory.Exists(Path.GetDirectoryName(absent)) AndAlso File.ReadAllBytes(badLegacy).SequenceEqual(before),
                    "duplicate legacy settings fail validation before creating a migration destination: " & index)
                owner.SavePreferenceValues(current, legacy, 0, True)
                Dim saved = JObject.Parse(File.ReadAllText(current))
                check(CInt(saved("externalLaunchers")("format")) = 1 AndAlso owner._preferenceBaseline IsNot Nothing AndAlso
                    File.ReadAllBytes(legacy).SequenceEqual(legacyBefore) AndAlso File.ReadAllBytes(badLegacy).SequenceEqual(before),
                    "explicit reviewed repair can replace ambiguous current JSON while retaining both legacy profiles: " & index)
                owner.SavePreferenceValues(current, legacy, 2.5)
                check(CStr(JObject.Parse(File.ReadAllText(current))("speed")) = "2.5" AndAlso Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
                    "normal saves retry after duplicate repair with the gate released and no partial publication: " & index)
            Finally
                owner.Close()
            End Try
        Next

        Dim valid = "{/* compatible comments */""future"":{""Keep"":1,""keep"":2},""array"": [{""id"":1},{""id"":2}],""source"":2,}"
        Dim validLegacy = Path.Combine(root, "duplicate-valid-utf16.json"), validTarget = Path.Combine(root, "duplicate-valid-migration", "settings.json")
        Dim bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(valid)).ToArray()
        File.WriteAllBytes(validLegacy, bytes)
        Dim loaded = DownloadSettingsFile.LoadCompatible(validTarget, validLegacy)
        check(CInt(loaded("future")("Keep")) = 1 AndAlso CInt(loaded("future")("keep")) = 2 AndAlso loaded("array").Count() = 2,
            "strict settings parsing allows case-sensitive names and repeated names in separate object scopes")
        check(File.ReadAllBytes(validTarget).SequenceEqual(bytes) AndAlso File.ReadAllBytes(validLegacy).SequenceEqual(bytes),
            "valid commented UTF16 preferences retain BOM and original migration bytes")
        DownloadSettingsFile.SaveChanges(validTarget, validLegacy, New JObject From {{"speed", "1.5"}})
        Dim roundTrip = DownloadSettingsFile.LoadCompatible(validTarget, validLegacy)
        check(JToken.DeepEquals(loaded("future"), roundTrip("future")) AndAlso JToken.DeepEquals(loaded("array"), roundTrip("array")) AndAlso
            CStr(roundTrip("speed")) = "1.5" AndAlso File.ReadAllBytes(validLegacy).SequenceEqual(bytes),
            "strict parsing preserves independent and unknown valid values during partial saves")

        Dim gateTarget = Path.Combine(root, "duplicate-gate.json")
        DownloadSettingsFile.Save(gateTarget, New JObject From {{"source", 1}})
        Dim factory = GetType(DownloadSettingsFile).GetMethod("GateName", Reflection.BindingFlags.Static Or Reflection.BindingFlags.NonPublic)
        Dim gateName = CStr(factory.Invoke(Nothing, New Object() {gateTarget}))
        Dim duplicateBytes As Byte() = Nothing, work As Task(Of Boolean) = Nothing
        Using gate As New Mutex(False, gateName), started As New ManualResetEventSlim(False)
            gate.WaitOne()
            Try
                work = Task.Run(Function()
                                    started.Set()
                                    Try
                                        DownloadSettingsFile.SaveChanges(gateTarget, Nothing, New JObject From {{"speed", "9"}})
                                        Return False
                                    Catch ex As Newtonsoft.Json.JsonException
                                        Return True
                                    End Try
                                End Function)
                If Not started.Wait(TimeSpan.FromSeconds(5)) Then Throw New TimeoutException("Duplicate gate fixture did not start.")
                File.WriteAllText(gateTarget, samples(0))
                duplicateBytes = File.ReadAllBytes(gateTarget)
            Finally
                gate.ReleaseMutex()
            End Try
            If Not work.Wait(TimeSpan.FromSeconds(5)) Then Throw New TimeoutException("Duplicate gate fixture did not finish.")
            check(work.GetAwaiter().GetResult() AndAlso File.ReadAllBytes(gateTarget).SequenceEqual(duplicateBytes),
                "a writer waiting for the settings gate rejects duplicates introduced while waiting")
        End Using
        DownloadSettingsFile.Save(gateTarget, New JObject From {{"source", 1}})
        DownloadSettingsFile.SaveChanges(gateTarget, Nothing, New JObject From {{"speed", "9"}})
        check(CStr(JObject.Parse(File.ReadAllText(gateTarget))("speed")) = "9" AndAlso Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
            "a duplicate parse failure releases the gate and permits a later valid save")
    End Sub
End Class
