Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' PCL 5ef81e4 PageInstanceMod InstallMods / EDMods identify one Mod by its
' enabled name, including .disabled/.old aliases. These fixtures exercise late
' external changes without overwriting launcher settings or running Mod code.
Friend Module LocalModActionFollowupChecks
    Friend Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), root = Path.Combine(testRoot, "local-mod-actions-followup-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        For Each kind In {"PCL", "CE", "HMCL"}
            For Each isolated In {False, True}
                Dim label = kind & "-" & isolated.ToString()
                For Each suffix In {".disabled", ".old"}
                    LateCurrentAlias(root, label & "-current" & suffix, kind, isolated, suffix, passed)
                    LatePublishedAlias(root, label & "-published" & suffix, kind, isolated, suffix, passed)
                    LateSkippedAlias(root, label & "-skipped" & suffix, kind, isolated, suffix, passed)
                Next
                CancellationRetry(root, label & "-cancel", kind, isolated, passed)
            Next
        Next
        Return Task.FromResult(passed)
    End Function

    Private Sub Check(condition As Boolean, message As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(message)
        passed.Add(message)
    End Sub

    Private Sub Reject(action As Action, message As String, passed As List(Of String))
        Dim rejected As Boolean
        Try
            action()
        Catch ex As IOException
            rejected = True
        End Try
        Check(rejected, message, passed)
    End Sub

    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub

    Private Function Fabric(full As String, id As String) As String
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.CreateNew, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using output = zip.CreateEntry("fabric.mod.json").Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                writer.Write(New JObject From {{"schemaVersion", 1}, {"id", id}, {"version", "1.0"}, {"name", id}}.ToString())
            End Using
        End Using
        Return full
    End Function

    Friend Class Fixture
        Friend ReadOnly Root As String, Instance As InstalledInstance, Mods As String
        Friend ReadOnly Originals As Dictionary(Of String, Byte())
        Friend Sub New(parent As String, label As String, kind As String, isolated As Boolean)
            Root = Path.Combine(parent, label)
            Dim game = Path.Combine(Root, "游戏目录"), work = Path.Combine(Root, "launcher"), name = "本地 Mod 实例"
            Dim instancePath = Path.Combine(game, "versions", name), config As String = Nothing
            Put(Path.Combine(instancePath, name & ".json"), New JObject From {{"id", name}, {"clientVersion", "1.20.1"}, {"type", "release"},
                {"mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient"}, {"libraries", New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.10"}})}}.ToString())
            Dim launcherKind = ExternalLauncherKind.Pcl
            Select Case kind
                Case "PCL"
                    Put(Path.Combine(work, "PCL/Setup.ini"), "LaunchArgumentIndieV2:0" & vbCrLf & "Unrelated:保留")
                    Put(Path.Combine(instancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2:" & isolated.ToString() & vbCrLf & "WindowWidth:854")
                Case "CE"
                    launcherKind = ExternalLauncherKind.PclCe : config = Path.Combine(work, "PCL/config.v1.yml")
                    Put(config, "LocalFileVersion: 1" & vbLf & "LaunchArgumentIndieV2: 0" & vbLf & "Unrelated: keep")
                    Put(Path.Combine(instancePath, "PCL/config.v1.yml"), "VersionArgumentIndieV2: " & isolated.ToString().ToLowerInvariant() & vbLf & "WindowWidth: 854")
                Case "HMCL"
                    launcherKind = ExternalLauncherKind.Hmcl : config = Path.Combine(work, ".hmcl/config/game-directories.json")
                    Put(config, New JObject From {{"directories", New JArray(New JObject From {{"id", "fixture"}, {"path", game}, {"legacyGameSettings", "preset"}})}}.ToString())
                    Put(Path.Combine(Path.GetDirectoryName(config), "launcher-settings.json"), "{""defaultGameSettingsPreset"":""preset""}")
                    Put(Path.Combine(Path.GetDirectoryName(config), "game-settings.json"), "{""presets"":[{""id"":""preset"",""defaultIsolationType"":""MODDED"",""runningDirectory"":"""",""Unrelated"":""keep""}]}")
                    Put(Path.Combine(instancePath, ".hmcl/config/instance-game-settings.json"), New JObject From {{"overrideProperties", New JArray("runningDirectory")},
                        {"runningDirectory", If(isolated, "", game)}, {"WindowWidth", 854}}.ToString())
            End Select
            Dim target As New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherWorkspace = work, .LauncherKind = launcherKind,
                .LauncherBindingId = label, .LauncherConfigPath = config, .LauncherSharedConfigPath = Path.Combine(work, "absent-shared.json")}, work)
            Instance = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
            If Not String.IsNullOrEmpty(Instance.Problem) Then Throw New InvalidOperationException(Instance.Problem)
            Mods = Path.Combine(Instance.RunDirectory, "mods")
            Originals = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        End Sub
        Friend Function OriginalsUnchanged() As Boolean
            Return Originals.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
        End Function
        Friend Function StageAbsent() As Boolean
            Return Not Directory.Exists(Mods) OrElse Not Directory.EnumerateDirectories(Mods, ".pcl-db-mod-stage-*").Any()
        End Function
    End Class

    Private Sub LateCurrentAlias(root As String, label As String, kind As String, isolated As Boolean, suffix As String, passed As List(Of String))
        Dim fixture As New Fixture(root, label, kind, isolated), source = Fabric(Path.Combine(fixture.Root, "source", "current.jar"), "late_current")
        Dim original = File.ReadAllBytes(source), target = Path.Combine(fixture.Mods, "current.jar"), aliasPath = target & suffix
        Dim aliasBytes = Encoding.UTF8.GetBytes("externally created state alias")
        Reject(Sub() LocalModActions.ImportFilesForChecks(fixture.Instance, {source}, CancellationToken.None,
            Sub(index, destination) File.WriteAllBytes(destination & suffix, aliasBytes)), "late current state alias rejects import: " & label, passed)
        Check(Not File.Exists(target) AndAlso File.ReadAllBytes(aliasPath).SequenceEqual(aliasBytes) AndAlso fixture.StageAbsent(), "late current alias survives rollback without publishing a duplicate enabled name: " & label, passed)
        Check(File.ReadAllBytes(source).SequenceEqual(original) AndAlso fixture.OriginalsUnchanged(), "late current conflict preserves source and native settings: " & label, passed)
        File.Delete(aliasPath)
        Dim retry = LocalModActions.ImportFiles(fixture.Instance, {source, source}, CancellationToken.None)
        Check(retry.ChangedCount = 1 AndAlso retry.SkippedCount = 1 AndAlso File.ReadAllBytes(target).SequenceEqual(original), "retry after current-alias conflict supports identical duplicate sources: " & label, passed)
        Dim entry = LocalModCatalog.Read(target, CancellationToken.None)
        LocalModActions.SetEnabled(fixture.Instance, entry, False, CancellationToken.None)
        entry = LocalModCatalog.Read(target & ".disabled", CancellationToken.None)
        LocalModActions.SetEnabled(fixture.Instance, entry, True, CancellationToken.None)
        Check(File.ReadAllBytes(target).SequenceEqual(original) AndAlso Not File.Exists(target & ".disabled"), "conflict retry retains the normal PCL disable-enable roundtrip: " & label, passed)
    End Sub

    Private Sub LatePublishedAlias(root As String, label As String, kind As String, isolated As Boolean, suffix As String, passed As List(Of String))
        Dim fixture As New Fixture(root, label, kind, isolated)
        Dim first = Fabric(Path.Combine(fixture.Root, "source", "first.jar"), "first"), second = Fabric(Path.Combine(fixture.Root, "source", "second.jar"), "second")
        Dim firstBytes = File.ReadAllBytes(first), secondBytes = File.ReadAllBytes(second)
        Dim firstTarget = Path.Combine(fixture.Mods, "first.jar"), secondTarget = Path.Combine(fixture.Mods, "second.jar"), aliasPath = firstTarget & suffix
        Dim aliasBytes = Encoding.UTF8.GetBytes("late alias for an earlier publication")
        Reject(Sub() LocalModActions.ImportFilesForChecks(fixture.Instance, {first, second}, CancellationToken.None,
            Sub(index, destination)
                If index = 1 Then File.WriteAllBytes(aliasPath, aliasBytes)
            End Sub), "later publication rechecks an earlier item's state aliases: " & label, passed)
        Check(Not File.Exists(firstTarget) AndAlso Not File.Exists(secondTarget) AndAlso File.ReadAllBytes(aliasPath).SequenceEqual(aliasBytes) AndAlso fixture.StageAbsent(), "batch alias conflict rolls back only owned new files: " & label, passed)
        Check(File.ReadAllBytes(first).SequenceEqual(firstBytes) AndAlso File.ReadAllBytes(second).SequenceEqual(secondBytes) AndAlso fixture.OriginalsUnchanged(), "later-item alias failure preserves both sources and settings: " & label, passed)
        File.Delete(aliasPath)
        Dim retry = LocalModActions.ImportFiles(fixture.Instance, {first, second}, CancellationToken.None)
        Check(retry.ChangedCount = 2 AndAlso File.ReadAllBytes(firstTarget).SequenceEqual(firstBytes) AndAlso File.ReadAllBytes(secondTarget).SequenceEqual(secondBytes), "complete batch can retry after an earlier-publication alias conflict: " & label, passed)
    End Sub

    Private Sub LateSkippedAlias(root As String, label As String, kind As String, isolated As Boolean, suffix As String, passed As List(Of String))
        Dim fixture As New Fixture(root, label, kind, isolated)
        Dim keep = Fabric(Path.Combine(fixture.Root, "source", "keep.jar"), "keep"), added = Fabric(Path.Combine(fixture.Root, "source", "added.jar"), "added")
        LocalModActions.ImportFiles(fixture.Instance, {keep}, CancellationToken.None)
        Dim keepTarget = Path.Combine(fixture.Mods, "keep.jar"), addedTarget = Path.Combine(fixture.Mods, "added.jar"), aliasPath = keepTarget & suffix
        Dim keepBytes = File.ReadAllBytes(keepTarget), addedBytes = File.ReadAllBytes(added), aliasBytes = Encoding.UTF8.GetBytes("late alias for identical skipped target")
        Reject(Sub() LocalModActions.ImportFilesForChecks(fixture.Instance, {keep, added}, CancellationToken.None,
            Sub(index, destination) File.WriteAllBytes(aliasPath, aliasBytes)), "identical skipped targets participate in the final alias check: " & label, passed)
        Check(File.ReadAllBytes(keepTarget).SequenceEqual(keepBytes) AndAlso File.ReadAllBytes(aliasPath).SequenceEqual(aliasBytes) AndAlso Not File.Exists(addedTarget) AndAlso fixture.StageAbsent(), "skipped target and external alias survive rollback of another new item: " & label, passed)
        Check(File.ReadAllBytes(keep).SequenceEqual(keepBytes) AndAlso File.ReadAllBytes(added).SequenceEqual(addedBytes) AndAlso fixture.OriginalsUnchanged(), "skipped-target alias failure preserves original sources and settings: " & label, passed)
        File.Delete(aliasPath)
        Dim retry = LocalModActions.ImportFiles(fixture.Instance, {keep, added}, CancellationToken.None)
        Check(retry.ChangedCount = 1 AndAlso retry.SkippedCount = 1 AndAlso File.ReadAllBytes(keepTarget).SequenceEqual(keepBytes) AndAlso File.ReadAllBytes(addedTarget).SequenceEqual(addedBytes), "skipped-target alias conflict retries without replacing existing bytes: " & label, passed)
    End Sub

    Private Sub CancellationRetry(root As String, label As String, kind As String, isolated As Boolean, passed As List(Of String))
        Dim fixture As New Fixture(root, label, kind, isolated)
        Dim first = Fabric(Path.Combine(fixture.Root, "source", "cancel-first.jar"), "cancel_first"), second = Fabric(Path.Combine(fixture.Root, "source", "cancel-second.jar"), "cancel_second")
        Dim firstBytes = File.ReadAllBytes(first), secondBytes = File.ReadAllBytes(second), cancelled As Boolean
        Using cancellation As New CancellationTokenSource()
            Try
                LocalModActions.ImportFilesForChecks(fixture.Instance, {first, second}, cancellation.Token,
                    Sub(index, destination)
                        If index = 1 Then cancellation.Cancel()
                    End Sub)
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
        End Using
        Check(cancelled AndAlso Not File.Exists(Path.Combine(fixture.Mods, "cancel-first.jar")) AndAlso Not File.Exists(Path.Combine(fixture.Mods, "cancel-second.jar")) AndAlso fixture.StageAbsent(), "cancelling a later publication rolls back the earlier owned file: " & label, passed)
        Check(File.ReadAllBytes(first).SequenceEqual(firstBytes) AndAlso File.ReadAllBytes(second).SequenceEqual(secondBytes) AndAlso fixture.OriginalsUnchanged(), "cancelled batch preserves sources and native settings: " & label, passed)
        Dim retry = LocalModActions.ImportFiles(fixture.Instance, {first, second}, CancellationToken.None)
        Check(retry.ChangedCount = 2 AndAlso File.ReadAllBytes(Path.Combine(fixture.Mods, "cancel-first.jar")).SequenceEqual(firstBytes) AndAlso File.ReadAllBytes(Path.Combine(fixture.Mods, "cancel-second.jar")).SequenceEqual(secondBytes), "cancelled import releases all handles for an immediate complete retry: " & label, passed)
    End Sub
End Module
