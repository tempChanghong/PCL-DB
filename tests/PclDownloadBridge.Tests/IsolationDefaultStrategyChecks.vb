Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Security.Cryptography
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module IsolationDefaultStrategyChecks
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
    Private Sub SetKind(session As IsolationResourceChecks.Session, kind As String)
        Dim metadata = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
        metadata("type") = kind
        session.Fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
        Using hash = SHA1.Create()
            session.Fixture.Entry.Sha1 = BitConverter.ToString(hash.ComputeHash(session.Fixture.Server.Files("/version"))).Replace("-", "").ToLowerInvariant()
        End Using
        session.Fixture.Entry.Kind = kind
    End Sub
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each invalid In {Integer.MinValue, -1, 5, Integer.MaxValue}
            Using session As New IsolationResourceChecks.Session(root, "default-invalid-" & invalid)
                Dim rejected = False
                Try
                    Await session.StartAsync(newInstanceIsolationStrategy:=invalid)
                Catch ex As ArgumentOutOfRangeException When ex.ParamName = "newInstanceIsolationStrategy"
                    rejected = True
                End Try
                Check(rejected AndAlso session.Fixture.Server.Requests.Count = 0 AndAlso Not Directory.Exists(session.Root), "invalid new-instance defaults fail before any root or network activity: " & invalid, passed)
            End Using
        Next
        For Each kind In {"release", "snapshot", "old_alpha"}
            For Each withComponents In {False, True}
                For strategy = 0 To 4
                    Dim label = kind & "-" & withComponents & "-" & strategy
                    Using session As New IsolationResourceChecks.Session(root, "default-matrix-" & label)
                        session.SetGlobal("invalid")
                        Dim globalBytes = File.ReadAllBytes(session.GlobalPath)
                        IsolationResourceChecks.Configure(session.Fixture, mapped:=kind = "old_alpha")
                        SetKind(session, kind)
                        Dim components = If(withComponents, IsolationModeChecks.Components(session), Nothing)
                        Dim nonRelease = kind = "old_alpha" OrElse (kind = "snapshot" AndAlso Not withComponents)
                        Dim isolated = strategy = 4 OrElse ((strategy = 1 OrElse strategy = 3) AndAlso withComponents) OrElse ((strategy = 2 OrElse strategy = 3) AndAlso nonRelease)
                        Await session.StartAsync(components:=components, newInstanceIsolationStrategy:=strategy)
                        Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = isolated.ToString() AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes), "bridge defaults reuse pinned PCL classification and freeze only the new instance: " & label, passed)
                        Check(File.Exists(session.Json) AndAlso File.Exists(session.ObjectFile()) AndAlso session.Released(), "modern and remapped defaults publish a complete instance with shared objects: " & label, passed)
                        If kind = "old_alpha" Then Check(File.Exists(session.Resource(isolated)) AndAlso Not File.Exists(session.Resource(Not isolated)), "default strategy places legacy resources only in its resolved game directory: " & label, passed)
                        If withComponents Then
                            Dim instance = Path.GetDirectoryName(session.Json)
                            Check(File.Exists(Path.Combine(If(isolated, instance, session.Root), "mods", "fabric-api.jar")) AndAlso Not File.Exists(Path.Combine(If(isolated, session.Root, instance), "mods", "fabric-api.jar")), "default strategy routes component Mods to exactly one directory: " & label, passed)
                        End If
                    End Using
                Next
            Next
        Next
        Using session As New IsolationResourceChecks.Session(root, "default-metadata-kind")
            SetKind(session, "snapshot") : session.Fixture.Entry.Kind = "release"
            Await session.StartAsync(newInstanceIsolationStrategy:=2)
            Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "True", "new defaults use verified metadata type instead of a stale catalogue classification", passed)
        End Using
        For Each mode In {InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled}
            For Each strategy In {0, 4}
                Using session As New IsolationResourceChecks.Session(root, "default-explicit-" & mode & "-" & strategy)
                    session.SetGlobal("invalid")
                    Await session.StartAsync(isolation:=mode, newInstanceIsolationStrategy:=strategy)
                    Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = (mode = InstanceIsolationMode.Enabled).ToString(), "explicit API modes take priority over the bridge default: " & mode & "-" & strategy, passed)
                End Using
            Next
        Next
        Using session As New IsolationResourceChecks.Session(root, "default-null-follow")
            session.SetGlobal("0")
            Await session.StartAsync(components:=IsolationModeChecks.Components(session), newInstanceIsolationStrategy:=Nothing)
            Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "an absent bridge default keeps the original follow-PCL behavior", passed)
        End Using
        For Each kind In {"v2", "legacy", "mods", "global"}
            For Each isolated In {False, True}
                Using session As New IsolationResourceChecks.Session(root, "default-repair-" & kind & "-" & isolated)
                    Await session.SeedAsync()
                    session.SetGlobal(If(isolated, "4", "0"))
                    Select Case kind
                        Case "v2" : session.SetIni("VersionArgumentIndieV2:" & isolated.ToString() & vbCrLf & "VersionArgumentIndie:" & If(isolated, "2", "1"), Encoding.Unicode)
                        Case "legacy" : session.SetIni("VersionArgumentIndie:" & If(isolated, "1", "2"))
                        Case "mods"
                            session.SetGlobal("0") : session.SetIni(Nothing)
                            If isolated Then session.Put(Path.Combine(Path.GetDirectoryName(session.Json), "mods", "user.jar"), session.Bad)
                        Case "global" : session.SetIni("Unrelated:keep")
                    End Select
                    session.Put(session.Resource(isolated), session.Bad) : session.Put(session.Resource(Not isolated), session.Bad)
                    Dim iniBytes = If(File.Exists(session.Ini), File.ReadAllBytes(session.Ini), Nothing), jsonBytes = File.ReadAllBytes(session.Json), globalBytes = File.ReadAllBytes(session.GlobalPath)
                    Dim result = Await session.StartAsync(repair:=True, isolation:=If(isolated, InstanceIsolationMode.Disabled, InstanceIsolationMode.Enabled), newInstanceIsolationStrategy:=If(isolated, 0, 4))
                    Check(If(iniBytes Is Nothing, Not File.Exists(session.Ini), File.ReadAllBytes(session.Ini).SequenceEqual(iniBytes)) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes), "repair ignores new defaults and preserves saved, legacy, inferred or global preferences byte-for-byte: " & kind & "-" & isolated, passed)
                    Check(Not File.ReadAllBytes(session.Resource(isolated)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(Not isolated)).SequenceEqual(session.Bad) AndAlso result.NetworkBytes = 0 AndAlso session.Released(), "repair keeps its original target despite opposing bridge and explicit choices: " & kind & "-" & isolated, passed)
                End Using
            Next
        Next
        For Each strategy In {0, 4}
            Using session As New IsolationResourceChecks.Session(root, "default-cancel-" & strategy), cancel As New CancellationTokenSource()
                session.SetGlobal(If(strategy = 0, "4", "0"))
                Dim globalBytes = File.ReadAllBytes(session.GlobalPath), components = IsolationModeChecks.Components(session), rejected = False
                Try
                    Await session.StartAsync(components:=components, newInstanceIsolationStrategy:=strategy, token:=cancel.Token, progress:=New Progress(Sub(value)
                        If value.Stage = "完成安装" Then cancel.Cancel()
                    End Sub))
                Catch ex As OperationCanceledException
                    rejected = True
                End Try
                Check(rejected AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Ini) AndAlso Not File.Exists(session.Resource(False)) AndAlso Not File.Exists(Path.Combine(session.Root, "mods", "fabric-api.jar")) AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso session.Released(), "cancellation with a bridge default leaves settings and shared payloads unpublished: " & strategy, passed)
                Dim result = Await session.StartAsync(components:=components, newInstanceIsolationStrategy:=strategy)
                Check(result.NetworkBytes = 0 AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = (strategy = 4).ToString() AndAlso session.Released(), "retry reuses verified bytes and atomically publishes the captured bridge default: " & strategy, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "default-shared-conflict")
            session.SetGlobal("4")
            Dim components = IsolationModeChecks.Components(session), sharedMod = Path.Combine(session.Root, "mods", "fabric-api.jar"), rejected = False
            session.Put(sharedMod, session.Bad)
            Try
                Await session.StartAsync(components:=components, newInstanceIsolationStrategy:=0)
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso File.ReadAllBytes(sharedMod).SequenceEqual(session.Bad) AndAlso Not File.Exists(session.Json) AndAlso Not session.Fixture.Server.Requests.ContainsKey("/api"), "the shared bridge default retains conflict preflight before component payload downloads", passed)
        End Using
        Return passed
    End Function
End Module
