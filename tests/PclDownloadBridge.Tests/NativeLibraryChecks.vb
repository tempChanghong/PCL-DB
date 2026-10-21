Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module NativeLibraryChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "native-profile" Then Profile(passed)
        If onlyCase Is Nothing OrElse onlyCase = "native-overrides" Then OverrideRoles(passed)
        If onlyCase Is Nothing OrElse onlyCase = "native-rules" Then Rules(passed)
        If onlyCase Is Nothing OrElse onlyCase = "native-install" Then Await InstallAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "native-cancel" Then Await CancelAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown native-library regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Bytes(version As String, native As Boolean) As Byte()
        Return Encoding.UTF8.GetBytes(If(native, "Windows native ", "Java runtime ") & version)
    End Function

    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function Relative(version As String, native As Boolean) As String
        Return "fixture/dual/" & version & "/dual-" & version & If(native, "-natives-windows", "") & ".jar"
    End Function

    Private Function Library(version As String, runtime As Boolean, native As Boolean, Optional server As String = "https://example.org") As JObject
        Dim result As New JObject From {{"name", "fixture:dual:" & version}}
        Dim downloads As New JObject()
        If runtime Then downloads("artifact") = Artifact(version, False, server)
        If native Then
            downloads("classifiers") = New JObject From {{"natives-windows", Artifact(version, True, server)}}
            result("natives") = New JObject From {{"windows", "natives-windows"}}
            result("extract") = New JObject From {{"exclude", New JArray("META-INF/")}}
        End If
        result("downloads") = downloads
        Return result
    End Function

    Private Function Artifact(version As String, native As Boolean, server As String) As JObject
        Dim content = Bytes(version, native)
        Return New JObject From {{"path", Relative(version, native)}, {"url", server & If(native, "/native", "/runtime") & version}, {"sha1", Hash(content)}, {"size", content.Length}}
    End Function

    Private Function Basis(libraries As JArray) As JObject
        Return New JObject From {{"id", "vanilla"}, {"mainClass", "fixture.Vanilla"}, {"libraries", libraries},
            {"downloads", New JObject From {{"client", New JObject From {{"url", "https://example.org/client"}, {"size", 1}, {"sha1", New String("a"c, 40)}}}}}}
    End Function

    Private Function Paths(profile As JObject) As List(Of String)
        Return MinecraftInstaller.BuildBasePlan(profile, "test").Where(Function(f) f.Category = "library").Select(Function(f) f.RelativePath).ToList()
    End Function

    Private Sub Profile(passed As List(Of String))
        For Each nativeFirst In {False, True}
            Dim entries = If(nativeFirst, New JArray(Library("1", False, True), Library("1", True, False)), New JArray(Library("1", True, False), Library("1", False, True)))
            Dim original = Basis(entries)
            Dim overlay As New JObject From {{"libraries", New JArray()}}
            Dim before = original.ToString()
            Dim merged = ComponentInstaller.MergeProfile(original, overlay)
            Dim plan = Paths(merged)
            Check(plan.Count = 2 AndAlso plan.Contains("libraries/" & Relative("1", False)) AndAlso plan.Contains("libraries/" & Relative("1", True)),
                  "same-coordinate Java and Windows native libraries both survive merge; native-first=" & nativeFirst, passed)
            Check(original.ToString() = before AndAlso DirectCast(overlay("libraries"), JArray).Count = 0,
                  "native library merge leaves both source profiles unchanged; native-first=" & nativeFirst, passed)
        Next
    End Sub

    Private Sub OverrideRoles(passed As List(Of String))
        Dim original = Basis(New JArray(Library("1", True, True)))
        Dim overlay As New JObject From {{"libraries", New JArray(Library("2", True, False))}}
        Dim beforeOriginal = original.ToString(), beforeOverlay = overlay.ToString()
        Dim merged = ComponentInstaller.MergeProfile(original, overlay)
        Dim plan = Paths(merged)
        Check(plan.Count = 2 AndAlso plan.Contains("libraries/" & Relative("2", False)) AndAlso plan.Contains("libraries/" & Relative("1", True)),
              "a loader's Java library upgrade preserves the existing Windows native artifact", passed)
        Dim libraries = DirectCast(merged("libraries"), JArray).OfType(Of JObject)().ToList()
        Dim nativeEntry = libraries.Single(Function(l) l("natives") IsNot Nothing)
        Check(nativeEntry.SelectToken("downloads.artifact") Is Nothing AndAlso CStr(nativeEntry.SelectToken("extract.exclude[0]")) = "META-INF/",
              "native entries retain extraction exclusions and do not advertise an obsolete Java artifact to PCL", passed)
        Check(libraries.Single(Function(l) l("natives") Is Nothing).SelectToken("extract") Is Nothing,
              "the ordinary Java entry has no native extraction settings", passed)
        Check(original.ToString() = beforeOriginal AndAlso overlay.ToString() = beforeOverlay AndAlso Not merged.ToString().Contains("_mergeKey"),
              "split native metadata preserves source profiles and emits no merge bookkeeping", passed)

        Dim nativeUpgrade = ComponentInstaller.MergeProfile(Basis(New JArray(Library("1", True, False), Library("1", False, True))),
            New JObject From {{"libraries", New JArray(Library("2", False, True))}})
        plan = Paths(nativeUpgrade)
        Check(plan.Count = 2 AndAlso plan.Contains("libraries/" & Relative("1", False)) AndAlso plan.Contains("libraries/" & Relative("2", True)),
              "a native-only upgrade preserves the independent Java library", passed)
        Dim combinedUpgrade = ComponentInstaller.MergeProfile(original, New JObject From {{"libraries", New JArray(Library("2", True, True))}})
        plan = Paths(combinedUpgrade)
        Check(plan.Count = 2 AndAlso plan.All(Function(p) p.Contains("/2/")), "a combined upgrade replaces each library role without retaining obsolete files", passed)
    End Sub

    Private Sub Rules(passed As List(Of String))
        Dim windows = Library("1", False, True)
        windows("rules") = JArray.Parse("[{'action':'allow','os':{'name':'windows'}}]")
        Dim linux = Library("9", False, True)
        linux("rules") = JArray.Parse("[{'action':'allow','os':{'name':'linux'}}]")
        Dim runtime = Library("2", True, False)
        runtime("rules") = windows("rules").DeepClone()
        Dim merged = ComponentInstaller.MergeProfile(Basis(New JArray(windows, linux)), New JObject From {{"libraries", New JArray(runtime)}})
        Dim plan = Paths(JObject.Parse(merged.ToString()))
        Check(plan.Count = 2 AndAlso plan.Contains("libraries/" & Relative("1", True)) AndAlso plan.Contains("libraries/" & Relative("2", False)),
              "Windows rules preserve both library roles after profile serialization", passed)
        Check(Not plan.Any(Function(p) p.Contains("/9/")) AndAlso DirectCast(merged("libraries"), JArray).Count = 3,
              "native merge retains conditional metadata while the Windows download plan excludes Linux-only entries", passed)
        Dim legacy As New JObject From {{"name", "fixture:dual:1"}, {"natives", JValue.CreateNull()}, {"url", "https://example.org/"}}
        merged = ComponentInstaller.MergeProfile(Basis(New JArray(legacy)), New JObject From {{"libraries", New JArray()}})
        Check(Paths(merged).SequenceEqual({"libraries/" & Relative("1", False)}), "an explicit null native marker keeps PCL's ordinary Maven fallback", passed)
        Check(merged("libraries")(0)("natives") Is Nothing AndAlso legacy("natives").Type = JTokenType.Null,
              "null native normalization does not mutate the source declaration", passed)
    End Sub

    Private Sub Configure(fixture As InstallationChecks.InstallFixture, baseLibraries As JArray, overlayLibraries As JArray)
        For Each version In {"1", "2"}
            fixture.Server.Files("/runtime" & version) = Bytes(version, False)
            fixture.Server.Files("/native" & version) = Bytes(version, True)
        Next
        Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        metadata("libraries") = baseLibraries
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
        fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
        Dim profile As New JObject From {{"inheritsFrom", fixture.Entry.Id}, {"mainClass", "fixture.FabricLoader"}, {"libraries", overlayLibraries}}
        fixture.Server.Files("/v2/versions/loader/" & fixture.Entry.Id & "/fixture/profile/json") = Encoding.UTF8.GetBytes(profile.ToString())
    End Sub

    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        For Each combined In {False, True}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "native-install-" & combined)
                Dim selection = fixture.Fabric()
                Dim baseLibraries = If(combined, New JArray(Library("1", True, True, fixture.Server.Url)),
                    New JArray(Library("1", True, False, fixture.Server.Url), Library("1", False, True, fixture.Server.Url)))
                Configure(fixture, baseLibraries, New JArray(Library("2", True, False, fixture.Server.Url)))
                Dim result = Await fixture.InstallAsync("native-result", selection)
                Dim installed = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "native-result.json")))
                Dim plan = Paths(installed)
                Check(plan.Count = 2 AndAlso plan.Contains("libraries/" & Relative("1", True)) AndAlso plan.Contains("libraries/" & Relative("2", False)),
                      "published Fabric JSON retains upgraded Java and original native library; combined=" & combined, passed)
                Check(File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("1", True))).SequenceEqual(Bytes("1", True)) AndAlso
                      File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("2", False))).SequenceEqual(Bytes("2", False)),
                      "real component installation publishes both verified library files; combined=" & combined, passed)
                Check(fixture.Server.Requests("/native1") = 1 AndAlso fixture.Server.Requests("/runtime2") = 1,
                      "component completion reuses the already verified vanilla native instead of downloading it again; combined=" & combined, passed)
                Dim nativeEntry = DirectCast(installed("libraries"), JArray).OfType(Of JObject)().Single(Function(l) l("natives") IsNot Nothing)
                Check(nativeEntry.SelectToken("downloads.artifact") Is Nothing AndAlso CStr(nativeEntry.SelectToken("extract.exclude[0]")) = "META-INF/",
                      "published native metadata exposes the PCL native extraction branch and exclusions; combined=" & combined, passed)
                Check(CStr(installed("id")) = "native-result" AndAlso CStr(installed("clientVersion")) = fixture.Entry.Id AndAlso
                      File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True"),
                      "native-library completion preserves PCL instance identity and component isolation; combined=" & combined, passed)
            End Using
        Next
    End Function

    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "native-cancel"), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(TimeSpan.FromSeconds(20))
            Dim selection = fixture.Fabric()
            Configure(fixture, New JArray(), New JArray(Library("1", True, True, fixture.Server.Url)))
            fixture.Server.Gates("/native1") = gate
            Dim install = fixture.InstallAsync("native-cancel", selection, token:=cancel.Token)
            Try
                Dim clock = Diagnostics.Stopwatch.StartNew()
                While Not fixture.Server.Requests.ContainsKey("/native1") AndAlso Not install.IsCompleted AndAlso clock.ElapsedMilliseconds < 8000
                    Await Task.Delay(20)
                End While
                Check(fixture.Server.Requests.ContainsKey("/native1") AndAlso Not install.IsCompleted,
                      "a native library introduced by the loader is required before instance publication", passed)
                cancel.Cancel()
                Dim cancelled = False
                Try
                    Await install
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "native-cancel")) AndAlso
                      Not File.Exists(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("1", True))),
                      "cancelling a native transfer publishes neither its incomplete library nor the component instance", passed)
                Dim work = Path.Combine(fixture.Root, ".vanilla-downloader", "components")
                Check(Not Directory.Exists(work) OrElse Not Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Any(),
                      "cancelled native installation removes its private component payload", passed)
                gate.Set()
                Dim result = Await fixture.InstallAsync("native-cancel", selection)
                Dim installed = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "native-cancel.json")))
                Check(Paths(installed).Count = 2 AndAlso File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/" & Relative("1", True))).SequenceEqual(Bytes("1", True)),
                      "retrying the cancelled native installation publishes a complete verified component profile", passed)
            Finally
                cancel.Cancel()
                gate.Set()
            End Try
        End Using
    End Function
End Module
