Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ComponentCatalogueChecks
    Private Const FabricGood As String = "[{""loader"":{""version"":""0.16.9"",""stable"":true}}]"
    Private Const OptiFineGood As String = "[{""mcversion"":""1.20.1"",""type"":""HD_U"",""patch"":""I5"",""filename"":""OptiFine_1.20.1_HD_U_I5.jar""}]"
    Private Const OptiFinePage As String = "<td class='colForge'>N/A</td><a href='adloadx?f=OptiFine_1.20.1_HD_U_I6.jar'>Mirror</a>"

    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "component-catalogue-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-fabric" Then Await FabricAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-optifine" Then Await OptiFineAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-behavior" Then Await BehaviorAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("component-catalogue-legacy-", StringComparison.Ordinal) Then passed.AddRange(Await LegacyCatalogueChecks.RunAsync(onlyCase))
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Async Function FabricAsync(passed As List(Of String)) As Task
        Dim invalid = {"{}", "null", "[{}]", "[{""loader"":{""version"":"" "",""stable"":true}}]",
                       "[{""loader"":{""version"":7,""stable"":true}}]",
                       "[{""loader"":{""version"":""bad"",""stable"":""true""}}]",
                       "[{""loader"":{""version"":""partial"",""stable"":true}},{}]"}
        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            For Each body In invalid
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                    Dim alternate = If(preferred = "/mirror", "/official", "/mirror")
                    server.Files(preferred) = Utf8(body) : server.Files(alternate) = Utf8(FabricGood)
                    Dim entries As List(Of ComponentEntry) = Nothing, failure As Exception = Nothing
                    Try
                        entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, preference)).LoadAsync(ComponentKind.Fabric, "1.20.1", timeout.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                    Console.WriteLine("FABRIC_CATALOGUE preference=" & preference.ToString() & " body=" & body &
                                      " fallback=" & server.Requests.ContainsKey(alternate) & " entries=" & If(entries?.Count.ToString(), "none") &
                                      " failure=" & If(failure?.GetType().Name, "none"))
                    Check(entries IsNot Nothing AndAlso entries.Count = 1 AndAlso entries(0).Version = "0.16.9" AndAlso
                          server.Requests.Count = 2 AndAlso server.Requests(preferred) = 1 AndAlso server.Requests(alternate) = 1,
                          "invalid Fabric catalogue falls back before publishing any entry: " & preference.ToString() & " " & body, passed)
                End Using
            Next
        Next
    End Function

    Private Async Function OptiFineAsync(passed As List(Of String)) As Task
        Dim invalid = {"{}", "null", "[{}]", "[{""mcversion"":""1.20.1"",""type"":""HD_U""}]",
                       "[{""mcversion"":""1.20.1"",""type"":""HD_U"",""patch"":""I5""}]",
                       "[{""mcversion"":""1.20.1"",""type"":""HD_U"",""patch"":""I5"",""filename"":3}]",
                       "[{""mcversion"":""1.20.1"",""type"":""HD_U"",""patch"":""I5"",""filename"":""OptiFine.jar"",""forge"":{}}]",
                       OptiFineGood.TrimEnd("]"c) & ",{""mcversion"":""1.8""}]"}
        For Each body In invalid
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/mirror") = Utf8(body) : server.Files("/official") = Utf8(OptiFinePage)
                Dim entries As List(Of ComponentEntry) = Nothing, failure As Exception = Nothing
                Try
                    entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFine, "1.20.1", timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("OPTIFINE_CATALOGUE body=" & body & " fallback=" & server.Requests.ContainsKey("/official") &
                                  " entries=" & If(entries?.Count.ToString(), "none") & " failure=" & If(failure?.GetType().Name, "none"))
                Check(entries IsNot Nothing AndAlso entries.Count = 1 AndAlso entries(0).Version = "I6" AndAlso
                      server.Requests.Count = 2 AndAlso server.Requests("/mirror") = 1 AndAlso server.Requests("/official") = 1,
                      "invalid OptiFine mirror catalogue falls back to parsed official HTML without partial entries: " & body, passed)
            End Using
        Next
    End Function

    Private Async Function BehaviorAsync(passed As List(Of String)) As Task
        For Each kind In {ComponentKind.Fabric, ComponentKind.OptiFine}
            For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    server.Files("/official") = Utf8(If(kind = ComponentKind.Fabric, FabricGood.Replace("0.16.9", "0.16.10"), OptiFinePage))
                    server.Files("/mirror") = Utf8(If(kind = ComponentKind.Fabric, FabricGood, OptiFineGood))
                    Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, preference)).LoadAsync(kind, "1.20.1", timeout.Token)
                    Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                    Dim expected = If(kind = ComponentKind.Fabric, If(preferred = "/mirror", "0.16.9", "0.16.10"), If(preferred = "/mirror", "I5", "I6"))
                    Check(entries.Count = 1 AndAlso entries(0).Version = expected AndAlso entries(0).Minecraft = "1.20.1" AndAlso
                          server.Requests.Count = 1 AndAlso server.Requests(preferred) = 1,
                          kind.ToString() & " valid catalogue honors the list preference independently of file settings: " & preference.ToString(), passed)
                End Using
            Next
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/mirror") = Utf8("[]")
                server.Files("/official") = Utf8(If(kind = ComponentKind.Fabric, FabricGood, OptiFinePage))
                Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(kind, "1.20.1", timeout.Token)
                Check(entries.Count = 0 AndAlso server.Requests.Count = 1 AndAlso Not server.Requests.ContainsKey("/official"),
                      kind.ToString() & " legitimate empty catalogue does not start an unnecessary fallback", passed)
            End Using
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/mirror") = Utf8("{}") : server.Files("/official") = Utf8("{}")
                Dim failure As AggregateException = Nothing
                Try
                    Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(kind, "1.20.1", timeout.Token)
                Catch ex As AggregateException
                    failure = ex
                End Try
                Check(failure IsNot Nothing AndAlso failure.Flatten().InnerExceptions.Count = 2 AndAlso server.Requests.Count = 2,
                      kind.ToString() & " invalid catalogues preserve both source failures instead of reporting an empty list", passed)
            End Using
            Using server As New TestServer(), body As New ManualResetEventSlim(), cancel As New CancellationTokenSource()
                server.Files("/mirror") = Utf8("[]") : server.BodyGates("/mirror") = body
                Dim work = New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(kind, "1.20.1", cancel.Token)
                ' Cancel while awaiting the first body, well before the 30s fallback.
                cancel.CancelAfter(TimeSpan.FromMilliseconds(200))
                Dim cancelled = False
                Try
                    Await work
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso Not body.IsSet AndAlso Not server.Requests.ContainsKey("/official"),
                      kind.ToString() & " cancellation still joins a stalled body without starting the fallback", passed)
            End Using
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            server.Files("/mirror") = Utf8(FabricGood.Replace("true", "false"))
            Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.Fabric, "1.20.1", timeout.Token)
            Check(entries.Count = 1 AndAlso Not entries(0).Stable, "Fabric preview loaders retain the upstream stable=false flag", passed)
            Dim rows = JArray.Parse(OptiFineGood)
            rows(0)("mcversion") = "1.8.0" : rows(0)("forge") = JValue.CreateNull()
            server.Files("/mirror") = Utf8(rows.ToString())
            entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFine, "1.8", timeout.Token)
            Check(entries.Count = 1 AndAlso entries(0).RequiredForge = "", "OptiFine legacy game normalization accepts a null optional Forge field", passed)
            rows(0)("mcversion") = "1.9.0" : rows(0)("forge") = "N/A"
            server.Files("/mirror") = Utf8(rows.ToString())
            entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFine, "1.9", timeout.Token)
            Check(entries.Count = 1 AndAlso entries(0).RequiredForge Is Nothing, "OptiFine N/A Forge compatibility remains unspecified at the legacy 1.9 boundary", passed)
            rows(0)("mcversion") = "1.20.1" : rows(0)("forge") = "Forge #47.2.18" : rows(0)("patch") = "I7_pre1"
            server.Files("/mirror") = Utf8(rows.ToString())
            entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFine, "1.20.1", timeout.Token)
            Check(entries.Count = 1 AndAlso Not entries(0).Stable AndAlso entries(0).RequiredForge = "47.2.18",
                  "OptiFine preview and explicit Forge compatibility survive mirror validation", passed)
            rows(0)("forge") = 2859
            server.Files("/mirror") = Utf8(rows.ToString())
            entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFine, "1.20.1", timeout.Token)
            Check(entries.Count = 1 AndAlso entries(0).RequiredForge = "2859",
                  "OptiFine preserves numeric legacy Forge build constraints", passed)
        End Using
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function

    Private Class RoutedPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String, preference As SourcePreference)
            _server = server : AllowLoopback = True : VersionPreference = preference
            Me.Preference = If(preference = SourcePreference.MirrorFirst, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst)
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & If(New Uri(source).Host.Contains("bmclapi"), "/mirror", "/official")
        End Function
    End Class
End Module
