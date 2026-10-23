Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Security.Cryptography
Imports System.Diagnostics
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module IsolationResourceChecks
    Private ReadOnly Content As Byte() = Encoding.UTF8.GetBytes("legacy resource bytes")
    Private Const Name As String = "sounds/微风.ogg"
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Friend Sub Configure(fixture As InstallationChecks.InstallFixture, Optional objects As JObject = Nothing, Optional mapped As Boolean = True)
        If objects Is Nothing Then objects = New JObject From {{Name, New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}}
        Dim index As New JObject From {{"virtual", mapped}, {"map_to_resources", mapped}, {"objects", objects}}
        Dim bytes = Encoding.UTF8.GetBytes(index.ToString()), metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        fixture.Server.Files("/index") = bytes
        fixture.Server.Files("/" & Hash(Content).Substring(0, 2) & "/" & Hash(Content)) = Content
        metadata("assetIndex")("sha1") = Hash(bytes) : metadata("assetIndex")("size") = bytes.Length
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString()) : fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
    End Sub
    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class
    Private Class InlineProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Friend Class Session
        Implements IDisposable
        Friend ReadOnly Fixture As InstallationChecks.InstallFixture
        Friend ReadOnly PclBase As String
        Friend ReadOnly GlobalPath As String
        Friend ReadOnly Ini As String
        Friend ReadOnly Json As String
        Friend ReadOnly Jar As String
        Friend ReadOnly Bad As Byte()
        Friend ReadOnly Root As String
        Friend Sub New(testRoot As String, label As String, Optional mc As String = "1.20.1")
            Fixture = New InstallationChecks.InstallFixture(testRoot, "isolation-" & label, mc)
            Root = Fixture.Root : PclBase = Root & "-pcl"
            GlobalPath = Path.Combine(PclBase, "PCL", "Setup.ini")
            Ini = Path.Combine(Root, "versions", "legacy", "PCL", "Setup.ini")
            Json = Path.Combine(Root, "versions", "legacy", "legacy.json") : Jar = Path.ChangeExtension(Json, ".jar")
            Bad = DirectCast(Content.Clone(), Byte()) : Bad(0) = Bad(0) Xor CByte(1)
            Configure(Fixture)
        End Sub
        Friend Sub SetGlobal(value As String)
            Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath))
            If value Is Nothing Then
                If File.Exists(GlobalPath) Then File.Delete(GlobalPath)
            Else
                File.WriteAllText(GlobalPath, "LaunchArgumentIndieV2:" & value & vbCrLf & "UnknownGlobal:保留" & vbCrLf, New UTF8Encoding(False))
            End If
        End Sub
        Friend Sub SetIni(text As String, Optional codec As Encoding = Nothing)
            If text Is Nothing Then
                If File.Exists(Ini) Then File.Delete(Ini)
            Else
                Directory.CreateDirectory(Path.GetDirectoryName(Ini))
                codec = If(codec, New UTF8Encoding(False))
                File.WriteAllBytes(Ini, codec.GetPreamble().Concat(codec.GetBytes(text & vbCrLf & "UserSetting:保留" & vbCrLf)).ToArray())
            End If
        End Sub
        Friend Function Resource(isolated As Boolean, Optional name As String = Nothing) As String
            Return Path.Combine(If(isolated, Path.Combine(Root, "versions", "legacy"), Root), "resources", If(name, IsolationResourceChecks.Name))
        End Function
        Friend Function ObjectFile() As String
            Return Path.Combine(Root, "assets", "objects", Hash(Content).Substring(0, 2), Hash(Content))
        End Function
        Friend Function Route() As String
            Return "/" & Hash(Content).Substring(0, 2) & "/" & Hash(Content)
        End Function
        Friend Sub Put(path As String, bytes As Byte())
            Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
        End Sub
        Friend Function StartAsync(Optional repair As Boolean = False, Optional progress As IProgress(Of TransferProgress) = Nothing, Optional token As CancellationToken = Nothing, Optional components As ComponentSelection = Nothing, Optional isolation As InstanceIsolationMode = InstanceIsolationMode.FollowPcl, Optional newInstanceIsolationStrategy As Integer? = Nothing) As Task(Of InstallResult)
            Return New MinecraftInstaller(New Policy(Fixture.Server.Url), PclBase, newInstanceIsolationStrategy).InstallAsync(Fixture.Entry, Root, "legacy", {}, progress, token, repair, 4, components:=components, isolationMode:=isolation)
        End Function
        Friend Async Function SeedAsync() As Task
            SetGlobal("0") : Await StartAsync()
        End Function
        Friend Function Released() As Boolean
            Dim staging = Path.Combine(Root, ".vanilla-downloader", "staging")
            If Directory.Exists(staging) AndAlso Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any() Then Return False
            Using hold As New FileStream(Path.Combine(Root, ".vanilla-downloader", "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Return True
            End Using
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Fixture.Dispose()
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-isolation-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-default" Then Await DefaultAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-strategies" Then Await StrategiesAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-repair" Then Await RepairAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-paths" Then Await PathsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-conflicts" Then Await ConflictsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-changes" Then Await ChangesAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-parallel" Then Await ParallelAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-guard" Then Await GuardAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-settings" Then Await SettingsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-links" Then Await LinksAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-recovery" Then Await RecoveryAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-readonly" Then Await ReadOnlyResourceAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-isolation-component-path" Then Await ComponentPathAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function DefaultAsync(root As String, passed As List(Of String)) As Task
        Using session As New Session(root, "default")
            Dim unpublished = False, snapshots As New List(Of TransferProgress)
            Dim progress As New InlineProgress(Sub(value)
                SyncLock snapshots
                    snapshots.Add(value)
                End SyncLock
                If value.Phase = "commit" Then unpublished = Not Directory.Exists(Path.GetDirectoryName(session.Json)) AndAlso Not File.Exists(session.Resource(False))
            End Sub)
            Await session.StartAsync(progress:=progress)
            Check(File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content) AndAlso Not File.Exists(session.Resource(False)), "new legacy instance follows PCL's default isolation and publishes resources inside the instance", passed)
            Check(unpublished AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "True" AndAlso Not File.Exists(session.GlobalPath), "isolated legacy payload and frozen per-instance setting appear only at final publication", passed)
            Check(File.ReadAllBytes(session.ObjectFile()).SequenceEqual(Content) AndAlso File.ReadAllBytes(Path.Combine(session.Root, "assets", "virtual", "legacy", Name)).SequenceEqual(Content) AndAlso session.Fixture.Server.Requests(session.Route()) = 1, "object, virtual and remapped aliases use one verified HTTP object while retaining shared assets", passed)
            Dim last = snapshots.Last(Function(value) value.PhaseFractions IsNot Nothing)
            Check(last.PhaseFractions("files") = 1 AndAlso last.PhaseCompleted = last.PhaseTotal AndAlso last.Completed = last.Total AndAlso session.Released(), "remap copy contributes to completed parallel file progress without leaking private payloads", passed)
            Dim iniBytes = File.ReadAllBytes(session.Ini), jsonBytes = File.ReadAllBytes(session.Json)
            Dim repair = Await session.StartAsync(True)
            Check(repair.NetworkBytes = 0 AndAlso File.ReadAllBytes(session.Ini).SequenceEqual(iniBytes) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes), "repeat isolated repair reuses all checked files and preserves PCL settings and metadata", passed)
        End Using
    End Function
    Private Async Function StrategiesAsync(root As String, passed As List(Of String)) As Task
        For strategy = 0 To 4
            For Each kind In {"release", "snapshot", "old_beta"}
                Using session As New Session(root, "strategy-" & strategy & "-" & kind)
                    session.SetGlobal(strategy.ToString())
                    Dim metadata = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
                    metadata("type") = kind
                    session.Fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString()) : session.Fixture.Entry.Sha1 = Hash(session.Fixture.Server.Files("/version"))
                    Dim globalBytes = File.ReadAllBytes(session.GlobalPath), expected = strategy = 4 OrElse ((strategy = 2 OrElse strategy = 3) AndAlso kind <> "release")
                    Await session.StartAsync()
                    Check(File.ReadAllBytes(session.Resource(expected)).SequenceEqual(Content) AndAlso Not File.Exists(session.Resource(Not expected)) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = expected.ToString(), "new remapped vanilla instance follows PCL global strategy: " & strategy & "/" & kind, passed)
                    Check(File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso session.Fixture.Server.Requests(session.Route()) = 1 AndAlso session.Released(), "global strategy is read only and remapped resources reuse one shared object: " & strategy & "/" & kind, passed)
                End Using
            Next
        Next
    End Function
    Private Async Function RepairAsync(root As String, passed As List(Of String)) As Task
        Dim cases As New Dictionary(Of String, Tuple(Of String, Boolean)) From {
            {"v2-on", Tuple.Create("VersionArgumentIndieV2:True" & vbCrLf & "VersionArgumentIndie:2", True)},
            {"v2-off", Tuple.Create("VersionArgumentIndieV2:False" & vbCrLf & "VersionArgumentIndie:1", False)},
            {"legacy-on", Tuple.Create("VersionArgumentIndie:1", True)}, {"legacy-off", Tuple.Create("VersionArgumentIndie:2", False)},
            {"mods", Tuple.Create(CType(Nothing, String), True)}, {"saves", Tuple.Create("VersionArgumentIndie:0", True)},
            {"empty-mods", Tuple.Create(CType(Nothing, String), False)}, {"global-on", Tuple.Create("Unrelated:1", True)},
            {"off-with-mods", Tuple.Create("VersionArgumentIndieV2:False", False)},
            {"duplicate-v2", Tuple.Create("VersionArgumentIndieV2:True" & vbCrLf & "VersionArgumentIndieV2:False", False)},
            {"equals-ignored", Tuple.Create("VersionArgumentIndieV2:True" & vbCrLf & "VersionArgumentIndieV2=False", True)},
            {"duplicate-legacy", Tuple.Create("VersionArgumentIndie:2" & vbCrLf & "VersionArgumentIndie:1", True)},
            {"utf16", Tuple.Create("VersionArgumentIndieV2:True", True)}, {"readonly", Tuple.Create("VersionArgumentIndieV2:False", False)}}
        For Each item In cases
            Using session As New Session(root, "repair-" & item.Key)
                Await session.SeedAsync()
                session.SetGlobal(If(item.Key = "global-on", "4", "0"))
                session.SetIni(item.Value.Item1, If(item.Key = "utf16", Encoding.Unicode, Nothing))
                Dim instance = Path.GetDirectoryName(session.Json)
                If item.Key = "mods" OrElse item.Key = "off-with-mods" Then session.Put(Path.Combine(instance, "mods", "user.jar"), Content)
                If item.Key = "saves" Then Directory.CreateDirectory(Path.Combine(instance, "saves", "世界"))
                If item.Key = "empty-mods" Then Directory.CreateDirectory(Path.Combine(instance, "mods"))
                Dim expected = item.Value.Item2
                session.Put(session.Resource(expected), session.Bad) : session.Put(session.Resource(Not expected), session.Bad)
                Dim iniBytes = If(File.Exists(session.Ini), File.ReadAllBytes(session.Ini), Nothing), jsonBytes = File.ReadAllBytes(session.Json), globalBytes = File.ReadAllBytes(session.GlobalPath)
                If item.Key = "readonly" Then File.SetAttributes(session.Ini, FileAttributes.ReadOnly)
                Try
                    Dim result = Await session.StartAsync(True)
                    Check(File.ReadAllBytes(session.Resource(expected)).SequenceEqual(Content) AndAlso File.ReadAllBytes(session.Resource(Not expected)).SequenceEqual(session.Bad), "repair targets the resolved game directory and preserves the other legacy location: " & item.Key, passed)
                    Check(If(iniBytes Is Nothing, Not File.Exists(session.Ini), File.ReadAllBytes(session.Ini).SequenceEqual(iniBytes)) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes), "repair preserves existing isolation keys, missing settings, encoding and global preferences: " & item.Key, passed)
                    Check(result.NetworkBytes = 0 AndAlso session.Fixture.Server.Requests(session.Route()) = 1 AndAlso session.Released(), "legacy repair uses the existing canonical object and joins all workers before cleanup: " & item.Key, passed)
                Finally
                    If item.Key = "readonly" Then File.SetAttributes(session.Ini, FileAttributes.Normal)
                End Try
            End Using
        Next
    End Function
    Private Async Function PathsAsync(root As String, passed As List(Of String)) As Task
        Dim badNames = {"../escape", "sounds/../../legacy.json", "/absolute", "C:/bad", "sounds//empty", "sounds/./same", "..\escape", "NUL.ogg", "trailing./file", "sounds/file. ", "file:stream", "CON/file"}
        For i = 0 To badNames.Length - 1
            Using session As New Session(root, "bad-name-" & i)
                Configure(session.Fixture, New JObject From {{badNames(i), New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}})
                Dim rejected = False
                Try
                    Await session.StartAsync()
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso session.Fixture.Server.Requests.Keys.All(Function(path) path = "/version" OrElse path = "/index") AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)), "unsafe logical resource name is rejected before payload downloads or instance creation: " & badNames(i), passed)
            End Using
        Next
        For Each mode In {"ancestor", "case-conflict", "library-alias", "library-ancestor", "directory", "parent-file", "shared-content"}
            Using session As New Session(root, "bad-layout-" & mode)
                Dim objects As New JObject From {{Name, New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}}
                If mode = "ancestor" Then objects("sounds") = New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}
                If mode = "case-conflict" Then objects(Name.ToUpperInvariant()) = New JObject From {{"hash", New String("b"c, 40)}, {"size", Content.Length}}
                Configure(session.Fixture, objects)
                If mode.StartsWith("library", StringComparison.Ordinal) Then
                    Dim metadata = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
                    Dim relative = "../versions/legacy/resources/" & If(mode = "library-alias", Name, "sounds")
                    metadata("libraries") = New JArray(New JObject From {{"name", "fixture:collision:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", relative}, {"url", session.Fixture.Server.Url & "/unwanted"}, {"sha1", Hash(Content)}, {"size", Content.Length}}}}}})
                    session.Fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString()) : session.Fixture.Entry.Sha1 = Hash(session.Fixture.Server.Files("/version"))
                End If
                Dim marker As String = Nothing
                If mode = "directory" OrElse mode = "parent-file" OrElse mode = "shared-content" Then
                    session.SetGlobal("0")
                    marker = If(mode = "parent-file", Path.GetDirectoryName(session.Resource(False)), If(mode = "directory", Path.Combine(session.Resource(False), "user.txt"), session.Resource(False)))
                    session.Put(marker, session.Bad)
                End If
                Dim rejected = False
                Try
                    Await session.StartAsync()
                Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
                    rejected = True
                End Try
                Check(rejected AndAlso session.Fixture.Server.Requests.Keys.All(Function(path) path = "/version" OrElse path = "/index") AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)), "resource file/directory and cross-branch destination conflicts stop the complete plan: " & mode, passed)
                If marker IsNot Nothing Then Check(File.ReadAllBytes(marker).SequenceEqual(session.Bad), "resource preflight preserves conflicting user files: " & mode, passed)
            End Using
        Next
        Using session As New Session(root, "unmapped-control")
            session.SetGlobal("invalid")
            Configure(session.Fixture, New JObject From {{"../unused-logical-name", New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}}, False)
            Await session.StartAsync()
            Check(File.Exists(session.Json) AndAlso File.Exists(session.ObjectFile()) AndAlso Not File.Exists(session.Ini) AndAlso Not Directory.Exists(Path.Combine(session.Root, "resources")), "modern object-only installations do not initialize isolation preferences or interpret unused logical paths", passed)
        End Using
    End Function
    Private Async Function ConflictsAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"cancel", "instance", "late-resource"}
            Using session As New Session(root, "publication-" & mode), cancel As New CancellationTokenSource()
                session.SetGlobal("0")
                Const extra As String = "sounds/second.ogg"
                Configure(session.Fixture, New JObject From {{Name, New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}, {extra, New JObject From {{"hash", Hash(Content)}, {"size", Content.Length}}}})
                session.Put(session.Resource(False), Content)
                Dim modified = File.GetLastWriteTimeUtc(session.Resource(False)), globalBytes = File.ReadAllBytes(session.GlobalPath)
                Dim reached = False, failure As Exception = Nothing, foreign = Path.Combine(Path.GetDirectoryName(session.Json), "user.txt")
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase <> "commit" OrElse reached Then Return
                    reached = True
                    If mode = "cancel" Then cancel.Cancel()
                    If mode = "instance" Then session.Put(foreign, session.Bad)
                    If mode = "late-resource" Then session.Put(session.Resource(False, extra), session.Bad)
                End Sub)
                Try
                    Await session.StartAsync(progress:=progress, token:=cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(reached AndAlso If(mode = "cancel", TypeOf failure Is OperationCanceledException, TypeOf failure Is IOException) AndAlso Not File.Exists(session.Json), "failed final publication never exposes a partial legacy instance: " & mode, passed)
                Check(File.ReadAllBytes(session.Resource(False)).SequenceEqual(Content) AndAlso File.GetLastWriteTimeUtc(session.Resource(False)) = modified, "failed shared publication preserves an existing matching resource and its modification time: " & mode, passed)
                Check(If(mode = "late-resource", File.ReadAllBytes(session.Resource(False, extra)).SequenceEqual(session.Bad), Not File.Exists(session.Resource(False, extra))) AndAlso (mode <> "instance" OrElse File.ReadAllBytes(foreign).SequenceEqual(session.Bad)), "shared rollback removes only newly published resources and keeps external conflicts: " & mode, passed)
                Check(session.Released() AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes), "publication failure joins workers, cleans its payload and preserves global preferences: " & mode, passed)
                If mode = "instance" Then
                    File.Delete(foreign) : Directory.Delete(Path.GetDirectoryName(session.Json))
                End If
                If mode = "late-resource" Then File.Delete(session.Resource(False, extra))
                Dim result = Await session.StartAsync()
                Check(result.NetworkBytes = 0 AndAlso File.ReadAllBytes(session.Resource(False, extra)).SequenceEqual(Content) AndAlso Not File.Exists(session.Resource(True)) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "resolved publication conflict reuses checked content and leaves shared remaps outside the instance: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function ChangesAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"toggle", "deleted", "created", "global", "evidence"}
            Using session As New Session(root, "change-" & mode)
                Await session.SeedAsync()
                session.SetGlobal(If(mode = "created" OrElse mode = "global", "4", "0"))
                session.SetIni(If(mode = "toggle" OrElse mode = "deleted", "VersionArgumentIndieV2:True", Nothing))
                Dim initialIni = If(File.Exists(session.Ini), File.ReadAllBytes(session.Ini), Nothing), initialGlobal = File.ReadAllBytes(session.GlobalPath)
                Dim expected = mode <> "evidence", reached = False, failure As Exception = Nothing
                session.Put(session.Resource(expected), session.Bad) : session.Put(session.Resource(Not expected), session.Bad)
                Dim jsonBytes = File.ReadAllBytes(session.Json), userMod = Path.Combine(Path.GetDirectoryName(session.Json), "mods", "external.jar")
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase <> "commit" OrElse reached Then Return
                    reached = True
                    If mode = "toggle" OrElse mode = "created" Then session.SetIni("VersionArgumentIndieV2:False")
                    If mode = "deleted" Then session.SetIni(Nothing)
                    If mode = "global" Then session.SetGlobal("0")
                    If mode = "evidence" Then session.Put(userMod, Content)
                End Sub)
                Try
                    Await session.StartAsync(True, progress)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(reached AndAlso TypeOf failure Is InvalidDataException AndAlso File.ReadAllBytes(session.Resource(expected)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(Not expected)).SequenceEqual(session.Bad), "changed isolation inputs stop repair before publishing remaps to either game directory: " & mode, passed)
                Check(File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso session.Released() AndAlso (mode <> "deleted" OrElse Not File.Exists(session.Ini)) AndAlso (mode <> "evidence" OrElse File.ReadAllBytes(userMod).SequenceEqual(Content)), "isolation conflict preserves external state, JSON and cleanup: " & mode, passed)
                If initialIni Is Nothing Then session.SetIni(Nothing) Else File.WriteAllBytes(session.Ini, initialIni)
                File.WriteAllBytes(session.GlobalPath, initialGlobal)
                If mode = "evidence" Then File.Delete(userMod)
                Dim result = Await session.StartAsync(True)
                Check(result.NetworkBytes = 0 AndAlso File.ReadAllBytes(session.Resource(expected)).SequenceEqual(Content) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes), "repair retries after isolation conflict without refetching verified objects: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function ParallelAsync(root As String, passed As List(Of String)) As Task
        Using session As New Session(root, "parallel"), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), safety As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            session.SetGlobal("0")
            session.Fixture.Server.Gates(session.Route()) = gate
            Dim selection = session.Fixture.Fabric(), ready As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim progress As New InlineProgress(Sub(value)
                If value.Stage = "组件安装完成" Then ready.TrySetResult(True)
            End Sub)
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, safety.Token)
                Dim work = session.StartAsync(progress:=progress, token:=linked.Token, components:=selection, isolation:=InstanceIsolationMode.Enabled)
                Try
                    Dim winner = Await Task.WhenAny(ready.Task, work, Task.Delay(10000, safety.Token))
                    Check(winner Is ready.Task AndAlso Not work.IsCompleted AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)) AndAlso Not File.Exists(session.Resource(False)), "component branch finishes while a shared object is blocked and no isolated instance is published", passed)
                    cancel.Cancel()
                    Dim cancelled = False
                    Try
                        Await work
                    Catch ex As OperationCanceledException When Not safety.IsCancellationRequested
                        cancelled = True
                    End Try
                    Check(cancelled AndAlso session.Released() AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)) AndAlso Not File.Exists(session.Resource(True)), "parallel cancellation joins all branches and removes staged remaps and component settings", passed)
                Finally
                    cancel.Cancel() : gate.Set()
                End Try
            End Using
            Dim result = Await session.StartAsync(components:=selection, token:=safety.Token, isolation:=InstanceIsolationMode.Enabled)
            Check(result.NetworkBytes = Content.Length AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content) AndAlso Not File.Exists(session.Resource(False)) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "True", "retry preserves existing component isolation policy and publishes staged resources with the instance", passed)
        End Using
    End Function
    Private Async Function GuardAsync(root As String, passed As List(Of String)) As Task
        For Each cancelled In {False, True}
            Using session As New Session(root, "guard-" & cancelled), cancel As New CancellationTokenSource()
                Await session.SeedAsync() : session.SetIni("VersionArgumentIndieV2:True") : session.Put(session.Resource(True), session.Bad)
                Dim iniBytes = File.ReadAllBytes(session.Ini), jsonBytes = File.ReadAllBytes(session.Json), armed = False, probed As Integer = 0, blocked = False, readable = False
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase = "commit" Then armed = True
                End Sub)
                CopyCancellationSha1.OnHash = Sub()
                    If Not armed OrElse Interlocked.CompareExchange(probed, 1, 0) <> 0 Then Return
                    Using reader = File.OpenRead(session.Ini)
                        readable = True
                    End Using
                    Try
                        Using writer As New FileStream(session.Ini, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
                        End Using
                    Catch ex As IOException
                        blocked = True
                    End Try
                    If cancelled Then cancel.Cancel()
                End Sub
                Dim failure As Exception = Nothing
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Try
                        Await session.StartAsync(True, progress, cancel.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each item In previous
                        CryptoConfig.AddAlgorithm(item.Value, item.Key)
                    Next
                End Try
                Check(armed AndAlso probed = 1 AndAlso blocked AndAlso readable, "isolation setting guard allows readers and blocks writers during actual remap publication: " & cancelled, passed)
                Check(If(cancelled, TypeOf failure Is OperationCanceledException AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad), failure Is Nothing AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content)), "guarded remap publication preserves cancellation and successful repair outcomes: " & cancelled, passed)
                Using writer As New FileStream(session.Ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    Check(File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso session.Released(), "repair releases settings guard and root lock after success or cancellation: " & cancelled, passed)
                End Using
                Dim result = Await session.StartAsync(True)
                Check(result.NetworkBytes = 0 AndAlso File.ReadAllBytes(session.Ini).SequenceEqual(iniBytes) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content), "guarded remap retry reuses verified content and preserves exact preferences: " & cancelled, passed)
            End Using
        Next
    End Function
    Private Async Function SettingsAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"bad-v2", "bad-legacy", "bad-global", "locked", "blocked-parent"}
            Using session As New Session(root, "settings-" & mode)
                Await session.SeedAsync()
                session.Put(session.Resource(False), session.Bad)
                If mode = "bad-v2" Then session.SetIni("VersionArgumentIndieV2:not-a-bool")
                If mode = "bad-legacy" Then session.SetIni("VersionArgumentIndie:not-an-int")
                If mode = "bad-global" Then
                    session.SetIni(Nothing) : session.SetGlobal("not-an-int")
                End If
                Dim blocker As String = Nothing
                If mode = "blocked-parent" Then
                    session.SetIni(Nothing) : blocker = Path.GetDirectoryName(session.Ini)
                    Directory.Delete(blocker) : File.WriteAllBytes(blocker, session.Bad)
                End If
                Dim before = session.Fixture.Server.Requests.Where(Function(item) item.Key <> "/version" AndAlso item.Key <> "/index").Sum(Function(item) item.Value)
                Dim settingsBytes = If(File.Exists(session.Ini), File.ReadAllBytes(session.Ini), Nothing), rejected = False
                Using hold As FileStream = If(mode = "locked", New FileStream(session.Ini, FileMode.Open, FileAccess.Read, FileShare.None), Nothing)
                    Try
                        Await session.StartAsync(True)
                    Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
                        rejected = True
                    End Try
                End Using
                Check(rejected AndAlso File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad) AndAlso session.Fixture.Server.Requests.Where(Function(item) item.Key <> "/version" AndAlso item.Key <> "/index").Sum(Function(item) item.Value) = before, "invalid or unavailable isolation preferences never silently select another resource directory: " & mode, passed)
                Check(session.Released() AndAlso If(settingsBytes Is Nothing, Not File.Exists(session.Ini), File.ReadAllBytes(session.Ini).SequenceEqual(settingsBytes)) AndAlso (blocker Is Nothing OrElse File.ReadAllBytes(blocker).SequenceEqual(session.Bad)), "isolation settings failure preserves user bytes, missing files and blockers: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function LinksAsync(root As String, passed As List(Of String)) As Task
        For Each late In {False, True}
            Using session As New Session(root, "link-" & late)
                session.SetGlobal("0")
                Dim link = Path.GetDirectoryName(session.Resource(False)), outside = session.Root & "-external", marker = Path.Combine(outside, "user.txt")
                session.Put(marker, session.Bad) : Directory.CreateDirectory(Path.GetDirectoryName(link))
                Dim makeLink As Action = Sub()
                    Dim allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                    If Not Path.GetFullPath(link).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFullPath(outside).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Junction fixture escaped its temporary root.")
                    Dim info As New ProcessStartInfo("cmd.exe", "/d /c mklink /J """ & link & """ """ & outside & """") With {.UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden, .RedirectStandardOutput = True, .RedirectStandardError = True}
                    Using process = Diagnostics.Process.Start(info)
                        Dim output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd()
                        process.WaitForExit()
                        If process.ExitCode <> 0 Then Throw New InvalidOperationException(output & errors)
                    End Using
                End Sub
                Dim reached = False, rejected = False
                If Not late Then makeLink()
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase = "commit" AndAlso Not reached Then
                        reached = True : makeLink()
                    End If
                End Sub)
                Try
                    Try
                        Await session.StartAsync(progress:=If(late, progress, Nothing))
                    Catch ex As IOException
                        rejected = True
                    End Try
                    Check(rejected AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)) AndAlso File.ReadAllBytes(marker).SequenceEqual(session.Bad) AndAlso Not File.Exists(Path.Combine(outside, Path.GetFileName(Name))), "resource junction at planning or final publication cannot redirect writes outside the game root: " & late, passed)
                    Check(session.Released() AndAlso If(late, reached, session.Fixture.Server.Requests.Keys.All(Function(path) path = "/version" OrElse path = "/index")), "junction rejection preserves atomic publication and worker cleanup: " & late, passed)
                Finally
                    If Directory.Exists(link) AndAlso (File.GetAttributes(link) And FileAttributes.ReparsePoint) <> 0 Then Directory.Delete(link)
                End Try
            End Using
        Next
    End Function
    Private Async Function RecoveryAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"locked-client", "cancel-after-resource"}
            Using session As New Session(root, mode), cancel As New CancellationTokenSource()
                Await session.SeedAsync() : session.SetIni("VersionArgumentIndieV2:True") : session.Put(session.Resource(True), session.Bad)
                Dim original = DirectCast(session.Fixture.Server.Files("/client").Clone(), Byte()) : original(0) = original(0) Xor CByte(1)
                File.WriteAllBytes(session.Jar, original)
                Dim iniBytes = File.ReadAllBytes(session.Ini), jsonBytes = File.ReadAllBytes(session.Json), armed = False
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase = "commit" Then armed = True
                End Sub)
                CopyCancellationSha1.OnHash = Sub()
                    If armed AndAlso mode = "cancel-after-resource" AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content) Then cancel.Cancel()
                End Sub
                Dim failure As Exception = Nothing
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Using hold As FileStream = If(mode = "locked-client", New FileStream(session.Jar, FileMode.Open, FileAccess.Read, FileShare.Read), Nothing)
                        Try
                            Await session.StartAsync(True, progress, cancel.Token)
                        Catch ex As Exception
                            failure = ex
                        End Try
                    End Using
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each item In previous
                        CryptoConfig.AddAlgorithm(item.Value, item.Key)
                    Next
                End Try
                Check(If(mode = "locked-client", TypeOf failure Is IOException, TypeOf failure Is OperationCanceledException) AndAlso File.ReadAllBytes(session.Jar).SequenceEqual(original) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content), "partial repair retains completed resource fixes but never replaces the client after failure or cancellation: " & mode, passed)
                Check(File.ReadAllBytes(session.Ini).SequenceEqual(iniBytes) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.Resource(False)).SequenceEqual(Content), "partial repair preserves metadata, isolation preference and the other resource location: " & mode, passed)
                Using writer As New FileStream(session.Ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    Check(session.Released(), "partial repair releases every settings and root lock before retry: " & mode, passed)
                End Using
                Dim result = Await session.StartAsync(True)
                Check(result.NetworkBytes = 0 AndAlso session.Fixture.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(session.Jar).SequenceEqual(session.Fixture.Server.Files("/client")), "partial repair retry reuses checked resources and the retained replacement client: " & mode, passed)
            End Using
        Next
        Using session As New Session(root, "bad-client-new")
            Dim original = session.Fixture.Server.Files("/client")
            Dim bad = DirectCast(original.Clone(), Byte()) : bad(0) = bad(0) Xor CByte(1)
            session.Fixture.Server.Files("/client") = bad
            Dim rejected = False
            Try
                Await session.StartAsync()
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)) AndAlso Not File.Exists(session.Resource(False)) AndAlso session.Released(), "client checksum failure cannot publish isolated remaps, settings or a partial instance", passed)
            Check(File.ReadAllBytes(session.ObjectFile()).SequenceEqual(Content), "failed new instance retains only verified shared objects for retry", passed)
            session.Fixture.Server.Files("/client") = original
            Dim result = Await session.StartAsync()
            Check(result.NetworkBytes = original.Length AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(Content), "new install retry downloads only its failed client and atomically publishes the remapped payload", passed)
        End Using
        Using session As New Session(root, "readonly-global")
            session.SetGlobal("0")
            Dim bytes = File.ReadAllBytes(session.GlobalPath)
            File.SetAttributes(session.GlobalPath, FileAttributes.ReadOnly)
            Try
                Await session.StartAsync()
                Check(File.ReadAllBytes(session.Resource(False)).SequenceEqual(Content) AndAlso Not File.Exists(session.Resource(True)), "read-only PCL global preferences can select shared legacy resources", passed)
                Check(File.ReadAllBytes(session.GlobalPath).SequenceEqual(bytes) AndAlso (File.GetAttributes(session.GlobalPath) And FileAttributes.ReadOnly) <> 0, "new installation preserves global preference bytes and read-only attributes", passed)
            Finally
                File.SetAttributes(session.GlobalPath, FileAttributes.Normal)
            End Try
        End Using
        Using session As New Session(root, "equals-global")
            session.SetGlobal("0")
            File.WriteAllText(session.GlobalPath, "LaunchArgumentIndieV2=0", New UTF8Encoding(False))
            Await session.StartAsync()
            Check(File.Exists(session.Resource(True)) AndAlso Not File.Exists(session.Resource(False)), "global equals-delimited text is ignored like pinned PCL IniGetContent rather than changing isolation", passed)
        End Using
    End Function
    Private Async Function ReadOnlyResourceAsync(root As String, passed As List(Of String)) As Task
        For Each repair In {False, True}
            Using session As New Session(root, "readonly-resource-" & repair)
                session.SetGlobal("0")
                If repair Then Await session.StartAsync()
                session.Put(session.Resource(False), Content)
                Dim modified = File.GetLastWriteTimeUtc(session.Resource(False))
                File.SetAttributes(session.Resource(False), FileAttributes.ReadOnly)
                Try
                    Await session.StartAsync(repair)
                    Check(File.ReadAllBytes(session.Resource(False)).SequenceEqual(Content) AndAlso File.GetLastWriteTimeUtc(session.Resource(False)) = modified AndAlso (File.GetAttributes(session.Resource(False)) And FileAttributes.ReadOnly) <> 0, "matching read-only user resources are reused without changing their bytes, time or attributes: " & repair, passed)
                    Check(Not File.Exists(session.Resource(True)) AndAlso session.Released(), "read-only private alias copies are removed before shared instance publication and cleanup: " & repair, passed)
                Finally
                    File.SetAttributes(session.Resource(False), FileAttributes.Normal)
                End Try
            End Using
        Next
        Using session As New Session(root, "readonly-object-rollback")
            session.SetGlobal("0") : session.Put(session.ObjectFile(), Content)
            File.SetAttributes(session.ObjectFile(), FileAttributes.ReadOnly)
            Dim foreign = Path.Combine(Path.GetDirectoryName(session.Json), "foreign.txt"), reached = False, rejected = False
            Dim progress As New InlineProgress(Sub(value)
                If value.Phase = "commit" AndAlso Not reached Then
                    reached = True : session.Put(foreign, session.Bad)
                End If
            End Sub)
            Try
                Try
                    Await session.StartAsync(progress:=progress)
                Catch ex As IOException
                    rejected = True
                End Try
                Check(reached AndAlso rejected AndAlso Not File.Exists(session.Resource(False)) AndAlso File.ReadAllBytes(foreign).SequenceEqual(session.Bad), "failed publication rolls back a newly created read-only shared alias while preserving the external instance", passed)
                Check(File.ReadAllBytes(session.ObjectFile()).SequenceEqual(Content) AndAlso (File.GetAttributes(session.ObjectFile()) And FileAttributes.ReadOnly) <> 0 AndAlso session.Released(), "read-only rollback keeps the original canonical object and its attributes and cleans private payloads", passed)
            Finally
                File.SetAttributes(session.ObjectFile(), FileAttributes.Normal)
            End Try
        End Using
    End Function
    Private Async Function ComponentPathAsync(root As String, passed As List(Of String)) As Task
        Using session As New Session(root, "component-path")
            Dim selection = session.Fixture.Fabric()
            Dim route = "/v2/versions/loader/" & session.Fixture.Entry.Id & "/fixture/profile/json"
            Dim profile = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files(route)))
            profile("libraries")(0)("downloads")("artifact")("path") = "../versions/legacy/resources/foreign.jar"
            session.Fixture.Server.Files(route) = Encoding.UTF8.GetBytes(profile.ToString())
            Dim rejected = False
            Try
                Await session.StartAsync(components:=selection)
            Catch ex As InvalidDataException
                rejected = True
            Catch ex As IOException
            End Try
            Console.WriteLine("COMPONENT_PATH rejected=" & rejected & " instanceCreated=" & Directory.Exists(Path.GetDirectoryName(session.Json)))
            Check(rejected AndAlso Not Directory.Exists(Path.GetDirectoryName(session.Json)), "component library paths cannot escape libraries and create the final isolated instance before publication", passed)
            Check(Not session.Fixture.Server.Requests.ContainsKey("/shared") AndAlso session.Released() AndAlso Not File.Exists(session.Resource(False)), "component namespace rejection happens before runtime payload fetch and cleans every private branch", passed)
        End Using
    End Function
End Module
