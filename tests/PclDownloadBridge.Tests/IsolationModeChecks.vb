Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports System.Security.Cryptography
Imports System.Diagnostics
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module IsolationModeChecks
    Private ReadOnly Api As Byte() = ModJar("fabric-api")
    Private Function Hash(bytes As Byte()) As String
        Using sha = SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Private Function ModJar(id As String) As Byte()
        Using bytes As New MemoryStream()
            Using archive As New ZipArchive(bytes, ZipArchiveMode.Create, True)
                Using writer As New StreamWriter(archive.CreateEntry("fabric.mod.json", CompressionLevel.NoCompression).Open())
                    writer.Write("{""schemaVersion"":1,""id"":""" & id & """,""version"":""fixture""}")
                End Using
            End Using
            Return bytes.ToArray()
        End Using
    End Function
    Private Function Components(session As IsolationResourceChecks.Session, Optional all As Boolean = False) As ComponentSelection
        Dim selected = session.Fixture.Fabric()
        selected.Entries(ComponentKind.FabricApi) = New ComponentEntry With {.Kind = ComponentKind.FabricApi, .Minecraft = session.Fixture.Entry.Id,
            .Version = "fixture", .FileName = "fabric-api.jar", .Url = session.Fixture.Server.Url & "/api", .Sha1 = Hash(Api), .Size = Api.Length}
        session.Fixture.Server.Files("/api") = Api
        If all Then
            Dim opt = OptiFinePayloadChecks.Jar(64 * 1024), bridge = ModJar("optifabric")
            session.Fixture.Server.Files("/opt") = opt : session.Fixture.Server.Files("/bridge") = bridge
            selected.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = session.Fixture.Entry.Id,
                .Version = "fixture", .FileName = "optifine.jar", .Url = session.Fixture.Server.Url & "/opt"}
            selected.Entries(ComponentKind.OptiFabric) = New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = session.Fixture.Entry.Id,
                .Version = "fixture", .FileName = "optifabric.jar", .Url = session.Fixture.Server.Url & "/bridge", .Sha1 = Hash(bridge), .Size = bridge.Length}
        End If
        Return selected
    End Function
    Private Function ModPath(session As IsolationResourceChecks.Session, isolated As Boolean, Optional name As String = "fabric-api.jar") As String
        Return Path.Combine(If(isolated, Path.GetDirectoryName(session.Json), session.Root), "mods", name)
    End Function
    Private Sub Kind(session As IsolationResourceChecks.Session, value As String)
        Dim meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
        meta("type") = value : session.Fixture.Entry.Kind = value
        session.Fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
        session.Fixture.Entry.Sha1 = Hash(session.Fixture.Server.Files("/version"))
    End Sub
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-mode-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-matrix" Then Await MatrixAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-repair" Then Await RepairAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-conflicts" Then Await ConflictsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-rollback" Then Await RollbackAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-optifine" Then Await OptiFineAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-parallel" Then Await ParallelAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-legacy" Then Await LegacyAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-classification" Then Await ClassificationAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-mode-links" Then Await LinksAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function ClassificationAsync(root As String, passed As List(Of String)) As Task
        For strategy = 0 To 4
            Using session As New IsolationResourceChecks.Session(root, "mode-opt-state-" & strategy), cancel As New CancellationTokenSource()
                session.SetGlobal(strategy.ToString()) : Kind(session, "snapshot")
                IsolationResourceChecks.Configure(session.Fixture, mapped:=False)
                Dim selected As New ComponentSelection(), captured As String = Nothing
                session.Fixture.Server.Files("/opt") = OptiFinePayloadChecks.Jar(320 * 1024)
                selected.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = session.Fixture.Entry.Id,
                    .Version = "fixture", .FileName = "optifine.jar", .Url = session.Fixture.Server.Url & "/opt"}
                Dim progress As New Progress(Sub(value)
                    If value.Stage <> "安装 OptiFine" Then Return
                    Dim ini = Directory.EnumerateFiles(Path.Combine(session.Root, ".vanilla-downloader", "staging"), "Setup.ini", SearchOption.AllDirectories).Single()
                    captured = PclFolders.ReadIniValue(ini, "VersionArgumentIndieV2")
                    cancel.Cancel() ' Inspect the actual plan before any Java processor runs.
                End Sub)
                Try
                    Await session.StartAsync(components:=selected, progress:=progress, token:=cancel.Token)
                Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is InvalidDataException
                End Try
                Check(captured = (strategy = 4).ToString() AndAlso Not File.Exists(session.Json) AndAlso session.Released(), "standalone OptiFine uses PCL's component state without a Modable flag: strategy " & strategy, passed)
            End Using
        Next
    End Function
    Private Async Function LegacyAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {InstanceIsolationMode.FollowPcl, InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled}
            Using session As New IsolationResourceChecks.Session(root, "mode-lite-" & mode, "1.12.2")
                session.SetGlobal("0")
                Dim selected As New ComponentSelection(), isolated = mode = InstanceIsolationMode.Enabled
                selected.Entries(ComponentKind.LiteLoader) = New ComponentEntry With {.Kind = ComponentKind.LiteLoader, .Minecraft = "1.12.2", .Version = "1.12.2", .Data = New JObject From {{"libraries", New JArray()}, {"tweakClass", "fixture.Tweaker"}}}
                selected.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.12.2", .Version = "fixture", .FileName = "optifine.jar", .Url = session.Fixture.Server.Url & "/opt"}
                session.Fixture.Server.Files("/opt") = OptiFinePayloadChecks.Jar(64 * 1024)
                session.Fixture.Server.Files("/versions/com/mumfrey/liteloader/1.12.2/liteloader-1.12.2.jar") = Encoding.UTF8.GetBytes("fixture LiteLoader runtime")
                Await session.StartAsync(components:=selected, isolation:=mode)
                Check(File.ReadAllBytes(ModPath(session, isolated, "1.12.2/optifine.jar")).SequenceEqual(session.Fixture.Server.Files("/opt")) AndAlso Not File.Exists(ModPath(session, Not isolated, "1.12.2/optifine.jar")), "LiteLoader keeps PCL's mods/MC-version/OptiFine subdirectory at the selected target: " & mode, passed)
                Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = isolated.ToString() AndAlso (isolated OrElse Not Directory.Exists(Path.Combine(Path.GetDirectoryName(session.Json), "mods"))), "LiteLoader publishes one isolation preference without stranded private Mods: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function LinksAsync(root As String, passed As List(Of String)) As Task
        For Each late In {False, True}
            Using session As New IsolationResourceChecks.Session(root, "mode-link-" & late)
                Dim selected = Components(session), link = Path.Combine(session.Root, "mods"), outside = session.Root & "-outside", rejected = False
                Dim marker = Path.Combine(outside, "fabric-api.jar") : session.Put(marker, session.Bad)
                Directory.CreateDirectory(session.Root)
                Dim makeLink As Action = Sub()
                    Dim allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                    If Not Path.GetFullPath(link).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFullPath(outside).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Junction fixture escaped its temporary root.")
                    Dim info As New ProcessStartInfo("cmd.exe", "/d /c mklink /J """ & link & """ """ & outside & """") With {.UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden, .RedirectStandardOutput = True, .RedirectStandardError = True}
                    Using process = System.Diagnostics.Process.Start(info)
                        Dim output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd()
                        process.WaitForExit()
                        If process.ExitCode <> 0 Then Throw New IOException(output & errors)
                    End Using
                End Sub
                Try
                    If Not late Then makeLink()
                    Dim progress As New Progress(Sub(value)
                        If late AndAlso value.Stage = "完成安装" Then makeLink()
                    End Sub)
                    Try
                        Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, progress:=progress)
                    Catch ex As IOException
                        rejected = True
                    End Try
                    Check(rejected AndAlso File.ReadAllBytes(marker).SequenceEqual(session.Bad) AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Resource(False)) AndAlso session.Released(), "shared Mods junctions cannot redirect planning or final publication writes: " & late, passed)
                Finally
                    If Directory.Exists(link) AndAlso (File.GetAttributes(link) And FileAttributes.ReparsePoint) <> 0 Then Directory.Delete(link, False)
                End Try
            End Using
        Next
    End Function
    Private Async Function MatrixAsync(root As String, passed As List(Of String)) As Task
        For Each withComponents In {False, True}
            For Each snapshot In {False, True}
                For strategy = 0 To 4
                    Dim label = "follow-" & withComponents & "-" & snapshot & "-" & strategy
                    Using session As New IsolationResourceChecks.Session(root, label)
                        session.SetGlobal(strategy.ToString()) : Kind(session, If(snapshot, "snapshot", "release"))
                        Dim globalBytes = File.ReadAllBytes(session.GlobalPath), selected = If(withComponents, Components(session), Nothing)
                        Dim isolated = strategy = 4 OrElse ((strategy = 1 OrElse strategy = 3) AndAlso withComponents) OrElse ((strategy = 2 OrElse strategy = 3) AndAlso snapshot AndAlso Not withComponents)
                        Await session.StartAsync(components:=selected)
                        Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = isolated.ToString() AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes), "follow PCL resolves the pinned strategy and freezes only new settings: " & label, passed)
                        Check(File.Exists(session.Resource(isolated)) AndAlso Not File.Exists(session.Resource(Not isolated)) AndAlso File.Exists(session.ObjectFile()), "resources use the resolved game directory and objects remain shared: " & label, passed)
                        If withComponents Then Check(File.ReadAllBytes(ModPath(session, isolated)).SequenceEqual(Api) AndAlso Not File.Exists(ModPath(session, Not isolated)) AndAlso Directory.Exists(Path.Combine(If(isolated, Path.GetDirectoryName(session.Json), session.Root), "resourcepacks")), "component Mods and resourcepacks follow the same isolation decision: " & label, passed)
                    End Using
                Next
            Next
            For Each mode In {InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled}
                Using session As New IsolationResourceChecks.Session(root, "override-" & mode & "-" & withComponents)
                    session.SetGlobal("invalid") ' Explicit choices must not depend on the global value.
                    IsolationResourceChecks.Configure(session.Fixture, mapped:=False)
                    Dim selected = If(withComponents, Components(session), Nothing), isolated = mode = InstanceIsolationMode.Enabled
                    Await session.StartAsync(components:=selected, isolation:=mode)
                    Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = isolated.ToString(), "explicit isolation overrides global settings for modern installs: " & mode & "-" & withComponents, passed)
                    Check(Not withComponents OrElse (File.Exists(ModPath(session, isolated)) AndAlso Not File.Exists(ModPath(session, Not isolated))), "modern component installs use only the selected Mods directory: " & mode & "-" & withComponents, passed)
                End Using
            Next
        Next
        Using session As New IsolationResourceChecks.Session(root, "invalid-mode")
            Dim rejected = False
            Try
                Await session.StartAsync(isolation:=CType(99, InstanceIsolationMode))
            Catch ex As ArgumentOutOfRangeException
                rejected = True
            End Try
            Check(rejected AndAlso session.Fixture.Server.Requests.Count = 0 AndAlso Not Directory.Exists(session.Root), "invalid API isolation values fail before touching the root or network", passed)
        End Using
    End Function
    Private Async Function RepairAsync(root As String, passed As List(Of String)) As Task
        For Each isolated In {False, True}
            For Each mode In {InstanceIsolationMode.FollowPcl, InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled}
                Using session As New IsolationResourceChecks.Session(root, "mode-repair-" & isolated & "-" & mode)
                    Await session.SeedAsync() : session.SetGlobal(If(isolated, "0", "4"))
                    session.SetIni("VersionArgumentIndieV2:" & isolated.ToString(), Encoding.Unicode)
                    session.Put(session.Resource(isolated), session.Bad) : session.Put(session.Resource(Not isolated), session.Bad)
                    File.SetAttributes(session.Ini, FileAttributes.ReadOnly)
                    Dim ini = File.ReadAllBytes(session.Ini), json = File.ReadAllBytes(session.Json)
                    Dim result = Await session.StartAsync(repair:=True, isolation:=mode)
                    Check(File.ReadAllBytes(session.Ini).SequenceEqual(ini) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(json) AndAlso (File.GetAttributes(session.Ini) And FileAttributes.ReadOnly) <> 0, "repair ignores the new-install choice and preserves UTF16 read-only settings: " & isolated & "-" & mode, passed)
                    Check(Not File.ReadAllBytes(session.Resource(isolated)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(Not isolated)).SequenceEqual(session.Bad) AndAlso result.NetworkBytes = 0, "repair completes only the existing isolation target using cached objects: " & isolated & "-" & mode, passed)
                End Using
            Next
        Next
    End Function
    Private Async Function ConflictsAsync(root As String, passed As List(Of String)) As Task
        For Each fault In {"different", "directory", "mods-file", "packs-file", "duplicate", "reserved"}
            Using session As New IsolationResourceChecks.Session(root, "mode-conflict-" & fault)
                Dim selected = Components(session, all:=fault = "duplicate"), target = ModPath(session, False)
                Select Case fault
                    Case "different" : session.Put(target, session.Bad)
                    Case "directory" : Directory.CreateDirectory(target)
                    Case "mods-file" : session.Put(Path.GetDirectoryName(target), session.Bad)
                    Case "packs-file" : session.Put(Path.Combine(session.Root, "resourcepacks"), session.Bad)
                    Case "duplicate" : selected.Item(ComponentKind.FabricApi).FileName = "OPTIFABRIC.jar"
                    Case "reserved" : selected.Item(ComponentKind.FabricApi).FileName = "CONIN$.jar"
                End Select
                Dim rejected = False
                Try
                    Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is ArgumentException
                    rejected = True
                End Try
                Check(rejected AndAlso Not File.Exists(session.Json) AndAlso Not session.Fixture.Server.Requests.ContainsKey("/client") AndAlso Not session.Fixture.Server.Requests.ContainsKey("/api"), "shared Mods preflight rejects paths or different content before payload downloads: " & fault, passed)
                Check(Not File.Exists(target) OrElse File.ReadAllBytes(target).SequenceEqual(session.Bad), "a rejected shared Mods conflict preserves existing content: " & fault, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "matching-readonly")
            Dim selected = Components(session), target = ModPath(session, False), stamp = New DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            session.Put(target, Api) : File.SetLastWriteTimeUtc(target, stamp) : File.SetAttributes(target, FileAttributes.ReadOnly)
            Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled)
            Check(File.ReadAllBytes(target).SequenceEqual(Api) AndAlso File.GetLastWriteTimeUtc(target) = stamp AndAlso (File.GetAttributes(target) And FileAttributes.ReadOnly) <> 0 AndAlso Not Directory.Exists(Path.Combine(Path.GetDirectoryName(session.Json), "mods")), "identical read-only shared Mods keep bytes, timestamp and attributes without a private alias", passed)
        End Using
    End Function
    Private Async Function RollbackAsync(root As String, passed As List(Of String)) As Task
        For Each fault In {"foreign-instance", "late-mod", "cancel"}
            Using session As New IsolationResourceChecks.Session(root, "mode-rollback-" & fault), cancel As New CancellationTokenSource()
                Dim selected = Components(session), target = ModPath(session, False), reached = False, rejected = False
                Dim untouched = ModPath(session, False, "user.jar") : session.Put(untouched, session.Bad)
                Dim progress As New Progress(Sub(value)
                    If value.Stage <> "完成安装" OrElse reached Then Return
                    reached = True
                    Select Case fault
                        Case "foreign-instance" : session.Put(Path.Combine(Path.GetDirectoryName(session.Json), "foreign.txt"), session.Bad)
                        Case "late-mod" : session.Put(target, session.Bad)
                        Case "cancel" : cancel.Cancel()
                    End Select
                End Sub)
                Try
                    Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, progress:=progress, token:=cancel.Token)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is OperationCanceledException
                    rejected = True
                End Try
                Check(reached AndAlso rejected AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Resource(False)) AndAlso session.Released(), "failed publication rolls back new shared resources and releases the writer: " & fault, passed)
                Check(File.ReadAllBytes(untouched).SequenceEqual(session.Bad) AndAlso If(fault = "late-mod", File.ReadAllBytes(target).SequenceEqual(session.Bad), Not File.Exists(target)), "failed publication removes only its own shared Mods and keeps external files: " & fault, passed)
                If fault = "foreign-instance" Then
                    Dim foreign = Path.Combine(Path.GetDirectoryName(session.Json), "foreign.txt")
                    Check(File.ReadAllBytes(foreign).SequenceEqual(session.Bad), "an externally created instance survives failed atomic publication", passed)
                    File.Delete(foreign) : Directory.Delete(Path.GetDirectoryName(session.Json), False)
                ElseIf fault = "late-mod" Then
                    File.Delete(target)
                End If
                Dim result = Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled)
                Check(result.NetworkBytes = 0 AndAlso File.ReadAllBytes(target).SequenceEqual(Api) AndAlso File.Exists(session.Json) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "retry reuses verified payloads and atomically publishes the selected settings: " & fault, passed)
            End Using
        Next
    End Function
    Private Async Function OptiFineAsync(root As String, passed As List(Of String)) As Task
        For Each same In {False, True}
            Using session As New IsolationResourceChecks.Session(root, "mode-optifine-" & same)
                Dim selected = Components(session, all:=True), target = ModPath(session, False, "optifine.jar")
                Dim original = DirectCast(session.Fixture.Server.Files("/opt").Clone(), Byte())
                If Not same Then original(original.Length - 1) = original(original.Length - 1) Xor CByte(1)
                session.Put(target, original)
                Dim rejected = False
                Try
                    Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled)
                Catch ex As IOException
                    rejected = True
                End Try
                Check(rejected <> same AndAlso File.ReadAllBytes(target).SequenceEqual(original), "hashless OptiFine shared conflict compares the staged SHA1, not filename or size: " & same, passed)
                Check(If(same, File.Exists(session.Json) AndAlso File.Exists(ModPath(session, False, "optifabric.jar")), Not File.Exists(session.Json) AndAlso Not File.Exists(ModPath(session, False)) AndAlso Not File.Exists(session.Resource(False))), "all selected Fabric/OptiFine/OptiFabric files publish together or leave no new shared Mods: " & same, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "mode-mod-cancel"), cancel As New CancellationTokenSource()
            Dim selected = Components(session, all:=True), armed = False, probed = False, rejected = False
            Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
            For Each aliasName In aliases
                Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                    previous.Add(aliasName, algorithm.GetType())
                End Using
            Next
            Try
                CopyCancellationSha1.OnHash = Sub()
                    If armed AndAlso File.Exists(ModPath(session, False, "optifine.jar")) AndAlso Not File.Exists(ModPath(session, False)) Then
                        probed = True : cancel.Cancel()
                    End If
                End Sub
                CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                Try
                    Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, token:=cancel.Token,
                        progress:=New Progress(Sub(value)
                            If value.Stage = "完成安装" Then armed = True
                        End Sub))
                Catch ex As OperationCanceledException
                    rejected = True
                End Try
            Finally
                CopyCancellationSha1.OnHash = Nothing
                For Each pair In previous
                    CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
                Next
            End Try
            Check(probed AndAlso rejected AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Resource(False)) AndAlso Not File.Exists(ModPath(session, False, "optifine.jar")) AndAlso Not File.Exists(ModPath(session, False)) AndAlso session.Released(), "cancellation after the first shared mod move rolls back both Mods and resources before instance publication", passed)
        End Using
    End Function
    Private Async Function ParallelAsync(root As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "mode-parallel"), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(20))
            Dim selected = Components(session), ready As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            session.SetGlobal("0") : session.Fixture.Server.Gates(session.Route()) = gate
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token)
                Dim work = session.StartAsync(components:=selected, token:=linked.Token, progress:=New Progress(Sub(value)
                    If value.Stage = "组件安装完成" Then ready.TrySetResult(True)
                End Sub))
                Try
                    Dim winner = Await Task.WhenAny(ready.Task, work, Task.Delay(10000, timeout.Token))
                    Check(winner Is ready.Task AndAlso Not work.IsCompleted AndAlso Not File.Exists(ModPath(session, False)) AndAlso Not File.Exists(session.Json), "components finish in parallel with blocked assets while shared Mods remain private", passed)
                    cancel.Cancel()
                    Dim rejected = False
                    Try
                        Await work
                    Catch ex As OperationCanceledException When Not timeout.IsCancellationRequested
                        rejected = True
                    End Try
                    Check(rejected AndAlso session.Released() AndAlso Not File.Exists(ModPath(session, False)) AndAlso Not File.Exists(session.Resource(False)), "parallel cancellation drains component and asset work without publishing shared files", passed)
                Finally
                    cancel.Cancel() : gate.Set()
                End Try
            End Using
            Dim result = Await session.StartAsync(components:=selected, token:=timeout.Token)
            Check(result.NetworkBytes = session.Fixture.Server.Files(session.Route()).Length AndAlso File.Exists(ModPath(session, False)) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "parallel retry reuses checked client, libraries and Mods while following PCL shared mode", passed)
        End Using
    End Function
End Module
