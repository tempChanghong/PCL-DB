Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module ComponentCacheHashCancellationChecks
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

    Private Class Fixture
        Friend ReadOnly Root As String
        Friend ReadOnly Donor As String
        Friend ReadOnly NextDonor As String
        Friend ReadOnly Source As String
        Friend ReadOnly Target As String
        Friend ReadOnly Item As DownloadFile
        Friend ReadOnly Processor As Boolean
        Friend Sub New(root As String, scope As String, algorithm As String, processor As Boolean)
            Me.Root = Path.Combine(root, scope, "root") : Donor = Path.Combine(root, scope, "donor") : NextDonor = Path.Combine(root, scope, "next-donor")
            Me.Processor = processor
            Source = Path.Combine(root, scope, "source.bin") : Target = Path.Combine(Me.Root, "libraries", "payload.bin")
            Directory.CreateDirectory(Path.GetDirectoryName(Source)) : File.WriteAllBytes(Source, Payload)
            Directory.CreateDirectory(Path.GetDirectoryName(Target))
            Item = New DownloadFile With {.RelativePath = "libraries/payload.bin", .Size = Payload.Length}
            Using hash As HashAlgorithm = If(algorithm = "sha512", DirectCast(New SHA512CryptoServiceProvider(), HashAlgorithm), If(algorithm = "md5" OrElse algorithm = "etag", DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider()))
                Dim bytes = hash.ComputeHash(Payload)
                If algorithm = "etag" Then bytes = hash.ComputeHash(bytes)
                Dim digest = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()
                If algorithm = "sha512" Then
                    Item.Sha512 = digest
                ElseIf algorithm = "md5" Then
                    Item.Md5 = digest
                ElseIf algorithm = "etag" Then
                    Item.S3SinglePartETag = digest & "-1"
                Else
                    Item.Sha1 = digest
                End If
            End Using
        End Sub
        Friend Function Store(root As String, ParamArray donors As String()) As VerifiedComponentCache
            Return New VerifiedComponentCache(root, donors, processorOutputs:=Processor)
        End Function
        Friend Function Seed(root As String) As String
            Store(root).Save(Source, Item, CancellationToken.None)
            Dim area = Path.Combine(root, ".vanilla-downloader", If(Processor, "processor-cache", "component-cache"))
            Return Directory.EnumerateFiles(area, "*.bin", SearchOption.AllDirectories).Single()
        End Function
        Friend Function NoCopies() As Boolean
            Dim area = Path.GetDirectoryName(Root)
            Return Not Directory.EnumerateFiles(area, "*.cache-*", SearchOption.AllDirectories).Any() AndAlso Not Directory.EnumerateFiles(area, ".pcldb.cache-*.tmp", SearchOption.AllDirectories).Any()
        End Function
        Friend Function CopyExists() As Boolean
            Return Not NoCopies()
        End Function
    End Class

    Private Class Observation
        Friend Failure As Exception
        Friend OriginalToken As Boolean
        Friend Returned As Boolean
        Friend Bytes As Long
        Friend Blocks As Integer
    End Class

    Private Function Cancel(scope As String, operation As Action(Of CancellationToken), armed As Func(Of Boolean)) As Observation
        Dim result As New Observation()
        Using cancelSource As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count)
            If count <= 0 OrElse Not armed() Then Return
            result.Blocks += 1 : result.Bytes += count
            If result.Blocks = 1 Then cancelSource.Cancel()
        End Sub)
            Try
                operation(cancelSource.Token)
                result.Returned = True
            Catch ex As Exception
                result.Failure = ex
            End Try
            result.OriginalToken = TypeOf result.Failure Is OperationCanceledException AndAlso DirectCast(result.Failure, OperationCanceledException).CancellationToken = cancelSource.Token
        End Using
        Console.WriteLine("COMPONENT_CACHE_HASH_CANCEL scope=" & scope & " bytes=" & result.Bytes & " length=" & Payload.Length & " blocks=" & result.Blocks & " returned=" & result.Returned.ToString() & " exception=" & If(result.Failure?.GetType().FullName, "none"))
        Return result
    End Function

    Private Sub VerifyCancellation(result As Observation, scope As String, passed As List(Of String))
        Check(result.OriginalToken AndAlso Not result.Returned, "component cache hash cancellation propagates the original token instead of success: " & scope, passed)
        Check(result.Bytes > 0 AndAlso result.Bytes < Payload.Length AndAlso result.Blocks > 0, "component cache hash cancellation stops before reading the complete file: " & scope, passed)
    End Sub

    Private Sub Released(paths As IEnumerable(Of String), scope As String, passed As List(Of String))
        For Each path In paths.Distinct(StringComparer.OrdinalIgnoreCase)
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                If lease.Length = 0 Then Throw New InvalidOperationException("Empty fixture file: " & path)
            End Using
        Next
        Check(True, "cancelled component cache hash releases source cache and target leases: " & scope, passed)
    End Sub

    Friend Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "component-cache-hash-all" Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "component-cache-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), before = Mappings()
        For Each scope In {"restore-candidate", "save-existing", "save-copy", "restore-copy"}
            Try
                If scope.StartsWith("save-", StringComparison.Ordinal) Then
                    Save(root, scope, passed)
                Else
                    Restore(root, scope, passed)
                End If
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Component cache verification cancellation: " & scope, ex))
            End Try
        Next
        For Each pair In before
            Using hash = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(hash.GetType() Is pair.Value, "component cache probes restore the original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(FileMatchHashProbe.OnBlock Is Nothing, "component cache probes clear their process-local callback", passed)
        If failures.Count > 0 Then Throw New AggregateException("Component cache hash cancellation regressions", failures)
        Return Task.FromResult(passed)
    End Function

    Private Sub Save(root As String, scope As String, passed As List(Of String))
        Dim existing = scope = "save-existing", fixture As New Fixture(root, scope, If(existing, "sha512", "md5"), existing)
        Dim cached = fixture.Seed(fixture.Root), original = DirectCast(Payload.Clone(), Byte())
        If Not existing Then original(original.Length - 1) = original(original.Length - 1) Xor CByte(1)
        File.WriteAllBytes(cached, original)
        File.SetLastWriteTimeUtc(cached, New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc))
        Dim stamp = File.GetLastWriteTimeUtc(cached)
        Dim result = Cancel(scope, Sub(token) fixture.Store(fixture.Root).Save(fixture.Source, fixture.Item, token), Function() existing OrElse fixture.CopyExists())
        VerifyCancellation(result, scope, passed)
        Check(File.ReadAllBytes(cached).SequenceEqual(original) AndAlso File.GetLastWriteTimeUtc(cached) = stamp AndAlso File.ReadAllBytes(fixture.Source).SequenceEqual(Payload), "cancelled cache save preserves the existing cache and original source: " & scope, passed)
        Check(Not File.Exists(fixture.Target) AndAlso fixture.NoCopies(), "cancelled cache save publishes no instance file and cleans the private copy: " & scope, passed)
        Released({cached, fixture.Source}, scope, passed)
        fixture.Store(fixture.Root).Save(fixture.Source, fixture.Item, CancellationToken.None)
        Check(File.ReadAllBytes(cached).SequenceEqual(Payload) AndAlso File.ReadAllBytes(fixture.Source).SequenceEqual(Payload) AndAlso fixture.NoCopies(), "cache save retries with a fresh token and verified original content: " & scope, passed)
        Check(fixture.Store(fixture.Root).TryRestore(fixture.Target, fixture.Item, CancellationToken.None) AndAlso File.ReadAllBytes(fixture.Target).SequenceEqual(Payload), "saved cache remains usable for a fresh restore in the same namespace: " & scope, passed)
    End Sub

    Private Sub Restore(root As String, scope As String, passed As List(Of String))
        Dim copy = scope = "restore-copy", fixture As New Fixture(root, scope, If(copy, "etag", "sha1"), copy)
        Dim cached = fixture.Seed(fixture.Donor), nextCached = fixture.Seed(fixture.NextDonor)
        Dim original = DirectCast(Payload.Clone(), Byte()) : original(0) = original(0) Xor CByte(1)
        File.WriteAllBytes(fixture.Target, original)
        Dim result = Cancel(scope, Sub(token) fixture.Store(fixture.Root, fixture.Donor, fixture.NextDonor).TryRestore(fixture.Target, fixture.Item, token), Function() Not copy OrElse fixture.CopyExists())
        VerifyCancellation(result, scope, passed)
        Check(File.ReadAllBytes(fixture.Target).SequenceEqual(original) AndAlso File.ReadAllBytes(fixture.Source).SequenceEqual(Payload), "cancelled cache restore preserves the existing target and original source: " & scope, passed)
        Check(File.ReadAllBytes(cached).SequenceEqual(Payload) AndAlso File.ReadAllBytes(nextCached).SequenceEqual(Payload) AndAlso Not Directory.Exists(Path.Combine(fixture.Root, ".vanilla-downloader")), "cancelled cache restore preserves both read roots without publishing a local cache: " & scope, passed)
        Check(fixture.NoCopies(), "cancelled cache restore removes its private destination copy: " & scope, passed)
        Released({cached, nextCached, fixture.Source, fixture.Target}, scope, passed)
        Check(fixture.Store(fixture.Root, fixture.Donor, fixture.NextDonor).TryRestore(fixture.Target, fixture.Item, CancellationToken.None) AndAlso File.ReadAllBytes(fixture.Target).SequenceEqual(Payload) AndAlso fixture.NoCopies(), "cache restore retries with a fresh token and checked donor content: " & scope, passed)
        Check(File.ReadAllBytes(cached).SequenceEqual(Payload) AndAlso File.ReadAllBytes(nextCached).SequenceEqual(Payload), "successful cache restore leaves both source roots unchanged: " & scope, passed)
    End Sub
End Module
