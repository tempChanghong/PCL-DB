Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ComponentVersionChecks
    Friend Async Function RunAsync() As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim pairs = {
            Tuple.Create("1.9-pre1", "1.13", -1),
            Tuple.Create("1.20.4-pre1", "1.20.4", -1),
            Tuple.Create("1.20.4-rc1", "1.20.4", -1),
            Tuple.Create("1.20.4-pre2", "1.20.4-pre10", -1),
            Tuple.Create("26.1-snapshot-2", "26.1-snapshot-10", -1),
            Tuple.Create("1.20.4-experimental1", "1.20.4-snapshot1", -1),
            Tuple.Create("1.20.4-snapshot1", "1.20.4-pre1", -1),
            Tuple.Create("1.20.4-pre1", "1.20.4-rc1", -1),
            Tuple.Create("26.1", "26.1.1", -1),
            Tuple.Create("1.20.4", "1.20.4", 0)}
        For Each pair In pairs
            check(Math.Sign(ComponentSelection.CompareMc(pair.Item1, pair.Item2)) = pair.Item3 AndAlso
                  Math.Sign(ComponentSelection.CompareMc(pair.Item2, pair.Item1)) = -pair.Item3,
                  "PCL component ordering compares " & pair.Item1 & " with " & pair.Item2)
        Next
        For Each mc In {"1.16-pre1", "1.20.4-pre1", "1.20.4-rc1", "1.20.4"}
            Dim selection = FabricOptiFine(mc), issue As String = Nothing
            Try
                selection.Validate(mc)
            Catch ex As InvalidDataException
                issue = ex.Message
            End Try
            check(issue Is Nothing, "Fabric OptiFine and OptiFabric validate within the supported PCL family at " & mc)
        Next
        Dim newer = FabricOptiFine("1.20.5-pre1"), rejected As Boolean
        Try
            newer.Validate("1.20.5-pre1")
        Catch ex As InvalidDataException
            rejected = True
        End Try
        check(rejected, "Fabric and OptiFine remain incompatible above the PCL 1.20.4 boundary")
        Dim forge As New ComponentEntry With {.Kind = ComponentKind.Forge, .Version = "27.0.0", .Minecraft = "1.14.3-pre1"}
        Dim opt As New ComponentEntry With {.Kind = ComponentKind.OptiFine, .RequiredForge = "27.0.0", .Minecraft = forge.Minecraft}
        check(Not ComponentSelection.OptiFineFits(opt, forge, forge.Minecraft), "Forge OptiFine exclusion still covers the 1.14.3 pre-release boundary")
        forge.Minecraft = "1.13-pre1" : opt.Minecraft = forge.Minecraft
        check(ComponentSelection.OptiFineFits(opt, forge, forge.Minecraft), "Forge OptiFine lower boundary follows PCL pre-release ordering")
        Using server As New TestServer()
            Dim policy As New VersionFixturePolicy(server.Url), mc = "1.20.4-pre1"
            Dim body As New JObject From {{"data", New JArray(New JObject From {
                {"id", 5025647}, {"projectId", 322385}, {"gameVersions", New JArray(mc, "1.16-pre1")},
                {"fileName", "optifabric-fixture.jar"}, {"displayName", "OptiFabric fixture"},
                {"fileLength", 3}, {"releaseType", 2}, {"hashes", New JArray()},
                {"downloadUrl", "https://example.invalid/optifabric-fixture.jar"}})}}
            server.Files("/api/v1/mods/322385/files") = Encoding.UTF8.GetBytes(body.ToString())
            server.Files("/curseforge/v1/mods/322385/files") = Encoding.UTF8.GetBytes(body.ToString())
            Dim catalog As New ComponentCatalog(policy)
            Dim versions = Await catalog.LoadAsync(ComponentKind.OptiFabric, mc, CancellationToken.None)
            check(versions.Count = 1 AndAlso versions(0).Minecraft = mc AndAlso versions(0).Version = "fixture",
                  "OptiFabric catalogue reaches and filters actual metadata below the 1.20.4 release boundary")
            versions = Await catalog.LoadAsync(ComponentKind.OptiFabric, "1.16-pre1", CancellationToken.None)
            check(versions.Count = 1 AndAlso versions(0).Minecraft = "1.16-pre1",
                  "OptiFabric catalogue accepts pre-releases belonging to the supported 1.16 family")
            Dim before = server.Requests.Values.Sum()
            versions = Await catalog.LoadAsync(ComponentKind.OptiFabric, "1.20.5-pre1", CancellationToken.None)
            check(versions.Count = 0 AndAlso server.Requests.Values.Sum() = before,
                  "OptiFabric catalogue does not request metadata above the supported PCL boundary")
            versions = Await catalog.LoadAsync(ComponentKind.OptiFabric, "1.15.2", CancellationToken.None)
            check(versions.Count = 0 AndAlso server.Requests.Values.Sum() = before,
                  "OptiFabric catalogue keeps the unsupported 1.15 family out of automatic installation")
        End Using
        Return passed
    End Function

    Private Function FabricOptiFine(mc As String) As ComponentSelection
        Dim selection As New ComponentSelection
        For Each kind In {ComponentKind.Fabric, ComponentKind.OptiFine, ComponentKind.OptiFabric}
            selection.Entries(kind) = New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = "fixture"}
        Next
        Return selection
    End Function

    Private Class VersionFixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Public Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & New Uri(source).AbsolutePath
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class
End Module
