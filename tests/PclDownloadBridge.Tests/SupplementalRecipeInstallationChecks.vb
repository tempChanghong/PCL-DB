Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalRecipeInstallationChecks
    Private Sub Check(ok As Boolean, name As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {If(New Uri(url).Host = "zkitefly.github.io", _server & "/version", url)}
        End Function
    End Class

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

    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Install As InstallationChecks.InstallFixture
        Friend ReadOnly Selected As VersionEntry = SupplementalInstallFixture.Entry("c0.0.12a_03")
        Friend ReadOnly Original As Byte()
        Friend Sub New(root As String, name As String)
            Install = New InstallationChecks.InstallFixture(root, "recipe-" & name)
            SupplementalInstallFixture.Apply(Install, Selected.Id) : Original = Install.Server.Files("/version")
        End Sub
        Friend Function Run(name As String, Optional repair As Boolean = False, Optional token As CancellationToken = Nothing, Optional progress As IProgress(Of TransferProgress) = Nothing) As Task(Of InstallResult)
            Dim entry = Selected.Copy()
            Using sha = SHA1.Create()
                entry.Sha1 = BitConverter.ToString(sha.ComputeHash(Install.Server.Files("/version"))).Replace("-", "").ToLowerInvariant()
            End Using
            Return New MinecraftInstaller(New Policy(Install.Server.Url)).InstallAsync(entry, Install.Root, name, {}, progress, token, repairExisting:=repair,
                isolationMode:=If(repair, InstanceIsolationMode.Disabled, InstanceIsolationMode.Enabled))
        End Function
        Friend Function Target(name As String, extension As String) As String
            Return Path.Combine(Install.Root, "versions", name, name & extension)
        End Function
        Friend Function Ini(name As String) As String
            Return Path.Combine(Install.Root, "versions", name, "PCL/Setup.ini")
        End Function
        Friend Function CorruptClient(name As String) As Byte()
            Dim corrupt = DirectCast(Install.Server.Files("/client").Clone(), Byte())
            corrupt(0) = corrupt(0) Xor CByte(1) : File.WriteAllBytes(Target(name, ".jar"), corrupt)
            Return corrupt
        End Function
        Friend Function Released() As Boolean
            Dim staging = Path.Combine(Install.Root, ".vanilla-downloader/staging")
            If Directory.Exists(staging) AndAlso Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any() Then Return False
            Using hold As New FileStream(Path.Combine(Install.Root, ".vanilla-downloader/install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Return True
            End Using
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Install.Dispose()
        End Sub
    End Class

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each mode In {"missing", "hybrid", "wrong-main", "hidden", "wrapper-hash", "empty-quoted", "quote-suffix"}
            Using fixture As New Fixture(root, "reject-" & mode)
                Dim bad = JObject.Parse(Encoding.UTF8.GetString(fixture.Original))
                Select Case mode
                    Case "missing" : bad.Remove("minecraftArguments")
                    Case "hybrid" : bad("arguments") = New JObject From {{"game", New JArray("--gameDir", "wrong")}}
                    Case "wrong-main" : bad("mainClass") = "not.the.expected.Main"
                    Case "hidden" : bad("minecraftArguments") = CStr(bad("minecraftArguments")) & " --researchTag -gameDir=D:\Wrong"
                    Case "wrapper-hash" : bad("libraries")(0)("downloads")("artifact")("sha1") = New String("a"c, 40)
                    Case "empty-quoted" : bad("minecraftArguments") = CStr(bad("minecraftArguments")).Replace("--gameDir ${game_directory}", "--gameDir """" ${game_directory}")
                    Case "quote-suffix" : bad("minecraftArguments") = CStr(bad("minecraftArguments")).Replace("${game_directory}", "${game_directory}""""")
                End Select
                fixture.Install.Server.Files("/version") = Encoding.UTF8.GetBytes(bad.ToString())
                Dim rejected = False
                Try
                    Await fixture.Run("retry")
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso fixture.Install.NonVersionRequests() = 0 AndAlso Not Directory.Exists(Path.GetDirectoryName(fixture.Target("retry", ".json"))) AndAlso fixture.Released(), "production gate rejects before content and releases root: " & mode, passed)
                fixture.Install.Server.Files("/version") = fixture.Original
                Await fixture.Run("retry")
                Check(File.ReadAllBytes(fixture.Target("retry", ".jar")).SequenceEqual(fixture.Install.Server.Files("/client")) AndAlso fixture.Released(), "same-name retry after production recipe rejection: " & mode, passed)
            End Using
        Next
        For Each mode In {"utf8-bom", "utf16-le", "cancel", "external-edit", "hidden", "hybrid"}
            Using fixture As New Fixture(root, "existing-" & mode)
                Await fixture.Run("renamed")
                Dim json = JObject.Parse(File.ReadAllText(fixture.Target("renamed", ".json")))
                json("minecraftArguments") = CStr(json("minecraftArguments")) & " --width 1280 --height 720 --server localhost --port 25565 --researchTag -version=research-profile"
                If mode = "hidden" Then json("minecraftArguments") = CStr(json("minecraftArguments")) & " --researchTag -gameDir=D:\Wrong"
                If mode = "hybrid" Then json("arguments") = New JObject From {{"game", New JArray("--gameDir", "wrong")}}
                Dim codec As Encoding = If(mode = "utf16-le", Encoding.Unicode, New UTF8Encoding(True))
                Dim jsonBytes = codec.GetPreamble().Concat(codec.GetBytes(json.ToString() & vbCrLf)).ToArray()
                File.WriteAllBytes(fixture.Target("renamed", ".json"), jsonBytes)
                File.AppendAllText(fixture.Ini("renamed"), "UserSetting:保留" & vbCrLf)
                Dim ini = File.ReadAllBytes(fixture.Ini("renamed")), corrupt = fixture.CorruptClient("renamed")
                Dim before = fixture.Install.NonVersionRequests(), reached = False, errorValue As Exception = Nothing, latest = jsonBytes
                If mode = "external-edit" Then
                    latest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(jsonBytes).Replace("1280", "1360"))
                    If latest.Length <> jsonBytes.Length Then Throw New InvalidOperationException("Same-length fixture changed length.")
                End If
                Using cancel As New CancellationTokenSource()
                    Dim observer As New Progress(Sub(value)
                                                     If value.Phase <> "commit" OrElse reached Then Return
                                                     reached = True
                                                     If mode = "cancel" Then cancel.Cancel()
                                                     If mode = "external-edit" Then File.WriteAllBytes(fixture.Target("renamed", ".json"), latest)
                                                 End Sub)
                    Try
                        Await fixture.Run("renamed", True, cancel.Token, observer)
                    Catch ex As Exception
                        errorValue = ex
                    End Try
                End Using
                If mode = "hidden" OrElse mode = "hybrid" Then
                    Check(TypeOf errorValue Is InvalidDataException AndAlso Not reached AndAlso fixture.Install.NonVersionRequests() = before, "existing production gate rejects before index: " & mode, passed)
                ElseIf mode = "cancel" OrElse mode = "external-edit" Then
                    Check(reached AndAlso If(mode = "cancel", TypeOf errorValue Is OperationCanceledException, TypeOf errorValue Is InvalidDataException), "recipe repair keeps cancellation and final byte guard: " & mode, passed)
                Else
                    Check(errorValue Is Nothing, "existing renamed encoded recipe accepts profile/window/server settings: " & mode, passed)
                End If
                Check(File.ReadAllBytes(fixture.Target("renamed", ".json")).SequenceEqual(latest) AndAlso File.ReadAllBytes(fixture.Ini("renamed")).SequenceEqual(ini) AndAlso fixture.Released(), "recipe repair preserves exact JSON and original isolation INI: " & mode, passed)
                If errorValue IsNot Nothing Then
                    Check(File.ReadAllBytes(fixture.Target("renamed", ".jar")).SequenceEqual(corrupt), "rejected repair leaves existing client untouched: " & mode, passed)
                    If mode = "hidden" OrElse mode = "hybrid" Then
                        json.Remove("arguments") : json("minecraftArguments") = SupplementalInstallFixture.Arguments(fixture.Selected.Id) & " --width 1360"
                        latest = codec.GetPreamble().Concat(codec.GetBytes(json.ToString())).ToArray()
                        File.WriteAllBytes(fixture.Target("renamed", ".json"), latest) ' Explicit user correction in the isolated fixture.
                    End If
                    Await fixture.Run("renamed", True)
                End If
                Check(File.ReadAllBytes(fixture.Target("renamed", ".jar")).SequenceEqual(fixture.Install.Server.Files("/client")) AndAlso File.ReadAllBytes(fixture.Target("renamed", ".json")).SequenceEqual(latest) AndAlso File.ReadAllBytes(fixture.Ini("renamed")).SequenceEqual(ini) AndAlso fixture.Released(), "production recipe repair or corrected retry succeeds without metadata rewrite: " & mode, passed)
            End Using
        Next
        For Each phase In {"pre-canceled", "metadata", "commit"}
            Using fixture As New Fixture(root, "cancel-" & phase), cancel As New CancellationTokenSource()
                If phase = "pre-canceled" Then cancel.Cancel()
                Dim observer As New Progress(Sub(value)
                                                 If (phase = "metadata" AndAlso value.Detail = "检查组件安装所需的 Java 环境") OrElse (phase = "commit" AndAlso value.Phase = "commit") Then cancel.Cancel()
                                             End Sub), canceled = False
                Try
                    Await fixture.Run("retry", token:=cancel.Token, progress:=observer)
                Catch ex As OperationCanceledException
                    canceled = True
                End Try
                Check(canceled AndAlso Not Directory.Exists(Path.GetDirectoryName(fixture.Target("retry", ".json"))) AndAlso If(phase = "pre-canceled", Not Directory.Exists(fixture.Install.Root), fixture.Released()), "production recipe cancellation releases state: " & phase, passed)
                Await fixture.Run("retry")
                Check(File.Exists(fixture.Target("retry", ".json")) AndAlso fixture.Released(), "same-name production recipe retry after cancellation: " & phase, passed)
            End Using
        Next
        Await LockedRepairAsync(root, passed)
        Return passed
    End Function

    Private Async Function LockedRepairAsync(root As String, passed As List(Of String)) As Task
        For Each cancelInside In {False, True}
            Using fixture As New Fixture(root, "locked-" & cancelInside), cancel As New CancellationTokenSource()
                Await fixture.Run("renamed")
                Dim json = File.ReadAllBytes(fixture.Target("renamed", ".json")), ini = File.ReadAllBytes(fixture.Ini("renamed")), corrupt = fixture.CorruptClient("renamed")
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim armed = False, probed = 0, readable = False, blocked = False, failure As Exception = Nothing
                CopyCancellationSha1.OnHash = Sub()
                                                 If Not armed OrElse Interlocked.CompareExchange(probed, 1, 0) <> 0 Then Return
                                                 Using reader = File.OpenRead(fixture.Target("renamed", ".json"))
                                                     readable = True
                                                 End Using
                                                 Try
                                                     Using writer As New FileStream(fixture.Target("renamed", ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
                                                     End Using
                                                 Catch ex As IOException
                                                     blocked = True
                                                 End Try
                                                 If cancelInside Then cancel.Cancel()
                                             End Sub
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Try
                        Await fixture.Run("renamed", True, cancel.Token, New Progress(Sub(value)
                                                                                          If value.Phase = "commit" Then armed = True
                                                                                      End Sub))
                    Catch ex As Exception
                        failure = ex
                    End Try
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each item In previous
                        CryptoConfig.AddAlgorithm(item.Value, item.Key)
                    Next
                End Try
                Check(probed = 1 AndAlso readable AndAlso blocked AndAlso If(cancelInside, TypeOf failure Is OperationCanceledException, failure Is Nothing), "supplemental final JSON lock permits readers and blocks writers through client check: " & cancelInside, passed)
                Check(File.ReadAllBytes(fixture.Target("renamed", ".jar")).SequenceEqual(If(cancelInside, corrupt, fixture.Install.Server.Files("/client"))) AndAlso File.ReadAllBytes(fixture.Target("renamed", ".json")).SequenceEqual(json) AndAlso File.ReadAllBytes(fixture.Ini("renamed")).SequenceEqual(ini) AndAlso fixture.Released(), "supplemental locked repair keeps atomic cancellation and preferences: " & cancelInside, passed)
                Await fixture.Run("renamed", True)
                Check(File.ReadAllBytes(fixture.Target("renamed", ".jar")).SequenceEqual(fixture.Install.Server.Files("/client")) AndAlso fixture.Released(), "supplemental locked repair retries after guard release: " & cancelInside, passed)
            End Using
        Next
    End Function
End Module
