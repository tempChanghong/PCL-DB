Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Real temporary archives and filesystem mutations; no game or mod code runs.
Friend Module LocalModChecks
    Private Sub Check(condition As Boolean, message As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(message)
        passed.Add(message)
    End Sub
    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub
    Private Function Archive(full As String, entries As IDictionary(Of String, String)) As String
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.Create, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            For Each pair In entries
                Using output = zip.CreateEntry(pair.Key).Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                    writer.Write(pair.Value)
                End Using
            Next
        End Using
        Return full
    End Function
    Private Function Fabric(full As String, Optional id As String = "fixture_mod", Optional version As String = "1.2.3") As String
        Return Archive(full, New Dictionary(Of String, String) From {{"fabric.mod.json", New JObject From {
            {"schemaVersion", 1}, {"id", id}, {"version", version}, {"name", "中文 Fixture Mod"}, {"description", "本地识别夹具"}}.ToString()}})
    End Function
    Private Function Snapshot(root As String) As Dictionary(Of String, Byte())
        Return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
    End Function
    Private Function Unchanged(files As IDictionary(Of String, Byte())) As Boolean
        Return files.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
    End Function
    Private Sub Reject(action As Action, message As String, passed As List(Of String))
        Dim rejected As Boolean
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is NotSupportedException
            rejected = True
        End Try
        Check(rejected, message, passed)
    End Sub
    Private Sub Cancelled(action As Action, message As String, passed As List(Of String))
        Dim cancelled As Boolean
        Try
            action()
        Catch ex As OperationCanceledException
            cancelled = True
        End Try
        Check(cancelled, message, passed)
    End Sub

    Friend Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), root = Path.Combine(testRoot, "local-mods-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        MetadataChecks(root, passed)
        NativeDirectoryChecks(root, passed)
        ConflictChecks(root, passed)
        PublicationChecks(root, passed)
        LinkChecks(root, passed)
        Return Task.FromResult(passed)
    End Function

    Private Sub MetadataChecks(root As String, passed As List(Of String))
        Dim folder = Path.Combine(root, "metadata")
        Dim plain = Fabric(Path.Combine(folder, "fabric.jar")), item = LocalModCatalog.Read(plain, CancellationToken.None)
        Check(item.Recognized AndAlso item.IsArchiveValid AndAlso item.CanToggle AndAlso item.Enabled AndAlso item.DisplayName = "中文 Fixture Mod" AndAlso item.Version = "1.2.3" AndAlso item.ModIds.Contains("fixture_mod") AndAlso item.LoaderSummary.Contains("Fabric"), "Fabric metadata identifies display name version ID and loader without executing the JAR", passed)
        Check(item.FilePath = Path.GetFullPath(plain) AndAlso item.FileName = "fabric.jar" AndAlso item.EnabledName = "fabric.jar" AndAlso item.Length = New FileInfo(plain).Length AndAlso item.Sha256.Length = 64, "local Mod identity binds the exact file path length and SHA-256", passed)
        For Each suffix In {".disabled", ".old", ".DISABLED"}
            Dim disabled = Fabric(Path.Combine(folder, "fabric.jar" & suffix)), entry = LocalModCatalog.Read(disabled, CancellationToken.None)
            Check(Not entry.Enabled AndAlso entry.EnabledName = "fabric.jar" AndAlso entry.Recognized AndAlso entry.CanToggle, "one PCL disabled suffix preserves metadata and enabled identity: " & suffix, passed)
        Next
        Dim quilt = Archive(Path.Combine(folder, "quilt.jar"), New Dictionary(Of String, String) From {{"quilt.mod.json", New JObject From {
            {"schema_version", 1}, {"quilt_loader", New JObject From {{"id", "quilt_fixture"}, {"version", "2.0"}, {"metadata", New JObject From {{"name", "Quilt fixture"}, {"description", "quilt description"}}}}}}.ToString()}})
        item = LocalModCatalog.Read(quilt, CancellationToken.None)
        Check(item.Recognized AndAlso item.ModIds.Contains("quilt_fixture") AndAlso item.LoaderSummary.Contains("Quilt") AndAlso item.DisplayName = "Quilt fixture" AndAlso item.Version = "2.0", "Quilt uses quilt_loader metadata instead of a filename guess", passed)
        Dim legacy = Archive(Path.Combine(folder, "legacy.jar"), New Dictionary(Of String, String) From {{"mcmod.info", New JArray(New JObject From {
            {"modid", "legacy_fixture"}, {"name", "Legacy Forge"}, {"version", "1.7"}, {"description", "Legacy"}}).ToString()}})
        item = LocalModCatalog.Read(legacy, CancellationToken.None)
        Check(item.Recognized AndAlso item.LoaderSummary.Contains("Forge") AndAlso item.ModIds.Contains("legacy_fixture") AndAlso item.DisplayName = "Legacy Forge", "legacy mcmod.info remains readable as a Forge Mod", passed)
        Dim wrapped = Archive(Path.Combine(folder, "wrapped.zip"), New Dictionary(Of String, String) From {{"mcmod.info", New JObject From {{"modList", New JArray(New JObject From {{"modid", "wrapped"}, {"name", "Wrapped legacy"}, {"version", "0.4"}})}}.ToString()}})
        item = LocalModCatalog.Read(wrapped, CancellationToken.None)
        Check(item.Recognized AndAlso item.ModIds.Contains("wrapped") AndAlso item.Version = "0.4", "legacy wrapped modList format is parsed independently of the archive extension", passed)
        For Each definition In {New With {.File = "META-INF/mods.toml", .Loader = "Forge"}, New With {.File = "META-INF/neoforge.mods.toml", .Loader = "NeoForge"}}
            Dim toml = "modLoader=""javafml""" & vbLf & "loaderVersion=""[40,)""" & vbLf & "[[mods]]" & vbLf &
                "modId = ""toml_one"" # outside comment" & vbLf & "displayName = ""A # quoted name""" & vbLf & "version = ""${file.jarVersion}""" & vbLf &
                "description = '''First line" & vbLf & "Second line'''" & vbLf & "[[mods]]" & vbLf & "modId='toml_two'" & vbLf & "version='2.1'" & vbLf & "displayName='Second Mod'" & vbLf &
                "[[dependencies.toml_one]]" & vbLf & "modId='minecraft'" & vbLf & "mandatory=true" & vbLf
            Dim archivePath = Archive(Path.Combine(folder, definition.Loader & ".jar"), New Dictionary(Of String, String) From {
                {definition.File, toml}, {"META-INF/MANIFEST.MF", "Manifest-Version: 1.0" & vbCrLf & "Implementation-Version: 9.4.2" & vbCrLf & vbCrLf}})
            item = LocalModCatalog.Read(archivePath, CancellationToken.None)
            Check(item.Recognized AndAlso item.LoaderSummary.Contains(definition.Loader) AndAlso item.ModIds.Contains("toml_one") AndAlso item.ModIds.Contains("toml_two") AndAlso Not item.ModIds.Contains("minecraft"), "TOML reads all Mod records while excluding dependency IDs: " & definition.Loader, passed)
            Check(item.Version.Contains("9.4.2") AndAlso item.DisplayName.Contains("A # quoted name") AndAlso Not item.DisplayName.Contains("outside comment"), "TOML quoting and manifest version placeholders follow actual metadata: " & definition.Loader, passed)
        Next
        Dim lite = Archive(Path.Combine(folder, "legacy.litemod"), New Dictionary(Of String, String) From {{"litemod.json", New JObject From {
            {"name", "lite_fixture"}, {"displayName", "Lite fixture"}, {"version", "1.0"}, {"mcversion", "1.12.2"}}.ToString()}})
        item = LocalModCatalog.Read(lite, CancellationToken.None)
        Check(item.Recognized AndAlso item.LoaderSummary.Contains("LiteLoader") AndAlso item.ModIds.Contains("lite_fixture"), "LiteLoader archives preserve their native litemod metadata", passed)
        Dim core = Archive(Path.Combine(folder, "coremod.jar"), New Dictionary(Of String, String) From {{"META-INF/MANIFEST.MF", "Manifest-Version: 1.0" & vbCrLf & "FMLCorePlugin: example.NeverRun" & vbCrLf & "Implementation-Version: 1.4" & vbCrLf & vbCrLf}})
        item = LocalModCatalog.Read(core, CancellationToken.None)
        Check(item.Recognized AndAlso item.LoaderSummary.Contains("Forge") AndAlso item.Version.Contains("1.4"), "legacy FMLCorePlugin is recognized from manifest text without loading its class", passed)
        Dim unknown = Archive(Path.Combine(folder, "unknown.jar"), New Dictionary(Of String, String) From {{"README.txt", "plain archive"}})
        item = LocalModCatalog.Read(unknown, CancellationToken.None)
        Check(item.IsArchiveValid AndAlso Not item.Recognized AndAlso item.CanToggle AndAlso String.IsNullOrEmpty(item.Problem) AndAlso item.Warnings.Count > 0, "an unrecognized valid JAR remains an explicit unknown rather than a fabricated Mod identity", passed)
        Dim broken = Path.Combine(folder, "broken.jar") : Put(broken, "not a zip")
        item = LocalModCatalog.Read(broken, CancellationToken.None)
        Check(Not item.IsArchiveValid AndAlso Not String.IsNullOrEmpty(item.Problem) AndAlso item.CanToggle, "a broken local archive remains visible and can still be disabled by exact file identity", passed)
        Dim badJson = Archive(Path.Combine(folder, "bad-json.jar"), New Dictionary(Of String, String) From {{"fabric.mod.json", "{"}})
        item = LocalModCatalog.Read(badJson, CancellationToken.None)
        Check(item.IsArchiveValid AndAlso Not String.IsNullOrEmpty(item.Problem), "a malformed Mod manifest is not misreported as an ordinary unknown archive", passed)
        Dim duplicate = Path.Combine(folder, "duplicate-metadata.jar")
        Archive(duplicate, New Dictionary(Of String, String) From {{"fabric.mod.json", "{""id"":""first"",""version"":""1"",""schemaVersion"":1}"}})
        Using stream As New FileStream(duplicate, FileMode.Open, FileAccess.ReadWrite), zip As New ZipArchive(stream, ZipArchiveMode.Update)
            Using output = zip.CreateEntry("fabric.mod.json").Open(), writer As New StreamWriter(output)
                writer.Write("{""id"":""second"",""version"":""2"",""schemaVersion"":1}")
            End Using
        End Using
        Check(Not String.IsNullOrEmpty(LocalModCatalog.Read(duplicate, CancellationToken.None).Problem), "duplicate root metadata cannot silently select one conflicting Mod identity", passed)
        Dim hugeMetadata = Archive(Path.Combine(folder, "large-metadata.jar"), New Dictionary(Of String, String) From {{"fabric.mod.json", New String(" "c, 2 * 1024 * 1024 + 1) & "{}"}})
        Check(Not String.IsNullOrEmpty(LocalModCatalog.Read(hugeMetadata, CancellationToken.None).Problem), "metadata parsing remains bounded independently of the archive payload size", passed)
        Check(Not LocalModCatalog.IsSupportedFile("double.jar.disabled.old") AndAlso Not LocalModCatalog.IsSupportedFile("program.exe") AndAlso LocalModCatalog.IsSupportedFile("archive.zip.old"), "state suffix normalization never converts repeated suffixes or executable names into accepted Mod archives", passed)
        Cancelled(Sub() LocalModCatalog.Read(plain, New CancellationToken(True)), "pre-cancelled metadata reading does not open an archive", passed)
    End Sub

    Private Class Fixture
        Friend ReadOnly Root As String, Game As String, Work As String, Instance As InstalledInstance, Target As LauncherInstallTarget
        Friend Sub New(rootPath As String, label As String, kind As String, isolated As Boolean, Optional customRun As String = Nothing)
            Root = Path.GetFullPath(Path.Combine(rootPath, label)) : Game = Path.Combine(Root, "游戏目录") : Work = Path.Combine(Root, "launcher")
            If customRun IsNot Nothing Then Directory.CreateDirectory(customRun)
            Dim instancePath = Path.Combine(Game, "versions", "本地 Mod 实例"), config As String = Nothing
            Dim metadata As New JObject From {{"id", "本地 Mod 实例"}, {"clientVersion", "1.20.1"}, {"type", "release"}, {"mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient"}, {"libraries", New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.10"}})}}
            Put(Path.Combine(instancePath, "本地 Mod 实例.json"), metadata.ToString())
            Dim launcherKind = ExternalLauncherKind.Pcl
            Select Case kind
                Case "PCL"
                    Put(Path.Combine(Work, "PCL/Setup.ini"), "LaunchArgumentIndieV2:0" & vbCrLf & "Unrelated:保留")
                    Put(Path.Combine(instancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2:" & isolated.ToString() & vbCrLf & "WindowWidth:854")
                Case "CE"
                    launcherKind = ExternalLauncherKind.PclCe : config = Path.Combine(Work, "PCL/config.v1.yml")
                    Put(config, "LocalFileVersion: 1" & vbLf & "LaunchArgumentIndieV2: 0" & vbLf & "Unrelated: keep")
                    Put(Path.Combine(instancePath, "PCL/config.v1.yml"), "VersionArgumentIndieV2: " & isolated.ToString().ToLowerInvariant() & vbLf & "WindowWidth: 854")
                Case "HMCL", "HMCL-legacy"
                    launcherKind = ExternalLauncherKind.Hmcl
                    If kind = "HMCL" Then
                        config = Path.Combine(Work, ".hmcl/config/game-directories.json")
                        Put(config, New JObject From {{"directories", New JArray(New JObject From {{"id", "fixture"}, {"path", Game}, {"legacyGameSettings", "preset"}})}}.ToString())
                        Put(Path.Combine(Path.GetDirectoryName(config), "launcher-settings.json"), "{""defaultGameSettingsPreset"":""preset""}")
                        Put(Path.Combine(Path.GetDirectoryName(config), "game-settings.json"), "{""presets"":[{""id"":""preset"",""defaultIsolationType"":""MODDED"",""runningDirectory"":"""",""Unrelated"":""keep""}]}")
                        Put(Path.Combine(instancePath, ".hmcl/config/instance-game-settings.json"), New JObject From {{"overrideProperties", New JArray("runningDirectory")}, {"runningDirectory", If(isolated, "", If(customRun, Game))}, {"WindowWidth", 854}}.ToString())
                    Else
                        config = Path.Combine(Work, "hmcl.json")
                        Put(config, New JObject From {{"configurations", New JObject From {{"fixture", New JObject From {{"gameDir", Game}, {"global", New JObject From {{"gameDirType", 0}, {"Unrelated", "keep"}}}}}}}}.ToString())
                        Put(Path.Combine(instancePath, "hmclversion.cfg"), New JObject From {{"usesGlobal", False}, {"gameDirType", If(isolated, 1, If(customRun Is Nothing, 0, 2))}, {"gameDir", If(customRun, "")}, {"WindowWidth", 854}}.ToString())
                    End If
            End Select
            Target = New LauncherInstallTarget(New GameFolder With {.Location = Game, .LauncherWorkspace = Work, .LauncherKind = launcherKind,
                .LauncherBindingId = label, .LauncherConfigPath = config, .LauncherSharedConfigPath = Path.Combine(Work, "absent-shared.json")}, Work)
            Instance = InstalledInstanceCatalog.Read(Target, "本地 Mod 实例", CancellationToken.None)
            If Not String.IsNullOrEmpty(Instance.Problem) Then Throw New InvalidOperationException("Local Mod fixture failed: " & Instance.Problem)
        End Sub
    End Class

    Private Sub NativeDirectoryChecks(root As String, passed As List(Of String))
        For Each kind In {"PCL", "CE", "HMCL", "HMCL-legacy"}
            For Each isolated In {False, True}
                Dim fixture As New Fixture(root, kind & "-" & isolated, kind, isolated), context = fixture.Instance
                Dim originals = Snapshot(fixture.Root), mods = Path.Combine(context.RunDirectory, "mods")
                Check(LocalModCatalog.Scan(context, CancellationToken.None).Count = 0 AndAlso Not Directory.Exists(mods), "empty local Mod scan leaves the native Mods directory absent: " & kind & " isolated=" & isolated, passed)
                Dim source = Fabric(Path.Combine(root, "sources", kind & "-" & isolated & ".jar")), sourceBytes = File.ReadAllBytes(source)
                Dim imported = LocalModActions.ImportFiles(context, {source}, CancellationToken.None)
                Check(imported.ChangedCount = 1 AndAlso imported.ModsDirectory = mods AndAlso File.ReadAllBytes(Path.Combine(mods, Path.GetFileName(source))).SequenceEqual(sourceBytes), "import publishes bytes only in the existing native run directory: " & kind & " isolated=" & isolated, passed)
                Dim entry = LocalModCatalog.Scan(context, CancellationToken.None).Single(), disabled = LocalModActions.SetEnabled(context, entry, False, CancellationToken.None)
                Check(disabled.ChangedCount = 1 AndAlso File.Exists(entry.FilePath & ".disabled") AndAlso Not File.Exists(entry.FilePath), "disabling uses PCL's suffix without rewriting the JAR: " & kind & " isolated=" & isolated, passed)
                entry = LocalModCatalog.Scan(context, CancellationToken.None).Single()
                Check(Not entry.Enabled AndAlso entry.Sha256 = LocalModCatalog.Read(source, CancellationToken.None).Sha256, "disabled native Mods retain their exact original content: " & kind & " isolated=" & isolated, passed)
                LocalModActions.SetEnabled(context, entry, True, CancellationToken.None)
                Check(File.Exists(Path.Combine(mods, Path.GetFileName(source))) AndAlso LocalModCatalog.Scan(context, CancellationToken.None).Single().Enabled, "enabled state can be restored in the same native Mods directory: " & kind & " isolated=" & isolated, passed)
                Check(Unchanged(originals) AndAlso File.ReadAllBytes(source).SequenceEqual(sourceBytes), "Mod import and toggles preserve every original instance external setting and source archive byte: " & kind & " isolated=" & isolated, passed)
                InstalledInstanceCatalog.EnsureUnchanged(context, CancellationToken.None)
            Next
        Next
        Dim customRun = Path.Combine(root, "custom-runtime"), custom As New Fixture(root, "hmcl-custom", "HMCL", False, customRun)
        Dim customSource = Fabric(Path.Combine(root, "sources", "custom.jar"))
        LocalModActions.ImportFiles(custom.Instance, {customSource}, CancellationToken.None)
        Check(File.Exists(Path.Combine(customRun, "mods/custom.jar")) AndAlso Not Directory.Exists(Path.Combine(custom.Game, "mods")), "HMCL custom runtime imports never fall back to the game root Mods directory", passed)
        Dim absentRun = Path.Combine(root, "absent-custom-runtime"), absent As New Fixture(root, "hmcl-absent-custom", "HMCL", False, absentRun)
        Directory.Delete(absentRun, False)
        Reject(Sub() LocalModActions.ImportFiles(absent.Instance, {customSource}, CancellationToken.None), "an absent external custom runtime is rejected before creating its directory tree", passed)
        Check(Not Directory.Exists(absentRun) AndAlso File.Exists(customSource), "rejected custom-runtime import preserves the missing root and original source archive", passed)
    End Sub

    Private Sub ConflictChecks(root As String, passed As List(Of String))
        Dim fixture As New Fixture(root, "conflicts", "PCL", True), context = fixture.Instance, mods = Path.Combine(context.RunDirectory, "mods")
        Dim source = Fabric(Path.Combine(root, "conflict-source", "alpha.jar")), sourceBytes = File.ReadAllBytes(source)
        LocalModActions.ImportFiles(context, {source}, CancellationToken.None)
        Dim target = Path.Combine(mods, "alpha.jar"), targetBytes = File.ReadAllBytes(target)
        Dim skip = LocalModActions.ImportFiles(context, {source}, CancellationToken.None)
        Check(skip.ChangedCount = 0 AndAlso skip.SkippedCount = 1 AndAlso File.ReadAllBytes(target).SequenceEqual(targetBytes), "reimporting the same content skips the existing file without replacing bytes", passed)
        skip = LocalModActions.ImportFiles(context, {target}, CancellationToken.None)
        Check(skip.ChangedCount = 0 AndAlso File.ReadAllBytes(target).SequenceEqual(targetBytes), "importing the destination itself cannot delete or truncate the existing Mod", passed)
        Dim different = Fabric(Path.Combine(root, "different-source", "alpha.jar"), "different_mod", "9.0")
        Reject(Sub() LocalModActions.ImportFiles(context, {different}, CancellationToken.None), "same-name different-content import rejects replacement", passed)
        Check(File.ReadAllBytes(target).SequenceEqual(targetBytes) AndAlso File.ReadAllBytes(source).SequenceEqual(sourceBytes), "rejected replacement preserves both the installed Mod and the original import archive", passed)
        Dim entry = LocalModCatalog.Read(target, CancellationToken.None)
        File.Copy(target, target & ".disabled")
        Dim aliases = LocalModCatalog.Scan(context, CancellationToken.None).Where(Function(modItem) modItem.EnabledName = "alpha.jar").ToList()
        Check(aliases.Count = 2 AndAlso aliases.All(Function(modItem) modItem.Warnings.Count > 0), "scanning preserves and diagnoses conflicting enabled and disabled siblings instead of hiding one", passed)
        Reject(Sub() LocalModActions.SetEnabled(context, entry, False, CancellationToken.None), "a disabled sibling is a toggle conflict even when bytes match", passed)
        Check(File.ReadAllBytes(target).SequenceEqual(targetBytes) AndAlso File.ReadAllBytes(target & ".disabled").SequenceEqual(targetBytes), "a conflicting toggle preserves both state siblings", passed)
        File.Delete(target & ".disabled")
        Directory.CreateDirectory(target & ".disabled")
        Reject(Sub() LocalModActions.SetEnabled(context, entry, False, CancellationToken.None), "a directory occupying the disabled target prevents a rename", passed)
        Directory.Delete(target & ".disabled", False)
        File.AppendAllText(target, "changed after catalogue")
        Reject(Sub() LocalModActions.SetEnabled(context, entry, False, CancellationToken.None), "a changed source digest invalidates a previously scanned toggle", passed)
        Check(File.Exists(target) AndAlso Not File.Exists(target & ".disabled"), "stale identity rejection cannot rename the replacement file", passed)
        File.WriteAllBytes(target, targetBytes)
        Dim old = Path.Combine(mods, "old.jar.old") : File.WriteAllBytes(old, sourceBytes)
        LocalModActions.SetEnabled(context, LocalModCatalog.Read(old, CancellationToken.None), True, CancellationToken.None)
        Check(File.Exists(Path.Combine(mods, "old.jar")) AndAlso Not File.Exists(old) AndAlso File.ReadAllBytes(Path.Combine(mods, "old.jar")).SequenceEqual(sourceBytes), "enabling the PCL old suffix restores its canonical archive name without rewriting content", passed)
        entry = LocalModCatalog.Read(target, CancellationToken.None)
        Dim unchanged = LocalModActions.SetEnabled(context, entry, True, CancellationToken.None)
        Check(unchanged.ChangedCount = 0 AndAlso File.ReadAllBytes(target).SequenceEqual(targetBytes), "requesting the existing enabled state is an idempotent no-op", passed)

        Dim dupOne = Fabric(Path.Combine(root, "duplicate-one", "dupe.jar"), "one"), dupTwo = Fabric(Path.Combine(root, "duplicate-two", "dupe.jar"), "two")
        Reject(Sub() LocalModActions.ImportFiles(context, {dupOne, dupTwo}, CancellationToken.None), "conflicting duplicate names within one import are rejected before publication", passed)
        Check(Not File.Exists(Path.Combine(mods, "dupe.jar")), "an invalid duplicate import does not publish its first source", passed)
        Dim duplicateResult = LocalModActions.ImportFiles(context, {dupOne, dupOne}, CancellationToken.None)
        Check(duplicateResult.ChangedCount = 1 AndAlso duplicateResult.SkippedCount = 1, "identical duplicate sources publish one file and report the skipped duplicate", passed)
        Dim disabledSource = Fabric(Path.Combine(root, "disabled-source", "disabled-input.jar.disabled"))
        Dim oldSource = Fabric(Path.Combine(root, "disabled-source", "old-input.jar.old"))
        LocalModActions.ImportFiles(context, {disabledSource, oldSource}, CancellationToken.None)
        Check(File.Exists(Path.Combine(mods, "disabled-input.jar")) AndAlso File.Exists(Path.Combine(mods, "old-input.jar")) AndAlso Not File.Exists(Path.Combine(mods, "old-input.jar.old")), "local import follows PCL and enables disabled or old input archive names", passed)
        Dim aliasSource = Fabric(Path.Combine(root, "alias-source", "alias.jar"))
        File.Copy(aliasSource, Path.Combine(mods, "alias.jar.disabled"))
        Reject(Sub() LocalModActions.ImportFiles(context, {aliasSource}, CancellationToken.None), "import refuses an existing disabled alias instead of enabling a second copy", passed)
        Check(Not File.Exists(Path.Combine(mods, "alias.jar")) AndAlso File.Exists(Path.Combine(mods, "alias.jar.disabled")), "disabled-alias import rejection preserves the user's existing disabled state", passed)
        Dim malformed = Archive(Path.Combine(root, "invalid-source", "invalid.jar"), New Dictionary(Of String, String) From {{"fabric.mod.json", "{"}})
        Reject(Sub() LocalModActions.ImportFiles(context, {malformed}, CancellationToken.None), "import rejects a malformed Mod manifest before exposing its archive", passed)
        Dim broken = Path.Combine(root, "invalid-source", "broken.jar") : Put(broken, "not ZIP")
        Reject(Sub() LocalModActions.ImportFiles(context, {broken}, CancellationToken.None), "import rejects an invalid archive while retaining the source file", passed)
        Dim unknown = Archive(Path.Combine(root, "unknown-source", "unknown.jar"), New Dictionary(Of String, String) From {{"README", "no metadata"}})
        LocalModActions.ImportFiles(context, {unknown}, CancellationToken.None)
        Check(File.Exists(Path.Combine(mods, "unknown.jar")), "an unknown valid archive can be copied without inventing loader compatibility", passed)
        For Each suffix In {".zip", ".zip.disabled", ".zip.old"}
            Dim zipSource = Fabric(Path.Combine(root, "zip-source", "archive" & suffix))
            Reject(Sub() LocalModActions.ImportFiles(context, {zipSource}, CancellationToken.None), "Mod import preserves ZIP modpack routing and rejects ZIP input state: " & suffix, passed)
        Next
        Put(Path.Combine(mods, "nested", "not-scanned.jar"), "nested placeholder")
        Check(Not LocalModCatalog.Scan(context, CancellationToken.None).Any(Function(modItem) modItem.FileName = "not-scanned.jar"), "local Mod scanning never descends into child directories", passed)
        Dim config = Path.Combine(context.InstancePath, "PCL/Setup.ini"), before = File.ReadAllBytes(config)
        File.AppendAllText(config, vbCrLf & "UnrelatedChanged:yes")
        Try
            Reject(Sub() LocalModActions.SetEnabled(context, LocalModCatalog.Read(target, CancellationToken.None), False, CancellationToken.None), "an external instance setting change prevents a stale-context toggle", passed)
            Reject(Sub() LocalModActions.ImportFiles(context, {Fabric(Path.Combine(root, "stale-config-source", "new.jar"))}, CancellationToken.None), "an external instance setting change prevents a stale-context import", passed)
        Finally
            File.WriteAllBytes(config, before)
        End Try

        Dim lockedSource = Fabric(Path.Combine(root, "locked-source", "locked.jar")), installLock = Path.Combine(fixture.Game, ".vanilla-downloader/install.lock")
        Directory.CreateDirectory(Path.GetDirectoryName(installLock))
        Using lease As New FileStream(installLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            Reject(Sub() LocalModActions.ImportFiles(context, {lockedSource}, CancellationToken.None), "an installation holding the root lock excludes local Mod writes", passed)
        End Using
        LocalModActions.ImportFiles(context, {lockedSource}, CancellationToken.None)
        Check(File.Exists(Path.Combine(mods, "locked.jar")), "local Mod import can retry after the installer releases its root lock", passed)
    End Sub
    Private Sub PublicationChecks(root As String, passed As List(Of String))
        Dim fixture As New Fixture(root, "publication", "CE", True), context = fixture.Instance, mods = Path.Combine(context.RunDirectory, "mods")
        Dim first = Fabric(Path.Combine(root, "publish-source", "first.jar")), second = Fabric(Path.Combine(root, "publish-source", "second.jar"))
        Cancelled(Sub() LocalModActions.ImportFiles(context, {first}, New CancellationToken(True)), "pre-cancelled import performs no publication", passed)
        Check(Not Directory.Exists(mods), "pre-cancelled import does not create the absent Mods directory", passed)
        Dim sources = New Dictionary(Of String, Byte()) From {{first, File.ReadAllBytes(first)}, {second, File.ReadAllBytes(second)}}
        Dim originals = Snapshot(fixture.Root)
        Using cancellation As New CancellationTokenSource()
            Cancelled(Sub() LocalModActions.ImportFilesForChecks(context, {first, second}, cancellation.Token,
                Sub(index, destination)
                    If index = 1 Then cancellation.Cancel()
                End Sub), "cancellation before the second publication rolls back the earlier new Mod", passed)
        End Using
        Check(Not File.Exists(Path.Combine(mods, "first.jar")) AndAlso Not File.Exists(Path.Combine(mods, "second.jar")) AndAlso Unchanged(originals) AndAlso Unchanged(sources), "cancelled batch preserves configurations sources and every pre-existing byte", passed)
        Reject(Sub() LocalModActions.ImportFilesForChecks(context, {first, second}, CancellationToken.None,
            Sub(index, destination)
                If index = 1 Then Throw New IOException("controlled second publication failure")
            End Sub), "a later publish failure rejects the batch", passed)
        Check(Not File.Exists(Path.Combine(mods, "first.jar")) AndAlso Not File.Exists(Path.Combine(mods, "second.jar")), "a later publish failure removes only the earlier file created by that batch", passed)
        Dim retried = LocalModActions.ImportFiles(context, {first, second}, CancellationToken.None)
        Check(retried.ChangedCount = 2 AndAlso File.ReadAllBytes(Path.Combine(mods, "first.jar")).SequenceEqual(sources(first)) AndAlso File.ReadAllBytes(Path.Combine(mods, "second.jar")).SequenceEqual(sources(second)), "the same sources retry successfully after a cancelled or failed batch", passed)
        Dim third = Fabric(Path.Combine(root, "publish-source", "third.jar"))
        Reject(Sub() LocalModActions.ImportFilesForChecks(context, {first, third}, CancellationToken.None,
            Sub(index, destination)
                Throw New IOException("controlled failure after existing-content skip")
            End Sub), "a skipped pre-existing Mod is not part of rollback ownership", passed)
        Check(File.ReadAllBytes(Path.Combine(mods, "first.jar")).SequenceEqual(sources(first)) AndAlso Not File.Exists(Path.Combine(mods, "third.jar")), "rollback preserves a reused identical Mod while removing no unrelated content", passed)
        Dim config = Path.Combine(context.InstancePath, "PCL/config.v1.yml"), configBytes = File.ReadAllBytes(config)
        Try
            Reject(Sub() LocalModActions.ImportFilesForChecks(context, {third}, CancellationToken.None,
                Sub(index, destination) File.AppendAllText(config, vbLf & "ChangedExternally: true")), "configuration changed during staging is rejected before publication", passed)
            Check(Not File.Exists(Path.Combine(mods, "third.jar")), "late configuration rejection cannot write a Mod into a changed runtime context", passed)
        Finally
            File.WriteAllBytes(config, configBytes)
        End Try
        Dim enabled = LocalModCatalog.Read(Path.Combine(mods, "first.jar"), CancellationToken.None)
        Cancelled(Sub() LocalModActions.SetEnabled(context, enabled, False, New CancellationToken(True)), "pre-cancelled toggle preserves its current name", passed)
        Check(File.Exists(enabled.FilePath) AndAlso Not File.Exists(enabled.FilePath & ".disabled"), "cancelled toggle leaves the installed Mod enabled", passed)

        Dim leaseFirst = Fabric(Path.Combine(root, "lease-source", "leased-first.jar")), leaseSecond = Fabric(Path.Combine(root, "lease-source", "leased-second.jar"))
        Dim blockedWrite = False, blockedDelete = False, blockedModsMove = False, blockedRunMove = False
        LocalModActions.ImportFilesForChecks(context, {leaseFirst, leaseSecond}, CancellationToken.None,
            Sub(index, destination)
                If index <> 1 Then Return
                Dim published = Path.Combine(mods, Path.GetFileName(leaseFirst))
                Try
                    File.AppendAllText(published, "external attempted replacement")
                Catch ex As IOException
                    blockedWrite = True
                End Try
                Try
                    File.Delete(published)
                Catch ex As IOException
                    blockedDelete = True
                End Try
                Dim movedMods = mods & "_moved", movedRun = context.RunDirectory & "_moved"
                Dim allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                If Not Path.GetFullPath(movedMods).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFullPath(movedRun).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Move fixture escaped its temporary root.")
                Try
                    Directory.Move(mods, movedMods)
                Catch ex As IOException
                    blockedModsMove = True
                End Try
                Try
                    Directory.Move(context.RunDirectory, movedRun)
                Catch ex As IOException
                    blockedRunMove = True
                End Try
            End Sub)
        Check(blockedWrite AndAlso blockedDelete AndAlso File.ReadAllBytes(Path.Combine(mods, Path.GetFileName(leaseFirst))).SequenceEqual(File.ReadAllBytes(leaseFirst)), "batch publication leases prevent external writes or deletion of already-published owned files", passed)
        Check(blockedModsMove AndAlso blockedRunMove AndAlso Directory.Exists(mods) AndAlso Directory.Exists(context.RunDirectory), "directory leases prevent moving the Mods directory or its runtime ancestor during publication", passed)

        Dim sharedRun = Path.Combine(root, "same-custom-runtime"), firstRoot As New Fixture(root, "shared-custom-a", "HMCL", False, sharedRun)
        Dim secondRoot As New Fixture(root, "shared-custom-b", "HMCL", False, sharedRun)
        Dim sharedSource = Fabric(Path.Combine(root, "shared-source", "shared.jar")), competingSource = Fabric(Path.Combine(root, "shared-source", "competing.jar"))
        Dim excluded = False
        LocalModActions.ImportFilesForChecks(firstRoot.Instance, {sharedSource}, CancellationToken.None,
            Sub(index, destination)
                Try
                    LocalModActions.ImportFiles(secondRoot.Instance, {competingSource}, CancellationToken.None)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
                    excluded = True
                End Try
            End Sub)
        Check(excluded AndAlso Not File.Exists(Path.Combine(sharedRun, "mods/competing.jar")), "different game roots pointing at the same physical custom Mods directory exclude overlapping writes", passed)
        LocalModActions.ImportFiles(secondRoot.Instance, {competingSource}, CancellationToken.None)
        Check(File.Exists(Path.Combine(sharedRun, "mods/shared.jar")) AndAlso File.Exists(Path.Combine(sharedRun, "mods/competing.jar")), "a second root can retry once the shared Mods directory operation releases ownership", passed)
        For Each kind In {"PCL", "CE"}
            Dim implicitFixture As New Fixture(root, "implicit-isolation-" & kind, kind, False)
            Dim localConfig = Path.Combine(implicitFixture.Instance.InstancePath, If(kind = "PCL", "PCL/Setup.ini", "PCL/config.v1.yml"))
            File.Delete(localConfig)
            Dim implicitContext = InstalledInstanceCatalog.Read(implicitFixture.Target, implicitFixture.Instance.Name, CancellationToken.None)
            Dim signal = Path.Combine(implicitContext.InstancePath, "mods/external-signal.txt")
            Reject(Sub() LocalModActions.ImportFilesForChecks(implicitContext, {third}, CancellationToken.None,
                Sub(index, destination) Put(signal, "another writer selected version-local data")), "a late implicit-isolation directory signal invalidates publication to the previous shared Mods path: " & kind, passed)
            Check(File.ReadAllText(signal) = "another writer selected version-local data" AndAlso Not File.Exists(Path.Combine(implicitFixture.Game, "mods/third.jar")) AndAlso Not File.Exists(localConfig),
                "implicit-isolation rejection keeps the external signal and never initializes or changes launcher configuration: " & kind, passed)
        Next
    End Sub
    Private Sub LinkChecks(root As String, passed As List(Of String))
        Dim fixture As New Fixture(root, "links", "PCL", True), context = fixture.Instance
        Dim mods = Path.Combine(context.RunDirectory, "mods"), outside = Path.Combine(fixture.Root, "outside")
        Directory.CreateDirectory(outside)
        Dim source = Fabric(Path.Combine(root, "link-source", "outside.jar")), outsideMod = Path.Combine(outside, "outside.jar")
        File.Copy(source, outsideMod)
        Dim allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Not Path.GetFullPath(mods).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFullPath(outside).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Local Mod junction fixture escaped its root.")
        Dim info As New ProcessStartInfo("cmd.exe", "/d /c mklink /J """ & mods & """ """ & outside & """") With {.UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden, .RedirectStandardOutput = True, .RedirectStandardError = True}
        Using process = System.Diagnostics.Process.Start(info)
            Dim output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd()
            process.WaitForExit()
            If process.ExitCode <> 0 Then Throw New IOException(output & errors)
        End Using
        Try
            Reject(Sub() LocalModCatalog.Scan(context, CancellationToken.None), "a Mods directory junction is rejected rather than recursively traversed", passed)
            Reject(Sub() LocalModActions.ImportFiles(context, {source}, CancellationToken.None), "import rejects a Mods directory replaced by a junction", passed)
            Check(File.ReadAllBytes(outsideMod).SequenceEqual(File.ReadAllBytes(source)) AndAlso Directory.GetFiles(outside).Length = 1, "linked outside data remains untouched by scan or rejected import", passed)
        Finally
            If Directory.Exists(mods) AndAlso (File.GetAttributes(mods) And FileAttributes.ReparsePoint) <> 0 Then Directory.Delete(mods, False)
        End Try
    End Sub
End Module
