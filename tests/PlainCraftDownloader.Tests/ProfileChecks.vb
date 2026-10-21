Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module ProfileChecks
    Friend Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "profile-modern" Then Modern(passed)
        If onlyCase Is Nothing OrElse onlyCase = "profile-legacy" Then Legacy(passed)
        If onlyCase Is Nothing OrElse onlyCase = "profile-legacy-rules" Then LegacyRules(passed)
        If onlyCase Is Nothing OrElse onlyCase = "profile-library-types" Then LibraryTypes(passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown profile regression: " & onlyCase)
        Return Task.FromResult(passed)
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Sub Modern(passed As List(Of String))
        Dim basis = JObject.Parse("{'id':'vanilla','arguments':{'jvm':['-cp','${classpath}'],'game':['--first','same']},'libraries':[]}")
        Dim overlay = JObject.Parse("{'id':'loader','arguments':{'jvm':['--add-opens','java.base/java.lang=ALL-UNNAMED','--add-opens','java.base/java.util=ALL-UNNAMED','--add-exports','java.base/sun.security.util=ALL-UNNAMED','--add-exports','jdk.naming.dns/com.sun.jndi.dns=java.naming'],'game':['--second','same','--tweakClass','first.Tweaker','--tweakClass','second.Tweaker']},'libraries':[]}")
        Dim originalBasis = basis.ToString(), originalOverlay = overlay.ToString()
        Dim merged = ComponentInstaller.MergeProfile(basis, overlay)
        Dim expectedJvm = New JArray(DirectCast(basis("arguments")("jvm"), JArray).Concat(DirectCast(overlay("arguments")("jvm"), JArray)))
        Dim expectedGame = New JArray("--first", "same", "--second", "same", "--tweakClass", "first.Tweaker", "--tweakClass", "second.Tweaker")
        Assert(JToken.DeepEquals(merged("arguments")("jvm"), expectedJvm), "JVM option/value order preserves every repeated add-opens and add-exports flag", passed)
        Assert(JToken.DeepEquals(merged("arguments")("game"), expectedGame), "game arguments retain repeated values and both tweakClass option/value pairs", passed)
        Assert(basis.ToString() = originalBasis AndAlso overlay.ToString() = originalOverlay AndAlso CStr(merged("id")) = "vanilla", "profile merge leaves both sources and base identity unchanged", passed)
    End Sub

    Private Sub Legacy(passed As List(Of String))
        Dim basis = JObject.Parse("{'minecraftArguments':'--username ${auth_player_name} --gameDir ${game_directory}','libraries':[]}")
        Dim overlay = JObject.Parse("{'arguments':{'game':['--tweakClass','fixture.Tweaker'],'jvm':['-Dfixture=value']},'libraries':[]}")
        Dim merged = ComponentInstaller.MergeProfile(basis, overlay)
        Assert(JToken.DeepEquals(merged.SelectToken("arguments.jvm")?.Last, New JValue("-Dfixture=value")), "legacy vanilla plus a new-style loader retains loader JVM options", passed)
        Assert(CStr(merged("minecraftArguments")) = CStr(basis("minecraftArguments")) AndAlso JToken.DeepEquals(merged.SelectToken("arguments.game"), overlay.SelectToken("arguments.game")), "PCL can read the original legacy string together with the structured loader game arguments", passed)
        Dim jvm = DirectCast(merged.SelectToken("arguments.jvm"), JArray).Values(Of String)().ToList()
        Dim cp = jvm.IndexOf("-cp")
        Assert(cp >= 0 AndAlso cp + 1 < jvm.Count AndAlso jvm(cp + 1) = "${classpath}" AndAlso jvm.Contains("-Djava.library.path=${natives_directory}"), "adding new-style JVM options keeps the PCL legacy classpath and native-library defaults", passed)
        Dim alreadyModern = JObject.Parse("{'minecraftArguments':'--username test','arguments':{'jvm':['-cp','${classpath}','-Dbase=true']},'libraries':[]}")
        Dim again = ComponentInstaller.MergeProfile(alreadyModern, overlay)
        Assert(DirectCast(again.SelectToken("arguments.jvm"), JArray).Values(Of String)().Count(Function(s) s = "-cp") = 1 AndAlso again.SelectToken("arguments.jvm").Values(Of String)().Contains("-Dbase=true"), "existing structured JVM arguments do not receive a second legacy default set", passed)
    End Sub

    Private Sub LegacyRules(passed As List(Of String))
        Dim basis = JObject.Parse("{'minecraftArguments':'--username ${auth_player_name}','libraries':[]}")
        Dim overlay = JObject.Parse("{'arguments':{'game':[{'rules':[{'action':'allow','os':{'name':'windows'}}],'value':['--fixture','value with spaces']},{'rules':[{'action':'allow','features':{'has_custom_resolution':true}}],'value':['--width','${resolution_width}']}]},'libraries':[]}")
        Dim before = overlay.ToString()
        Dim merged = ComponentInstaller.MergeProfile(basis, overlay)
        Assert(JToken.DeepEquals(merged.SelectToken("arguments.game"), overlay.SelectToken("arguments.game")), "conditional game arguments and space-containing values remain structured for PCL to evaluate later", passed)
        Assert(merged.SelectToken("arguments.jvm") Is Nothing AndAlso CStr(merged("minecraftArguments")) = CStr(basis("minecraftArguments")), "a game-only loader preserves PCL legacy JVM fallback behavior", passed)
        Assert(overlay.ToString() = before AndAlso JToken.DeepEquals(JObject.Parse(merged.ToString()), merged), "mixed legacy and structured arguments survive JSON serialization without changing the source", passed)
    End Sub

    Private Sub LibraryTypes(passed As List(Of String))
        Dim basis = JObject.Parse("{'libraries':[{'name':'fixture:runtime:1'},{'name':'fixture:runtime:1@zip'},{'name':'fixture:runtime:1:natives-windows'},{'name':'fixture:runtime:1:natives-windows@zip'}]}")
        Dim overlay = JObject.Parse("{'libraries':[{'name':'fixture:runtime:2@jar'},{'name':'fixture:runtime:2:natives-windows@jar'}]}")
        Dim merged = ComponentInstaller.MergeProfile(basis, overlay)
        Dim libraries = DirectCast(merged("libraries"), JArray)
        Assert(libraries.Count = 4 AndAlso libraries.Any(Function(libraryInfo) CStr(libraryInfo("name")) = "fixture:runtime:1@zip"), "a jar override cannot remove a distinct zip runtime artifact", passed)
        Assert(libraries.Any(Function(libraryInfo) CStr(libraryInfo("name")) = "fixture:runtime:1:natives-windows@zip") AndAlso libraries.Any(Function(libraryInfo) CStr(libraryInfo("name")) = "fixture:runtime:2:natives-windows@jar"), "classifier and extension both participate in library override identity", passed)
        Assert(Not libraries.Any(Function(libraryInfo) CStr(libraryInfo("name")) = "fixture:runtime:1") AndAlso libraries.Any(Function(libraryInfo) CStr(libraryInfo("name")) = "fixture:runtime:2@jar"), "implicit jar and explicit jar represent the same overridable library type", passed)
        Assert(MinecraftInstaller.MavenPath("fixture:runtime:1@zip") = "fixture/runtime/1/runtime-1.zip" AndAlso MinecraftInstaller.MavenPath("fixture:runtime:2:natives-windows@jar").EndsWith("runtime-2-natives-windows.jar", StringComparison.Ordinal), "retained library types map to their distinct on-disk files", passed)
    End Sub

    ' Opt-in integration check: parse installed JVM options with Java -version.
    ' No Minecraft main class, login, game window, or account access is involved.
    Friend Async Function CheckJvmAsync(profilePath As String, reportPath As String) As Task
        profilePath = Path.GetFullPath(profilePath)
        reportPath = Path.GetFullPath(reportPath)
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath))
        Dim profile = JObject.Parse(File.ReadAllText(profilePath))
        Dim instance = Path.GetDirectoryName(profilePath)
        Dim root = Directory.GetParent(Directory.GetParent(instance).FullName).FullName
        Dim plan = MinecraftInstaller.BuildBasePlan(profile, CStr(profile("id")))
        Dim classpath = plan.Where(Function(f) f.Category = "library" OrElse f.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Select(Function(f) SafeFiles.UnderRoot(root, f.RelativePath))
        Dim substitutions As New Dictionary(Of String, String) From {
            {"${library_directory}", Path.Combine(root, "libraries")}, {"${classpath_separator}", Path.PathSeparator.ToString()},
            {"${classpath}", String.Join(Path.PathSeparator.ToString(), classpath)}, {"${natives_directory}", Path.Combine(instance, "natives-verification")},
            {"${launcher_name}", "PlainCraftDownloader"}, {"${launcher_version}", "verification"}, {"${version_name}", CStr(profile("id"))}}
        Dim arguments As New List(Of String)
        For Each argument In DirectCast(profile.SelectToken("arguments.jvm"), JArray)
            If argument.Type = JTokenType.String Then
                arguments.Add(CStr(argument))
            ElseIf MinecraftInstaller.RulesAllow(argument("rules")) Then
                Dim value = argument("value")
                If value.Type = JTokenType.String Then
                    arguments.Add(CStr(value))
                Else
                    arguments.AddRange(value.Values(Of String)())
                End If
            End If
        Next
        For i = 0 To arguments.Count - 1
            For Each substitution In substitutions
                arguments(i) = arguments(i).Replace(substitution.Key, substitution.Value)
            Next
            If Text.RegularExpressions.Regex.IsMatch(arguments(i), "\$\{[^}]+\}") Then Throw New InvalidDataException("Unresolved JVM placeholder: " & arguments(i))
        Next
        Dim java = JavaRuntime.Find(Nothing, CInt(If(profile.SelectToken("javaVersion.majorVersion"), New JValue(21))))
        Dim lines As New List(Of String)
        Dim failure As Exception = Nothing
        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(30))
            Try
                Await JavaRuntime.RunAsync(java, arguments.Concat({"-version"}), root,
                    Sub(line)
                        SyncLock lines
                            lines.Add(line)
                        End SyncLock
                    End Sub, timeout.Token)
            Catch ex As Exception
                failure = ex
            End Try
        End Using
        Dim record As New JObject From {{"Profile", profilePath}, {"Java", java}, {"Passed", failure Is Nothing},
            {"Check", "installed JVM options followed by -version; no game main class"},
            {"AddOpensFlags", arguments.Where(Function(a) a = "--add-opens").Count()}, {"AddExportsFlags", arguments.Where(Function(a) a = "--add-exports").Count()},
            {"Output", JArray.FromObject(lines)}, {"Error", If(failure Is Nothing, Nothing, failure.Message)}}
        File.WriteAllText(reportPath, record.ToString())
        If failure IsNot Nothing Then Throw failure
        Console.WriteLine("JVM_PROFILE_OK " & reportPath)
    End Function
End Module
