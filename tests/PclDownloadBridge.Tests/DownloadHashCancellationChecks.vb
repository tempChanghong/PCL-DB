Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module DownloadHashCancellationChecks
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

    Private Class ProgressCapture
        Implements IProgress(Of TransferProgress)
        Friend Completed As Boolean
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Completed > 0 Then Completed = True
        End Sub
    End Class

    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String
        Friend ReadOnly Donor As String
        Friend ReadOnly Item As DownloadFile
        Friend Sub New(root As String, label As String, algorithm As String)
            Me.Root = Path.Combine(root, label) : Donor = Me.Root & "-donor"
            Item = New DownloadFile With {.RelativePath = "libraries/payload.bin", .Url = Server.Url & "/payload", .Size = Payload.Length, .Category = "library"}
            Using hash As HashAlgorithm = If(algorithm = "sha512", DirectCast(New SHA512CryptoServiceProvider(), HashAlgorithm), If(algorithm = "md5", DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider()))
                Dim digest = BitConverter.ToString(hash.ComputeHash(Payload)).Replace("-", "")
                If algorithm = "sha512" Then
                    Item.Sha512 = digest
                ElseIf algorithm = "md5" Then
                    Item.Md5 = digest
                Else
                    Item.Sha1 = digest
                End If
            End Using
            Server.Files("/payload") = Payload
        End Sub
        Friend Function Target() As String
            Return SafeFiles.UnderRoot(Root, Item.RelativePath)
        End Function
        Friend Function Source() As String
            Return SafeFiles.UnderRoot(Donor, Item.RelativePath)
        End Function
        Friend Function Engine(progress As IProgress(Of TransferProgress), useDonor As Boolean) As DownloadEngine
            Return New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, If(useDonor, {Donor}, New String() {}), 1, Root)
        End Function
        Friend Function NoCopies() As Boolean
            Return Not Directory.Exists(Root) OrElse Not Directory.EnumerateFiles(Root, ".pcldb.reuse-*.tmp", SearchOption.AllDirectories).Any()
        End Function
        Friend Function Complete() As String
            Return Directory.EnumerateFiles(Path.Combine(Root, ".vanilla-downloader/transfers"), "complete.tmp", SearchOption.AllDirectories).Single()
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class

    Private Class Observation
        Friend Failure As Exception
        Friend Bytes As Long
        Friend Blocks As Integer
        Friend Reused As Integer
        Friend Retries As Integer
        Friend NetworkBytes As Long
        Friend Completed As Boolean
        Friend OriginalToken As Boolean
    End Class

    Private Async Function CancelAsync(fixture As Fixture, label As String, useDonor As Boolean, armed As Func(Of Boolean)) As Task(Of Observation)
        Dim result As New Observation(), progress As New ProgressCapture()
        Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count)
            If count <= 0 OrElse Not armed() Then Return
            result.Blocks += 1 : result.Bytes += count
            If result.Blocks = 1 Then cancel.Cancel()
        End Sub), engine = fixture.Engine(progress, useDonor)
            Try
                Await engine.DownloadAsync(fixture.Root, {fixture.Item}, cancel.Token)
            Catch ex As Exception
                result.Failure = ex
            End Try
            result.OriginalToken = TypeOf result.Failure Is OperationCanceledException AndAlso DirectCast(result.Failure, OperationCanceledException).CancellationToken = cancel.Token
            result.Reused = engine.Reused : result.Retries = engine.Retries : result.NetworkBytes = engine.NetworkBytes : result.Completed = progress.Completed
        End Using
        Console.WriteLine("DOWNLOAD_HASH_CANCEL scope=" & label & " bytes=" & result.Bytes & " length=" & Payload.Length & " blocks=" & result.Blocks & " reused=" & result.Reused & " exception=" & If(result.Failure?.GetType().FullName, "none"))
        Return result
    End Function

    Private Sub VerifyCancellation(result As Observation, label As String, passed As List(Of String))
        Check(result.OriginalToken, "download hash cancellation preserves the outer caller token: " & label, passed)
        Check(result.Bytes > 0 AndAlso result.Bytes < Payload.Length AndAlso result.Blocks > 0, "download hash cancellation stops before consuming the complete file: " & label, passed)
        Check(result.Reused = 0 AndAlso result.Retries = 0 AndAlso Not result.Completed, "cancelled verification cannot report reuse retry or completion: " & label, passed)
    End Sub

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "download-hash-all" Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "download-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), before = Mappings()
        For Each scope In {"target", "donor", "copy", "final", "cache", "head"}
            Try
                If scope = "target" OrElse scope = "donor" OrElse scope = "copy" Then
                    Await LocalAsync(root, scope, passed)
                Else
                    Await CachedAsync(root, scope, passed)
                End If
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Download verification cancellation: " & scope, ex))
            End Try
        Next
        For Each pair In before
            Using hash = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(hash.GetType() Is pair.Value, "download probes restore the original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(FileMatchHashProbe.OnBlock Is Nothing, "download probes clear their process-local callback", passed)
        If failures.Count > 0 Then Throw New AggregateException("Download hash cancellation regressions", failures)
        Return passed
    End Function

    Private Async Function LocalAsync(root As String, scope As String, passed As List(Of String)) As Task
        Dim algorithm = If(scope = "donor", "sha512", If(scope = "copy", "md5", "sha1"))
        Using fixture As New Fixture(root, scope, algorithm)
            Dim original = If(scope = "target", fixture.Target(), fixture.Source())
            Directory.CreateDirectory(Path.GetDirectoryName(original)) : File.WriteAllBytes(original, Payload)
            Dim result = Await CancelAsync(fixture, scope, scope <> "target", Function()
                Return scope <> "copy" OrElse (Directory.Exists(Path.GetDirectoryName(fixture.Target())) AndAlso Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Target()), ".pcldb.reuse-*.tmp").Any())
            End Function)
            VerifyCancellation(result, scope, passed)
            Check(result.NetworkBytes = 0 AndAlso fixture.Server.Requests.IsEmpty AndAlso File.ReadAllBytes(original).SequenceEqual(Payload), "local hash cancellation preserves the original and sends no HTTP: " & scope, passed)
            Check((scope = "target" OrElse Not File.Exists(fixture.Target())) AndAlso fixture.NoCopies(), "local hash cancellation publishes no new target and removes private copies: " & scope, passed)
            Using released As New FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(released.Length = Payload.Length, "local hash cancellation releases its original read lease: " & scope, passed)
            End Using
            Using retry = fixture.Engine(Nothing, scope <> "target")
                Await retry.DownloadAsync(fixture.Root, {fixture.Item}, CancellationToken.None)
                Check(retry.Reused = 1 AndAlso retry.NetworkBytes = 0 AndAlso retry.Retries = 0 AndAlso fixture.Server.Requests.IsEmpty AndAlso SafeFiles.Matches(fixture.Target(), fixture.Item), "local retry verifies and reuses the original without network or retries: " & scope, passed)
            End Using
            Check(File.ReadAllBytes(original).SequenceEqual(Payload) AndAlso fixture.NoCopies(), "local retry preserves donor bytes and leaves no private copy: " & scope, passed)
        End Using
    End Function

    Private Async Function CachedAsync(root As String, scope As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(root, scope, If(scope = "cache", "sha512", "sha1"))
            If scope = "head" Then
                fixture.Item.Category = "modrinth"
                fixture.Server.HeadLengths("/payload") = Payload.Length + 1L
            End If
            If scope = "cache" Then
                Dim seeded = Await CancelAsync(fixture, "cache-seed", False, Function() True)
                Check(seeded.OriginalToken AndAlso Not File.Exists(fixture.Target()) AndAlso File.ReadAllBytes(fixture.Complete()).SequenceEqual(Payload), "real cancelled download seeds the complete verified retry cache without a target", passed)
            End If
            Dim requests = fixture.Server.Requests.Values.Sum()
            Dim result = Await CancelAsync(fixture, scope, False, Function() True)
            VerifyCancellation(result, scope, passed)
            Dim cache = fixture.Complete()
            Check(Not File.Exists(fixture.Target()) AndAlso File.ReadAllBytes(cache).SequenceEqual(Payload) AndAlso fixture.NoCopies(), "cancelled final or cached hash retains complete content without publishing the target: " & scope, passed)
            Check(fixture.Server.Requests.Values.Sum() = requests + If(scope = "head", 2, If(scope = "final", 1, 0)), "cached verification cancellation does not issue another payload request: " & scope, passed)
            Using released As New FileStream(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(released.Length = Payload.Length, "cancelled hash releases the complete-cache read lease: " & scope, passed)
            End Using
            requests = fixture.Server.Requests.Values.Sum()
            Using retry = fixture.Engine(Nothing, False)
                Await retry.DownloadAsync(fixture.Root, {fixture.Item}, CancellationToken.None)
                Check(retry.Reused = 1 AndAlso retry.NetworkBytes = 0 AndAlso fixture.Server.Requests.Values.Sum() = requests AndAlso SafeFiles.Matches(fixture.Target(), fixture.Item), "retry publishes the checked complete cache without HTTP: " & scope, passed)
            End Using
        End Using
    End Function
End Module
