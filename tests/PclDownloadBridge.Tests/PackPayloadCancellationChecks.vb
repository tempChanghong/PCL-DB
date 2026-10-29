Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module PackPayloadCancellationChecks
    Private ReadOnly Payload As String = New String("p"c, 512 * 1024 + 19)
    Private Const RelativePayload As String = "resourcepacks/payload.bin"

    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Digest(bytes As Byte()) As String
        Using sha As New SHA256CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
        End Using
    End Function

    Private Function Package(root As String, format As String, label As String, Optional content As String = Nothing) As String
        Dim entries As New Dictionary(Of String, String) From {{"overrides/" & RelativePayload, If(content, Payload)}}
        If format = "cf" Then
            Dim manifest = CurseForgeChecks.Manifest(Nothing) : manifest("files") = New JArray()
            Return CurseForgeChecks.Pack(root, label, manifest, entries)
        End If
        Dim index = ModrinthChecks.Manifest(False) : index("files") = New JArray()
        Return ModrinthChecks.Pack(root, label, index, entries)
    End Function

    Private Function Plan(source As String, format As String) As ModpackInstallPlan
        If format = "cf" Then Return New CurseForgeInstallPlan(CurseForgePackage.Read(source), CancellationToken.None)
        Return New ModrinthInstallPlan(ModrinthPackage.Read(source), {}, CancellationToken.None)
    End Function

    Private Class HashProbe
        Implements IDisposable
        Private ReadOnly _previous As New Dictionary(Of String, Type)
        Friend Sub New(block As Action(Of Integer), Optional finalized As Action(Of Byte()) = Nothing)
            For Each aliasName In {"SHA256", "System.Security.Cryptography.SHA256"}
                Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA256)
                    _previous.Add(aliasName, algorithm.GetType())
                End Using
            Next
            PackPayloadProbeSha256.OnBlock = block : PackPayloadProbeSha256.OnDigest = finalized
            CryptoConfig.AddAlgorithm(GetType(PackPayloadProbeSha256), _previous.Keys.ToArray())
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            PackPayloadProbeSha256.OnBlock = Nothing : PackPayloadProbeSha256.OnDigest = Nothing
            For Each item In _previous
                CryptoConfig.AddAlgorithm(item.Value, {item.Key})
            Next
        End Sub
    End Class

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        root = Path.Combine(root, "pack-payload-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception)
        Dim providers As New Dictionary(Of String, Type)
        For Each aliasName In {"SHA256", "System.Security.Cryptography.SHA256"}
            Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA256)
                providers.Add(aliasName, algorithm.GetType())
            End Using
        Next
        Dim formats = If(onlyCase = "pack-payload-cancellation-cf", {"cf"}, If(onlyCase = "pack-payload-cancellation-mr", {"mr"}, {"cf", "mr"}))
        If onlyCase IsNot Nothing AndAlso Not {"pack-payload-cancellation", "pack-payload-cancellation-cf", "pack-payload-cancellation-mr"}.Contains(onlyCase) Then Throw New ArgumentException(onlyCase)
        For Each packFormat As String In formats
            Try
                Await InstallationAsync(root, packFormat, passed)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Final payload installation cancellation: " & packFormat, ex))
            End Try
            Try
                DirectValidation(root, packFormat, passed)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Direct final payload validation: " & packFormat, ex))
            End Try
        Next
        For Each pair In providers
            Using algorithm = DirectCast(CryptoConfig.CreateFromName(pair.Key), SHA256)
                Check(algorithm.GetType() Is pair.Value, "final payload probes restore the original CryptoConfig mapping: " & pair.Key, passed)
            End Using
        Next
        Check(PackPayloadProbeSha256.OnBlock Is Nothing AndAlso PackPayloadProbeSha256.OnDigest Is Nothing,
              "final payload probes clear both process-local callbacks", passed)
        If failures.Count > 0 Then Throw New AggregateException("Final override verification regressions", failures)
        Return passed
    End Function

    Private Async Function InstallationAsync(root As String, format As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "payload-" & format)
            ModrinthChecks.Configure(fixture)
            Dim source = Package(root, format, "install-" & format)
            Dim cf = If(format = "cf", CurseForgePackage.Read(source), Nothing)
            Dim mr = If(format = "mr", ModrinthPackage.Read(source), Nothing)
            Dim policy As New ModrinthChecks.Policy(fixture.Server.Url)
            Dim install As Func(Of IProgress(Of TransferProgress), CancellationToken, Task(Of InstallResult)) =
                Function(progress, token)
                    If format = "cf" Then Return New CurseForgeInstaller(policy).InstallAsync(cf, fixture.Root, "pack", {}, {}, progress, token, connections:=2)
                    Return New ModrinthInstaller(policy).InstallAsync(mr, fixture.Root, "pack", {}, {}, progress, token, connections:=2)
                End Function
            Dim preserved As New Dictionary(Of String, Byte()) From {
                {Path.Combine(fixture.Root, "mods/user.jar"), Encoding.UTF8.GetBytes("existing shared Mod")},
                {Path.Combine(fixture.Root, "resourcepacks/user.zip"), Encoding.UTF8.GetBytes("existing shared resource pack")},
                {Path.Combine(fixture.Root, "PCL/Setup.ini"), Encoding.UTF8.GetBytes("LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:keep")},
                {Path.Combine(fixture.Root, "config/user.txt"), Encoding.UTF8.GetBytes("existing shared configuration")}}
            For Each pair In preserved
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)) : File.WriteAllBytes(pair.Key, pair.Value)
            Next
            Dim armed = False, blocks = 0, hashed As Long = 0, failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(count)
                If Not armed OrElse count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Try
                    Await install(New ModrinthChecks.Progress(Sub(value)
                        If value.Stage = "完成安装" Then armed = True
                    End Sub), cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("PACK_PAYLOAD_CANCEL format=" & format & " phase=final-install bytes=" & hashed & " length=" & Payload.Length & " blocks=" & blocks & " exception=" & If(failure?.GetType().FullName, "none"))
                Check(armed AndAlso TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "final override validation cancels with the original installation token: " & format, passed)
                Check(blocks > 0 AndAlso hashed > 0 AndAlso hashed < Payload.Length,
                      "final override validation stops hashing before reading the complete payload: " & format, passed)
            End Using
            Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions/pack")), "cancelled final override validation never publishes the instance: " & format, passed)
            Check(PreservedFiles(preserved), "cancelled final override validation preserves shared files and external configuration: " & format, passed)
            Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader/staging")
            Check(Not Directory.Exists(staging) OrElse (Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any() AndAlso Not Directory.EnumerateDirectories(staging, "payload", SearchOption.AllDirectories).Any()),
                  "cancelled final override validation removes its owned payload and leaves no staged files: " & format, passed)
            Using released As New FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(released.Length > 0, "cancelled final override validation releases the original package: " & format, passed)
            End Using
            Dim result = Await install(Nothing, CancellationToken.None)
            Dim installed = Path.Combine(result.InstancePath, RelativePayload), expected = Encoding.UTF8.GetBytes(Payload)
            Check(File.Exists(Path.Combine(result.InstancePath, "pack.json")) AndAlso File.ReadAllBytes(installed).SequenceEqual(expected) AndAlso Digest(File.ReadAllBytes(installed)) = Digest(expected),
                  "same-name retry after final validation cancellation publishes the exact override bytes and SHA256: " & format, passed)
            Check(result.NetworkBytes = 0, "same-name retry reuses previously verified Minecraft payloads: " & format, passed)
            Check(PreservedFiles(preserved) AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2") = "True",
                  "retry preserves shared configuration and creates the isolated package settings: " & format, passed)
            Dim ledger = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "PCL/" & If(format = "cf", "CurseForgeInstall.json", "ModrinthInstall.json"))))
            Check(CStr(ledger("archiveSha256")) = If(format = "cf", cf.Sha256, mr.Sha256), "retry publishes the exact selected package provenance: " & format, passed)
            If format = "mr" Then
                Check(CStr(ledger("overrides")(0)("sha256")) = Digest(expected), "Modrinth ledger retains the original uppercase SHA256 representation", passed)
            End If
        End Using
    End Function

    Private Function PreservedFiles(expected As Dictionary(Of String, Byte())) As Boolean
        Return expected.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
    End Function

    Private Sub DirectValidation(root As String, format As String, passed As List(Of String))
        Dim source = Package(root, format, "direct-" & format), stage = Path.Combine(root, "stage-" & format)
        Directory.CreateDirectory(Path.Combine(stage, "PCL"))
        File.WriteAllText(Path.Combine(stage, "PCL/Setup.ini"), "VersionArgumentIndie:1" & vbCrLf & "VersionArgumentIndieV2:True")
        Using plan = PackPayloadCancellationChecks.Plan(source, format)
            plan.ValidateTargets("direct") : plan.CopyOverrides(stage, CancellationToken.None)
            Dim hashed As Long = 0, completed As New List(Of String)
            Using probe As New HashProbe(Sub(count) hashed += count,
                                         Sub(hash) completed.Add(BitConverter.ToString(hash).Replace("-", "")))
                plan.WriteLedger(stage, CancellationToken.None)
            End Using
            Check(hashed = Payload.Length AndAlso completed.SequenceEqual({Digest(Encoding.UTF8.GetBytes(Payload))}),
                  "normal final override verification computes the complete raw SHA256 without changing digest format: " & format, passed)
            Dim target = Path.Combine(stage, RelativePayload), original = File.ReadAllBytes(target), changed = DirectCast(original.Clone(), Byte())
            changed(changed.Length - 1) = changed(changed.Length - 1) Xor CByte(1) : File.WriteAllBytes(target, changed)
            Dim rejected = False
            Try
                plan.WriteLedger(stage, CancellationToken.None)
            Catch ex As InvalidDataException
                rejected = ex.Message.Contains(RelativePayload)
            End Try
            Check(rejected AndAlso File.ReadAllBytes(target).SequenceEqual(changed), "final override verification rejects same-length tampering without rewriting the changed file: " & format, passed)
            File.WriteAllBytes(target, original)
            Dim blocks = 0 : hashed = 0
            Dim failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(count)
                If count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Try
                    plan.WriteLedger(stage, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("PACK_PAYLOAD_CANCEL format=" & format & " phase=direct-validation bytes=" & hashed & " length=" & Payload.Length & " blocks=" & blocks & " exception=" & If(failure?.GetType().FullName, "none"))
                Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "direct final override verification propagates its original token: " & format, passed)
                Check(blocks > 0 AndAlso hashed > 0 AndAlso hashed < Payload.Length, "direct final override verification stops before hashing the complete file: " & format, passed)
            End Using
            Using released As New FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(released.Length = original.Length, "cancelled final override verification releases its payload read lease: " & format, passed)
            End Using
            Dim ledgerPath = Path.Combine(stage, "PCL/" & If(format = "cf", "CurseForgeInstall.json", "ModrinthInstall.json"))
            File.SetLastWriteTimeUtc(ledgerPath, New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc))
            Dim ledgerStamp = File.GetLastWriteTimeUtc(ledgerPath), ledgerBytes = File.ReadAllBytes(ledgerPath)
            hashed = 0 : failure = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(count)
                hashed += count
                If hashed = Payload.Length Then cancel.Cancel()
            End Sub)
                Try
                    plan.WriteLedger(stage, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(hashed = Payload.Length AndAlso TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "cancellation on the final payload block is observed before finalization: " & format, passed)
            End Using
            Check(File.GetLastWriteTimeUtc(ledgerPath) = ledgerStamp AndAlso File.ReadAllBytes(ledgerPath).SequenceEqual(ledgerBytes),
                  "cancellation on the final payload block never rewrites the ledger: " & format, passed)
            plan.WriteLedger(stage, CancellationToken.None)
            Check(File.ReadAllBytes(target).SequenceEqual(original), "direct final override validation can retry unchanged content after cancellation: " & format, passed)
        End Using
        Dim emptySource = Package(root, format, "empty-" & format, ""), emptyStage = Path.Combine(root, "empty-stage-" & format)
        Directory.CreateDirectory(Path.Combine(emptyStage, "PCL"))
        File.WriteAllText(Path.Combine(emptyStage, "PCL/Setup.ini"), "VersionArgumentIndie:1" & vbCrLf & "VersionArgumentIndieV2:True")
        Using emptyPlan = Plan(emptySource, format)
            emptyPlan.ValidateTargets("empty") : emptyPlan.CopyOverrides(emptyStage, CancellationToken.None)
            Dim digests As New List(Of String), hashed As Long = 0
            Using probe As New HashProbe(Sub(count) hashed += count, Sub(hash) digests.Add(BitConverter.ToString(hash).Replace("-", "")))
                emptyPlan.WriteLedger(emptyStage, CancellationToken.None)
            End Using
            Check(hashed = 0 AndAlso digests.SequenceEqual({Digest(New Byte() {})}), "empty overrides preserve the canonical raw SHA256 through EOF finalization: " & format, passed)
        End Using
    End Sub
End Module

' Process-local CryptoConfig provider: observes real HashCore input after the actual
' digest consumes each block. The test arms it only at final payload validation.
Public Class PackPayloadProbeSha256
    Inherits SHA256
    Friend Shared OnBlock As Action(Of Integer)
    Friend Shared OnDigest As Action(Of Byte())
    Private ReadOnly _inner As New SHA256CryptoServiceProvider()
    Public Sub New()
        HashSizeValue = 256
    End Sub
    Public Overrides Sub Initialize()
        _inner.Initialize()
    End Sub
    Protected Overrides Sub HashCore(array As Byte(), ibStart As Integer, cbSize As Integer)
        _inner.TransformBlock(array, ibStart, cbSize, array, ibStart)
        OnBlock?.Invoke(cbSize)
    End Sub
    Protected Overrides Function HashFinal() As Byte()
        _inner.TransformFinalBlock(New Byte() {}, 0, 0)
        Dim result = _inner.Hash
        OnDigest?.Invoke(result)
        Return result
    End Function
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
