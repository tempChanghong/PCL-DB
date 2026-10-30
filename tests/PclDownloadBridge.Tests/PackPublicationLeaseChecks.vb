Imports System.Diagnostics
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module PackPublicationLeaseChecks
    Private Const FirstRelative As String = "config/first.bin"
    Private Const SecondRelative As String = "config/second.bin"
    Private ReadOnly FirstBytes As Byte() = Encoding.UTF8.GetBytes(New String("a"c, 131072 + 19))
    Private ReadOnly SecondBytes As Byte() = Encoding.UTF8.GetBytes(New String("b"c, 262144 + 31))

    Private Sub Record(value As Boolean, label As String, passed As List(Of String), failures As List(Of Exception))
        If value Then
            passed.Add(label)
        Else
            failures.Add(New InvalidOperationException(label))
        End If
    End Sub
    Private Function Digest(bytes As Byte()) As String
        Using sha As New SHA256CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
        End Using
    End Function
    Private Function Mappings() As Dictionary(Of String, Type)
        Dim values As New Dictionary(Of String, Type)
        For Each name As String In {"SHA256", "System.Security.Cryptography.SHA256"}
            Using sha = DirectCast(CryptoConfig.CreateFromName(name), SHA256)
                values(name) = sha.GetType()
            End Using
        Next
        Return values
    End Function
    Private Class Probe
        Implements IDisposable
        Private ReadOnly _previous As Dictionary(Of String, Type) = Mappings()
        Private ReadOnly _block As Action(Of Integer) = PackPayloadProbeSha256.OnBlock
        Private ReadOnly _digest As Action(Of Byte()) = PackPayloadProbeSha256.OnDigest
        Friend Sub New(block As Action(Of Integer), finalized As Action(Of Byte()))
            PackPayloadProbeSha256.OnBlock = block : PackPayloadProbeSha256.OnDigest = finalized
            CryptoConfig.AddAlgorithm(GetType(PackPayloadProbeSha256), _previous.Keys.ToArray())
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            PackPayloadProbeSha256.OnBlock = _block : PackPayloadProbeSha256.OnDigest = _digest
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class
    Private Function InPayloadValidation() As Boolean
        Return New StackTrace().GetFrames().Any(Function(frame) frame.GetMethod().Name = "ValidatePayload" AndAlso frame.GetMethod().DeclaringType.FullName = "PclDownloadBridge.Core.ModpackInstallPlan")
    End Function
    Private Function Rejected(action As Action) As Boolean
        Try
            action()
            Return False
        Catch ex As IOException
            Return True
        End Try
    End Function
    Private Function Released(path As String) As Boolean
        Try
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
            Return True
        Catch ex As IOException
            Return False
        End Try
    End Function
    Private Function StageClean(root As String) As Boolean
        Dim stage = Path.Combine(root, ".vanilla-downloader/staging")
        Return Not Directory.Exists(stage) OrElse (Not Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Any() AndAlso Not Directory.EnumerateDirectories(stage, "payload", SearchOption.AllDirectories).Any())
    End Function

    Private Class CaseFixture
        Implements IDisposable
        Friend ReadOnly Fixture As InstallationChecks.InstallFixture
        Friend ReadOnly Source As String
        Friend ReadOnly Format As String
        Friend ReadOnly ArchiveDigest As String
        Private ReadOnly _cf As CurseForgePackage
        Private ReadOnly _mr As ModrinthPackage
        Private ReadOnly _policy As ModrinthChecks.Policy
        Private ReadOnly _saved As New Dictionary(Of String, Byte())
        Friend Sub New(root As String, packFormat As String, label As String)
            Format = packFormat : Fixture = New InstallationChecks.InstallFixture(root, "lease-" & packFormat & "-" & label)
            ModrinthChecks.Configure(Fixture)
            Dim entries As New Dictionary(Of String, String) From {{"overrides/" & FirstRelative, Encoding.UTF8.GetString(FirstBytes)}, {"overrides/" & SecondRelative, Encoding.UTF8.GetString(SecondBytes)}}
            If Format = "cf" Then
                Dim manifest = CurseForgeChecks.Manifest(Nothing) : manifest("files") = New JArray()
                Source = CurseForgeChecks.Pack(root, "lease-" & Format & "-" & label, manifest, entries)
                _cf = CurseForgePackage.Read(Source) : ArchiveDigest = _cf.Sha256
            Else
                Dim manifest = ModrinthChecks.Manifest(False) : manifest("files") = New JArray()
                Source = ModrinthChecks.Pack(root, "lease-" & Format & "-" & label, manifest, entries)
                _mr = ModrinthPackage.Read(Source) : ArchiveDigest = _mr.Sha256
            End If
            _policy = New ModrinthChecks.Policy(Fixture.Server.Url)
            For Each relative In {"config/user.txt", "mods/user.jar", "PCL/Setup.ini"}
                Dim path = IO.Path.Combine(Fixture.Root, relative), bytes = Encoding.UTF8.GetBytes("LaunchArgumentIndieV2:0" & vbCrLf & "user setting: keep " & relative)
                Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes) : _saved(path) = bytes
            Next
        End Sub
        Friend Function Install(progress As IProgress(Of TransferProgress), token As CancellationToken) As Task(Of InstallResult)
            If Format = "cf" Then Return New CurseForgeInstaller(_policy).InstallAsync(_cf, Fixture.Root, "pack", {}, {}, progress, token, connections:=2)
            Return New ModrinthInstaller(_policy).InstallAsync(_mr, Fixture.Root, "pack", {}, {}, progress, token, connections:=2)
        End Function
        Friend Function StageFirst() As String
            Return Directory.EnumerateFiles(Path.Combine(Fixture.Root, ".vanilla-downloader/staging"), "first.bin", SearchOption.AllDirectories).Single()
        End Function
        Friend Function Preserved() As Boolean
            Return _saved.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
        End Function
        Friend Function InputsReleased() As Boolean
            Return Released(Source) AndAlso Released(Path.Combine(Fixture.Root, ".vanilla-downloader/install.lock"))
        End Function
        Friend Function Correct(result As InstallResult) As Boolean
            Return result IsNot Nothing AndAlso File.Exists(Path.Combine(result.InstancePath, "pack.json")) AndAlso File.ReadAllBytes(Path.Combine(result.InstancePath, FirstRelative)).SequenceEqual(FirstBytes) AndAlso File.ReadAllBytes(Path.Combine(result.InstancePath, SecondRelative)).SequenceEqual(SecondBytes)
        End Function
        Friend Function LedgerCorrect(result As InstallResult) As Boolean
            If result Is Nothing Then Return False
            Dim ledger = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "PCL/" & If(Format = "cf", "CurseForgeInstall.json", "ModrinthInstall.json"))))
            Dim valid = CStr(ledger("archiveSha256")) = ArchiveDigest AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2") = "True"
            If Format = "mr" Then valid = valid AndAlso CStr(DirectCast(ledger("overrides"), JArray).OfType(Of JObject)().Single(Function(item) CStr(item("path")) = FirstRelative)("sha256")) = Digest(FirstBytes)
            Return valid
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Fixture.Dispose()
        End Sub
    End Class

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso Not {"pack-publication-lease-all", "pack-publication-lease-cf", "pack-publication-lease-mr"}.Contains(onlyCase) Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "pack-publication-lease") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings()
        Dim oldBlock = PackPayloadProbeSha256.OnBlock, oldDigest = PackPayloadProbeSha256.OnDigest
        For Each packFormat As String In If(onlyCase = "pack-publication-lease-cf", {"cf"}, If(onlyCase = "pack-publication-lease-mr", {"mr"}, {"cf", "mr"}))
            For Each scenario As String In {"guard", "cancel", "writer"}
                Try
                    If scenario = "guard" Then
                        Await GuardAsync(root, packFormat, passed, failures, cases)
                    ElseIf scenario = "cancel" Then
                        Await CancelAsync(root, packFormat, passed, failures, cases)
                    Else
                        Await WriterAsync(root, packFormat, passed, failures, cases)
                    End If
                Catch ex As Exception
                    failures.Add(New InvalidOperationException("Pack publication lease scenario: " & packFormat & "/" & scenario, ex))
                End Try
            Next
        Next
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), SHA256)
                Record(sha.GetType() Is pair.Value, "pack publication restores algorithm mapping: " & pair.Key, passed, failures)
            End Using
        Next
        Record(Object.ReferenceEquals(oldBlock, PackPayloadProbeSha256.OnBlock) AndAlso Object.ReferenceEquals(oldDigest, PackPayloadProbeSha256.OnDigest), "pack publication restores both existing callbacks", passed, failures)
        Dim report = Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}, {"Cases", cases}}.ToString())
        Console.WriteLine("PACK_PUBLICATION_LEASE_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Pack publication lease regressions", failures)
        Return passed
    End Function

    Private Async Function GuardAsync(root As String, packFormat As String, passed As List(Of String), failures As List(Of Exception), cases As JObject) As Task
        Using context As New CaseFixture(root, packFormat, "guard")
            Dim observation As New JObject : cases(packFormat & "-guard") = observation
            Dim armed = False, attempted = False, writeBlocked = False, deleteBlocked = False, renameBlocked = False, readAllowed = False
            Dim target As String = Nothing, completed = 0, hashes As New List(Of String), result As InstallResult = Nothing, failure As Exception = Nothing
            Using probe As New Probe(Sub(count)
                If Not armed OrElse attempted OrElse completed <> 1 OrElse count <= 0 OrElse Not InPayloadValidation() Then Return
                attempted = True
                readAllowed = File.ReadAllBytes(target).SequenceEqual(FirstBytes)
                Dim changed = DirectCast(FirstBytes.Clone(), Byte()) : changed(0) = changed(0) Xor CByte(1)
                writeBlocked = Rejected(Sub() File.WriteAllBytes(target, changed))
                If Not writeBlocked Then File.WriteAllBytes(target, FirstBytes)
                deleteBlocked = Rejected(Sub() File.Delete(target))
                If Not deleteBlocked Then File.WriteAllBytes(target, FirstBytes)
                Dim renamed = target & ".renamed"
                renameBlocked = Rejected(Sub() File.Move(target, renamed))
                If Not renameBlocked Then File.Move(renamed, target)
            End Sub, Sub(hash)
                If Not armed OrElse Not InPayloadValidation() Then Return
                completed += 1 : hashes.Add(BitConverter.ToString(hash).Replace("-", ""))
            End Sub)
                Try
                    result = Await context.Install(New ModrinthChecks.Progress(Sub(value)
                        If value.Stage = "完成安装" Then target = context.StageFirst() : armed = True
                    End Sub), CancellationToken.None)
                Catch ex As Exception
                    failure = ex
                End Try
            End Using
            observation("Armed") = armed : observation("AttemptedAfterFirstDigest") = attempted : observation("WriteBlocked") = writeBlocked : observation("DeleteBlocked") = deleteBlocked : observation("RenameBlocked") = renameBlocked : observation("ReadAllowed") = readAllowed
            observation("Digests") = New JArray(hashes) : observation("Failure") = If(failure?.ToString(), "")
            Record(armed AndAlso attempted AndAlso hashes.SequenceEqual({Digest(FirstBytes), Digest(SecondBytes)}), "pack publication probes the real second override after the first digest: " & packFormat, passed, failures)
            Record(writeBlocked, "pack publication blocks rewriting the earlier checked override: " & packFormat, passed, failures)
            Record(deleteBlocked, "pack publication blocks deleting the earlier checked override: " & packFormat, passed, failures)
            Record(renameBlocked, "pack publication blocks renaming the earlier checked override: " & packFormat, passed, failures)
            Record(readAllowed, "pack publication allows readers of the earlier checked override: " & packFormat, passed, failures)
            Record(failure Is Nothing AndAlso context.Correct(result), "pack publication returns success with both original overrides and instance JSON: " & packFormat, passed, failures)
            Record(context.LedgerCorrect(result), "pack publication preserves archive provenance, original ledger hashes and isolation: " & packFormat, passed, failures)
            Record(context.Preserved(), "pack publication preserves existing shared files and external settings: " & packFormat, passed, failures)
            Record(context.InputsReleased() AndAlso result IsNot Nothing AndAlso Released(Path.Combine(result.InstancePath, FirstRelative)) AndAlso Released(Path.Combine(result.InstancePath, SecondRelative)) AndAlso StageClean(context.Fixture.Root), "pack publication closes payload, package and root leases after success: " & packFormat, passed, failures)
        End Using
    End Function

    Private Async Function CancelAsync(root As String, packFormat As String, passed As List(Of String), failures As List(Of Exception), cases As JObject) As Task
        Using context As New CaseFixture(root, packFormat, "cancel")
            Dim observation As New JObject : cases(packFormat & "-cancel") = observation
            Dim armed = False, completed = 0, cancelled = False, failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(count)
                If armed AndAlso completed = 1 AndAlso count > 0 AndAlso Not cancelled AndAlso InPayloadValidation() Then cancelled = True : cancel.Cancel()
            End Sub, Sub(hash)
                If armed AndAlso InPayloadValidation() Then completed += 1
            End Sub)
                Try
                    Await context.Install(New ModrinthChecks.Progress(Sub(value)
                        If value.Stage = "完成安装" Then armed = True
                    End Sub), cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                observation("OriginalToken") = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                Record(armed AndAlso cancelled AndAlso CBool(observation("OriginalToken")), "pack publication second-file cancellation preserves its caller token: " & packFormat, passed, failures)
            End Using
            Record(Not Directory.Exists(Path.Combine(context.Fixture.Root, "versions/pack")) AndAlso StageClean(context.Fixture.Root), "pack publication cancellation releases earlier payload leases and removes private work: " & packFormat, passed, failures)
            Record(context.InputsReleased(), "pack publication cancellation releases package and root locks: " & packFormat, passed, failures)
            Record(context.Preserved(), "pack publication cancellation preserves existing files and configuration: " & packFormat, passed, failures)
            Dim retry = Await context.Install(Nothing, CancellationToken.None)
            observation("RetryNetworkBytes") = retry.NetworkBytes : observation("RetrySuccess") = context.Correct(retry)
            Record(context.Correct(retry) AndAlso context.LedgerCorrect(retry), "pack publication same-name retry restores exact content, ledger and isolation: " & packFormat, passed, failures)
            Record(retry.NetworkBytes = 0 AndAlso context.InputsReleased() AndAlso StageClean(context.Fixture.Root), "pack publication retry reuses checked transfers and closes all task locks: " & packFormat, passed, failures)
        End Using
    End Function

    Private Async Function WriterAsync(root As String, packFormat As String, passed As List(Of String), failures As List(Of Exception), cases As JObject) As Task
        Using context As New CaseFixture(root, packFormat, "writer")
            Dim observation As New JObject : cases(packFormat & "-writer") = observation
            Dim writer As FileStream = Nothing, failure As Exception = Nothing
            Try
                Try
                    Await context.Install(New ModrinthChecks.Progress(Sub(value)
                        If value.Stage <> "完成安装" Then Return
                        Dim second = Path.Combine(Path.GetDirectoryName(context.StageFirst()), "second.bin")
                        writer = New FileStream(second, FileMode.Open, FileAccess.Write, FileShare.Read Or FileShare.Delete)
                    End Sub), CancellationToken.None)
                Catch ex As Exception
                    failure = ex
                End Try
                observation("FailureType") = If(failure?.GetType().FullName, "")
                Record(writer IsNot Nothing AndAlso TypeOf failure Is IOException AndAlso Not Directory.Exists(Path.Combine(context.Fixture.Root, "versions/pack")), "pack publication rejects an already open writer before committing: " & packFormat, passed, failures)
                Record(StageClean(context.Fixture.Root) AndAlso context.InputsReleased() AndAlso context.Preserved(), "pack publication constructor failure releases partial leases before owned cleanup: " & packFormat, passed, failures)
            Finally
                writer?.Dispose()
            End Try
            Dim retry = Await context.Install(Nothing, CancellationToken.None)
            observation("RetrySuccess") = context.Correct(retry)
            Record(context.Correct(retry) AndAlso context.LedgerCorrect(retry) AndAlso context.InputsReleased() AndAlso StageClean(context.Fixture.Root), "pack publication writer conflict can retry the same name after releasing the writer: " & packFormat, passed, failures)
        End Using
    End Function
End Module
