Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' PCL 5ef81e4 LocalResourceFile keeps the archive identity and enabled suffix
' separate from metadata. PCL-CE 915ac297 ModLocalComp uses Implementation-Version
' for build placeholders. JAR continuation lines are byte based, not characters:
' https://docs.oracle.com/en/java/javase/25/docs/specs/jar/jar.html
Friend Module LocalModManifestChecks
    Private Sub Check(condition As Boolean, message As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(message)
        passed.Add(message)
    End Sub
    Private Function Utf8(text As String) As Byte()
        Return New UTF8Encoding(False, True).GetBytes(text)
    End Function
    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub
    Private Function Archive(full As String, entries As IDictionary(Of String, Byte())) As String
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.Create, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            For Each pair In entries
                Using output = zip.CreateEntry(pair.Key).Open()
                    output.Write(pair.Value, 0, pair.Value.Length)
                End Using
            Next
        End Using
        Return full
    End Function
    Private Function Toml(Optional version As String = "${file.jarVersion}") As Byte()
        Return Utf8("modLoader=""javafml""" & vbLf & "[[mods]]" & vbLf & "modId=""manifest_fixture""" & vbLf &
            "displayName=""Manifest fixture""" & vbLf & "version=""" & version & """" & vbLf)
    End Function
    Private Function JdkManifest() As Byte()
        ' Exact META-INF/MANIFEST.MF bytes emitted by JDK 25.0.2 jar --create.
        ' Title is 29 U+4E2D characters; E4 B8 / AD is split by CRLF + SPACE.
        ' SHA-256: 7B555393B52C9E35CFAA07CBAFF246F65EDD3CA1E8162BC6BFFACAA5EA60DEE6.
        Return Convert.FromBase64String("TWFuaWZlc3QtVmVyc2lvbjogMS4wDQpJbXBsZW1lbnRhdGlvbi1UaXRsZTog5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5LgNCiCt5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5Lit5LitDQpJbXBsZW1lbnRhdGlvbi1WZXJzaW9uOiA3LjguOQ0KRk1MQ29yZVBsdWdpbjogZml4dHVyZS5OZXZlclJ1bg0KQ3JlYXRlZC1CeTogMjUuMC4yIChPcmFjbGUgQ29ycG9yYXRpb24pDQoNCg==")
    End Function
    Private Sub ImportUnchanged(instance As InstalledInstance, source As String, passed As List(Of String))
        Dim original = File.ReadAllBytes(source), result = LocalModActions.ImportFiles(instance, {source}, CancellationToken.None)
        Dim destination = Path.Combine(instance.RunDirectory, "mods", LocalModCatalog.EnabledFileName(source))
        Check(result.ChangedCount = 1 AndAlso File.ReadAllBytes(source).SequenceEqual(original) AndAlso File.ReadAllBytes(destination).SequenceEqual(original), "manifest parsing preserves source and imported archive bytes: " & Path.GetFileName(source), passed)
    End Sub
    Private Sub ManifestWarning(entry As LocalModEntry, text As String, passed As List(Of String))
        Check(entry.IsArchiveValid AndAlso entry.Recognized AndAlso entry.CanToggle AndAlso String.IsNullOrEmpty(entry.Problem) AndAlso entry.Version = "5.0" AndAlso
            entry.Warnings.Any(Function(value) value.StartsWith("无法读取 JAR 清单：", StringComparison.Ordinal)), text, passed)
    End Sub

    Friend Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), root = Path.Combine(testRoot, "local-mod-manifest-" & Guid.NewGuid().ToString("N"))
        Dim game = Path.Combine(root, "game"), work = Path.Combine(root, "launcher"), name = "Manifest fixture", instancePath = Path.Combine(game, "versions", name)
        Dim globalPath = Path.Combine(work, "PCL/Setup.ini"), localPath = Path.Combine(instancePath, "PCL/Setup.ini"), profilePath = Path.Combine(instancePath, name & ".json")
        Put(globalPath, "LaunchArgumentIndieV2:0" & vbCrLf & "Unrelated:keep")
        Put(localPath, "VersionArgumentIndieV2:True" & vbCrLf & "WindowWidth:854")
        Put(profilePath, New JObject From {{"id", name}, {"clientVersion", "1.20.1"}, {"type", "release"}, {"mainClass", "net.minecraftforge.bootstrap.ForgeBootstrap"},
            {"libraries", New JArray(New JObject From {{"name", "net.minecraftforge:forge:1.20.1-47.3.0"}})}}.ToString())
        Dim settings = {globalPath, localPath, profilePath}.ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        Dim target As New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherWorkspace = work, .LauncherKind = ExternalLauncherKind.Pcl, .LauncherBindingId = "manifest-fixture"}, work)
        Dim instance = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
        Check(String.IsNullOrEmpty(instance.Problem) AndAlso instance.IsIsolated.Value, "manifest fixtures retain PCL's native isolated run directory", passed)
        Dim oracle = JdkManifest(), wholeStreamRejected As Boolean
        Try
            Dim decoded = New UTF8Encoding(False, True).GetString(oracle)
        Catch ex As DecoderFallbackException
            wholeStreamRejected = True
        End Try
        Check(wholeStreamRejected, "real JDK manifest oracle contains a UTF-8 code point split by byte continuation", passed)
        For Each metadataPath In {"META-INF/mods.toml", "META-INF/neoforge.mods.toml"}
            Dim loader = If(metadataPath.Contains("neoforge"), "NeoForge", "Forge")
            Dim source = Archive(Path.Combine(root, "sources", loader & "-jdk.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", oracle}, {metadataPath, Toml()}})
            Dim entry = LocalModCatalog.Read(source, CancellationToken.None)
            Check(entry.Recognized AndAlso entry.IsArchiveValid AndAlso entry.Version = "7.8.9" AndAlso entry.LoaderSummary = loader AndAlso String.IsNullOrEmpty(entry.Problem) AndAlso entry.Warnings.Count = 0,
                "real JDK byte-folded Unicode manifest resolves the build version: " & loader, passed)
            ImportUnchanged(instance, source, passed)
            For Each newline In {vbLf, vbCrLf, vbCr}
                Dim suffix = If(newline = vbLf, "LF", If(newline = vbCrLf, "CRLF", "CR"))
                Dim text = "Manifest-Version: 1.0" & newline & "Implementation-Version: 1.2" & newline & " .3" & newline & newline &
                    "Name: fake/Entry.class" & newline & "Implementation-Version: 99.0" & newline & "FMLCorePlugin: named.NotMain" & newline & newline
                source = Archive(Path.Combine(root, "sources", loader & "-" & suffix & ".jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", Utf8(text)}, {metadataPath, Toml()}})
                entry = LocalModCatalog.Read(source, CancellationToken.None)
                Check(entry.Version = "1.2.3" AndAlso String.IsNullOrEmpty(entry.Problem) AndAlso entry.Warnings.Count = 0, "ordinary continuation and main-section boundaries remain valid: " & loader & " / " & suffix, passed)
            Next
        Next
        Dim core = Archive(Path.Combine(root, "sources", "coremod-jdk.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", oracle}})
        Dim coreEntry = LocalModCatalog.Read(core, CancellationToken.None)
        Check(coreEntry.Recognized AndAlso coreEntry.LoaderSummary = "Forge" AndAlso coreEntry.ModIds.Count = 0 AndAlso coreEntry.DisplayName = New String(ChrW(&H4E2D), 29) AndAlso
            coreEntry.Version = "7.8.9" AndAlso String.IsNullOrEmpty(coreEntry.Problem) AndAlso Not coreEntry.Warnings.Any(Function(value) value.StartsWith("无法读取", StringComparison.Ordinal)), "manifest-only FMLCorePlugin retains its exact folded Unicode title and version", passed)
        ImportUnchanged(instance, core, passed)
        Dim namedOnly = Archive(Path.Combine(root, "sources", "named-only.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", Utf8(
            "Manifest-Version: 1.0" & vbCrLf & vbCrLf & "Name: fake/Entry.class" & vbCrLf & "FMLCorePlugin: named.NotMain" & vbCrLf & "Implementation-Version: 99.0" & vbCrLf & vbCrLf)}})
        Dim namedEntry = LocalModCatalog.Read(namedOnly, CancellationToken.None)
        Check(namedEntry.IsArchiveValid AndAlso Not namedEntry.Recognized AndAlso namedEntry.Version Is Nothing AndAlso String.IsNullOrEmpty(namedEntry.Problem), "named-entry attributes cannot fabricate a main FMLCorePlugin identity", passed)
        Dim namedVersion = Archive(Path.Combine(root, "sources", "named-version.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", Utf8(
            "Manifest-Version: 1.0" & vbLf & vbLf & "Name: fake.class" & vbLf & "Implementation-Version: 99.0" & vbLf)}, {"META-INF/mods.toml", Toml()}})
        Check(LocalModCatalog.Read(namedVersion, CancellationToken.None).Version Is Nothing, "a named-entry version cannot resolve a Mod's main build placeholder", passed)
        Dim invalidNamedBytes = Utf8("Manifest-Version: 1.0" & vbLf & "Implementation-Version: 3.2.1" & vbLf & vbLf & "Name: ").Concat(New Byte() {255}).ToArray()
        Dim invalidNamed = Archive(Path.Combine(root, "sources", "invalid-named.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", invalidNamedBytes}, {"META-INF/mods.toml", Toml()}})
        Dim invalidNamedEntry = LocalModCatalog.Read(invalidNamed, CancellationToken.None)
        Check(invalidNamedEntry.Version = "3.2.1" AndAlso invalidNamedEntry.Warnings.Count = 0, "unused malformed named-section bytes cannot invalidate the main Mod metadata", passed)
        Dim invalidMain = Utf8("Manifest-Version: 1.0" & vbCrLf & "Implementation-Title: ").Concat(New Byte() {255}).Concat(Utf8(vbCrLf & vbCrLf)).ToArray()
        Dim badManifest = Archive(Path.Combine(root, "sources", "invalid-main.jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", invalidMain}, {"META-INF/mods.toml", Toml("5.0")}})
        ManifestWarning(LocalModCatalog.Read(badManifest, CancellationToken.None), "a genuinely invalid main UTF-8 byte remains a manifest warning without replacing valid TOML", passed)
        ImportUnchanged(instance, badManifest, passed)
        For Each malformed In New Dictionary(Of String, Byte()) From {
            {"duplicate", Utf8("Manifest-Version: 1.0" & vbLf & "Implementation-Version: 1.0" & vbLf & "implementation-version: 2.0" & vbLf & vbLf)},
            {"orphan-continuation", Utf8(" orphan" & vbLf & "Manifest-Version: 1.0" & vbLf)},
            {"missing-colon", Utf8("Manifest-Version: 1.0" & vbLf & "bad header" & vbLf)},
            {"size-limit", New Byte(2 * 1024 * 1024) {}}}
            Dim source = Archive(Path.Combine(root, "sources", malformed.Key & ".jar"), New Dictionary(Of String, Byte()) From {{"META-INF/MANIFEST.MF", malformed.Value}, {"META-INF/mods.toml", Toml("5.0")}})
            ManifestWarning(LocalModCatalog.Read(source, CancellationToken.None), "manifest fallback keeps its existing validation boundary: " & malformed.Key, passed)
        Next
        Dim invalidJson = Utf8("{""schemaVersion"":1,""id"":""bad_utf8"",""version"":""1.0"",""description"":""").Concat(New Byte() {255}).Concat(Utf8("""}")).ToArray()
        Dim badMetadata = Archive(Path.Combine(root, "sources", "invalid-fabric.jar"), New Dictionary(Of String, Byte()) From {{"fabric.mod.json", invalidJson}})
        Dim badEntry = LocalModCatalog.Read(badMetadata, CancellationToken.None), rejected As Boolean
        Try
            LocalModActions.ImportFiles(instance, {badMetadata}, CancellationToken.None)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(badEntry.IsArchiveValid AndAlso badEntry.CanToggle AndAlso Not String.IsNullOrEmpty(badEntry.Problem) AndAlso rejected AndAlso
            Not File.Exists(Path.Combine(instancePath, "mods", Path.GetFileName(badMetadata))), "manifest unfolding does not loosen strict UTF-8 validation of Mod metadata or its import boundary", passed)
        Dim mods = Path.Combine(instancePath, "mods"), badLocal = Path.Combine(mods, "invalid-local.jar"), invalidLocalBytes = File.ReadAllBytes(badMetadata)
        File.WriteAllBytes(badLocal, invalidLocalBytes)
        Dim broken = Path.Combine(mods, "broken-local.jar") : Put(broken, "not a ZIP archive")
        For Each full In {badLocal, broken, Path.Combine(mods, "coremod-jdk.jar")}
            Dim original = File.ReadAllBytes(full)
            LocalModActions.SetEnabled(instance, LocalModCatalog.Read(full, CancellationToken.None), False, CancellationToken.None)
            Check(Not File.Exists(full) AndAlso File.ReadAllBytes(full & ".disabled").SequenceEqual(original), "metadata and archive failures do not prevent a byte-preserving disable: " & Path.GetFileName(full), passed)
            LocalModActions.SetEnabled(instance, LocalModCatalog.Read(full & ".disabled", CancellationToken.None), True, CancellationToken.None)
            Check(File.ReadAllBytes(full).SequenceEqual(original) AndAlso Not File.Exists(full & ".disabled"), "metadata and archive entries can restore their enabled name unchanged: " & Path.GetFileName(full), passed)
        Next
        Check(settings.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "manifest parsing imports and toggles preserve existing PCL and instance settings", passed)
        InstalledInstanceCatalog.EnsureUnchanged(instance, CancellationToken.None)
        Return Task.FromResult(passed)
    End Function
End Module
