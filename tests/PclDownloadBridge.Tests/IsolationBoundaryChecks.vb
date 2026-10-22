Imports System.IO
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module IsolationBoundaryChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte()) As String
        Using sha As New SHA1CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
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
    Private Class HashProbe
        Implements IDisposable
        Private ReadOnly _previous As New Dictionary(Of String, Type)
        Friend Sub New(Optional creating As Action = Nothing, Optional hashing As Action = Nothing)
            For Each aliasName In {"SHA1", "System.Security.Cryptography.SHA1"}
                Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                    _previous.Add(aliasName, algorithm.GetType())
                End Using
            Next
            PublicationProbeSha1.OnCreate = creating : CopyCancellationSha1.OnHash = hashing
            CryptoConfig.AddAlgorithm(GetType(PublicationProbeSha1), _previous.Keys.ToArray())
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            PublicationProbeSha1.OnCreate = Nothing : CopyCancellationSha1.OnHash = Nothing
            For Each item In _previous
                CryptoConfig.AddAlgorithm(item.Value, {item.Key})
            Next
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-boundary-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-boundary-snapshot" Then Snapshot(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-boundary-mods" Then Await ModsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-boundary-failure" Then Await FailureAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-boundary-display" Then Await DisplayAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-boundary-settings" Then Await SettingsAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Sub Snapshot(root As String, passed As List(Of String))
        For Each algorithm In {"sha1", "md5", "none"}
            Dim stage = Path.Combine(root, "boundary-snapshot-" & algorithm), modFile = Path.Combine(stage, "mods", "fixture.jar")
            Dim good = Encoding.UTF8.GetBytes("checked mod bytes"), bad = Encoding.UTF8.GetBytes("changed mod bytes")
            Directory.CreateDirectory(Path.GetDirectoryName(modFile)) : File.WriteAllBytes(modFile, good)
            Dim item As New DownloadFile With {.RelativePath = "mods/fixture.jar", .Size = good.Length}
            If algorithm = "sha1" Then item.Sha1 = Hash(good)
            If algorithm = "md5" Then
                Using md5 As New MD5CryptoServiceProvider()
                    item.Md5 = BitConverter.ToString(md5.ComputeHash(good)).Replace("-", "").ToLowerInvariant()
                End Using
            End If
            Dim blocked As Integer = 0, changed As Integer = 0, result As List(Of DownloadFile) = Nothing
            Using probe As New HashProbe(creating:=Sub()
                Try
                    File.WriteAllBytes(modFile, bad) : changed += 1
                Catch ex As IOException
                    blocked += 1
                End Try
            End Sub)
                Dim method = GetType(MinecraftInstaller).Assembly.GetType("PclDownloadBridge.Core.SharedModsPublication").GetMethod("CheckedFiles", BindingFlags.Static Or BindingFlags.NonPublic)
                result = DirectCast(method.Invoke(Nothing, {stage, New List(Of DownloadFile) From {item}, CancellationToken.None}), List(Of DownloadFile))
            End Using
            Console.WriteLine("MOD_SNAPSHOT algorithm=" & algorithm & " changed=" & changed & " blocked=" & blocked)
            Check(changed = 0 AndAlso blocked > 0 AndAlso File.ReadAllBytes(modFile).SequenceEqual(good) AndAlso result.Single().Sha1 = Hash(good), "a shared Mod snapshot keeps validation and digest recording under the same read lease: " & algorithm, passed)
            Using writer As New FileStream(modFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(writer.Length = good.Length, "shared Mod snapshot releases its read lease: " & algorithm, passed)
            End Using
        Next
        For Each fault In {"checksum", "cancel"}
            Dim stage = Path.Combine(root, "boundary-snapshot-" & fault), modFile = Path.Combine(stage, "mods", "fixture.jar")
            Dim good = Encoding.UTF8.GetBytes("checked mod bytes"), bad = Encoding.UTF8.GetBytes("changed mod bytes")
            Directory.CreateDirectory(Path.GetDirectoryName(modFile)) : File.WriteAllBytes(modFile, If(fault = "checksum", bad, good))
            Dim item As New DownloadFile With {.RelativePath = "mods/fixture.jar", .Size = good.Length, .Sha1 = Hash(good)}, failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(hashing:=Sub()
                If fault = "cancel" Then cancel.Cancel()
            End Sub)
                Try
                    Dim method = GetType(MinecraftInstaller).Assembly.GetType("PclDownloadBridge.Core.SharedModsPublication").GetMethod("CheckedFiles", BindingFlags.Static Or BindingFlags.NonPublic)
                    method.Invoke(Nothing, {stage, New List(Of DownloadFile) From {item}, cancel.Token})
                Catch ex As TargetInvocationException
                    failure = ex.InnerException
                End Try
            End Using
            Check(If(fault = "checksum", TypeOf failure Is InvalidDataException, TypeOf failure Is OperationCanceledException), "a bad or cancelled shared Mod snapshot cannot become publication input: " & fault, passed)
            Using writer As New FileStream(modFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(writer.Length = good.Length, "a failed shared Mod snapshot releases its lease: " & fault, passed)
            End Using
        Next
    End Sub
    Private Async Function ModsAsync(root As String, passed As List(Of String)) As Task
        For Each existing In {False, True}
            For Each action In {"write", "delete", "rename", "cancel"}
                Using session As New IsolationResourceChecks.Session(root, "boundary-mod-" & existing & "-" & action), cancel As New CancellationTokenSource()
                    Dim selection = IsolationModeChecks.Components(session, all:=True)
                    Dim first = Path.Combine(session.Root, "mods", "optifine.jar"), second = Path.Combine(session.Root, "mods", "fabric-api.jar")
                    Dim original = session.Fixture.Server.Files("/opt"), stamp = New DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                    If existing Then
                        session.Put(first, original) : File.SetLastWriteTimeUtc(first, stamp)
                    End If
                    Dim privateFirst As String = Nothing, armed = False, probed = False, blocked = False, readable = False, cancelled = False, writableBeforeCommit = Not existing
                    Dim progress As New Progress(Sub(value)
                        If existing AndAlso value.Stage = "组件安装完成" Then
                            Using writer As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                                writableBeforeCommit = True
                            End Using
                        End If
                        If value.Stage <> "完成安装" Then Return
                        privateFirst = Directory.EnumerateFiles(Path.Combine(session.Root, ".vanilla-downloader", "staging"), "optifine.jar", SearchOption.AllDirectories).Single()
                        armed = True
                    End Sub)
                    Using probe As New HashProbe(hashing:=Sub()
                        If Not armed OrElse probed OrElse File.Exists(privateFirst) OrElse Not File.Exists(first) OrElse File.Exists(second) Then Return
                        probed = True
                        Using reader = File.OpenRead(first)
                            readable = reader.Length = original.Length
                        End Using
                        Try
                            Select Case action
                                Case "write" : File.WriteAllBytes(first, session.Bad)
                                Case "delete" : File.Delete(first)
                                Case "rename" : File.Move(first, first & ".external")
                                Case "cancel"
                                    Using writer As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
                                    End Using
                            End Select
                        Catch ex As IOException
                            blocked = True
                        End Try
                        If action = "cancel" Then cancel.Cancel()
                    End Sub)
                        Try
                            Await session.StartAsync(components:=selection, isolation:=InstanceIsolationMode.Disabled, progress:=progress, token:=cancel.Token)
                        Catch ex As OperationCanceledException
                            cancelled = True
                        End Try
                    End Using
                    Console.WriteLine("MOD_LEASE existing=" & existing & " action=" & action & " blocked=" & blocked & " cancelled=" & cancelled)
                    Check(probed AndAlso blocked AndAlso readable AndAlso writableBeforeCommit, "shared Mods remain writable before commit and readable but protected during later publication: " & existing & "-" & action, passed)
                    Check(session.Released() AndAlso If(existing, File.GetLastWriteTimeUtc(first) = stamp, True), "publication releases private work and preserves an existing Mod timestamp: " & existing & "-" & action, passed)
                    If action = "cancel" Then
                        Check(cancelled AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Resource(False)) AndAlso Not File.Exists(second) AndAlso File.Exists(first) = existing, "cancellation releases Mod leases before rolling back only new files: " & existing, passed)
                        Dim result = Await session.StartAsync(components:=selection, isolation:=InstanceIsolationMode.Disabled)
                        Check(result.NetworkBytes = original.Length AndAlso File.ReadAllBytes(first).SequenceEqual(original) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "retry preserves shared isolation and reuses declared caches without trusting hashless filenames: " & existing, passed)
                    Else
                        Check(Not cancelled AndAlso File.Exists(session.Json) AndAlso File.ReadAllBytes(first).SequenceEqual(original) AndAlso File.Exists(second), "a published instance still references every selected shared Mod: " & existing & "-" & action, passed)
                    End If
                    Using hold As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Check(hold.Length = original.Length, "success or retry releases every shared Mod handle: " & existing & "-" & action, passed)
                    End Using
                End Using
            Next
        Next
    End Function
    Private Async Function FailureAsync(root As String, passed As List(Of String)) As Task
        For Each existing In {False, True}
            For Each fault In {"instance", "resourcepacks"}
                Using session As New IsolationResourceChecks.Session(root, "boundary-failure-" & existing & "-" & fault)
                    Dim selection = IsolationModeChecks.Components(session, all:=True), first = Path.Combine(session.Root, "mods", "optifine.jar")
                    Dim original = session.Fixture.Server.Files("/opt"), marker = If(fault = "instance", Path.Combine(Path.GetDirectoryName(session.Json), "external.txt"), Path.Combine(session.Root, "resourcepacks"))
                    If existing Then session.Put(first, original)
                    Dim privateFirst As String = Nothing, armed = False, probed = False, rejected = False
                    Using probe As New HashProbe(hashing:=Sub()
                        If Not armed OrElse probed OrElse File.Exists(privateFirst) OrElse Not File.Exists(first) Then Return
                        probed = True : session.Put(marker, session.Bad)
                    End Sub)
                        Try
                            Await session.StartAsync(components:=selection, isolation:=InstanceIsolationMode.Disabled, progress:=New Progress(Sub(value)
                                If value.Stage <> "完成安装" Then Return
                                privateFirst = Directory.EnumerateFiles(Path.Combine(session.Root, ".vanilla-downloader", "staging"), "optifine.jar", SearchOption.AllDirectories).Single()
                                armed = True
                            End Sub))
                        Catch ex As IOException
                            rejected = True
                        End Try
                    End Using
                    Check(probed AndAlso rejected AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Ini) AndAlso session.Released(), "late publication failure releases leases without initializing an external instance: " & existing & "-" & fault, passed)
                    Check(File.ReadAllBytes(marker).SequenceEqual(session.Bad) AndAlso File.Exists(first) = existing AndAlso Not File.Exists(Path.Combine(session.Root, "mods", "fabric-api.jar")) AndAlso Not File.Exists(session.Resource(False)), "late failure rolls back only new Mods/resources and keeps external blockers: " & existing & "-" & fault, passed)
                    If existing Then
                        Using writer As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        End Using
                    End If
                    File.Delete(marker)
                    If fault = "instance" Then Directory.Delete(Path.GetDirectoryName(session.Json), False)
                    Dim result = Await session.StartAsync(components:=selection, isolation:=InstanceIsolationMode.Disabled)
                    Check(result.NetworkBytes = original.Length AndAlso File.ReadAllBytes(first).SequenceEqual(original) AndAlso File.Exists(session.Json), "late publication failure retries with checked payloads and an unlocked target: " & existing & "-" & fault, passed)
                    Using writer As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Check(PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "retry releases Mod leases and keeps the selected shared mode: " & existing & "-" & fault, passed)
                    End Using
                End Using
            Next
        Next
    End Function
    Private Async Function SettingsAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"relevant", "irrelevant", "saved"}
            Using session As New IsolationResourceChecks.Session(root, "boundary-settings-" & mode)
                Await session.SeedAsync() : session.SetGlobal(If(mode = "irrelevant", "0", "1"))
                session.SetIni("DisplayType:invalid" & If(mode = "saved", vbCrLf & "VersionArgumentIndieV2:False", ""))
                session.Put(session.Resource(True), session.Bad) : session.Put(session.Resource(False), session.Bad)
                Dim ini = File.ReadAllBytes(session.Ini), rejected = False
                Try
                    Await session.StartAsync(repair:=True)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected = (mode = "relevant") AndAlso If(rejected, File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad), Not File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad)) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad), "invalid display classification blocks only a repair that depends on it: " & mode, passed)
                Check(ini.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso session.Released(), "classification errors or irrelevant values never rewrite preferences or leak locks: " & mode, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "boundary-settings-changed")
            Await session.SeedAsync() : session.SetGlobal("1") : session.SetIni("DisplayType:2")
            session.Put(session.Resource(True), session.Bad) : session.Put(session.Resource(False), session.Bad)
            Dim changed As Byte() = Nothing, rejected = False
            Try
                Await session.StartAsync(repair:=True, progress:=New Progress(Sub(value)
                    If value.Stage <> "完成安装" Then Return
                    session.SetIni("DisplayType:3") : changed = File.ReadAllBytes(session.Ini)
                End Sub))
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad), "a classification change aborts before touching either legacy resource target", passed)
            Check(changed.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso session.Released(), "a concurrent classification change remains the user's current preference", passed)
            Dim result = Await session.StartAsync(repair:=True, isolation:=InstanceIsolationMode.Enabled)
            Check(result.NetworkBytes = 0 AndAlso changed.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad) AndAlso Not File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad), "retry resolves the current PCL classification instead of retaining the failed task's path", passed)
        End Using
    End Function
    Private Async Function DisplayAsync(root As String, passed As List(Of String)) As Task
        For Each strategy In {1, 3}
            For Each mode In {"api", "original", "duplicate", "saved-off", "legacy-off"}
                Using session As New IsolationResourceChecks.Session(root, "boundary-display-" & strategy & "-" & mode)
                    Await session.SeedAsync() : session.SetGlobal(strategy.ToString())
                    Dim ini = "DisplayType:2"
                    If mode = "original" Then ini = "DisplayType:3"
                    If mode = "duplicate" Then ini = "DisplayType:3" & vbCrLf & "DisplayType:2"
                    If mode = "saved-off" Then ini &= vbCrLf & "VersionArgumentIndieV2:False"
                    If mode = "legacy-off" Then ini &= vbCrLf & "VersionArgumentIndie:2"
                    session.SetIni(ini, Encoding.Unicode) : File.SetAttributes(session.Ini, FileAttributes.ReadOnly)
                    Dim isolated = mode = "api" OrElse mode = "duplicate", originalIni = File.ReadAllBytes(session.Ini), originalJson = File.ReadAllBytes(session.Json)
                    session.Put(session.Resource(True), session.Bad) : session.Put(session.Resource(False), session.Bad)
                    Dim result = Await session.StartAsync(repair:=True, isolation:=InstanceIsolationMode.Disabled)
                    Console.WriteLine("DISPLAY_REPAIR strategy=" & strategy & " mode=" & mode & " isolated=" & Not File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad))
                    Check(Not File.ReadAllBytes(session.Resource(isolated)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(Not isolated)).SequenceEqual(session.Bad), "existing PCL API display classification participates in Modable after explicit preferences: " & strategy & "-" & mode, passed)
                    Check(result.NetworkBytes = 0 AndAlso originalIni.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso originalJson.SequenceEqual(File.ReadAllBytes(session.Json)) AndAlso (File.GetAttributes(session.Ini) And FileAttributes.ReadOnly) <> 0, "display-based repair preserves UTF16 preferences, metadata and read-only attributes: " & strategy & "-" & mode, passed)
                End Using
            Next
        Next
        Using session As New IsolationResourceChecks.Session(root, "boundary-display-cancel"), cancel As New CancellationTokenSource()
            Await session.SeedAsync() : session.SetGlobal("1") : session.SetIni("DisplayType:2")
            session.Put(session.Resource(True), session.Bad) : session.Put(session.Resource(False), session.Bad)
            Dim oldJar = DirectCast(session.Fixture.Server.Files("/client").Clone(), Byte()) : oldJar(0) = oldJar(0) Xor CByte(1)
            File.WriteAllBytes(session.Jar, oldJar)
            Dim ini = File.ReadAllBytes(session.Ini), json = File.ReadAllBytes(session.Json), armed = False, cancelled = False
            Using probe As New HashProbe(hashing:=Sub()
                If armed AndAlso Not File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad) Then cancel.Cancel()
            End Sub)
                Try
                    Await session.StartAsync(repair:=True, token:=cancel.Token, progress:=New Progress(Sub(value)
                        If value.Stage = "完成安装" Then armed = True
                    End Sub))
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(cancelled AndAlso File.ReadAllBytes(session.Jar).SequenceEqual(oldJar) AndAlso ini.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso json.SequenceEqual(File.ReadAllBytes(session.Json)) AndAlso session.Released(), "cancelled API-classified repair retains the old client, original settings and completed resource fixes", passed)
            Dim result = Await session.StartAsync(repair:=True, isolation:=InstanceIsolationMode.Enabled)
            Check(result.NetworkBytes = 0 AndAlso File.ReadAllBytes(session.Jar).SequenceEqual(session.Fixture.Server.Files("/client")) AndAlso File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad) AndAlso ini.SequenceEqual(File.ReadAllBytes(session.Ini)), "API-classified repair retries from checked caches without initializing or migrating isolation settings", passed)
        End Using
    End Function
End Module

' Runs real SHA-1 and probes only documented algorithm creation/hash boundaries.
' No filesystem watcher timing, sleeps or production test hooks are required.
Public Class PublicationProbeSha1
    Inherits CopyCancellationSha1
    Friend Shared OnCreate As Action
    Public Sub New()
        OnCreate?.Invoke()
    End Sub
End Class
