Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module CacheBoundaryChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub
    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String
        Friend ReadOnly Donor As String
        Friend ReadOnly Cached As String
        Friend ReadOnly Item As DownloadFile
        Friend ReadOnly Bytes As Byte() = Encoding.UTF8.GetBytes("verified optional cache payload")
        Friend Sub New(root As String, name As String)
            Me.Root = Path.Combine(root, "cache-boundary-" & name) : Donor = Me.Root & "-donor"
            Dim digest As String
            Using sha As New SHA1CryptoServiceProvider()
                digest = BitConverter.ToString(sha.ComputeHash(Bytes)).Replace("-", "").ToLowerInvariant()
            End Using
            Item = New DownloadFile With {.RelativePath = "downloads/payload.bin", .Url = Server.Url & "/payload", .Size = Bytes.Length, .Sha1 = digest}
            Cached = CachePath(Donor)
            Directory.CreateDirectory(Path.GetDirectoryName(Cached)) : File.WriteAllBytes(Cached, Bytes)
            Server.Files("/payload") = Bytes
        End Sub
        Friend Function CachePath(root As String) As String
            Return SafeFiles.UnderRoot(root, ".vanilla-downloader/component-cache/sha1/" & Item.Sha1.Substring(0, 2) & "/" & Item.Sha1 & ".bin")
        End Function
        Friend Function Engine(Optional roots As IEnumerable(Of String) = Nothing) As DownloadEngine
            Return New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, If(roots, {Donor}), 1, Root, useComponentCache:=True)
        End Function
        Friend Function Target() As String
            Return SafeFiles.UnderRoot(Root, Item.RelativePath)
        End Function
        Friend Function NoCopies() As Boolean
            Return Not Directory.EnumerateFiles(Root, "*.cache-*", SearchOption.AllDirectories).Any()
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "component-cache-boundary-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-boundary-publish" Then Await PublishAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-boundary-read" Then Await ReadAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-boundary-cancel" Then Await CancelAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function PublishAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"locked", "directory"}
            Using fixture As New Fixture(root, "publish-" & mode), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim target = fixture.Target(), original(fixture.Bytes.Length - 1) As Byte
                Directory.CreateDirectory(Path.GetDirectoryName(target))
                If mode = "locked" Then
                    File.WriteAllBytes(target, original)
                Else
                    Directory.CreateDirectory(target) : File.WriteAllText(Path.Combine(target, "keep.txt"), "user folder")
                End If
                Using hold As FileStream = If(mode = "locked", New FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read), Nothing), engine = fixture.Engine()
                    Dim failure As Exception = Nothing
                    Try
                        Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                    Console.WriteLine("CACHE_PUBLISH mode=" & mode & " http=" & fixture.Server.Requests.Values.Sum() & " retries=" & engine.Retries)
                    Check(TypeOf failure Is IOException OrElse TypeOf failure Is UnauthorizedAccessException, "cache publication returns the actual destination error: " & mode, passed)
                    Check(fixture.Server.Requests.IsEmpty AndAlso engine.Reused = 0 AndAlso engine.NetworkBytes = 0 AndAlso engine.Retries = 0, "cache destination failure does not fall through to HTTP or count as a source retry: " & mode, passed)
                    Check(If(mode = "locked", File.ReadAllBytes(target).SequenceEqual(original), File.ReadAllText(Path.Combine(target, "keep.txt")) = "user folder"), "cache destination failure preserves the existing target: " & mode, passed)
                    Check(fixture.NoCopies() AndAlso File.ReadAllBytes(fixture.Cached).SequenceEqual(fixture.Bytes), "cache publication failure cleans its private copy and preserves donor content: " & mode, passed)
                End Using
                If mode = "directory" Then
                    File.Delete(Path.Combine(target, "keep.txt")) : Directory.Delete(target)
                End If
                Using engine = fixture.Engine()
                    Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0 AndAlso fixture.Server.Requests.IsEmpty AndAlso SafeFiles.Matches(target, fixture.Item), "retry after resolving the destination conflict restores the checked cache without HTTP: " & mode, passed)
                End Using
            End Using
        Next
    End Function
    Private Async Function ReadAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"corrupt-network", "locked-network", "corrupt-next-cache", "locked-next-cache"}
            Using fixture As New Fixture(root, mode), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim nextCache = mode.EndsWith("next-cache", StringComparison.Ordinal), locked = mode.StartsWith("locked", StringComparison.Ordinal)
                Dim donor2 = fixture.Donor & "-next", second = fixture.CachePath(donor2)
                If nextCache Then
                    Directory.CreateDirectory(Path.GetDirectoryName(second)) : File.WriteAllBytes(second, fixture.Bytes)
                End If
                Dim original = DirectCast(fixture.Bytes.Clone(), Byte())
                If Not locked Then original(0) = original(0) Xor CByte(1)
                File.WriteAllBytes(fixture.Cached, original)
                Using hold As FileStream = If(locked, New FileStream(fixture.Cached, FileMode.Open, FileAccess.Read, FileShare.None), Nothing), engine = fixture.Engine({fixture.Donor, donor2})
                    Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(SafeFiles.Matches(fixture.Target(), fixture.Item) AndAlso engine.Reused = If(nextCache, 1, 0) AndAlso engine.NetworkBytes = If(nextCache, 0L, CLng(fixture.Bytes.Length)) AndAlso fixture.Server.Requests.Values.Sum() = If(nextCache, 0, 1), "unavailable optional cache falls back to the next cache or checked download: " & mode, passed)
                    Check(engine.Retries = 0 AndAlso fixture.NoCopies(), "optional cache read failure leaves no partial copies or source retry statistics: " & mode, passed)
                End Using
                Check(File.ReadAllBytes(fixture.Cached).SequenceEqual(original), "optional cache fallback preserves the unavailable donor: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function CancelAsync(root As String, passed As List(Of String)) As Task
        For Each boundary In {1, 2}
            Using fixture As New Fixture(root, "cancel-" & boundary), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim count As Integer
                CopyCancellationSha1.OnHash = Sub()
                    If Interlocked.Increment(count) = boundary Then cancel.Cancel()
                End Sub
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Using engine = fixture.Engine(), linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token)
                        Dim cancelled = False
                        Try
                            Await engine.DownloadAsync(fixture.Root, {fixture.Item}, linked.Token)
                        Catch ex As OperationCanceledException When cancel.IsCancellationRequested AndAlso Not timeout.IsCancellationRequested
                            cancelled = True
                        End Try
                        Check(cancelled AndAlso Not File.Exists(fixture.Target()) AndAlso engine.Reused = 0, "cancellation after cache source or copy verification publishes nothing: " & boundary, passed)
                        Check(engine.NetworkBytes = 0 AndAlso engine.Retries = 0 AndAlso fixture.Server.Requests.IsEmpty AndAlso fixture.NoCopies(), "cache verification cancellation does not trigger HTTP and removes its private copy: " & boundary, passed)
                    End Using
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each entry In previous
                        CryptoConfig.AddAlgorithm(entry.Value, entry.Key)
                    Next
                End Try
                Check(File.ReadAllBytes(fixture.Cached).SequenceEqual(fixture.Bytes), "cache verification cancellation preserves the original donor: " & boundary, passed)
                Using engine = fixture.Engine()
                    Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(engine.Reused = 1 AndAlso fixture.Server.Requests.IsEmpty AndAlso SafeFiles.Matches(fixture.Target(), fixture.Item), "retry after cache verification cancellation restores checked content: " & boundary, passed)
                End Using
            End Using
        Next
    End Function
End Module
