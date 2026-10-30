Imports System.IO
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Sub VerifySettingsConflicts(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim root = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-SettingsConflicts-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Dim legacy = Path.Combine(root, "legacy.json"), current = Path.Combine(root, "current.json")
        Dim original As New JObject From {{"futureField", New JObject From {{"keep", True}}}, {"appearance", New JObject From {{"theme", "green"}, {"futureAppearance", "keep"}}}, {"source", 2}}
        File.WriteAllText(legacy, original.ToString())
        Dim legacyBytes = File.ReadAllBytes(legacy)
        DownloadSettingsFile.LoadCompatible(current, legacy)
        SavePreferenceValues(current, legacy, 0)
        Dim saved = JObject.Parse(File.ReadAllText(current))
        Dim normalPreserved = JToken.DeepEquals(saved("futureField"), original("futureField"))
        Dim nestedPreserved = CStr(saved("appearance")("futureAppearance")) = "keep"
        Dim broken = Path.Combine(root, "broken-current.json")
        File.WriteAllText(broken, "{invalid after startup")
        Dim rejected = False
        Try
            SavePreferenceValues(broken, legacy, 0)
        Catch ex As Newtonsoft.Json.JsonException
            rejected = True
        End Try
        Dim brokenPreserved = rejected AndAlso File.ReadAllText(broken) = "{invalid after startup"
        IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "settings-conflicts.json"), New JObject From {
            {"Scope", "temporary profiles and actual save helpers"}, {"FixtureRoot", root}, {"NormalSavePreservesUnknown", normalPreserved},
            {"NormalSavePreservesNestedAppearance", nestedPreserved}, {"AutomaticSavePreservesNewlyCorruptFile", brokenPreserved},
            {"LegacyFileUnchanged", File.ReadAllBytes(legacy).SequenceEqual(legacyBytes)}}.ToString())
        check(normalPreserved AndAlso nestedPreserved, "normal saves retain migrated unknown fields and nested appearance preferences")
        check(brokenPreserved, "automatic saves reject a current profile corrupted after startup without replacing it")

        Dim sourceBefore = saved("source").DeepClone()
        SaveAppearanceValues(current, legacy)
        saved = JObject.Parse(File.ReadAllText(current))
        check(JToken.DeepEquals(sourceBefore, saved("source")) AndAlso JToken.DeepEquals(saved("futureField"), original("futureField")) AndAlso CStr(saved("appearance")("futureAppearance")) = "keep",
            "the real appearance writer retains unrelated download choices and unknown nested values")
        Dim textBefore = original.ToString(), model = _appearance.Merge(original)
        check(textBefore = original.ToString() AndAlso CStr(model("appearance")("futureAppearance")) = "keep" AndAlso CStr(model("appearance")("theme")) = _appearance.Theme,
            "appearance merging changes known values without mutating or discarding future settings")
        rejected = False
        Try
            SaveAppearanceValues(broken, legacy)
        Catch ex As Newtonsoft.Json.JsonException
            rejected = True
        End Try
        check(rejected AndAlso File.ReadAllText(broken) = "{invalid after startup",
            "appearance auto-save also preserves a profile corrupted after startup")
        SavePreferenceValues(broken, legacy, 0, True)
        check(JObject.Parse(File.ReadAllText(broken))("connectionsIndex") IsNot Nothing AndAlso File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
            "explicit recovery replaces malformed current JSON without changing the legacy profile")

        Dim brokenLegacy = Path.Combine(root, "broken-legacy.json"), missing = Path.Combine(root, "not-created", "settings.json")
        File.WriteAllText(brokenLegacy, "{invalid obsolete profile")
        rejected = False
        Try
            SavePreferenceValues(missing, brokenLegacy, 0)
        Catch ex As Newtonsoft.Json.JsonException
            rejected = True
        End Try
        check(rejected AndAlso Not Directory.Exists(Path.GetDirectoryName(missing)),
            "automatic recovery never publishes defaults over a malformed legacy profile")
        SavePreferenceValues(missing, brokenLegacy, 0, True)
        check(JObject.Parse(File.ReadAllText(missing))("connectionsIndex") IsNot Nothing AndAlso File.ReadAllText(brokenLegacy) = "{invalid obsolete profile",
            "a reviewed explicit save can create a new PCL-DB profile while preserving broken legacy bytes")

        Dim mergeInput As New JObject From {{"array", New JArray(1, 2)}, {"nested", New JObject From {{"keep", True}, {"clear", "old"}}}}
        Dim inputText = mergeInput.ToString()
        Dim merged = DownloadSettingsFile.MergeValues(mergeInput, New JObject From {{"array", New JArray(3)}, {"nested", New JObject From {{"clear", JValue.CreateNull()}}}})
        check(merged("array").Count() = 1 AndAlso CInt(merged("array")(0)) = 3 AndAlso CBool(merged("nested")("keep")) AndAlso merged("nested")("clear").Type = JTokenType.Null AndAlso inputText = mergeInput.ToString(),
            "settings patches replace arrays retain unrelated object keys and apply explicit nulls without mutating inputs")

        Dim concurrent = Path.Combine(root, "concurrent-updates.json")
        DownloadSettingsFile.Save(concurrent, original)
        Dim writers = Enumerable.Range(0, 24).Select(Function(index) Task.Run(Sub()
                                                                               Dim key = "writer-" & index
                                                                               DownloadSettingsFile.SaveChanges(concurrent, legacy, New JObject From {{key, index}, {"appearance", New JObject From {{key, index}}}})
                                                                           End Sub)).ToArray()
        Task.WhenAll(writers).GetAwaiter().GetResult()
        saved = JObject.Parse(File.ReadAllText(concurrent))
        check(Enumerable.Range(0, 24).All(Function(index) CInt(If(saved("writer-" & index), New JValue(-1))) = index AndAlso CInt(If(saved("appearance")("writer-" & index), New JValue(-1))) = index),
            "parallel PCL-DB writers retain every independent top-level and appearance update")
        check(JToken.DeepEquals(saved("futureField"), original("futureField")) AndAlso CStr(saved("appearance")("futureAppearance")) = "keep" AndAlso CInt(saved("source")) = 2,
            "parallel partial saves preserve the migrated baseline and all unrelated preferences")
        check(File.ReadAllBytes(legacy).SequenceEqual(legacyBytes) AndAlso Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
            "serialized settings updates preserve legacy bytes and remove owned temporary files")

        Dim beforeLock = File.ReadAllBytes(concurrent)
        Using held As New FileStream(concurrent, FileMode.Open, FileAccess.Read, FileShare.None)
            rejected = False
            Try
                DownloadSettingsFile.SaveChanges(concurrent, legacy, New JObject From {{"source", 1}})
            Catch ex As IOException
                rejected = True
            End Try
            check(rejected, "an unreadable current profile blocks partial saves instead of falling back to obsolete data")
        End Using
        check(File.ReadAllBytes(concurrent).SequenceEqual(beforeLock) AndAlso Not Directory.EnumerateFiles(root, ".settings-*.tmp").Any(),
            "a locked-file update failure leaves current bytes intact and no partial write")
        DownloadSettingsFile.SaveChanges(concurrent, legacy, New JObject From {{"source", 1}})
        check(CInt(JObject.Parse(File.ReadAllText(concurrent))("source")) = 1,
            "a failed read releases the settings gate so the next save can succeed")

        Dim gateFactory = GetType(DownloadSettingsFile).GetMethod("GateName", Reflection.BindingFlags.Static Or Reflection.BindingFlags.NonPublic)
        Dim gateName = CStr(gateFactory.Invoke(Nothing, New Object() {concurrent}))
        Dim aliasName = CStr(gateFactory.Invoke(Nothing, New Object() {Path.Combine(root, ".", "concurrent-updates.json").ToUpperInvariant()}))
        check(gateName = aliasName, "Windows case and dot path aliases coordinate through the same settings gate")
        Dim otherName = CStr(gateFactory.Invoke(Nothing, New Object() {Path.Combine(root, "other-profile.json")}))
        check(gateName <> otherName, "separate settings files do not share a global save lock")
        Dim abandonedTarget = Path.Combine(root, "abandoned.json")
        Dim abandonedName = CStr(gateFactory.Invoke(Nothing, New Object() {abandonedTarget}))
        Dim abandoned As Threading.Mutex = Nothing
        Dim thread As New Threading.Thread(Sub()
                                              abandoned = New Threading.Mutex(False, abandonedName)
                                              abandoned.WaitOne()
                                          End Sub) With {.IsBackground = True}
        thread.Start()
        If Not thread.Join(TimeSpan.FromSeconds(5)) Then Throw New InvalidOperationException("Abandoned gate fixture did not finish.")
        Try
            DownloadSettingsFile.SaveChanges(abandonedTarget, legacy, New JObject From {{"recovered", True}})
            check(CBool(JObject.Parse(File.ReadAllText(abandonedTarget))("recovered")),
                "a settings writer recovers an abandoned gate after the previous owner exits")
        Finally
            abandoned?.Dispose()
        End Try

        Dim stale = Path.Combine(root, "stale-window.json")
        SavePreferenceValues(stale, legacy, 0)
        Dim uiSource = CInt(BuildSettings(0)("source")), otherSource = If(uiSource = 1, 2, 1)
        DownloadSettingsFile.SaveChanges(stale, legacy, New JObject From {{"source", otherSource}})
        SavePreferenceValues(stale, legacy, 0)
        check(CInt(JObject.Parse(File.ReadAllText(stale))("source")) = otherSource,
            "closing an unchanged stale window cannot revert another instance's source choice")
        SavePreferenceValues(stale, legacy, 1.25)
        saved = JObject.Parse(File.ReadAllText(stale))
        check(CInt(saved("source")) = otherSource AndAlso CStr(saved("speed")) = "1.25",
            "a window auto-saves its edited speed without reverting another instance's untouched source")
        SaveAppearanceValues(stale, legacy)
        Dim otherTheme = If(_appearance.Theme = "green", "blue", "green")
        DownloadSettingsFile.SaveChanges(stale, legacy, New JObject From {{"appearance", New JObject From {{"theme", otherTheme}}}})
        SavePreferenceValues(stale, legacy, 1.25)
        check(CStr(JObject.Parse(File.ReadAllText(stale))("appearance")("theme")) = otherTheme,
            "closing after an appearance save does not reapply stale theme values")
        SavePreferenceValues(stale, legacy, 0, True)
        saved = JObject.Parse(File.ReadAllText(stale))
        check(CInt(saved("source")) = uiSource AndAlso CStr(saved("speed")) = "0" AndAlso CStr(saved("appearance")("theme")) = _appearance.Theme,
            "explicit reviewed saves still apply every visible preference and refresh the window baseline")
        Dim stamp As New DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(stale, stamp)
        SavePreferenceValues(stale, legacy, 0)
        check(File.GetLastWriteTimeUtc(stale) = stamp,
            "an unchanged auto-save leaves the current settings timestamp untouched")
        Dim oldSpeed = SpeedLimitBox.Text
        Try
            SpeedLimitBox.Text = "invalid stored speed"
            CapturePreferenceBaseline()
            SpeedLimitBox.Text = "0"
            Dim correction = DownloadSettingsFile.ChangedValues(_preferenceBaseline, BuildSettings(0))
            check(CStr(correction("speed")) = "0" AndAlso _preferenceBaselinePath = IO.Path.GetFullPath(SettingsFile),
                "fixing an invalid restored speed remains a real edit in the startup baseline")
        Finally
            SpeedLimitBox.Text = oldSpeed
        End Try
        VerifyInstallTargetSaveConflicts(check, root)
        VerifySettingsFormatConflicts(check, root)
        VerifySettingsDuplicateKeys(check, root)
        VerifyScalarPreferenceRecovery(check, root)
    End Sub
End Class
