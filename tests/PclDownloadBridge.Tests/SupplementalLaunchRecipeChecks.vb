Imports System.IO
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalLaunchRecipeChecks
    Private ReadOnly ExistingCheck As MethodInfo = GetType(MinecraftInstaller).Assembly.GetType("PclDownloadBridge.Core.SupplementalLaunchRecipe", True).
        GetMethod("ValidateExisting", BindingFlags.Static Or BindingFlags.NonPublic)

    Private Sub Check(ok As Boolean, name As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Function ExistingAccepts(id As String, metadata As JObject) As Boolean
        Try
            ExistingCheck.Invoke(Nothing, {id, metadata}) : Return True
        Catch ex As TargetInvocationException When TypeOf ex.InnerException Is InvalidDataException
            Return False
        End Try
    End Function

    Private Class ReferencePolicy
        Inherits SourcePolicy
        Private ReadOnly _url As String
        Friend Sub New(url As String)
            _url = url : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_url & If(url = SupplementalVersions.AlphaLibraryReferenceUrl, "/alpha-reference", "/reference")}
        End Function
    End Class

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Using server As New TestServer()
            server.Files("/reference") = SupplementalInstallFixture.Resource("SupplementalLibraryReference.json")
            server.Files("/alpha-reference") = SupplementalInstallFixture.Resource("Historical.AlphaLibraryReference.json")
            For Each id In SupplementalInstallFixture.Ids
                Dim metadata = JObject.Parse(Encoding.UTF8.GetString(SupplementalInstallFixture.Resource("Historical." & id & ".json")))
                Dim entry = SupplementalInstallFixture.Entry(id), original = metadata.ToString()
                Dim prepared = Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, New ReferencePolicy(server.Url), CancellationToken.None)
                SupplementalVersions.ValidatePlan(MinecraftInstaller.BuildBasePlan(prepared, "recipe"))
                Check(metadata.ToString() = original, "real archived recipe prepares without mutating source: " & id, passed)
                For Each form In {"quoted", "equals", "inline-quotes", "session-alias", "newline", "explicit-tweaker"}
                    Dim changedProfile = DirectCast(prepared.DeepClone(), JObject), text = CStr(changedProfile("minecraftArguments"))
                    Select Case form
                        Case "quoted" : text = text.Replace("${game_directory}", """${game_directory}""").Replace("${game_assets}", """${game_assets}""")
                        Case "equals" : text = text.Replace("--gameDir ", "--gameDir=").Replace("--assetsDir ", "--assetsDir=")
                        Case "inline-quotes" : text = text.Replace("--gameDir ${game_directory}", "--gameDir=""${game_directory}""")
                        Case "session-alias" : text = text.Replace("${auth_session}", "${auth_access_token}")
                        Case "newline" : text = text.Replace(" --assetsDir", vbCrLf & " --assetsDir")
                        Case "explicit-tweaker"
                            If Not SupplementalInstallFixture.UsesAlpha(id) Then text &= " --tweakClass net.minecraft.launchwrapper.VanillaTweaker"
                    End Select
                    changedProfile("minecraftArguments") = text
                    SupplementalVersions.ValidateMetadata(entry, changedProfile)
                    Check(ExistingAccepts(id, changedProfile), "source and existing recipe accept equivalent form: " & id & " " & form, passed)
                Next
            Next
        End Using
        Using fixture As New InstallationChecks.InstallFixture(root, "recipe-shapes")
            Dim source = SupplementalInstallFixture.Apply(fixture, "c0.0.12a_03"), entry = SupplementalInstallFixture.Entry("c0.0.12a_03")
            Dim mutations As New Dictionary(Of String, Action(Of JObject)) From {
                {"missing arguments", Sub(meta) meta.Remove("minecraftArguments")},
                {"null arguments", Sub(meta) meta("minecraftArguments") = JValue.CreateNull()},
                {"object arguments", Sub(meta) meta("minecraftArguments") = New JObject()},
                {"array arguments", Sub(meta) meta("minecraftArguments") = New JArray()},
                {"empty arguments", Sub(meta) meta("minecraftArguments") = ""},
                {"wrong main class", Sub(meta) meta("mainClass") = "not.the.expected.Main"},
                {"mixed game arguments", Sub(meta) meta("arguments") = New JObject From {{"game", New JArray("--gameDir", "wrong")}}},
                {"mixed JVM arguments", Sub(meta) meta("arguments") = New JObject From {{"jvm", New JArray("-Dresearch=true")}}},
                {"null modern arguments", Sub(meta) meta("arguments") = JValue.CreateNull()},
                {"directory suffix", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("${game_directory}", "${game_directory}/other")},
                {"wrong assets role", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("${game_assets}", "${assets_root}")},
                {"wrong option case", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir", "--gamedir")},
                {"missing value", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir ${game_directory}", "--gameDir")},
                {"empty quoted value", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir ${game_directory}", "--gameDir """" ${game_directory}")},
                {"empty equals value", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir ${game_directory}", "--gameDir= ${game_directory}")},
                {"empty quote suffix", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("${game_directory}", "${game_directory}""""")},
                {"empty quote prefix", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("${game_directory}", """""${game_directory}")},
                {"unquoted variable after quoted key", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir ${game_directory}", """--gameDir=""${game_directory}")},
                {"unclosed quote", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & " """},
                {"tab separator", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("--gameDir ", "--gameDir" & vbTab)},
                {"wrong tweaker", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")).Replace("AlphaVanillaTweaker", "IndevVanillaTweaker")},
                {"missing Alpha", Sub(meta) meta("minecraftArguments") = SupplementalInstallFixture.Arguments("2.0_blue")},
                {"duplicate directory", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & " --gameDir ${game_directory}"},
                {"additional tweaker", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & " --tweakClass net.minecraft.launchwrapper.VanillaTweaker"},
                {"terminator", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & " --"},
                {"oversized string", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & New String(" "c, 4097)},
                {"token limit", Sub(meta) meta("minecraftArguments") = CStr(meta("minecraftArguments")) & String.Concat(Enumerable.Repeat(" """"", 65))}}
            For Each mutation In mutations
                Dim bad = DirectCast(source.DeepClone(), JObject), rejected = False
                mutation.Value.Invoke(bad)
                Try
                    SupplementalVersions.ValidateMetadata(entry, bad)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected, "source recipe rejects " & mutation.Key, passed)
                If mutation.Key = "empty quote suffix" OrElse mutation.Key = "empty quote prefix" OrElse mutation.Key = "unquoted variable after quoted key" Then
                    Check(Not ExistingAccepts(entry.Id, bad), "existing recipe rejects PCL path-quoting ambiguity: " & mutation.Key, passed)
                End If
            Next
            For Each mutation In New Action(Of JObject)() {
                Sub(meta) meta("libraries") = New JArray(),
                Sub(meta) DirectCast(meta("libraries"), JArray).Add(meta("libraries")(0).DeepClone()),
                Sub(meta) meta("libraries")(0)("name") = "net.minecraft:launchwrapper:1.5",
                Sub(meta) meta("libraries")(0)("rules") = New JArray(),
                Sub(meta) meta("libraries")(0)("natives") = New JObject(),
                Sub(meta) meta("libraries")(0)("downloads")("artifact")("sha1") = New String("a"c, 40),
                Sub(meta) meta("libraries")(0)("downloads")("artifact")("size") = 1}
                Dim bad = DirectCast(source.DeepClone(), JObject), rejected = False
                mutation(bad)
                Try
                    Await SupplementalVersions.PrepareLibrariesAsync(entry, bad, New ReferencePolicy(fixture.Server.Url), CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso fixture.Server.Requests.Count = 0, "wrapper identity rejected before network: " & passed.Count, passed)
            Next
            For Each extra In {" --width 1280 --height 720 --fullscreen", " --server localhost --port 25565", " --researchTag retained", " --researchTag -50", " --researchTag=-gameDir=literal", " --version ${version_name}", " --researchTag -v=research-profile", " --version -gameDir=literal"}
                Dim existing = DirectCast(source.DeepClone(), JObject)
                existing("id") = "user-renamed" : existing("clientVersion") = entry.Id
                existing("minecraftArguments") = CStr(existing("minecraftArguments")) & extra
                Dim before = existing.ToString()
                Check(ExistingAccepts(entry.Id, existing) AndAlso existing.ToString() = before, "renamed existing recipe preserves compatible option: " & extra, passed)
            Next
            For Each extra In {" --researchTag -gameDir=D:\Wrong", " --researchTag -g=D:\Wrong", " --researchTag -assetsD=D:\Wrong", " --researchTag -t=net.minecraft.launchwrapper.VanillaTweaker", " --version one -v two"}
                Dim existing = DirectCast(source.DeepClone(), JObject)
                existing("minecraftArguments") = CStr(existing("minecraftArguments")) & extra
                Check(Not ExistingAccepts(entry.Id, existing), "hidden or duplicate reserved option rejected: " & extra, passed)
            Next
            Dim oracle = JObject.Parse(Encoding.UTF8.GetString(SupplementalInstallFixture.Resource("LegacyLaunch.ArgumentOracle.json")))
            Dim cases = DirectCast(oracle("Cases"), JArray)
            Check(cases.Count = 558 AndAlso CStr(oracle("ParserSHA1")) = "6065cc95c661255349c1d0756657be17c29a4fd3", "argument oracle identifies the verified real parser corpus", passed)
            For Each item In cases.OfType(Of JObject)()
                Dim id = CStr(item("id")), profile = SupplementalInstallFixture.Profile(fixture, id)
                profile("id") = "renamed" : profile("minecraftArguments") = item("arguments").DeepClone()
                Check(ExistingAccepts(id, profile) = CBool(item("accepted")), "production validator agrees with real parser oracle: " & passed.Count, passed)
            Next
            Dim modern As New JObject From {{"id", "modern"}, {"mainClass", "net.minecraft.client.main.Main"}, {"arguments", New JObject From {{"game", New JArray("--gameDir", "${game_directory}")}}}}
            SupplementalVersions.ValidateMetadata(New VersionEntry With {.Id = "modern"}, modern)
            Check(True, "ordinary modern metadata stays outside legacy recipe validation", passed)
        End Using
        Await Alpha15Async(root, passed)
        passed.AddRange(Await SupplementalRecipeInstallationChecks.RunAsync(root))
        Return passed
    End Function

    Private Async Function Alpha15Async(root As String, passed As List(Of String)) As Task
        For Each id In {"a1.1.0-1", "a1.0.17_03", "a1.0.16_02"}
            Using fixture As New InstallationChecks.InstallFixture(root, "alpha15-recipe-" & id)
                Dim source = SupplementalInstallFixture.Profile(fixture, id), entry = SupplementalInstallFixture.Entry(id)
                Dim vanillaArguments = SupplementalInstallFixture.Arguments("a1.1.0-2")
                For Each arguments In {vanillaArguments, vanillaArguments & " --tweakClass net.minecraft.launchwrapper.VanillaTweaker",
                    vanillaArguments & " --tweakClass net.minecraft.launchwrapper.IndevVanillaTweaker",
                    CStr(source("minecraftArguments")) & " --tweakClass net.minecraft.launchwrapper.AlphaVanillaTweaker"}
                    Dim bad = DirectCast(source.DeepClone(), JObject), rejected = False
                    bad("minecraftArguments") = arguments
                    Try
                        SupplementalVersions.ValidateMetadata(entry, bad)
                    Catch ex As InvalidDataException
                        rejected = True
                    End Try
                    Check(rejected AndAlso Not ExistingAccepts(id, bad), "Alpha 1.5 rejects missing, replaced or duplicate tweaker in source and existing profile: " & id & " " & passed.Count, passed)
                Next
                Dim otherWrapper = SupplementalInstallFixture.Profile(fixture, "c0.0.12a_03")
                Dim swapped = DirectCast(source.DeepClone(), JObject), wrapperRejected = False
                swapped("libraries") = otherWrapper("libraries").DeepClone()
                Try
                    Await SupplementalVersions.PrepareLibrariesAsync(entry, swapped, New ReferencePolicy(fixture.Server.Url), CancellationToken.None)
                Catch ex As InvalidDataException
                    wrapperRejected = True
                End Try
                Check(wrapperRejected AndAlso fixture.Server.Requests.Count = 0, "Alpha 1.5 rejects a correctly hashed 1.6 wrapper before network: " & id, passed)
                Dim vanilla = SupplementalInstallFixture.Profile(fixture, "a1.1.0-2")
                vanilla("minecraftArguments") = source("minecraftArguments").DeepClone()
                Check(Not ExistingAccepts("a1.1.0-2", vanilla), "neighboring Alpha revision retains its default Vanilla recipe: " & id, passed)

                ' A broken existing profile is refused before shared-file repair; the user,
                ' not the installer, must explicitly correct its incompatible launch settings.
                SupplementalInstallFixture.Apply(fixture, id)
                Dim installer As New MinecraftInstaller(New AlphaPolicy(fixture.Server.Url))
                Await installer.InstallAsync(entry, fixture.Root, "renamed", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
                Dim instance = Path.Combine(fixture.Root, "versions/renamed"), jsonPath = Path.Combine(instance, "renamed.json")
                Dim iniPath = Path.Combine(instance, "PCL/Setup.ini"), clientPath = Path.Combine(instance, "renamed.jar")
                File.AppendAllText(iniPath, "UserSetting:保留" & vbCrLf)
                Dim ini = File.ReadAllBytes(iniPath), json = JObject.Parse(File.ReadAllText(jsonPath)), corrupt = Encoding.UTF8.GetBytes("corrupt existing client")
                json("minecraftArguments") = vanillaArguments & " --width 1360 --researchTag retained"
                Dim original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(json.ToString() & vbCrLf)).ToArray()
                File.WriteAllBytes(jsonPath, original) : File.WriteAllBytes(clientPath, corrupt)
                Dim before = fixture.NonVersionRequests(), repairRejected = False
                Try
                    Await installer.InstallAsync(entry, fixture.Root, "renamed", {}, Nothing, CancellationToken.None, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
                Catch ex As InvalidDataException
                    repairRejected = True
                End Try
                Check(repairRejected AndAlso fixture.NonVersionRequests() = before AndAlso File.ReadAllBytes(clientPath).SequenceEqual(corrupt) AndAlso
                    File.ReadAllBytes(jsonPath).SequenceEqual(original) AndAlso File.ReadAllBytes(iniPath).SequenceEqual(ini), "Alpha 1.5 refuses incompatible repair without altering existing bytes or downloading shared files: " & id, passed)
                json("minecraftArguments") = CStr(source("minecraftArguments")) & " --width 1360 --researchTag retained"
                Dim corrected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(json.ToString() & vbCrLf)).ToArray()
                File.WriteAllBytes(jsonPath, corrected) ' Explicit correction in this isolated test only.
                Await installer.InstallAsync(entry, fixture.Root, "renamed", {}, Nothing, CancellationToken.None, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
                Check(File.ReadAllBytes(clientPath).SequenceEqual(fixture.Server.Files("/client")) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(corrected) AndAlso
                    File.ReadAllBytes(iniPath).SequenceEqual(ini), "Alpha 1.5 corrected same-name repair preserves UTF-16, custom arguments and original isolation: " & id, passed)
            End Using
        Next
    End Function

    Private Class AlphaPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {If(New Uri(url).Host = "zkitefly.github.io", _server & "/version", url)}
        End Function
    End Class
End Module
