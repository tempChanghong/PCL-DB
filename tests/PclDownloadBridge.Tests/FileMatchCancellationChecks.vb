Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module FileMatchCancellationChecks
    Private ReadOnly Payload As Byte() = Encoding.UTF8.GetBytes(New String("f"c, 512 * 1024 + 19))
    Private Const RelativePayload As String = "mods/payload.jar"

    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Digest(bytes As Byte(), algorithm As String) As String
        Using hash As HashAlgorithm = If(algorithm = "sha512", DirectCast(New SHA512CryptoServiceProvider(), HashAlgorithm), If(algorithm = "md5", DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider()))
            Return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "")
        End Using
    End Function

    Private Function Aliases() As Dictionary(Of String, Type)
        Dim result As New Dictionary(Of String, Type)
        For Each name In {"SHA1", "System.Security.Cryptography.SHA1", "SHA512", "System.Security.Cryptography.SHA512", "MD5", "System.Security.Cryptography.MD5"}
            Using hash = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                result.Add(name, hash.GetType())
            End Using
        Next
        Return result
    End Function

    Private Class HashProbe
        Implements IDisposable
        Private ReadOnly _previous As Dictionary(Of String, Type) = Aliases()
        Friend Sub New(block As Action(Of String, Integer))
            FileMatchHashProbe.OnBlock = block
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha1), {"SHA1", "System.Security.Cryptography.SHA1"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha512), {"SHA512", "System.Security.Cryptography.SHA512"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeMd5), {"MD5", "System.Security.Cryptography.MD5"})
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            FileMatchHashProbe.OnBlock = Nothing
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        root = Path.Combine(root, "file-match-cancellation") : Directory.CreateDirectory(root)
        If onlyCase IsNot Nothing AndAlso Not {"file-match-all", "file-match-integration", "file-match-boundaries"}.Contains(onlyCase) Then Throw New ArgumentException(onlyCase)
        Dim passed As New List(Of String), failures As New List(Of Exception), before = Aliases()
        If onlyCase <> "file-match-boundaries" Then
            For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
                Try
                    Await AcquisitionAsync(root, platform, passed)
                Catch ex As Exception
                    failures.Add(New InvalidOperationException("Final cached archive verification: " & platform.ToString(), ex))
                End Try
            Next
            For Each packFormat In {"cf", "mr"}
                Try
                    FinalPayload(root, packFormat, passed)
                Catch ex As Exception
                    failures.Add(New InvalidOperationException("Non-override final payload verification: " & packFormat, ex))
                End Try
            Next
        End If
        If onlyCase <> "file-match-integration" Then Boundaries(root, passed)
        For Each pair In before
            Using hash = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(hash.GetType() Is pair.Value, "file match probes restore the original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(FileMatchHashProbe.OnBlock Is Nothing, "file match probes clear their process-local callback", passed)
        If failures.Count > 0 Then Throw New AggregateException("File verification cancellation regressions", failures)
        Return passed
    End Function

    Private Function ArchiveBytes() As Byte()
        Using output As New MemoryStream()
            Using archive As New ZipArchive(output, ZipArchiveMode.Create, True)
                Using entry = archive.CreateEntry("pack.mcmeta", CompressionLevel.NoCompression).Open()
                    Dim metadata = Encoding.UTF8.GetBytes("{""pack"":{""pack_format"":15,""description"":""fixture""}}")
                    entry.Write(metadata, 0, metadata.Length)
                End Using
                Using entry = archive.CreateEntry("assets/payload.bin", CompressionLevel.NoCompression).Open()
                    entry.Write(Payload, 0, Payload.Length)
                End Using
            End Using
            Return output.ToArray()
        End Using
    End Function

    Private Async Function AcquisitionAsync(root As String, platform As ModpackPlatform, passed As List(Of String)) As Task
        Dim bytes = ArchiveBytes(), cache = Path.Combine(root, "cache-" & platform.ToString())
        Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.ResourcePack, .Platform = platform, .ProjectId = "123", .Id = "1234567", .FileName = "fixture.zip",
            .Url = If(platform = ModpackPlatform.Modrinth, "https://cdn.modrinth.com/data/fixture.zip", "https://edge.forgecdn.net/files/1234/567/fixture.zip"), .Size = bytes.Length, .Sha1 = Digest(bytes, "sha1")}
        Using server As New TestServer()
            server.Files(New Uri(release.Url).AbsolutePath) = bytes
            Dim policy As New ModrinthChecks.Policy(server.Url)
            Dim cached = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, Nothing, CancellationToken.None, connections:=1)
            Dim requests = server.Requests.Values.Sum(), armed = False, hashed As Long = 0, blocks = 0, failure As Exception = Nothing, returned As String = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(algorithm, count)
                If Not armed OrElse count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Try
                    returned = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, New ModrinthChecks.Progress(Sub(value)
                        If value.Detail = "文件下载完成" Then armed = True
                    End Sub), cancel.Token, connections:=1)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("FILE_MATCH_CANCEL scope=archive platform=" & platform.ToString() & " bytes=" & hashed & " length=" & bytes.Length & " blocks=" & blocks & " returned=" & (returned IsNot Nothing).ToString() & " exception=" & If(failure?.GetType().FullName, "none"))
                Check(armed AndAlso TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token AndAlso returned Is Nothing,
                      "final cached archive verification propagates the original token instead of returning success: " & platform.ToString(), passed)
                Check(blocks > 0 AndAlso hashed > 0 AndAlso hashed < bytes.Length, "final cached archive verification stops before hashing the complete archive: " & platform.ToString(), passed)
            End Using
            Check(File.ReadAllBytes(cached).SequenceEqual(bytes) AndAlso requests = server.Requests.Values.Sum(), "cancelled final archive verification preserves cached bytes without HTTP: " & platform.ToString(), passed)
            Using lease As New FileStream(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(lease.Length = bytes.Length, "cancelled final archive verification releases its cache read lease: " & platform.ToString(), passed)
            End Using
            Dim retry = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, Nothing, CancellationToken.None, connections:=1)
            Check(retry = cached AndAlso requests = server.Requests.Values.Sum() AndAlso File.ReadAllBytes(retry).SequenceEqual(bytes), "archive retry reuses verified cache with unchanged content and no HTTP: " & platform.ToString(), passed)
            Using archive = ZipFile.OpenRead(retry), input = archive.GetEntry("assets/payload.bin").Open(), payloadCopy As New MemoryStream()
                input.CopyTo(payloadCopy)
                Check(payloadCopy.ToArray().SequenceEqual(Payload), "archive retry retains readable original ZIP payload: " & platform.ToString(), passed)
            End Using
        End Using
    End Function

    Private Function PayloadPlan(root As String, packFormat As String) As ModpackInstallPlan
        If packFormat = "cf" Then
            Dim manifest = CurseForgeChecks.Manifest(Nothing) : manifest("files") = New JArray()
            Dim source = CurseForgeChecks.Pack(root, "match-" & packFormat, manifest, New Dictionary(Of String, String)())
            Dim result As New CurseForgeInstallPlan(CurseForgePackage.Read(source), CancellationToken.None)
            Dim file As New DownloadFile With {.RelativePath = RelativePayload, .Size = Payload.Length, .Sha1 = Digest(Payload, "sha1"), .Url = "https://edge.forgecdn.net/files/1/1/payload.jar", .Category = "curseforge"}
            result.SetFiles(New List(Of CurseForgeResolvedFile) From {New CurseForgeResolvedFile(New CurseForgeReference(1, 1001, True), file, "fixture", "sha1")})
            Return result
        End If
        Dim index = ModrinthChecks.Manifest(False)
        index("files") = New JArray(New JObject From {{"path", RelativePayload}, {"fileSize", Payload.Length}, {"hashes", New JObject From {{"sha1", Digest(Payload, "sha1")}, {"sha512", Digest(Payload, "sha512")}}}, {"downloads", New JArray("https://cdn.modrinth.com/payload.jar")}})
        Return New ModrinthInstallPlan(ModrinthPackage.Read(ModrinthChecks.Pack(root, "match-" & packFormat, index, New Dictionary(Of String, String)())), {}, CancellationToken.None)
    End Function

    Private Sub FinalPayload(root As String, packFormat As String, passed As List(Of String))
        Dim stage = Path.Combine(root, "payload-" & packFormat), payloadPath = SafeFiles.UnderRoot(stage, RelativePayload)
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)) : File.WriteAllBytes(payloadPath, Payload)
        Directory.CreateDirectory(Path.Combine(stage, "PCL")) : File.WriteAllText(Path.Combine(stage, "PCL/Setup.ini"), "VersionArgumentIndie:1" & vbCrLf & "VersionArgumentIndieV2:True")
        Using plan = PayloadPlan(root, packFormat)
            plan.ValidateTargets("payload") : plan.WriteLedger(stage, CancellationToken.None)
            Dim ledger = Path.Combine(stage, "PCL/" & If(packFormat = "cf", "CurseForgeInstall.json", "ModrinthInstall.json"))
            File.SetLastWriteTimeUtc(ledger, New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc))
            Dim originalLedger = File.ReadAllBytes(ledger), stamp = File.GetLastWriteTimeUtc(ledger), hashed As Long = 0, blocks = 0, failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(algorithm, count)
                If count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Try
                    plan.WriteLedger(stage, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("FILE_MATCH_CANCEL scope=payload platform=" & packFormat & " bytes=" & hashed & " length=" & Payload.Length & " blocks=" & blocks & " exception=" & If(failure?.GetType().FullName, "none"))
                Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "non-override final payload verification propagates the original token: " & packFormat, passed)
                Check(blocks > 0 AndAlso hashed > 0 AndAlso hashed < Payload.Length, "non-override final payload verification stops before reading the complete file: " & packFormat, passed)
            End Using
            Check(File.GetLastWriteTimeUtc(ledger) = stamp AndAlso File.ReadAllBytes(ledger).SequenceEqual(originalLedger) AndAlso File.ReadAllBytes(payloadPath).SequenceEqual(Payload), "cancelled non-override verification preserves ledger and payload bytes: " & packFormat, passed)
            Using lease As New FileStream(payloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(lease.Length = Payload.Length, "cancelled non-override verification releases its payload lease: " & packFormat, passed)
            End Using
            plan.WriteLedger(stage, CancellationToken.None)
            Check(File.ReadAllBytes(payloadPath).SequenceEqual(Payload), "non-override final payload verification can retry unchanged content: " & packFormat, passed)
            Dim changed = DirectCast(Payload.Clone(), Byte()) : changed(changed.Length - 1) = changed(changed.Length - 1) Xor CByte(1) : File.WriteAllBytes(payloadPath, changed)
            Dim rejected = False
            Try
                plan.WriteLedger(stage, CancellationToken.None)
            Catch ex As InvalidDataException
                rejected = ex.Message.Contains(RelativePayload)
            End Try
            Check(rejected AndAlso File.ReadAllBytes(payloadPath).SequenceEqual(changed), "non-override final payload verification rejects same-length tampering without rewriting bytes: " & packFormat, passed)
        End Using
    End Sub

    Private Function Download(bytes As Byte(), algorithm As String) As DownloadFile
        Dim result As New DownloadFile With {.RelativePath = RelativePayload, .Size = bytes.Length}
        If algorithm = "sha1" OrElse algorithm = "all" Then result.Sha1 = Digest(bytes, "sha1").ToLowerInvariant()
        If algorithm = "sha512" OrElse algorithm = "all" Then result.Sha512 = Digest(bytes, "sha512").ToLowerInvariant()
        If algorithm = "md5" OrElse algorithm = "all" Then result.Md5 = Digest(bytes, "md5").ToLowerInvariant()
        If algorithm = "etag" OrElse algorithm = "all" Then
            Using hash As New MD5CryptoServiceProvider()
                result.S3SinglePartETag = BitConverter.ToString(hash.ComputeHash(hash.ComputeHash(bytes))).Replace("-", "").ToLowerInvariant() & "-1"
            End Using
        End If
        Return result
    End Function

    ' The frozen integration mode must JIT only original API references against old Core.
    <MethodImpl(MethodImplOptions.NoInlining)>
    Private Sub Boundaries(root As String, passed As List(Of String))
        Dim full = Path.Combine(root, "boundary.bin"), empty = Path.Combine(root, "empty.bin"), missing = Path.Combine(root, "missing.bin")
        File.WriteAllBytes(full, Payload) : File.WriteAllBytes(empty, New Byte() {})
        For Each algorithm In {"sha1", "sha512", "md5", "etag", "all"}
            Dim item = Download(Payload, algorithm), emptyItem = Download(New Byte() {}, algorithm)
            Using cancel As New CancellationTokenSource()
                Check(SafeFiles.Matches(full, item, cancel.Token), "cancellable matching accepts the independently computed digest: " & algorithm, passed)
                Check(SafeFiles.Matches(full, item), "legacy two-argument matching accepts the original digest: " & algorithm, passed)
                Check(SafeFiles.Matches(empty, emptyItem, cancel.Token) AndAlso SafeFiles.Matches(empty, emptyItem), "empty files retain declared-size and digest semantics: " & algorithm, passed)
            End Using
            Dim changed = DirectCast(Payload.Clone(), Byte()) : changed(changed.Length - 1) = changed(changed.Length - 1) Xor CByte(1) : File.WriteAllBytes(full, changed)
            Using cancel As New CancellationTokenSource()
                Check(Not SafeFiles.Matches(full, item, cancel.Token) AndAlso Not SafeFiles.Matches(full, item), "same-length tampering is rejected without weakening any digest: " & algorithm, passed)
            End Using
            File.WriteAllBytes(full, Payload)
            Dim firstHash = If(algorithm = "all", "sha512", If(algorithm = "etag", "md5", algorithm))
            Dim hashed As Long = 0, blocks = 0, failure As Exception = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(name, count)
                If name <> firstHash OrElse count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Try
                    SafeFiles.Matches(full, item, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token AndAlso hashed = 65536 AndAlso blocks = 1,
                      "cancellable matching observes the original token after its first fixed-size block: " & algorithm, passed)
            End Using
            hashed = 0 : blocks = 0
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(name, count)
                If name <> firstHash OrElse count <= 0 Then Return
                blocks += 1 : hashed += count
                If blocks = 1 Then cancel.Cancel()
            End Sub)
                Check(SafeFiles.Matches(full, item) AndAlso cancel.IsCancellationRequested AndAlso hashed >= Payload.Length,
                      "legacy matching remains non-cancellable and preserves its successful result: " & algorithm, passed)
            End Using
            hashed = 0 : failure = Nothing
            Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(name, count)
                If name <> firstHash Then Return
                hashed += count
                If hashed = Payload.Length Then cancel.Cancel()
            End Sub)
                Try
                    SafeFiles.Matches(full, item, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(hashed = Payload.Length AndAlso TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "cancellation on the final data block is observed before a digest result escapes: " & algorithm, passed)
            End Using
            Check(File.ReadAllBytes(full).SequenceEqual(Payload), "matching and cancellation never rewrite source bytes: " & algorithm, passed)
            Using lease As New FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(lease.Length = Payload.Length, "matching and cancellation release every read handle: " & algorithm, passed)
            End Using
        Next
        Dim s3 = Download(Payload, "etag"), s3Failure As Exception = Nothing, outer = False
        Using cancel As New CancellationTokenSource(), probe As New HashProbe(Sub(name, count)
            If name = "md5" AndAlso count = 16 Then outer = True : cancel.Cancel()
        End Sub)
            Try
                SafeFiles.Matches(full, s3, cancel.Token)
            Catch ex As Exception
                s3Failure = ex
            End Try
            Check(outer AndAlso TypeOf s3Failure Is OperationCanceledException AndAlso DirectCast(s3Failure, OperationCanceledException).CancellationToken = cancel.Token,
                  "S3 single-part ETag retains raw inner MD5 and observes cancellation in the outer digest", passed)
        End Using
        For Each bad In {"invalid", New String("a"c, 32) & "-2", New String("a"c, 31) & "-1"}
            s3.S3SinglePartETag = bad
            Using cancel As New CancellationTokenSource()
                Check(Not SafeFiles.Matches(full, s3, cancel.Token) AndAlso Not SafeFiles.Matches(full, s3), "both matching entry points reject malformed single-part ETags: " & bad, passed)
            End Using
        Next
        Dim plain As New DownloadFile With {.Size = -1}
        Using cancel As New CancellationTokenSource()
            Check(SafeFiles.Matches(full, plain, cancel.Token) AndAlso SafeFiles.Matches(full, plain), "unknown-size nonempty un-hashed files retain their legacy matching rule", passed)
            Check(Not SafeFiles.Matches(empty, plain, cancel.Token) AndAlso Not SafeFiles.Matches(empty, plain), "unknown-size empty un-hashed files remain unusable", passed)
            Check(Not SafeFiles.Matches(missing, plain, cancel.Token) AndAlso Not SafeFiles.Matches(missing, plain), "missing files retain the false result", passed)
            Dim mismatch = Download(Payload, "all") : mismatch.Size += 1
            Dim scanned = False
            Using probe As New HashProbe(Sub(name, count) scanned = True)
                Check(Not SafeFiles.Matches(full, mismatch, cancel.Token) AndAlso Not scanned, "size mismatches are rejected before any digest work", passed)
            End Using
        End Using
        Using cancel As New CancellationTokenSource()
            cancel.Cancel()
            For Each target In {full, empty, missing}
                Dim failure As Exception = Nothing
                Try
                    SafeFiles.Matches(target, plain, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                      "an already-cancelled token wins before file inspection: " & Path.GetFileName(target), passed)
            Next
        End Using
    End Sub
End Module

Friend Module FileMatchHashProbe
    Friend OnBlock As Action(Of String, Integer)
End Module

Public Class FileMatchProbeSha1
    Inherits SHA1
    Private ReadOnly _inner As New SHA1CryptoServiceProvider()
    Public Sub New()
        HashSizeValue = 160
    End Sub
    Public Overrides Sub Initialize()
        _inner.Initialize()
    End Sub
    Protected Overrides Sub HashCore(array As Byte(), start As Integer, count As Integer)
        _inner.TransformBlock(array, start, count, array, start) : FileMatchHashProbe.OnBlock?.Invoke("sha1", count)
    End Sub
    Protected Overrides Function HashFinal() As Byte()
        _inner.TransformFinalBlock(New Byte() {}, 0, 0) : Return _inner.Hash
    End Function
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class

Public Class FileMatchProbeSha512
    Inherits SHA512
    Private ReadOnly _inner As New SHA512CryptoServiceProvider()
    Public Sub New()
        HashSizeValue = 512
    End Sub
    Public Overrides Sub Initialize()
        _inner.Initialize()
    End Sub
    Protected Overrides Sub HashCore(array As Byte(), start As Integer, count As Integer)
        _inner.TransformBlock(array, start, count, array, start) : FileMatchHashProbe.OnBlock?.Invoke("sha512", count)
    End Sub
    Protected Overrides Function HashFinal() As Byte()
        _inner.TransformFinalBlock(New Byte() {}, 0, 0) : Return _inner.Hash
    End Function
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class

Public Class FileMatchProbeMd5
    Inherits MD5
    Private ReadOnly _inner As New MD5CryptoServiceProvider()
    Public Sub New()
        HashSizeValue = 128
    End Sub
    Public Overrides Sub Initialize()
        _inner.Initialize()
    End Sub
    Protected Overrides Sub HashCore(array As Byte(), start As Integer, count As Integer)
        _inner.TransformBlock(array, start, count, array, start) : FileMatchHashProbe.OnBlock?.Invoke("md5", count)
    End Sub
    Protected Overrides Function HashFinal() As Byte()
        _inner.TransformFinalBlock(New Byte() {}, 0, 0) : Return _inner.Hash
    End Function
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
