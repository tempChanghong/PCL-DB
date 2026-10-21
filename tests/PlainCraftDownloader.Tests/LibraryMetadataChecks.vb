Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module LibraryMetadataChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "library-meta-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-path" Then Paths(passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-url" Then Urls(passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-explicit" Then ExplicitFields(passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-null-size" Then NullSize(passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-invalid" Then InvalidPaths(passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-install" Then Await InstallAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "library-meta-extended" Then Await ExtendedAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown library metadata regression: " & onlyCase)
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
        Return Encoding.UTF8.GetBytes(If(native, "fixture Windows native ", "fixture Java runtime ") & version)
    End Function

    Private Function Relative(version As String, native As Boolean) As String
        Return "fixture/runtime/" & version & "/runtime-" & version & If(native, "-natives-windows", "") & ".jar"
    End Function

    Private Function Library(version As String, native As Boolean) As JObject
        Dim bytes = Content(version, native)
        Dim data As New JObject From {{"url", "https://example.org/payload"}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}
        Dim result As New JObject From {{"name", "fixture:runtime:" & version}}
        If native Then
            result("natives") = New JObject From {{"windows", "natives-windows"}}
            result("downloads") = New JObject From {{"classifiers", New JObject From {{"natives-windows", data}}}}
            result("extract") = New JObject From {{"exclude", New JArray("META-INF/")}}
        Else
            result("downloads") = New JObject From {{"artifact", data}}
        End If
        Return result
    End Function

    Private Function Data(libraryInfo As JObject, native As Boolean) As JObject
        Return DirectCast(If(native, libraryInfo.SelectToken("downloads.classifiers.natives-windows"), libraryInfo.SelectToken("downloads.artifact")), JObject)
    End Function

    Private Function Profile(libraryInfo As JObject) As JObject
        Return New JObject From {{"libraries", New JArray(libraryInfo)}, {"downloads", New JObject From {{"client", New JObject From {{"url", "https://example.org/client"}, {"size", 1}, {"sha1", New String("a"c, 40)}}}}}}
    End Function

    Private Function Plan(libraryInfo As JObject) As DownloadFile
        Return MinecraftInstaller.BuildBasePlan(Profile(libraryInfo), "fixture").Single(Function(item) item.Category = "library")
    End Function

    Private Sub Paths(passed As List(Of String))
        For Each native In {False, True}
            For Each explicitNull In {False, True}
                Dim libraryInfo = Library("1", native)
                If explicitNull Then Data(libraryInfo, native)("path") = JValue.CreateNull()
                Dim before = libraryInfo.ToString()
                Dim item = Plan(libraryInfo)
                Check(item.RelativePath = "libraries/" & Relative("1", native) AndAlso item.Size = Content("1", native).Length AndAlso item.Sha1 = Hash(Content("1", native)),
                      "missing or null artifact path resolves its checked Maven destination; native=" & native & "; null=" & explicitNull, passed)
                Check(libraryInfo.ToString() = before, "Maven path fallback leaves the input profile unchanged; native=" & native & "; null=" & explicitNull, passed)
            Next
        Next
    End Sub

    Private Sub Urls(passed As List(Of String))
        For Each native In {False, True}
            For Each customRepo In {False, True}
                Dim libraryInfo = Library("1", native)
                Dim artifact = Data(libraryInfo, native)
                artifact("path") = "custom/saved-file.jar"
                artifact("url") = JValue.CreateNull()
                If customRepo Then libraryInfo("url") = "https://repo.example.test/repository"
                Dim before = libraryInfo.ToString()
                Dim item = Plan(libraryInfo)
                Dim repo = If(customRepo, "https://repo.example.test/repository/", "https://libraries.minecraft.net/")
                Check(item.Url = repo & Relative("1", native), "absent artifact URL resolves its repository and classifier coordinate; native=" & native & "; custom=" & customRepo, passed)
                Check(item.RelativePath = "libraries/custom/saved-file.jar" AndAlso libraryInfo.ToString() = before AndAlso item.Sha1 = Hash(Content("1", native)),
                      "repository URL fallback preserves explicit destination, digest and source metadata; native=" & native & "; custom=" & customRepo, passed)
            Next
        Next
    End Sub

    Private Sub ExplicitFields(passed As List(Of String))
        For Each native In {False, True}
            Dim libraryInfo = Library("1", native)
            libraryInfo("url") = "https://repo.example.test/"
            Data(libraryInfo, native)("path") = "custom/kept.jar"
            Dim item = Plan(libraryInfo)
            Check(item.RelativePath = "libraries/custom/kept.jar" AndAlso item.Url = "https://example.org/payload", "explicit artifact path and URL take precedence over Maven fallback; native=" & native, passed)
        Next
        Dim classified = Library("1", False)
        classified("name") = "fixture:runtime:1:api@zip"
        Check(Plan(classified).RelativePath = "libraries/fixture/runtime/1/runtime-1-api.zip", "ordinary Maven path fallback retains classifier and explicit extension", passed)
    End Sub

    Private Sub NullSize(passed As List(Of String))
        For Each native In {False, True}
            Dim libraryInfo = Library("1", native)
            Data(libraryInfo, native)("path") = Relative("1", native)
            Data(libraryInfo, native)("size") = JValue.CreateNull()
            Dim item = Plan(libraryInfo)
            Check(item.Size = -1 AndAlso item.Sha1 = Hash(Content("1", native)), "null size remains unknown and retains its declared digest; native=" & native, passed)
        Next
    End Sub

    Private Sub InvalidPaths(passed As List(Of String))
        For Each value In {"", " "}
            Dim libraryInfo = Library("1", False)
            Data(libraryInfo, False)("path") = value
            Dim failure As Exception = Nothing
            Try
                Plan(libraryInfo)
            Catch ex As Exception
                failure = ex
            End Try
            Check(TypeOf failure Is InvalidDataException, "explicit empty artifact destination is rejected instead of targeting libraries; length=" & value.Length, passed)
        Next
    End Sub

    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        For Each withComponents In {False, True}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "library-meta-" & withComponents), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                Dim baseLibraries As New JArray()
                For Each native In {False, True}
                    Dim libraryInfo = Library("1", native)
                    Data(libraryInfo, native).Remove("url")
                    libraryInfo("url") = fixture.Server.Url & "/maven/"
                    baseLibraries.Add(libraryInfo)
                    fixture.Server.Files("/maven/" & Relative("1", native)) = Content("1", native)
                Next
                Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                metadata("libraries") = baseLibraries
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
                fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
                Dim selection As ComponentSelection = Nothing
                If withComponents Then
                    selection = fixture.Fabric()
                    Dim libraryInfo = Library("2", False)
                    Data(libraryInfo, False).Remove("url")
                    libraryInfo("url") = fixture.Server.Url & "/maven/"
                    fixture.Server.Files("/maven/" & Relative("2", False)) = Content("2", False)
                    Dim profileUrl = "/v2/versions/loader/" & fixture.Entry.Id & "/fixture/profile/json"
                    Dim profileJson = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files(profileUrl)))
                    profileJson("libraries") = New JArray(libraryInfo)
                    fixture.Server.Files(profileUrl) = Encoding.UTF8.GetBytes(profileJson.ToString())
                End If
                Dim result = Await fixture.InstallAsync("first", selection, token:=timeout.Token)
                Dim version = If(withComponents, "2", "1")
                Dim runtime = SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative(version, False))
                Dim nativeFile = SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("1", True))
                Check(File.ReadAllBytes(runtime).SequenceEqual(Content(version, False)) AndAlso File.ReadAllBytes(nativeFile).SequenceEqual(Content("1", True)),
                      "real installer publishes ordinary and native libraries at PCL Maven fallback paths; components=" & withComponents, passed)
                Dim jsonPath = Path.Combine(result.InstancePath, "first.json")
                Dim jsonBytes = File.ReadAllBytes(jsonPath)
                Dim published = JObject.Parse(Encoding.UTF8.GetString(jsonBytes))
                Dim libraries = DirectCast(published("libraries"), JArray).OfType(Of JObject)().ToList()
                Check(libraries.Count = 2 AndAlso libraries.Any(Function(item) CStr(item("name")) = "fixture:runtime:" & version AndAlso item("natives") Is Nothing) AndAlso
                      libraries.Any(Function(item) CStr(item.SelectToken("natives.windows")) = "natives-windows") AndAlso CStr(published("id")) = "first" AndAlso CStr(published("clientVersion")) = fixture.Entry.Id,
                      "published PCL profile retains separate library roles and correct instance identity; components=" & withComponents, passed)
                Dim preferences = Path.Combine(result.InstancePath, "PCL", "Setup.ini")
                Directory.CreateDirectory(Path.GetDirectoryName(preferences))
                Dim preferenceBytes = Encoding.UTF8.GetBytes("fixture existing instance preferences")
                File.WriteAllBytes(preferences, preferenceBytes)
                Dim retry = Await fixture.InstallAsync("second", selection, token:=timeout.Token)
                Check(retry.NetworkBytes = 0 AndAlso fixture.Server.Requests("/client") = 1 AndAlso fixture.Server.Requests("/maven/" & Relative("1", True)) = 1,
                      "another instance reuses checked Maven fallback files without downloading content again; components=" & withComponents, passed)
                Check(File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(preferences).SequenceEqual(preferenceBytes),
                      "subsequent Maven fallback installation preserves existing JSON and PCL preference bytes; components=" & withComponents, passed)
            End Using
        Next
    End Function

    Private Async Function ExtendedAsync(testRoot As String, passed As List(Of String)) As Task
        For Each withComponents In {False, True}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "library-meta-extended-" & withComponents), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                Dim libraryInfo = Library("1", False)
                libraryInfo("name") = "fixture:runtime:1:api@zip"
                Data(libraryInfo, False)("url") = fixture.Server.Url & "/archive"
                fixture.Server.Files("/archive") = Content("1", False)
                Dim selection As ComponentSelection = Nothing
                Dim nativeName = "natives-windows-" & If(Environment.Is64BitOperatingSystem, "64", "32")
                Dim nativePath = "fixture/native/1/native-1-" & nativeName & ".jar"
                If withComponents Then
                    selection = fixture.Fabric()
                    Dim profileUrl = "/v2/versions/loader/" & fixture.Entry.Id & "/fixture/profile/json"
                    Dim profileJson = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files(profileUrl)))
                    Dim nativeLibrary = Library("1", True)
                    Dim nativeData = DirectCast(Data(nativeLibrary, True).DeepClone(), JObject)
                    nativeData.Remove("url")
                    nativeLibrary("name") = "fixture:native:1@jar"
                    nativeLibrary("url") = fixture.Server.Url & "/maven/"
                    nativeLibrary("natives") = New JObject From {{"windows", "natives-windows-${arch}"}}
                    nativeLibrary("downloads") = New JObject From {{"classifiers", New JObject From {{nativeName, nativeData}}}}
                    fixture.Server.Files("/maven/" & nativePath) = Content("1", True)
                    profileJson("libraries") = New JArray(libraryInfo, nativeLibrary)
                    fixture.Server.Files(profileUrl) = Encoding.UTF8.GetBytes(profileJson.ToString())
                Else
                    Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                    metadata("libraries") = New JArray(libraryInfo)
                    fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
                    fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
                End If
                Dim sourceBefore = fixture.Server.Files("/version").ToArray()
                Dim result = Await fixture.InstallAsync("extended", selection, token:=timeout.Token)
                Dim jsonPath = Path.Combine(result.InstancePath, "extended.json")
                Dim jsonBytes = File.ReadAllBytes(jsonPath)
                Dim profile = JObject.Parse(Encoding.UTF8.GetString(jsonBytes))
                Dim artifact = profile.SelectToken("libraries[0].downloads.artifact")
                Check(CStr(artifact("path")) = "fixture/runtime/1/runtime-1-api.zip" AndAlso CStr(artifact("url")) = fixture.Server.Url & "/archive",
                      "published extended coordinate has an explicit artifact path that PCL can read; components=" & withComponents, passed)
                Check(File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & CStr(artifact("path")))).SequenceEqual(Content("1", False)) AndAlso
                      fixture.Server.Files("/version").SequenceEqual(sourceBefore), "extended-coordinate materialization preserves declared data and source metadata; components=" & withComponents, passed)
                If Not withComponents Then
                    Dim repair = Await fixture.InstallAsync("extended", repair:=True, token:=timeout.Token)
                    Check(repair.NetworkBytes = 0 AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes),
                          "repair compares the same extended-coordinate normalization and preserves existing JSON", passed)
                Else
                    Dim again = Await fixture.InstallAsync("extended-second", selection, token:=timeout.Token)
                    Check(again.NetworkBytes = 0 AndAlso fixture.Server.Requests("/archive") = 1 AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes),
                          "another component instance reuses the extended artifact without changing existing metadata", passed)
                    Dim nativeLibrary = profile("libraries")(1)
                    Check(CStr(nativeLibrary.SelectToken("natives.windows")) = nativeName AndAlso
                          CStr(nativeLibrary("downloads")("classifiers")(nativeName)("path")) = nativePath AndAlso
                          File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & nativePath)).SequenceEqual(Content("1", True)),
                          "extended native profile materializes its architecture key and path for the PCL classifier branch", passed)
                End If
            End Using
        Next
    End Function
End Module
