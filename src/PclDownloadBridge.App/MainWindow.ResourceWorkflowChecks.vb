Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    ' This server and all actual install destinations belong to the UI fixture.
    ' It exercises the production downloader/publisher without public endpoints.
    Private NotInheritable Class ResourceWorkflowServer
        Implements IDisposable
        Private ReadOnly _listener As New TcpListener(IPAddress.Loopback, 0), _bytes As Byte(), _gate As New Object()
        Private ReadOnly _worker As Task
        Private _client As TcpClient, _stopped As Boolean, _requests As Integer
        Friend ReadOnly Property Url As String
        Friend ReadOnly Property Requests As Integer
            Get
                Return Volatile.Read(_requests)
            End Get
        End Property
        Friend Sub New(bytes As Byte())
            _bytes = bytes : _listener.Start()
            Url = "http://127.0.0.1:" & DirectCast(_listener.LocalEndpoint, IPEndPoint).Port
            _worker = Task.Run(Sub() Serve())
        End Sub
        Private Sub Serve()
            Try
                While Not Volatile.Read(_stopped)
                    Using client = _listener.AcceptTcpClient()
                        SyncLock _gate
                            If _stopped Then Return
                            _client = client
                        End SyncLock
                        Try
                            Using stream = client.GetStream(), reader As New StreamReader(stream, Encoding.ASCII, False, 1024, True)
                                Dim first = reader.ReadLine()
                                If first Is Nothing Then Continue While
                                While Not String.IsNullOrEmpty(reader.ReadLine())
                                End While
                                Interlocked.Increment(_requests)
                                Dim header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK" & vbCrLf & "Content-Length: " & _bytes.Length & vbCrLf & "Connection: close" & vbCrLf & vbCrLf)
                                stream.Write(header, 0, header.Length)
                                If Not first.StartsWith("HEAD ", StringComparison.Ordinal) Then stream.Write(_bytes, 0, _bytes.Length)
                                stream.Flush()
                            End Using
                        Finally
                            SyncLock _gate
                                _client = Nothing
                            End SyncLock
                        End Try
                    End Using
                End While
            Catch ex As Exception When _stopped AndAlso (TypeOf ex Is SocketException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException)
            End Try
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _gate
                Volatile.Write(_stopped, True) : _client?.Dispose() : _listener.Stop()
            End SyncLock
            _worker.GetAwaiter().GetResult()
        End Sub
    End Class
    Private Class ResourceWorkflowPolicy
        Inherits SourcePolicy
        Private ReadOnly _url As String
        Friend Sub New(url As String)
            _url = url : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_url & "/content"}
        End Function
    End Class
    Private Class ResourceWorkflowProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _report As Action(Of TransferProgress)
        Friend Sub New(report As Action(Of TransferProgress))
            _report = report
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _report(value)
        End Sub
    End Class
    Private Async Function CheckResourceWorkflowAsync(directory As String, fixture As String, target As LauncherInstallTarget, configPath As String, config As Byte(), host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        SelectResourceKind(CommunityResourceKind.Mod) : _categoryMotion.Finish() : layout()
        PackSearchSource.SelectedIndex = 2 : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : _packResultMotion.Finish() : layout()
        Dim project = DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject)
        OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailCompletedProject Is project) : _categoryMotion.Finish() : layout()
        Dim source = LocalModJarForChecks(Path.Combine(fixture, "source/resource.jar"), "installed_via_download"), bytes = File.ReadAllBytes(source), release = _packReleases.Single()
        release.Size = bytes.Length
        Using sha = SHA1.Create() : release.Sha1 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() : End Using
        Dim cache = Path.Combine(fixture, "workflow-cache"), destination = Path.GetFullPath(Path.Combine(target.Root, "mods/resource.jar")), attempts = 0
        Using server As New ResourceWorkflowServer(bytes)
            _resourceInstallRunner = Async Function(plan, ignoredCache, ignoredPolicy, progress, token)
                                         Dim attempt = Interlocked.Increment(attempts), cancellationSent = 0
                                         Dim observed As New ResourceWorkflowProgress(Sub(value)
                                             progress.Report(value)
                                             If attempt = 1 AndAlso value.Phase = "commit" AndAlso value.StageFraction = 0 AndAlso Interlocked.Exchange(cancellationSent, 1) = 0 Then
                                                 Dispatcher.Invoke(Sub() CancelInstall(Nothing, Nothing))
                                             End If
                                         End Sub)
                                         Await plan.InstallAsync(cache, New ResourceWorkflowPolicy(server.Url), observed, token, connections:=1)
                                     End Function
            Dim install As Action = Sub()
                                        Dim rows = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel), row = rows.Children.OfType(Of PclListItem)().Single()
                                        InstallCommunityResource(release, row.Children.OfType(Of PclIconButton)().Single())
                                    End Sub
            install() : Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading)
            ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = "Shared")
            ConfirmOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()
            Dim requests = server.Requests
            check(attempts = 1 AndAlso requests > 0 AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso Not File.Exists(destination) AndAlso _managementDirty, "real resource acquisition followed by UI cancellation before publication preserves the instance and invalidates management")
            Await ResumeResourceTaskForChecksAsync() : layout()
            check(_packProject Is project AndAlso _packReleases.Single() Is release AndAlso _packMode = PackBrowserMode.Detail, "cancelled real installation returns to the same release and dependency detail context")
            install() : Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading)
            ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = "Shared")
            ConfirmOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()
            check(attempts = 2 AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso File.ReadAllBytes(destination).SequenceEqual(bytes) AndAlso server.Requests = requests, "real picker retry publishes its verified Mod cache without another HTTP request")
            check(_taskResourceFile = destination AndAlso TaskFolderPath() = Path.GetDirectoryName(destination) AndAlso Not _localModReadSucceeded AndAlso Not File.Exists(Path.Combine(target.Root, "versions/Isolated/mods/resource.jar")), "successful shared Mod installation exposes the correct folder and invalidates only the bound management context")
            Await ResumeResourceTaskForChecksAsync() : ShowManagement(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish()
            Await WaitPackUiAsync(Function() Not _managementDirty AndAlso Not _managementScanning AndAlso _localModReadSucceeded AndAlso Not _localModScanning) : layout()
            check(_localMods.Any(Function(entry) entry.ModIds.Contains("installed_via_download") AndAlso entry.FilePath = destination AndAlso entry.Enabled) AndAlso _localModInstance.Name = "Shared" AndAlso File.ReadAllBytes(configPath).SequenceEqual(config), "returning from a real download and publication rescans the bound shared Mod and preserves external settings")
            File.WriteAllText(Path.Combine(directory, "resource-workflow.json"), New JObject From {{"Attempts", attempts}, {"RequestsAfterCancel", requests}, {"RequestsAfterRetry", server.Requests}, {"Destination", destination}, {"Size", bytes.Length}, {"Sha1", release.Sha1}, {"LocalModCount", _localMods.Count}, {"ExternalConfigUnchanged", File.ReadAllBytes(configPath).SequenceEqual(config)}, {"GameLaunched", False}, {"UserGameDirectoriesWritten", False}}.ToString())
        End Using
    End Function
End Class
