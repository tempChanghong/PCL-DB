Imports System.IO
Imports System.Text
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Public Function VerifyCompatibilityUi(artifactDirectory As String, Optional checks As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("Compatibility checks require isolated preview mode.")
        checks = If(checks, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     checks.Add(label)
                                                 End Sub
        Dim assembly = Reflection.Assembly.GetExecutingAssembly()
        check(assembly.GetName().Name = "PlainCraftDownloader" AndAlso GetType(MainWindow).FullName = "PlainCraftDownloader.MainWindow" AndAlso
            Not assembly.GetTypes().Any(Function(t) t.FullName.StartsWith(DownloadSettingsFile.LegacyDirectoryName & ".", StringComparison.Ordinal)),
            "PCD app assembly and every application type use the approved namespace")
        check(GetType(MinecraftInstaller).Assembly.GetName().Name = "PlainCraftDownloader.Core" AndAlso GetType(MinecraftInstaller).FullName = "PlainCraftDownloader.Core.MinecraftInstaller",
            "PCD loads the renamed Core assembly and namespace")
        check(ProductInfo.DisplayName = "PCD" AndAlso ProductInfo.FullName = "Plain Craft Downloader" AndAlso ProductInfo.VersionReport().Contains(ProductInfo.FullName),
            "PCD short name full name and copied version report remain consistent")
        check(IO.Path.GetFileName(_settingsDirectory) = "PlainCraftDownloader" AndAlso IO.Path.GetFileName(IO.Path.GetDirectoryName(LegacySettingsFile)) = DownloadSettingsFile.LegacyDirectoryName AndAlso SettingsFile <> LegacySettingsFile,
            "PCD writes preferences under its new name while keeping a distinct legacy read path")

        Dim root = IO.Path.Combine(IO.Path.GetTempPath(), "PlainCraftDownloader-SettingsCompatibility-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Dim current = IO.Path.Combine(root, "current", "settings.json"), legacy = IO.Path.Combine(root, "legacy", "settings.json")
        Dim notices As New List(Of String)
        Dim report As Action(Of String) = Sub(message) notices.Add(message)
        check(DownloadSettingsFile.LoadCompatible(current, legacy, report) Is Nothing AndAlso Not Directory.Exists(IO.Path.GetDirectoryName(current)),
            "a fresh PCD profile does not create or invent settings during compatibility lookup")
        Directory.CreateDirectory(IO.Path.GetDirectoryName(legacy))
        Dim text = "{""gameRoot"":""D:\\游戏\\.minecraft"",""source"":2,""versionSource"":0,""appearance"":{""theme"":""green""},""futureField"":{""keep"":true}}" & vbCrLf
        Dim utf8 As New UTF8Encoding(True)
        Dim bytes = utf8.GetPreamble().Concat(utf8.GetBytes(text)).ToArray()
        File.WriteAllBytes(legacy, bytes)
        Dim loaded = DownloadSettingsFile.LoadCompatible(current, legacy, report)
        check(CStr(loaded("gameRoot")) = "D:\游戏\.minecraft" AndAlso CInt(loaded("source")) = 2 AndAlso CInt(loaded("versionSource")) = 0 AndAlso CStr(loaded("appearance")("theme")) = "green",
            "legacy migration restores the game directory both source choices and appearance")
        check(File.ReadAllBytes(current).SequenceEqual(bytes) AndAlso File.ReadAllBytes(legacy).SequenceEqual(bytes) AndAlso CBool(loaded("futureField")("keep")),
            "migration publishes the exact BOM and unknown fields without altering the legacy file")
        check(notices.Count = 0 AndAlso Not Directory.EnumerateFiles(IO.Path.GetDirectoryName(current), ".settings-*.tmp").Any(),
            "successful settings migration leaves no temporary file or failure notice")
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(current, stamp)
        DownloadSettingsFile.LoadCompatible(current, legacy, report)
        check(File.GetLastWriteTimeUtc(current) = stamp AndAlso File.ReadAllBytes(current).SequenceEqual(bytes),
            "repeated startup reads existing PCD settings without rewriting their bytes or timestamp")

        Dim newSettings As New JObject From {{"gameRoot", "D:\新目录\.minecraft"}, {"source", 1}, {"futureField", New JObject From {{"current", True}}}}
        DownloadSettingsFile.Save(current, newSettings)
        Dim newBytes = File.ReadAllBytes(current)
        loaded = DownloadSettingsFile.LoadCompatible(current, legacy, report)
        check(JToken.DeepEquals(loaded, newSettings) AndAlso File.ReadAllBytes(current).SequenceEqual(newBytes) AndAlso File.ReadAllBytes(legacy).SequenceEqual(bytes),
            "existing PCD preferences win a conflict without overwriting either profile")
        File.WriteAllText(legacy, "{invalid legacy")
        loaded = DownloadSettingsFile.LoadCompatible(current, legacy, report)
        check(JToken.DeepEquals(loaded, newSettings), "an invalid obsolete profile cannot block a valid existing PCD profile")
        File.WriteAllBytes(legacy, bytes)
        File.WriteAllText(current, "{invalid current")
        Dim rejected = False
        Try
            DownloadSettingsFile.LoadCompatible(current, legacy, report)
        Catch ex As Newtonsoft.Json.JsonException
            rejected = True
        End Try
        check(rejected AndAlso File.ReadAllText(current) = "{invalid current" AndAlso File.ReadAllBytes(legacy).SequenceEqual(bytes),
            "a corrupt current profile is reported instead of silently replacing it with old preferences")

        Dim invalidCurrent = IO.Path.Combine(root, "invalid-old", "settings.json")
        File.WriteAllText(legacy, "{invalid legacy")
        rejected = False
        Try
            DownloadSettingsFile.LoadCompatible(invalidCurrent, legacy, report)
        Catch ex As Newtonsoft.Json.JsonException
            rejected = True
        End Try
        check(rejected AndAlso Not Directory.Exists(IO.Path.GetDirectoryName(invalidCurrent)),
            "invalid legacy JSON is rejected before any migration directory or destination is created")
        File.WriteAllBytes(legacy, bytes)
        Using held As New FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            rejected = False
            Try
                DownloadSettingsFile.LoadCompatible(invalidCurrent, legacy, report)
            Catch ex As IOException
                rejected = True
            End Try
            check(rejected AndAlso Not Directory.Exists(IO.Path.GetDirectoryName(invalidCurrent)),
                "an unreadable legacy profile cannot create a partial destination")
        End Using

        Dim blocker = IO.Path.Combine(root, "blocked-parent")
        File.WriteAllText(blocker, "keep this conflicting file")
        Dim blockedCurrent = IO.Path.Combine(blocker, "settings.json")
        loaded = DownloadSettingsFile.LoadCompatible(blockedCurrent, legacy, report)
        check(CStr(loaded("gameRoot")) = "D:\游戏\.minecraft" AndAlso notices.Count = 1,
            "a blocked migration destination still returns validated legacy preferences with a diagnostic")
        check(File.ReadAllText(blocker) = "keep this conflicting file" AndAlso File.ReadAllBytes(legacy).SequenceEqual(bytes),
            "migration failure preserves the conflicting destination and the original profile")
        File.Delete(blocker)
        loaded = DownloadSettingsFile.LoadCompatible(blockedCurrent, legacy, report)
        check(File.ReadAllBytes(blockedCurrent).SequenceEqual(bytes) AndAlso notices.Count = 1,
            "migration can retry after a destination conflict is resolved without changing the old profile")

        Dim directoryTarget = IO.Path.Combine(root, "directory-target", "settings.json")
        Directory.CreateDirectory(directoryTarget)
        Dim sentinel = IO.Path.Combine(directoryTarget, "keep.txt")
        File.WriteAllText(sentinel, "keep directory")
        loaded = DownloadSettingsFile.LoadCompatible(directoryTarget, legacy, report)
        check(CBool(loaded("futureField")("keep")) AndAlso File.ReadAllText(sentinel) = "keep directory" AndAlso notices.Count = 2,
            "a directory occupying settings.json is preserved and old preferences remain usable")
        check(Not Directory.EnumerateFiles(IO.Path.GetDirectoryName(directoryTarget), ".settings-*.tmp").Any(),
            "a failed atomic migration removes its owned temporary file")

        Dim utf16Legacy = IO.Path.Combine(root, "utf16.json"), utf16Current = IO.Path.Combine(root, "utf16-current", "settings.json")
        Dim utf16Bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()
        File.WriteAllBytes(utf16Legacy, utf16Bytes)
        loaded = DownloadSettingsFile.LoadCompatible(utf16Current, utf16Legacy, report)
        check(CStr(loaded("gameRoot")) = "D:\游戏\.minecraft" AndAlso File.ReadAllBytes(utf16Current).SequenceEqual(utf16Bytes) AndAlso File.ReadAllBytes(utf16Legacy).SequenceEqual(utf16Bytes),
            "BOM-detected UTF16 legacy settings migrate without changing their encoding")
        Dim merged = _appearance.Merge(loaded)
        DownloadSettingsFile.Save(utf16Current, merged)
        loaded = DownloadSettingsFile.LoadCompatible(utf16Current, utf16Legacy, report)
        check(CBool(loaded("futureField")("keep")) AndAlso CInt(loaded("source")) = 2 AndAlso CStr(loaded("gameRoot")) = "D:\游戏\.minecraft",
            "subsequent appearance saves preserve migrated download choices and unknown fields")

        Dim raceCurrent = IO.Path.Combine(root, "concurrent", "settings.json"), otherLegacy = IO.Path.Combine(root, "other-legacy.json")
        File.WriteAllText(otherLegacy, "{""profile"":""other""}")
        Dim otherBytes = File.ReadAllBytes(otherLegacy)
        Dim results As JObject()
        Using start As New Threading.ManualResetEventSlim(False)
            Dim first = Task.Run(Function()
                                     start.Wait()
                                     Return DownloadSettingsFile.LoadCompatible(raceCurrent, legacy)
                                 End Function)
            Dim second = Task.Run(Function()
                                      start.Wait()
                                      Return DownloadSettingsFile.LoadCompatible(raceCurrent, otherLegacy)
                                  End Function)
            start.Set()
            results = Task.WhenAll(first, second).GetAwaiter().GetResult()
        End Using
        Dim winner = JObject.Parse(File.ReadAllText(raceCurrent))
        check(results.All(Function(value) JToken.DeepEquals(value, winner)) AndAlso
            (File.ReadAllBytes(raceCurrent).SequenceEqual(bytes) OrElse File.ReadAllBytes(raceCurrent).SequenceEqual(otherBytes)),
            "parallel migrations agree on one complete destination without merging or overwriting competing profiles")
        check(File.ReadAllBytes(legacy).SequenceEqual(bytes) AndAlso File.ReadAllBytes(otherLegacy).SequenceEqual(otherBytes) AndAlso
            Not Directory.EnumerateFiles(IO.Path.GetDirectoryName(raceCurrent), ".settings-*.tmp").Any(),
            "parallel migration preserves both sources and cleans its temporary files")

        Dim oldSpeed = SpeedLimitBox.Text, oldFailed = _settingsLoadFailed
        Try
            SpeedLimitBox.Text = "0"
            _settingsLoadFailed = True
            check(Not PersistSettings(), "automatic saves cannot overwrite a profile that failed to load")
            SaveSettings(Me, New Windows.RoutedEventArgs())
            check(Not _settingsLoadFailed AndAlso SettingsSaveFeedback.Text = "下载设置已保存。",
                "an explicit settings save can accept the reviewed current values after a load failure")
        Finally
            _settingsLoadFailed = oldFailed
            SpeedLimitBox.Text = oldSpeed
        End Try

        IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "settings-compatibility.json"), New JObject From {
            {"Scope", "temporary profiles only"}, {"FixtureRoot", root}, {"MigrationFailureNotices", New JArray(notices)},
            {"LegacyProfileBytesPreserved", File.ReadAllBytes(legacy).SequenceEqual(bytes)},
            {"RealUserPreferencesAccessed", False}, {"ExistingGameCachesMoved", False}}.ToString())
        VerifySettingsConflicts(check, artifactDirectory)
        Return checks
    End Function
End Class
