Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LibrarySelectionChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "lib-select-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "lib-select-plan" Then Plans(passed)
        If onlyCase Is Nothing OrElse onlyCase = "lib-select-overlay" Then Overlays(passed)
        If onlyCase Is Nothing OrElse onlyCase = "lib-select-rules" Then Rules(passed)
        If onlyCase Is Nothing OrElse onlyCase = "lib-select-integrity" Then Integrity(passed)
        If onlyCase Is Nothing OrElse onlyCase = "lib-select-install" Then Await InstallAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown library selection regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function Content(version As String, native As Boolean) As Byte()
        Return Encoding.UTF8.GetBytes(If(native, "fixture native ", "fixture runtime ") & version)
    End Function

    Private Function Relative(version As String, native As Boolean) As String
        Return "fixture/selected/" & version & "/selected-" & version & If(native, "-natives-windows", "") & ".jar"
    End Function

    Private Function Library(version As String, Optional native As Boolean = False, Optional server As String = "https://example.org") As JObject
        Dim bytes = Content(version, native)
        Dim data As New JObject From {{"path", Relative(version, native)}, {"url", server & If(native, "/native", "/runtime") & version}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}
        Dim result As New JObject From {{"name", "fixture:selected:" & version}}
        If native Then
            result("natives") = New JObject From {{"windows", "natives-windows"}}
            result("downloads") = New JObject From {{"classifiers", New JObject From {{"natives-windows", data}}}}
        Else
            result("downloads") = New JObject From {{"artifact", data}}
        End If
        Return result
    End Function

    Private Function Profile(entries As JArray) As JObject
        Return New JObject From {{"id", "base"}, {"mainClass", "fixture.Main"}, {"libraries", entries}, {"downloads", New JObject From {{"client", New JObject From {{"url", "https://example.org/client"}, {"size", 1}, {"sha1", New String("a"c, 40)}}}}}}
    End Function

    Private Function Plan(profileJson As JObject) As List(Of DownloadFile)
        Return MinecraftInstaller.BuildBasePlan(profileJson, "fixture").Where(Function(item) item.Category = "library").ToList()
    End Function

    Private Sub Plans(passed As List(Of String))
        For Each reverse In {False, True}
            Dim entries = If(reverse, New JArray(Library("1.10"), Library("1.9")), New JArray(Library("1.9"), Library("1.10")))
            Dim profileJson = Profile(entries)
            Dim before = profileJson.ToString()
            Dim files = Plan(profileJson)
            Check(files.Count = 1 AndAlso files(0).RelativePath = "libraries/" & Relative("1.10", False), "base plan selects only the newer numeric library version in either order; reverse=" & reverse, passed)
            Check(profileJson.ToString() = before, "duplicate-version planning leaves source metadata unchanged; reverse=" & reverse, passed)
        Next
        Dim runtime = Library("1.10")
        Dim native = Library("1.9", True)
        Dim separate = Plan(Profile(New JArray(Library("1.9"), runtime, native, Library("1.10", True))))
        Check(separate.Count = 2 AndAlso separate.Any(Function(item) item.RelativePath = "libraries/" & Relative("1.10", False)) AndAlso separate.Any(Function(item) item.RelativePath = "libraries/" & Relative("1.10", True)),
              "ordinary and native library versions are selected independently", passed)
    End Sub

    Private Sub Overlays(passed As List(Of String))
        Dim pairs = New String()() {New String() {"1.10", "1.9", "1.10"}, New String() {"1.0-rc1", "1.0", "1.0"},
            New String() {"1.0-pre1", "1.0-rc1", "1.0-rc1"}, New String() {"1.0-snapshot2", "1.0-pre1", "1.0-pre1"},
            New String() {"1.0-experimental1", "1.0-snapshot1", "1.0-snapshot1"}, New String() {"1.0.0", "1.0", "1.0.0"},
            New String() {"1.0-alpha", "1.0", "1.0-alpha"}, New String() {"1.2147483648", "1.2", "1.2"}}
        For Each pair In pairs
            Dim basis = Profile(New JArray(Library(pair(0))))
            Dim addition As New JObject From {{"libraries", New JArray(Library(pair(1)))}}
            Dim before = basis.ToString() & addition.ToString()
            Dim merged = ComponentInstaller.MergeProfile(basis, addition)
            Check(CStr(merged("libraries")(0)("name")) = "fixture:selected:" & pair(2), "component merge follows PCL version-token ordering: " & pair(0) & " versus " & pair(1), passed)
            Check(basis.ToString() & addition.ToString() = before, "version resolution preserves both profiles: " & pair(0) & " versus " & pair(1), passed)
        Next
        Dim custom = Library("100")
        custom("downloads")("artifact")("path") = "fixture/selected/1/custom.jar"
        Dim mergedCustom = ComponentInstaller.MergeProfile(Profile(New JArray(custom)), New JObject From {{"libraries", New JArray(Library("2"))}})
        Check(CStr(mergedCustom("libraries")(0)("name")) = "fixture:selected:2", "PCL version selection uses the saved file's parent directory instead of the coordinate version", passed)
    End Sub

    Private Sub Rules(passed As List(Of String))
        Dim windows = Library("1.10")
        windows("rules") = JArray.Parse("[{'action':'allow','os':{'name':'windows'}}]")
        Dim linux = Library("99")
        linux("rules") = JArray.Parse("[{'action':'allow','os':{'name':'linux'}}]")
        Dim denied = Library("100")
        denied("rules") = JArray.Parse("[{'action':'disallow'}]")
        Dim merged = ComponentInstaller.MergeProfile(Profile(New JArray(windows, linux, denied)), New JObject From {{"libraries", New JArray(Library("1.9"))}})
        Dim files = Plan(merged)
        Check(files.Count = 1 AndAlso files(0).RelativePath = "libraries/" & Relative("1.10", False), "rules are applied before selecting the newest applicable library", passed)
        Check(DirectCast(merged("libraries"), JArray).Count = 4, "different conditional declarations remain available in the published profile", passed)
        Check(MinecraftInstaller.RulesAllow(JValue.CreateNull()), "explicit null rules have PCL's absent-rule meaning", passed)
        Dim legacy As New JObject From {{"name", "fixture:selected:1"}, {"url", "https://example.org/"}, {"natives", JValue.CreateNull()}, {"rules", JValue.CreateNull()}}
        files = Plan(Profile(New JArray(legacy)))
        Check(files.Count = 1 AndAlso files(0).RelativePath = "libraries/" & Relative("1", False), "raw null rules and native markers retain ordinary Maven fallback", passed)
    End Sub

    Private Sub Integrity(passed As List(Of String))
        Dim strong = Library("1")
        Dim weak = Library("2")
        weak("downloads")("artifact")("path") = Relative("1", False)
        DirectCast(weak.SelectToken("downloads.artifact"), JObject).Remove("sha1")
        DirectCast(weak.SelectToken("downloads.artifact"), JObject).Remove("size")
        Dim files = Plan(Profile(New JArray(strong, weak)))
        Check(files.Count = 1 AndAlso files(0).Sha1 = Hash(Content("1", False)) AndAlso files(0).Size = Content("1", False).Length,
              "duplicate versions that resolve to one file retain the strongest compatible integrity checks", passed)
        Dim conflict = Library("2")
        conflict("downloads")("artifact")("path") = Relative("1", False)
        Dim failure As Exception = Nothing
        Try
            Plan(Profile(New JArray(strong, conflict)))
        Catch ex As Exception
            failure = ex
        End Try
        Check(TypeOf failure Is InvalidDataException, "conflicting declarations of the same selected destination still fail before downloading", passed)
    End Sub

    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "lib-selection"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            For Each version In {"1", "2"}
                fixture.Server.Files("/runtime" & version) = Content(version, False)
                fixture.Server.Files("/native" & version) = Content(version, True)
            Next
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("libraries") = New JArray(Library("2", server:=fixture.Server.Url), Library("1", True, fixture.Server.Url))
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            Dim selection = fixture.Fabric()
            Dim profileUrl = "/v2/versions/loader/" & fixture.Entry.Id & "/fixture/profile/json"
            Dim addition = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files(profileUrl)))
            addition("libraries") = New JArray(Library("1", server:=fixture.Server.Url), Library("2", True, fixture.Server.Url))
            fixture.Server.Files(profileUrl) = Encoding.UTF8.GetBytes(addition.ToString())
            Dim result = Await fixture.InstallAsync("selected", selection, token:=timeout.Token)
            Dim jsonPath = Path.Combine(result.InstancePath, "selected.json")
            Dim jsonBytes = File.ReadAllBytes(jsonPath)
            Dim profileJson = JObject.Parse(Encoding.UTF8.GetString(jsonBytes))
            Dim libraries = DirectCast(profileJson("libraries"), JArray).OfType(Of JObject)().ToList()
            Check(libraries.Count = 2 AndAlso libraries.All(Function(item) CStr(item("name")) = "fixture:selected:2"), "real component installation preserves the newer ordinary library while upgrading the native library", passed)
            Check(File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("2", False))).SequenceEqual(Content("2", False)) AndAlso
                  File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("2", True))).SequenceEqual(Content("2", True)), "selected ordinary and native runtime files both pass their declared content checks", passed)
            Check(Not fixture.Server.Requests.ContainsKey("/runtime1") AndAlso fixture.Server.Requests("/runtime2") = 1 AndAlso fixture.Server.Requests("/native2") = 1,
                  "component completion does not download the discarded older ordinary version", passed)
            Dim preferences = Path.Combine(result.InstancePath, "PCL", "Setup.ini")
            Dim preferenceBytes = File.ReadAllBytes(preferences)
            Dim second = Await fixture.InstallAsync("selected-second", selection, token:=timeout.Token)
            Check(second.NetworkBytes = 0 AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(preferences).SequenceEqual(preferenceBytes),
                  "subsequent selected-version installation reuses checked files and preserves existing JSON and PCL settings", passed)
        End Using
    End Function
End Module
