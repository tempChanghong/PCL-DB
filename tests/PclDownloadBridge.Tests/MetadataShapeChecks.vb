Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module MetadataShapeChecks
    Friend Async Function RunAsync() As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each body In {"[]", "[1]", "null", "true", "42", """unavailable"""}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/bad") = Utf8(body)
                server.Files("/good") = Utf8("{""ok"":true}")
                Dim result As JObject = Nothing, failure As Exception = Nothing
                Try
                    result = Await New SourcePolicy With {.AllowLoopback = True}.RaceJsonAsync(
                        {server.Url & "/bad", server.Url & "/good"}, timeout.Token, fallbackDelay:=60000)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("METADATA_SHAPE body=" & body & " fallback=" & server.Requests.ContainsKey("/good") &
                                  " failure=" & If(failure?.GetType().Name, "none"))
                Check(result IsNot Nothing AndAlso CBool(result("ok")) AndAlso server.Requests("/bad") = 1 AndAlso
                      server.Requests.ContainsKey("/good") AndAlso server.Requests("/good") = 1,
                      "non-object metadata immediately falls back to a valid object without duplicate requests: " & body, passed)
            End Using
        Next

        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(4))
                Dim policy As New RoutedPolicy(server.Url) With {.Preference = preference}
                Dim preferred = If(preference = SourcePreference.OfficialFirst, "/official", "/mirror")
                Dim alternate = If(preferred = "/official", "/mirror", "/official")
                server.Files(preferred) = Utf8("[]")
                server.Files(alternate) = Utf8("{""mainClass"":""fixture.FabricMain"",""libraries"":[]}")
                Dim profile As JObject = Nothing, failure As Exception = Nothing
                Try
                    profile = Await policy.GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader/1.20.1/fixture/profile/json", timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("FABRIC_PROFILE_SHAPE preference=" & preference.ToString() & " failure=" & If(failure?.GetType().Name, "none"))
                Check(profile IsNot Nothing AndAlso CStr(profile("mainClass")) = "fixture.FabricMain",
                      "Fabric profile JSON recovers from the preferred source's array response: " & preference.ToString(), passed)
                Check(server.Requests.Count = 2 AndAlso server.Requests(preferred) = 1 AndAlso server.Requests(alternate) = 1,
                      "Fabric profile fallback preserves file-source ordering and contacts each source once: " & preference.ToString(), passed)
            End Using
        Next

        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Dim policy As New SourcePolicy With {.AllowLoopback = True}
            server.Files("/array") = Utf8("[]") : server.Files("/null") = Utf8("null")
            Dim failure As AggregateException = Nothing
            Try
                Await policy.RaceJsonAsync({server.Url & "/array", server.Url & "/null"}, timeout.Token, fallbackDelay:=60000)
            Catch ex As AggregateException
                failure = ex
            End Try
            Check(failure IsNot Nothing AndAlso failure.Flatten().InnerExceptions.Count = 2 AndAlso
                  failure.Flatten().InnerExceptions.All(Function(ex) TypeOf ex Is InvalidDataException),
                  "all non-object sources report their individual metadata errors instead of an external cast failure", passed)
            Dim list = Await policy.RaceVersionListTokenAsync({server.Url & "/array"}, timeout.Token)
            Check(TypeOf list Is JArray AndAlso Not list.HasValues,
                  "generic component-list requests still accept legitimate empty JSON arrays", passed)

            server.Files("/object") = Utf8("{""ok"":true}")
            Dim good = Await policy.RaceJsonAsync({server.Url & "/object", server.Url & "/unused"}, timeout.Token, fallbackDelay:=60000)
            Check(CBool(good("ok")) AndAlso Not server.Requests.ContainsKey("/unused"),
                  "a valid preferred object still cancels the deferred fallback before any HTTP request", passed)

            server.Files("/wrong-hash") = Utf8("{""ok"":false}")
            Dim hash As String
            Using digest = Security.Cryptography.SHA1.Create()
                hash = BitConverter.ToString(digest.ComputeHash(server.Files("/object"))).Replace("-", "").ToLowerInvariant()
            End Using
            good = Await policy.RaceJsonAsync({server.Url & "/wrong-hash", server.Url & "/object"}, timeout.Token, hash, fallbackDelay:=60000)
            Check(CBool(good("ok")) AndAlso server.Requests("/wrong-hash") = 1 AndAlso server.Requests("/object") = 2,
                  "object-shape validation keeps SHA-1 rejection and immediate verified fallback intact", passed)
        End Using
        Return passed
    End Function

    Private Sub Check(condition As Boolean, name As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function

    ' Keep the real official/BMCL candidate order; route only the HTTP transport.
    Private Class RoutedPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & If(New Uri(source).Host.Contains("bmclapi"), "/mirror", "/official")
        End Function
    End Class
End Module
