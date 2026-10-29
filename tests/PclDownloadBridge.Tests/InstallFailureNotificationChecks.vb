Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module InstallFailureNotificationChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    <MethodImpl(MethodImplOptions.NoInlining)>
    Private Sub ThrowOriginal(failure As Exception)
        Throw failure
    End Sub

    Private Function Hash(bytes As Byte()) As String
        Using digest = SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function Artifact(url As String, bytes As Byte()) As JObject
        Return New JObject From {{"url", url}, {"sha1", Hash(bytes)}, {"size", bytes.Length}}
    End Function

    Private Class FailingPolicy
        Inherits SourcePolicy
        Friend ReadOnly Primary As New IOException("original install operation failure")
        Friend FailurePath As String
        Friend Enabled As Boolean = True
        Friend Failures As Integer
        Private ReadOnly _server As String
        Friend Sub New(server As String, failurePath As String)
            _server = server : Me.FailurePath = failurePath : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Dim path = New Uri(url).AbsolutePath
            If Enabled AndAlso path = FailurePath Then
                Interlocked.Increment(Failures)
                ThrowOriginal(Primary)
            End If
            Return New List(Of String) From {_server & path}
        End Function
    End Class

    Private Class Observer
        Implements IProgress(Of TransferProgress)
        Friend ReadOnly Secondary As New InvalidOperationException("failure observer also failed")
        Friend Notices As Integer
        Friend FailOnNotice As Boolean = True
        Friend FailOnOrdinaryProgress As Boolean
        Friend CancelOnNotice As CancellationTokenSource
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Stage = "安装失败" Then
                Interlocked.Increment(Notices)
                CancelOnNotice?.Cancel()
                If FailOnNotice Then ThrowOriginal(Secondary)
            ElseIf FailOnOrdinaryProgress Then
                ThrowOriginal(Secondary)
            End If
        End Sub
    End Class

    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String
        Friend ReadOnly Entry As VersionEntry
        Friend ReadOnly Policy As FailingPolicy
        Friend ReadOnly Selection As ComponentSelection
        Friend ReadOnly UserPath As String
        Friend ReadOnly UserBytes As Byte() = Encoding.UTF8.GetBytes("existing user configuration")
        Friend Sub New(parent As String, name As String, components As Boolean)
            Root = Path.Combine(parent, name, ".minecraft")
            UserPath = Path.Combine(Root, "versions", "existing-user", "PCL", "Setup.ini")
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)) : File.WriteAllBytes(UserPath, UserBytes)
            Dim client = Encoding.UTF8.GetBytes("fixture original Minecraft client"), index = Encoding.UTF8.GetBytes("{""objects"":{}}")
            Server.Files("/client") = client : Server.Files("/index") = index
            Dim asset = Artifact(Server.Url & "/index", index) : asset("id") = "fixture"
            Dim metadata As New JObject From {{"id", "1.20.1"}, {"type", "release"}, {"mainClass", "vanilla.Main"},
                {"downloads", New JObject From {{"client", Artifact(Server.Url & "/client", client)}}}, {"libraries", New JArray()}, {"assets", "fixture"}, {"assetIndex", asset}}
            Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            Entry = New VersionEntry With {.Id = "1.20.1", .Url = Server.Url & "/version", .Sha1 = Hash(Server.Files("/version"))}
            Dim profilePath = "/v2/versions/loader/1.20.1/fixture/profile/json"
            Server.Files(profilePath) = Encoding.UTF8.GetBytes(New JObject From {{"inheritsFrom", Entry.Id}, {"mainClass", "fixture.FabricLoader"}, {"libraries", New JArray()}}.ToString())
            Policy = New FailingPolicy(Server.Url, If(components, profilePath, "/client"))
            If components Then
                Selection = New ComponentSelection()
                Selection.Entries(ComponentKind.Fabric) = New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = Entry.Id, .Version = "fixture"}
            End If
        End Sub
        Friend Function InstallAsync(progress As IProgress(Of TransferProgress), token As CancellationToken) As Task(Of InstallResult)
            Return New MinecraftInstaller(Policy).InstallAsync(Entry, Root, "same-name", {}, progress, token, connections:=2, components:=Selection)
        End Function
        Friend Function ReleasedFiles() As Boolean
            For Each file In Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                Using owned As New FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                End Using
            Next
            Return True
        End Function
        Friend Function PrivatePayloadsGone() As Boolean
            Dim stage = Path.Combine(Root, ".vanilla-downloader", "staging"), component = Path.Combine(Root, ".vanilla-downloader", "components")
            Return (Not Directory.Exists(stage) OrElse Not Directory.EnumerateDirectories(stage, "payload", SearchOption.AllDirectories).Any()) AndAlso
                   (Not Directory.Exists(component) OrElse Not Directory.EnumerateDirectories(component).Any())
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class

    Private Async Function FailureAsync(task As Task) As Task(Of Exception)
        If Await Task.WhenAny(task, Task.Delay(10000)) IsNot task Then Throw New TimeoutException("Installation failure cleanup did not finish.")
        Try
            Await task
            Return Nothing
        Catch ex As Exception
            Return ex
        End Try
    End Function

    Private Async Function OperationFailureAsync(root As String, passed As List(Of String), components As Boolean, cancelOnNotice As Boolean) As Task
        Dim label = If(components, "component", "download") & If(cancelOnNotice, " with caller cancellation", "")
        Using fixture As New Fixture(root, "failure-" & components & "-" & cancelOnNotice, components), source As New CancellationTokenSource()
            Dim progress As New Observer With {.CancelOnNotice = If(cancelOnNotice, source, Nothing)}
            Dim failure = Await FailureAsync(fixture.InstallAsync(progress, source.Token))
            Check(fixture.Policy.Failures = 1 AndAlso progress.Notices = 1 AndAlso fixture.Policy.Primary.StackTrace IsNot Nothing, "real install operation fails before its secondary notification observer: " & label, passed)
            If cancelOnNotice Then
                Dim cancelled = TryCast(failure, OperationCanceledException)
                Check(cancelled IsNot Nothing AndAlso cancelled.CancellationToken = source.Token, "caller cancellation during failure notification preserves its original token", passed)
            Else
                Check(failure Is fixture.Policy.Primary AndAlso failure.StackTrace.Contains(NameOf(ThrowOriginal)), "secondary failure notification cannot replace the operation exception or its throw site: " & label, passed)
            End If
            Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "same-name")) AndAlso File.ReadAllBytes(fixture.UserPath).SequenceEqual(fixture.UserBytes), "failure notification error publishes no instance and preserves existing configuration: " & label, passed)
            Check(fixture.ReleasedFiles() AndAlso fixture.PrivatePayloadsGone(), "failed installation joins downloads, releases file ownership and removes its private payloads: " & label, passed)
            fixture.Policy.Enabled = False : progress.FailOnNotice = False : progress.CancelOnNotice = Nothing
            Dim retry = Await fixture.InstallAsync(progress, CancellationToken.None)
            Check(File.Exists(Path.Combine(retry.InstancePath, "same-name.json")) AndAlso File.Exists(Path.Combine(retry.InstancePath, "same-name.jar")) AndAlso fixture.ReleasedFiles() AndAlso File.ReadAllBytes(fixture.UserPath).SequenceEqual(fixture.UserBytes), "same-name retry succeeds after notification failure without altering existing user configuration: " & label, passed)
        End Using
    End Function

    Private Async Function OrdinaryProgressAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(root, "ordinary-progress", False)
            fixture.Policy.Enabled = False
            Dim progress As New Observer With {.FailOnOrdinaryProgress = True}
            Dim failure = Await FailureAsync(fixture.InstallAsync(progress, CancellationToken.None))
            Check(failure Is progress.Secondary AndAlso failure.StackTrace.Contains(NameOf(ThrowOriginal)), "ordinary progress observer remains a meaningful original operation failure with its instance and stack", passed)
            Check(progress.Notices = 0 AndAlso fixture.Server.Requests.IsEmpty AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "same-name")), "ordinary progress failures are not globally swallowed or converted to successful installs", passed)
            Check(fixture.ReleasedFiles() AndAlso File.ReadAllBytes(fixture.UserPath).SequenceEqual(fixture.UserBytes), "ordinary progress failure releases the installation lock and preserves user configuration", passed)
            progress.FailOnOrdinaryProgress = False
            Dim retry = Await fixture.InstallAsync(progress, CancellationToken.None)
            Check(File.Exists(Path.Combine(retry.InstancePath, "same-name.json")), "ordinary progress failure permits a same-name retry", passed)
        End Using
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), scope = Path.Combine(root, "install-failure-notice-" & Guid.NewGuid().ToString("N"))
        Await OperationFailureAsync(scope, passed, False, False)
        Await OperationFailureAsync(scope, passed, True, False)
        Await OperationFailureAsync(scope, passed, False, True)
        Await OrdinaryProgressAsync(scope, passed)
        Return passed
    End Function
End Module
