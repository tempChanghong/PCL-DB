Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModrinthEnvironmentChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function ClientOnlyManifest() As JObject
        Dim manifest = ModrinthChecks.Manifest()
        For Each item As JObject In DirectCast(manifest("files"), JArray)
            DirectCast(item("env"), JObject).Remove("server")
        Next
        Return manifest
    End Function

    Friend Async Function RunAsync(root As String, passed As List(Of String)) As Task
        ' HMCL's export writes only env.client for disabled/optional mods.
        ' PCL's client importer bases the choice on client, not server.
        Dim package = ModrinthPackage.Read(ModrinthChecks.Pack(root, "client-env", ClientOnlyManifest()))
        Check(package.References.Select(Function(item) item.Client).SequenceEqual({"required", "optional", "unsupported"}), "MR accepts client-only environment maps without losing client choices", passed)
        For Each mode In {"required", "optional", "unsupported"}
            Dim manifest = ClientOnlyManifest()
            manifest("files")(0)("env")("server") = mode
            Check(ModrinthPackage.Read(ModrinthChecks.Pack(root, "server-env-" & mode, manifest)).References(0).Client = "required", "MR explicit server mode cannot change the client requirement: " & mode, passed)
        Next
        For Each issue In {"missing-client", "empty-env", "invalid-client", "null-client", "invalid-server", "null-server", "numeric-server"}
            Dim manifest = ClientOnlyManifest(), environment = DirectCast(manifest("files")(0)("env"), JObject)
            Select Case issue
                Case "missing-client" : environment.Remove("client") : environment("server") = "required"
                Case "empty-env" : environment.RemoveAll()
                Case "invalid-client" : environment("client") = "sometimes"
                Case "null-client" : environment("client") = JValue.CreateNull()
                Case "invalid-server" : environment("server") = "sometimes"
                Case "null-server" : environment("server") = JValue.CreateNull()
                Case "numeric-server" : environment("server") = 1
            End Select
            Dim rejected = False
            Try
                ModrinthPackage.Read(ModrinthChecks.Pack(root, "env-rejected-" & issue, manifest))
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected, "MR still rejects ambiguous or invalid environment declarations: " & issue, passed)
        Next
        For Each selected In {False, True}
            Using fixture As New InstallationChecks.InstallFixture(root, "mr-client-env-" & selected)
                ModrinthChecks.Configure(fixture)
                Dim source = ModrinthChecks.Pack(root, "client-env-install-" & selected, ClientOnlyManifest())
                Dim current = ModrinthPackage.Read(source), installer As New ModrinthInstaller(New ModrinthChecks.Policy(fixture.Server.Url))
                Dim choices = If(selected, {"mods/optional.jar"}, New String() {})
                Dim sharedModPath = Path.Combine(fixture.Root, "mods", "existing.jar"), settings = Path.Combine(fixture.Root, "PCL", "Setup.ini")
                Directory.CreateDirectory(Path.GetDirectoryName(sharedModPath)) : File.WriteAllText(sharedModPath, "existing shared mod")
                Directory.CreateDirectory(Path.GetDirectoryName(settings)) : File.WriteAllText(settings, "LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:keep")
                Dim cancelled = False
                Using cancel As New CancellationTokenSource()
                    Try
                        Await installer.InstallAsync(current, fixture.Root, "pack", {}, choices, New ModrinthChecks.Progress(Sub(value)
                            If value.Stage = "完成安装" Then cancel.Cancel()
                        End Sub), cancel.Token, connections:=4)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                End Using
                Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack")), "MR client-only env cancellation leaves no published instance: " & selected, passed)
                Dim result = Await installer.InstallAsync(ModrinthPackage.Read(source), fixture.Root, "pack", {}, choices, Nothing, CancellationToken.None, connections:=4)
                Check(result.NetworkBytes = 0, "MR client-only env retry reuses verified downloads: " & selected, passed)
                Check(File.ReadAllText(Path.Combine(result.InstancePath, "mods", "required.jar")) = "fixture required content" AndAlso File.Exists(Path.Combine(result.InstancePath, "mods", "optional.jar")) = selected AndAlso Not File.Exists(Path.Combine(result.InstancePath, "mods", "unsupported.jar")), "MR client-only env publishes required and chosen optional files only: " & selected, passed)
                Check(Not fixture.Server.Requests.ContainsKey("/unsupported") AndAlso (fixture.Server.Requests.ContainsKey("/optional") = selected), "MR client-only env skips unchosen and unsupported-client payload requests: " & selected, passed)
                Check(File.ReadAllText(sharedModPath) = "existing shared mod" AndAlso File.ReadAllText(settings) = "LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:keep", "MR client-only env cancellation and retry preserve shared Mods and global settings: " & selected, passed)
                Check(PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True" AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "UserSetting") = "client", "MR client-only env keeps native isolation and unrelated packaged settings: " & selected, passed)
                Dim requests = fixture.Server.Requests.Values.Sum(), conflict = False
                Try
                    Await installer.InstallAsync(current, fixture.Root, "pack", {}, choices, Nothing, CancellationToken.None)
                Catch ex As IOException
                    conflict = True
                End Try
                Check(conflict AndAlso requests = fixture.Server.Requests.Values.Sum(), "MR client-only env refuses an existing target before requests: " & selected, passed)
            End Using
        Next
    End Function
End Module
