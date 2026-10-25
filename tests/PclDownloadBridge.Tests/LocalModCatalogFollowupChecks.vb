Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' PCL 5ef81e4 keeps local archive state independent of metadata recognition;
' PCL-CE 915ac297 documents Forge / NeoForge mods.toml and multi-Mod records.
' These valid TOML string forms must not make an otherwise valid Mod unimportable.
Friend Module LocalModCatalogFollowupChecks
    Private Sub Check(condition As Boolean, message As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(message)
        passed.Add(message)
    End Sub
    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub
    Private Function Archive(full As String, metadataPath As String, text As String) As String
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.Create, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using output = zip.CreateEntry(metadataPath).Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                writer.Write(text)
            End Using
        End Using
        Return full
    End Function
    Private Function Metadata(description As String) As String
        Return "modLoader=""javafml""" & vbLf & "loaderVersion=""[40,)""" & vbLf &
            "# [[mods]] is not a real record" & vbLf & "[[mods]]" & vbLf &
            "modId=""fixture_primary""" & vbLf & "version=""1.0""" & vbLf & "displayName=""Valid TOML fixture""" & vbLf &
            "description=" & description & " # comment after the closing delimiter" & vbLf &
            "[[dependencies.fixture_primary]]" & vbLf & "modId=""minecraft""" & vbLf & "mandatory=true" & vbLf &
            "[[mods]]" & vbLf & "modId=""fixture_secondary""" & vbLf & "version=""2.0""" & vbLf
    End Function
    Private NotInheritable Class StringCase
        Friend ReadOnly Name As String, Value As String, Expected As String
        Friend Sub New(label As String, value As String, expected As String)
            Name = label : Me.Value = value : Me.Expected = expected
        End Sub
    End Class

    Friend Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), root = Path.Combine(testRoot, "local-mod-catalog-followup-" & Guid.NewGuid().ToString("N"))
        Dim game = Path.Combine(root, "game"), work = Path.Combine(root, "launcher"), instancePath = Path.Combine(game, "versions", "Mod fixture")
        Dim globalPath = Path.Combine(work, "PCL/Setup.ini"), localPath = Path.Combine(instancePath, "PCL/Setup.ini"), profilePath = Path.Combine(instancePath, "Mod fixture.json")
        Put(globalPath, "LaunchArgumentIndieV2:0" & vbCrLf & "Unrelated:keep")
        Put(localPath, "VersionArgumentIndieV2:True" & vbCrLf & "WindowWidth:854")
        Put(profilePath, New JObject From {{"id", "Mod fixture"}, {"clientVersion", "1.20.1"}, {"type", "release"},
            {"mainClass", "net.minecraftforge.bootstrap.ForgeBootstrap"}, {"libraries", New JArray(New JObject From {{"name", "net.minecraftforge:forge:1.20.1-47.3.0"}})}}.ToString())
        Dim originalSettings = {globalPath, localPath, profilePath}.ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        Dim target As New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherWorkspace = work, .LauncherKind = ExternalLauncherKind.Pcl, .LauncherBindingId = "toml-followup"}, work)
        Dim instance = InstalledInstanceCatalog.Read(target, "Mod fixture", CancellationToken.None)
        Check(String.IsNullOrEmpty(instance.Problem) AndAlso instance.IsIsolated.Value AndAlso instance.RunDirectory = instancePath, "TOML import fixture retains PCL native isolation", passed)
        Dim doubleQuote = ChrW(34), singleQuote = ChrW(39), basicEdge = New String(doubleQuote, 3), literalEdge = New String(singleQuote, 3)
        Dim contentWithFakeTable = "Quoted text [[mods]]" & vbLf & "modId=""not_a_mod"""
        Dim cases As New List(Of StringCase) From {
            New StringCase("basic-ending-one-quote", basicEdge & "A quote: " & New String(doubleQuote, 4), "A quote: " & doubleQuote),
            New StringCase("basic-ending-two-quotes", basicEdge & "Two quotes: " & New String(doubleQuote, 5), "Two quotes: " & New String(doubleQuote, 2)),
            New StringCase("literal-ending-one-quote", literalEdge & "An apostrophe: " & New String(singleQuote, 4), "An apostrophe: " & singleQuote),
            New StringCase("literal-ending-two-quotes", literalEdge & "Two apostrophes: " & New String(singleQuote, 5), "Two apostrophes: " & New String(singleQuote, 2)),
            New StringCase("basic-quotes-inside-both-delimiters", New String(doubleQuote, 4) & "Quoted" & New String(doubleQuote, 4), doubleQuote & "Quoted" & doubleQuote),
            New StringCase("literal-quotes-inside-both-delimiters", New String(singleQuote, 4) & "Quoted" & New String(singleQuote, 4), singleQuote & "Quoted" & singleQuote),
            New StringCase("continuation-spaces-tab-lf", basicEdge & "First \ " & vbTab & vbLf & "  second" & basicEdge, "First second"),
            New StringCase("continuation-spaces-crlf", basicEdge & "First \  " & vbCrLf & vbTab & "second" & basicEdge, "First second"),
            New StringCase("continuation-immediate-newline", basicEdge & "First \" & vbLf & vbLf & "  second" & basicEdge, "First second"),
            New StringCase("continuation-before-closing-delimiter", basicEdge & "End\ " & vbLf & vbTab & basicEdge, "End"),
            New StringCase("multiline-does-not-create-mods", basicEdge & contentWithFakeTable & basicEdge, contentWithFakeTable)}
        For Each metadataPath In {"META-INF/mods.toml", "META-INF/neoforge.mods.toml"}
            Dim loader = If(metadataPath.Contains("neoforge"), "NeoForge", "Forge")
            For Each definition In cases
                Dim source = Archive(Path.Combine(root, "sources", loader & "-" & definition.Name & ".jar"), metadataPath, Metadata(definition.Value))
                Dim original = File.ReadAllBytes(source), entry = LocalModCatalog.Read(source, CancellationToken.None)
                Check(entry.IsArchiveValid AndAlso entry.Recognized AndAlso String.IsNullOrEmpty(entry.Problem) AndAlso entry.Description = definition.Expected AndAlso entry.LoaderSummary = loader AndAlso
                    entry.ModIds.SequenceEqual({"fixture_primary", "fixture_secondary"}) AndAlso entry.DisplayName = "Valid TOML fixture" AndAlso entry.Version = "1.0", "valid multiline TOML retains exact description and real Mod records: " & loader & " / " & definition.Name, passed)
                Dim imported = LocalModActions.ImportFiles(instance, {source}, CancellationToken.None), destination = Path.Combine(instancePath, "mods", Path.GetFileName(source))
                Check(imported.ChangedCount = 1 AndAlso File.ReadAllBytes(source).SequenceEqual(original) AndAlso File.ReadAllBytes(destination).SequenceEqual(original), "valid multiline TOML imports through the real action without changing archive bytes: " & loader & " / " & definition.Name, passed)
            Next
            For Each invalid In New Dictionary(Of String, String) From {
                {"basic-unclosed", basicEdge & "unfinished"},
                {"basic-six-closing-quotes", basicEdge & "invalid" & New String(doubleQuote, 6)},
                {"literal-six-closing-quotes", literalEdge & "invalid" & New String(singleQuote, 6)},
                {"continuation-space-without-newline", basicEdge & "invalid\ text" & basicEdge},
                {"continuation-tab-without-newline", basicEdge & "invalid\" & vbTab & "text" & basicEdge},
                {"single-line-space-escape", """invalid\ text"""},
                {"single-line-newline-escape", """invalid\" & vbLf & "text"""}}
                Dim source = Archive(Path.Combine(root, "invalid", loader & "-" & invalid.Key & ".jar"), metadataPath, Metadata(invalid.Value))
                Dim original = File.ReadAllBytes(source), entry = LocalModCatalog.Read(source, CancellationToken.None), rejected As Boolean = False
                Try
                    LocalModActions.ImportFiles(instance, {source}, CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(entry.IsArchiveValid AndAlso entry.CanToggle AndAlso Not String.IsNullOrEmpty(entry.Problem) AndAlso rejected AndAlso
                    Not File.Exists(Path.Combine(instancePath, "mods", Path.GetFileName(source))) AndAlso File.ReadAllBytes(source).SequenceEqual(original), "malformed TOML remains visible but cannot be imported: " & loader & " / " & invalid.Key, passed)
            Next
        Next
        Dim mods = Path.Combine(instancePath, "mods"), damagedMetadata = Archive(Path.Combine(mods, "damaged-metadata.jar"), "META-INF/mods.toml", Metadata(basicEdge & "unfinished"))
        Dim damagedArchive = Path.Combine(mods, "damaged-archive.jar") : Put(damagedArchive, "not a ZIP archive")
        For Each full In {damagedMetadata, damagedArchive}
            Dim original = File.ReadAllBytes(full), entry = LocalModCatalog.Read(full, CancellationToken.None)
            Check(entry.CanToggle AndAlso Not String.IsNullOrEmpty(entry.Problem), "malformed local Mod remains available for disabling: " & Path.GetFileName(full), passed)
            LocalModActions.SetEnabled(instance, entry, False, CancellationToken.None)
            Check(Not File.Exists(full) AndAlso File.ReadAllBytes(full & ".disabled").SequenceEqual(original), "disabling malformed Mod preserves its original bytes: " & Path.GetFileName(full), passed)
            LocalModActions.SetEnabled(instance, LocalModCatalog.Read(full & ".disabled", CancellationToken.None), True, CancellationToken.None)
            Check(File.ReadAllBytes(full).SequenceEqual(original) AndAlso Not File.Exists(full & ".disabled"), "malformed Mod can be restored without a metadata rewrite: " & Path.GetFileName(full), passed)
        Next
        Check(originalSettings.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "multiline metadata import and malformed Mod toggles preserve all original PCL instance and launcher settings", passed)
        InstalledInstanceCatalog.EnsureUnchanged(instance, CancellationToken.None)
        Return Task.FromResult(passed)
    End Function
End Module
