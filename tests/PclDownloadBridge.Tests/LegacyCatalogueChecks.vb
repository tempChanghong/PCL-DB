Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LegacyCatalogueChecks
    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-legacy-paging" Then Await PagingAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-legacy-optifabric" Then Await OptiFabricAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-legacy-liteloader" Then Await LiteLoaderAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-legacy-behavior" Then Await BehaviorAsync(passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Function ModRow(id As Long) As JObject
        Return New JObject From {{"id", id}, {"projectId", 322385}, {"gameVersions", New JArray("1.20.1")},
            {"fileName", "optifabric-v" & id.ToString() & ".jar"}, {"displayName", "OptiFabric " & id.ToString()},
            {"fileLength", 3}, {"releaseType", 2}, {"hashes", New JArray()}, {"downloadUrl", JValue.CreateNull()}}
    End Function

    Private Function Page(ParamArray rows As JObject()) As String
        Return New JObject From {{"data", New JArray(rows)}}.ToString()
    End Function

    Private Function Lite(Optional version As String = "1.12.2-SNAPSHOT", Optional stream As String = "SNAPSHOT") As JObject
        Return New JObject From {{"versions", New JObject From {{"1.12.2", New JObject From {{"snapshots", New JObject From {
            {"com.mumfrey:liteloader", New JObject From {{"latest", New JObject From {{"version", version}, {"stream", stream},
            {"tweakClass", "fixture.LiteTweaker"}, {"libraries", New JArray()}}}}}}}}}}}}
    End Function

    Private Async Function PagingAsync(passed As List(Of String)) As Task
        Dim first = Enumerable.Range(1000, 50).Select(Function(id) ModRow(id)).ToArray()
        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                Dim alternate = If(preferred = "/mirror", "/official", "/mirror")
                server.Files(preferred & "/0") = Utf8(Page(first))
                server.Files(preferred & "/50") = Utf8(Page(first)) ' Broken server repeats page zero.
                server.Files(alternate & "/50") = Utf8(Page(ModRow(1050)))
                Dim entries As List(Of ComponentEntry) = Nothing, failure As Exception = Nothing
                Try
                    entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, preference)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("OPTIFABRIC_PAGING preference=" & preference.ToString() & " fallback=" & server.Requests.ContainsKey(alternate & "/50") &
                                  " entries=" & If(entries?.Count.ToString(), "none") & " failure=" & If(failure?.GetType().Name, "none"))
                Check(entries IsNot Nothing AndAlso entries.Count = 51 AndAlso entries.Select(Function(entry) entry.Version).Distinct().Count() = 51 AndAlso
                      entries.Last().Version = "1050" AndAlso server.Requests.Count = 3 AndAlso Not server.Requests.ContainsKey(alternate & "/0"),
                      "repeated OptiFabric page enables the other source at the same offset without losing accepted entries: " & preference.ToString(), passed)
            End Using
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            server.Files("/mirror/0") = Utf8(Page(first))
            server.Files("/mirror/50") = Utf8(Page(first)) : server.Files("/official/50") = Utf8(Page(first))
            Dim failure As AggregateException = Nothing
            Try
                Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)
            Catch ex As AggregateException
                failure = ex
            End Try
            Check(failure IsNot Nothing AndAlso failure.Flatten().InnerExceptions.Count = 2 AndAlso server.Requests.Count = 3 AndAlso
                  Not server.Requests.Keys.Any(Function(path) path.EndsWith("/100", StringComparison.Ordinal)),
                  "two repeated OptiFabric sources fail the page without looping or returning a partial catalogue", passed)
        End Using
    End Function

    Private Async Function OptiFabricAsync(passed As List(Of String)) As Task
        Dim invalid As New List(Of String) From {"{}", "{""data"":{}}", "{""data"":[null]}"}
        For Each field In {"id", "gameVersions", "fileName", "fileLength", "releaseType", "hashes", "projectId"}
            Dim row = ModRow(1000)
            Select Case field
                Case "id" : row(field) = "1000"
                Case "gameVersions" : row(field) = New JObject()
                Case "fileName" : row(field) = JValue.CreateNull()
                Case "fileLength" : row(field) = -2
                Case "releaseType" : row(field) = "beta"
                Case "hashes" : row(field) = New JArray(New JObject From {{"algo", 1}, {"value", "broken-sha1"}})
                Case "projectId" : row(field) = 123
            End Select
            invalid.Add(Page(row))
        Next
        invalid.Add(Page(ModRow(1000), ModRow(1000)))
        invalid.Add(Page(Enumerable.Range(1000, 51).Select(Function(id) ModRow(id)).ToArray()))
        For Each body In invalid
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/mirror/0") = Utf8(body) : server.Files("/official/0") = Utf8(Page(ModRow(1000)))
                Dim entries As List(Of ComponentEntry) = Nothing, failure As Exception = Nothing
                Try
                    entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("OPTIFABRIC_PAGE fallback=" & server.Requests.ContainsKey("/official/0") & " failure=" & If(failure?.GetType().Name, "none"))
                Check(entries IsNot Nothing AndAlso entries.Count = 1 AndAlso entries(0).Version = "1000" AndAlso server.Requests.Count = 2 AndAlso
                      server.Requests("/official/0") = 1 AndAlso server.Requests("/mirror/0") = 1,
                      "invalid OptiFabric page cannot poison accepted IDs or prevent fallback; case=" & invalid.IndexOf(body).ToString(), passed)
            End Using
        Next
    End Function

    Private Async Function LiteLoaderAsync(passed As List(Of String)) As Task
        Dim invalid As New List(Of String) From {"{}", "{""versions"":[]}", "{""versions"":{""1.12.2"":{}}}"}
        For Each field In {"version", "stream", "tweakClass", "libraries"}
            Dim body = Lite()
            Dim entry = DirectCast(body("versions")("1.12.2")("snapshots")("com.mumfrey:liteloader")("latest"), JObject)
            entry.Remove(field) : invalid.Add(body.ToString())
        Next
        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            For Each body In invalid
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                    Dim alternate = If(preferred = "/mirror", "/official", "/mirror")
                    server.Files(preferred) = Utf8(body) : server.Files(alternate) = Utf8(Lite("healthy").ToString())
                    Dim entries As List(Of ComponentEntry) = Nothing, failure As Exception = Nothing
                    Try
                        entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, preference)).LoadAsync(ComponentKind.LiteLoader, "1.12.2", timeout.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                    Console.WriteLine("LITE_CATALOGUE preference=" & preference.ToString() & " fallback=" & server.Requests.ContainsKey(alternate) &
                                      " entries=" & If(entries?.Count.ToString(), "none") & " failure=" & If(failure?.GetType().Name, "none"))
                    Check(entries IsNot Nothing AndAlso entries.Count = 1 AndAlso entries(0).Version = "healthy" AndAlso server.Requests.Count = 2,
                          "invalid selected LiteLoader metadata enables fallback: " & preference.ToString() & " case=" & invalid.IndexOf(body).ToString(), passed)
                End Using
            Next
        Next
    End Function

    Private Async Function BehaviorAsync(passed As List(Of String)) As Task
        For Each stream In {"SNAPSHOT", "snapshot", "Snapshot", "RELEASE"}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/mirror") = Utf8(Lite(stream:=stream).ToString())
                Dim entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.LiteLoader, "1.12.2", timeout.Token)).Single()
                Check(entry.Stable = (stream = "RELEASE"), "LiteLoader stream classification follows PCL's case-insensitive snapshot comparison: " & stream, passed)
                Dim profile = ComponentInstaller.LiteProfile(entry)
                Check(CStr(profile("arguments")("game")(1)) = "fixture.LiteTweaker" AndAlso DirectCast(profile("libraries"), JArray).Count = 1,
                      "validated LiteLoader metadata remains usable by the actual profile builder: " & stream, passed)
            End Using
        Next
        For Each kind In {ComponentKind.LiteLoader, ComponentKind.OptiFabric}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                Dim preferred = If(kind = ComponentKind.OptiFabric, "/mirror/0", "/mirror")
                server.Files(preferred) = Utf8(If(kind = ComponentKind.OptiFabric, "{""data"":[]}", "{""versions"":{}}"))
                Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(kind, If(kind = ComponentKind.OptiFabric, "1.20.1", "1.12.2"), timeout.Token)
                Check(entries.Count = 0 AndAlso server.Requests.Count = 1, kind.ToString() & " valid empty metadata does not start a fallback", passed)
            End Using
            Using server As New TestServer(), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource()
                Dim preferred = If(kind = ComponentKind.OptiFabric, "/mirror/0", "/mirror")
                If kind = ComponentKind.OptiFabric Then
                    server.Files(preferred) = Utf8(Page(Enumerable.Range(1000, 50).Select(Function(id) ModRow(id)).ToArray()))
                    preferred = "/mirror/50"
                End If
                server.Files(preferred) = Utf8(If(kind = ComponentKind.OptiFabric, "{""data"":[]}", Lite().ToString()))
                server.BodyGates(preferred) = gate
                Dim work = New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(kind, If(kind = ComponentKind.OptiFabric, "1.20.1", "1.12.2"), cancel.Token)
                Dim waiting = Diagnostics.Stopwatch.StartNew()
                While Not server.Requests.ContainsKey(preferred) AndAlso waiting.Elapsed < TimeSpan.FromSeconds(3)
                    Await Task.Delay(10)
                End While
                Dim reachedPendingPage = server.Requests.ContainsKey(preferred)
                cancel.Cancel()
                Dim cancelled = False
                Try : Await work : Catch ex As OperationCanceledException : cancelled = True : End Try
                Check(reachedPendingPage AndAlso cancelled AndAlso Not gate.IsSet AndAlso Not server.Requests.Keys.Any(Function(path) path.StartsWith("/official", StringComparison.Ordinal)),
                      kind.ToString() & " cancellation joins the pending body without returning a partial list or starting fallback", passed)
            End Using
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Dim row = ModRow(5025647) : row("fileLength") = 0
            server.Files("/mirror/0") = Utf8(Page(row))
            Dim entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)).Single()
            Check(entry.Size = -1, "OptiFabric maps a reported zero size to unknown as PCL does for CurseForge files", passed)
            Check(entry.Url = "https://edge.forgecdn.net/files/5025/647/optifabric-v5025647.jar" AndAlso Not entry.Stable,
                  "OptiFabric preserves null-URL CDN synthesis and beta classification", passed)
            row("fileLength") = -1
            server.Files("/mirror/0") = Utf8(Page(row))
            entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)).Single()
            Check(entry.Size = -1, "OptiFabric also preserves an explicit unknown size", passed)
        End Using
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Dim body = Lite("snapshots")
            body("versions")("1.12.2")("artefacts") = Lite("artefacts", "RELEASE")("versions")("1.12.2")("snapshots").DeepClone()
            server.Files("/mirror") = Utf8(body.ToString())
            Dim entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.LiteLoader, "1.12.2", timeout.Token)).Single()
            Check(entry.Version = "artefacts" AndAlso entry.Stable, "LiteLoader retains artefacts precedence over snapshots", passed)
            server.Files("/mirror") = Utf8("{""versions"":{""1.7.10"":{}}}")
            Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.LiteLoader, "1.12.2", timeout.Token)
            Check(entries.Count = 0 AndAlso Not server.Requests.ContainsKey("/official"), "LiteLoader missing the selected game remains a legitimate empty result", passed)
        End Using
        For Each mc In {"1.5.2", "1.6.4", "1.13"}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                Dim entries = Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.LiteLoader, mc, timeout.Token)
                Check(entries.Count = 0 AndAlso server.Requests.Count = 0, "unsupported LiteLoader games do not request a catalogue: " & mc, passed)
            End Using
        Next
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
            Dim uri As New Uri(source)
            Dim prefix = If(uri.Host.Contains("mcimirror") OrElse uri.Host.Contains("bmclapi"), "/mirror", "/official")
            Dim query = Regex.Match(uri.Query, "[?&](pageIndex|index)=(\d+)")
            Return _server & prefix & If(query.Success, "/" & (Integer.Parse(query.Groups(2).Value) * If(query.Groups(1).Value = "pageIndex", 50, 1)).ToString(), "")
        End Function
    End Class
End Module
