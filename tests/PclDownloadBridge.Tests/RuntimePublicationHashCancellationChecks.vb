Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module RuntimePublicationHashCancellationChecks
    Private ReadOnly Payload As Byte() = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()

    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Mappings() As Dictionary(Of String, Type)
        Dim result As New Dictionary(Of String, Type)
        For Each name In {"SHA1", "System.Security.Cryptography.SHA1", "SHA512", "System.Security.Cryptography.SHA512", "MD5", "System.Security.Cryptography.MD5"}
            Using hash = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                result.Add(name, hash.GetType())
            End Using
        Next
        Return result
    End Function

    Private Class Probe
        Implements IDisposable
        Private ReadOnly _previous As Dictionary(Of String, Type) = Mappings()
        Private ReadOnly _callback As Action(Of String, Integer) = FileMatchHashProbe.OnBlock
        Friend Sub New(block As Action(Of String, Integer))
            FileMatchHashProbe.OnBlock = block
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha1), {"SHA1", "System.Security.Cryptography.SHA1"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha512), {"SHA512", "System.Security.Cryptography.SHA512"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeMd5), {"MD5", "System.Security.Cryptography.MD5"})
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            FileMatchHashProbe.OnBlock = _callback
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class

    Private Class Observation
        Friend Failure As Exception
        Friend OriginalToken As Boolean
        Friend Returned As Boolean
        Friend Bytes As Long
        Friend Blocks As Integer
        Friend WriteBlocked As Boolean
    End Class

    Private Function Item(algorithm As String) As DownloadFile
        Dim result As New DownloadFile With {.RelativePath = "libraries/runtime/payload.jar", .Size = Payload.Length, .Category = "library"}
        Using hash As HashAlgorithm = If(algorithm = "sha512", DirectCast(New SHA512CryptoServiceProvider(), HashAlgorithm), If(algorithm = "md5" OrElse algorithm = "etag", DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider()))
            Dim digest = hash.ComputeHash(Payload)
            If algorithm = "etag" Then digest = hash.ComputeHash(digest)
            Dim text = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant()
            If algorithm = "sha512" Then
                result.Sha512 = text
            ElseIf algorithm = "md5" Then
                result.Md5 = text
            ElseIf algorithm = "etag" Then
                result.S3SinglePartETag = text & "-1"
            Else
                result.Sha1 = text
            End If
        End Using
        Return result
    End Function

    Private Sub Released(paths As IEnumerable(Of String))
        For Each full In paths.Where(Function(path) File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase)
            Using lease As New FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                If lease.Length <> Payload.Length Then Throw New InvalidOperationException("Unexpected runtime fixture length.")
            End Using
        Next
    End Sub

    Friend Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "runtime-publication-hash-all" Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "runtime-publication-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), observations As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        For Each scope In {"source", "existing-match", "existing-mismatch", "temporary-copy", "committed-new", "committed-replace"}
            Try
                RunCase(root, scope, passed, observations)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Runtime publication hash cancellation: " & scope, ex))
            End Try
        Next
        For Each pair In before
            Using hash = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(hash.GetType() Is pair.Value, "runtime publication cancellation restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "runtime publication cancellation restores the process-local callback", passed)
        Dim report = Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)}, {"Cases", observations},
            {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("RUNTIME_PUBLICATION_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Runtime publication hash cancellation regressions", failures)
        Return Task.FromResult(passed)
    End Function

    Private Sub RunCase(root As String, scope As String, passed As List(Of String), observations As JObject)
        Dim area = Path.Combine(root, scope), work = Path.Combine(area, "work"), destination = Path.Combine(area, "shared")
        Dim source = SafeFiles.UnderRoot(work, "libraries/runtime/payload.jar"), target = SafeFiles.UnderRoot(destination, "libraries/runtime/payload.jar")
        Directory.CreateDirectory(Path.GetDirectoryName(source)) : Directory.CreateDirectory(Path.GetDirectoryName(target))
        File.WriteAllBytes(source, Payload)
        Dim existed = scope.StartsWith("existing-", StringComparison.Ordinal) OrElse scope = "committed-replace"
        Dim committed = scope.StartsWith("committed-", StringComparison.Ordinal), original = DirectCast(Payload.Clone(), Byte())
        If scope = "existing-mismatch" OrElse scope = "committed-replace" Then original(0) = original(0) Xor CByte(1)
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
        If existed Then File.WriteAllBytes(target, original) : File.SetLastWriteTimeUtc(target, stamp)
        Dim algorithm = If(scope = "existing-match" OrElse scope = "committed-new", "sha512", If(scope = "existing-mismatch" OrElse scope = "committed-replace", "md5", If(scope = "temporary-copy", "etag", "sha1")))
        Dim expected = Item(algorithm), result As New Observation(), frozen As List(Of DownloadFile) = Nothing
        Dim temporary As Func(Of String) = Function() Directory.EnumerateFiles(destination, "*.component-*", SearchOption.AllDirectories).SingleOrDefault()
        Dim armed As Func(Of Boolean) = Function()
                                            If scope = "source" Then Return True
                                            If scope = "temporary-copy" Then Return temporary() IsNot Nothing
                                            If committed Then Return File.Exists(target) AndAlso (Not existed OrElse File.GetLastWriteTimeUtc(target) <> stamp)
                                            Return True
                                        End Function
        Dim seen As Long = 0
        Using cancel As New CancellationTokenSource(), publication As New ComponentRuntimePublication(destination)
            Using probe As New Probe(Sub(name, count)
                                         If count <= 0 Then Return
                                         ' Existing-target checks follow exactly one complete source hash.
                                         Dim inPhase = armed() AndAlso (Not scope.StartsWith("existing-", StringComparison.Ordinal) OrElse seen >= Payload.Length)
                                         seen += count
                                         If Not inPhase Then Return
                                         result.Blocks += 1 : result.Bytes += count
                                         If result.Blocks <> 1 Then Return
                                         Dim protectedPath = If(scope = "source", source, If(scope = "temporary-copy", temporary(), target))
                                         Try
                                             Using blocked As New FileStream(protectedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                                             End Using
                                         Catch ex As IOException
                                             result.WriteBlocked = True
                                         End Try
                                         cancel.Cancel()
                                     End Sub)
                Try
                    publication.Publish(work, {expected}, cancel.Token)
                    result.Returned = True
                Catch ex As Exception
                    result.Failure = ex
                End Try
                result.OriginalToken = TypeOf result.Failure Is OperationCanceledException AndAlso DirectCast(result.Failure, OperationCanceledException).CancellationToken = cancel.Token
            End Using
            frozen = publication.FrozenFiles
        End Using
        observations(scope) = New JObject From {{"Algorithm", algorithm}, {"Bytes", result.Bytes}, {"Length", Payload.Length}, {"Blocks", result.Blocks}, {"Returned", result.Returned},
            {"OriginalToken", result.OriginalToken}, {"WriteBlocked", result.WriteBlocked}, {"Exception", result.Failure?.GetType().FullName},
            {"FrozenCount", frozen.Count}, {"SourceUnchanged", File.ReadAllBytes(source).SequenceEqual(Payload)}, {"TargetExists", File.Exists(target)},
            {"TargetContainsOriginal", File.Exists(target) AndAlso File.ReadAllBytes(target).SequenceEqual(original)},
            {"TargetContainsPublishedPayload", File.Exists(target) AndAlso File.ReadAllBytes(target).SequenceEqual(Payload)},
            {"OriginalTargetTimestampPreserved", File.Exists(target) AndAlso File.GetLastWriteTimeUtc(target) = stamp}, {"PrivateCopyRemoved", temporary() Is Nothing}}
        Console.WriteLine("RUNTIME_PUBLICATION_HASH_CANCEL scope=" & scope & " bytes=" & result.Bytes & " length=" & Payload.Length & " blocks=" & result.Blocks & " returned=" & result.Returned & " originalToken=" & result.OriginalToken)
        Check(result.OriginalToken AndAlso Not result.Returned, "runtime publication hash cancellation propagates the caller token without success: " & scope, passed)
        Check(frozen.Count = 0, "cancelled publication does not export an unaccepted runtime expectation: " & scope, passed)
        Check(File.ReadAllBytes(source).SequenceEqual(Payload) AndAlso If(committed, File.Exists(target) AndAlso File.ReadAllBytes(target).SequenceEqual(Payload),
            If(existed, File.ReadAllBytes(target).SequenceEqual(original) AndAlso File.GetLastWriteTimeUtc(target) = stamp, Not File.Exists(target))),
            "cancellation preserves source and the correct precommit or committed destination state: " & scope, passed)
        Check(temporary() Is Nothing, "cancelled runtime hash removes only its private uncommitted copy: " & scope, passed)
        Released({source, target})
        Check(result.WriteBlocked, "runtime hash blocks replacement while reading and releases every lease after cancellation: " & scope, passed)
        Using retryToken As New CancellationTokenSource()
            Dim retry As New ComponentRuntimePublication(destination)
            Try
                retry.Publish(work, {expected}, retryToken.Token)
                Check(retry.FrozenFiles.Count = 1 AndAlso SafeFiles.Matches(target, expected) AndAlso File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso temporary() Is Nothing,
                    "runtime publication retries with a fresh token and checked original payload: " & scope, passed)
                retryToken.Cancel()
            Finally
                retry.Dispose()
            End Try
        End Using
        Check(File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso File.ReadAllBytes(source).SequenceEqual(Payload) AndAlso temporary() Is Nothing,
            "late cancellation after successful publication does not remove the complete shared runtime: " & scope, passed)
        Released({source, target})
        Check(True, "successful publication cleanup releases its retained read leases despite late cancellation: " & scope, passed)
        Check(result.Bytes > 0 AndAlso result.Bytes < Payload.Length AndAlso result.Blocks > 0,
            "runtime publication cancellation stops the active hash before reading the complete file: " & scope, passed)
    End Sub
End Module
