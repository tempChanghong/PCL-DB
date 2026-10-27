Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Module Program
    Private _passed As Integer
    Private ReadOnly TestRoot As String = Path.Combine(Path.GetTempPath(), "PclDownloadBridgeTests-" & Guid.NewGuid().ToString("N"))
    Sub Main(args As String())
        Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
        Try
            If ProcessorStreamChecks.TryRunFixture(args) Then Return
            If args.Length = 2 AndAlso args(0) = "--live-community-resources" Then
                CommunityResourceChecks.LiveAsync(args(1)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 4 AndAlso args(0) = "--large-modpack-samples" Then
                LargeModpackChecks.SamplesAsync(args(1), args(2), args(3)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length > 0 AndAlso args(0) = "--verify-supplemental" Then
                If args.Length < 3 OrElse args.Length > 4 Then Throw New ArgumentException("Supplemental verification expects an output directory, canonical IDs and optional --install.")
                If args.Length = 4 AndAlso args(3) <> "--install" Then Throw New ArgumentException("Unknown supplemental verification option.")
                SupplementalCandidateVerification.LiveAsync(args(1), args(2).Split(","c), args.Length = 4).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 2 AndAlso args(0) = "--live-curseforge-catalogue" Then
                CurseForgeCatalogLiveChecks.RunAsync(args(1)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length >= 2 AndAlso args(0) = "--live-modpack-search" Then
                ModpackCatalogChecks.LiveAsync(args(1), args.Contains("--install")).GetAwaiter().GetResult()
                Return
            End If
            If args.Length >= 3 AndAlso args(0) = "--live-modrinth" Then
                ModrinthLiveChecks.RunAsync(args(1), args(2), args.Skip(3)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 3 AndAlso args(0) = "--live-curseforge" Then
                CurseForgeLiveChecks.RunAsync(args(1), args(2)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 2 AndAlso args(0) = "--live-supplemental" Then
                SupplementalVersionChecks.LiveAsync(args(1)).GetAwaiter().GetResult()
                Return
            End If
            If JavaProbeChecks.TryRunFixture(args) Then Return
            If args.Length = 2 AndAlso args(0) = "--live-source-selection" Then
                SourcePreferenceChecks.LiveAsync(args(1)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 5 AndAlso args(0) = "--live-single-resume" Then
                SingleResumeChecks.LiveAsync(args(1), args(2), Long.Parse(args(3), Globalization.CultureInfo.InvariantCulture), args(4)).GetAwaiter().GetResult()
                Return
            End If
            If args.Length > 0 AndAlso args(0) = "--live" Then
                LiveAsync(args).GetAwaiter().GetResult()
                Return
            End If
            If args.Length = 3 AndAlso args(0) = "--check-jvm-profile" Then
                ProfileChecks.CheckJvmAsync(args(1), args(2)).GetAwaiter().GetResult()
                Return
            End If
            Directory.CreateDirectory(TestRoot)
            If args.Length = 2 AndAlso args(0) = "--regression" Then
                Dim run As Task(Of List(Of String))
                If args(1) = "local-mods" Then
                    run = LocalModChecks.RunAsync(TestRoot)
                ElseIf args(1) = "local-mod-updates" Then
                    run = LocalModUpdateChecks.RunAsync(TestRoot)
                ElseIf args(1) = "community-resources" Then
                    run = CommunityResourceChecks.RunAsync(TestRoot)
                ElseIf args(1) = "resource-installation" Then
                    run = ResourceInstallationChecks.RunAsync(TestRoot)
                ElseIf args(1) = "local-mod-actions-followup" Then
                    run = LocalModActionFollowupChecks.RunAsync(TestRoot)
                ElseIf args(1) = "local-mod-catalog-followup" Then
                    run = LocalModCatalogFollowupChecks.RunAsync(TestRoot)
                ElseIf args(1) = "local-mod-manifest" Then
                    run = LocalModManifestChecks.RunAsync(TestRoot)
                ElseIf args(1) = "installed-instances" Then
                    run = InstalledInstanceChecks.RunAsync(TestRoot)
                ElseIf args(1) = "large-modpacks" Then
                    run = LargeModpackChecks.RunAsync(TestRoot)
                ElseIf args(1) = "native-isolation" Then
                    run = NativeIsolationChecks.RunAsync(TestRoot)
                ElseIf args(1) = "external-launchers" Then
                    run = ExternalLauncherChecks.RunAsync(TestRoot)
                ElseIf args(1) = "modpack-catalogue" Then
                    run = ModpackCatalogChecks.RunAsync(TestRoot)
                ElseIf args(1) = "archive-cancellation" Then
                    run = ArchiveCancellationChecks.RunAsync(TestRoot)
                ElseIf args(1) = "modpack-paths" Then
                    run = ModpackPathChecks.RunAsync(TestRoot)
                ElseIf args(1) = "component-file-names" Then
                    run = ComponentFileNameChecks.RunAsync(TestRoot)
                ElseIf args(1).StartsWith("modrinth-", StringComparison.Ordinal) Then
                    run = ModrinthChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("curseforge-", StringComparison.Ordinal) Then
                    run = CurseForgeChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1) = "supplemental-indexes" Then
                    run = SupplementalAssetIndexChecks.RunAsync(TestRoot)
                ElseIf args(1) = "supplemental-versions" Then
                    run = SupplementalVersionChecks.RunAsync(TestRoot)
                ElseIf args(1) = "supplemental-recipes" Then
                    run = SupplementalLaunchRecipeChecks.RunAsync(TestRoot)
                ElseIf args(1) = "supplemental-early" Then
                    run = EarlySupplementalChecks.RunAsync(TestRoot)
                ElseIf args(1) = "supplemental-candidates" Then
                    run = SupplementalCandidateVerificationChecks.RunAsync(TestRoot)
                ElseIf args(1).StartsWith("processor-streams-", StringComparison.Ordinal) Then
                    run = ProcessorStreamChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("component-catalogue-", StringComparison.Ordinal) Then
                    run = ComponentCatalogueChecks.RunAsync(args(1))
                ElseIf args(1).StartsWith("component-plan-", StringComparison.Ordinal) Then
                    run = ComponentDependencyChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1) = "neoforge-checksums" Then
                    run = ForgeChecksumChecks.RunAsync(TestRoot, ComponentKind.NeoForge)
                ElseIf args(1) = "forge-checksums" Then
                    run = ForgeChecksumChecks.RunAsync(TestRoot)
                ElseIf args(1) = "forge-versions" Then
                    run = ForgeVersionChecks.RunAsync(TestRoot)
                ElseIf args(1) = "component-versions" Then
                    run = ComponentVersionChecks.RunAsync()
                ElseIf args(1) = "neoforge-versions" Then
                    run = NeoForgeVersionChecks.RunAsync()
                ElseIf args(1).StartsWith("folder-", StringComparison.Ordinal) Then
                    run = FolderDiscoveryChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("java-probe-", StringComparison.Ordinal) Then
                    run = JavaProbeChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("lib-select-", StringComparison.Ordinal) Then
                    run = LibrarySelectionChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("metadata-transfer-", StringComparison.Ordinal) Then
                    run = MetadataTransferChecks.RunAsync(args(1), TestRoot)
                ElseIf args(1).StartsWith("library-meta-", StringComparison.Ordinal) Then
                    run = LibraryMetadataChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("copy-", StringComparison.Ordinal) Then
                    run = ReuseCopyChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("reuse-", StringComparison.Ordinal) Then
                    run = ReuseAlignmentChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("transfer-cleanup-", StringComparison.Ordinal) Then
                    run = TransferCleanupChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("commit-", StringComparison.Ordinal) Then
                    run = CommitCancellationChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("cleanup-", StringComparison.Ordinal) Then
                    run = CleanupChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("native-", StringComparison.Ordinal) Then
                    run = NativeLibraryChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("plan-", StringComparison.Ordinal) Then
                    run = DownloadPlanChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1) = "name-preflight" Then
                    run = NamePreflightChecks.RunAsync(TestRoot)
                ElseIf args(1).StartsWith("adaptive-", StringComparison.Ordinal) OrElse args(1).StartsWith("parallel-", StringComparison.Ordinal) Then
                    run = AdaptiveParallelChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("install-", StringComparison.Ordinal) Then
                    run = InstallationChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("component-cache-", StringComparison.Ordinal) Then
                    run = ComponentCacheChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("processor-output-", StringComparison.Ordinal) Then
                    run = ProcessorOutputChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("single-resume-", StringComparison.Ordinal) Then
                    run = SingleResumeChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("source-preference-", StringComparison.Ordinal) Then
                    run = SourcePreferenceChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("source-", StringComparison.Ordinal) Then
                    run = SourceAlignmentChecks.RunAsync(TestRoot, args(1))
                ElseIf args(1).StartsWith("profile-", StringComparison.Ordinal) Then
                    run = ProfileChecks.RunAsync(args(1))
                ElseIf args(1).StartsWith("rate-", StringComparison.Ordinal) Then
                    run = RateLimitChecks.RunAsync(TestRoot, args(1))
                Else
                    run = BugChecks.RunAsync(TestRoot, args(1))
                End If
                Dim labels = run.GetAwaiter().GetResult()
                For Each label In labels
                    Check(True, label)
                Next
                Return
            End If
            For Each label In CommunityResourceChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ResourceInstallationChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ArchiveCancellationChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ModpackPathChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ModpackCatalogChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ModrinthChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In CurseForgeChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentFileNameChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SupplementalVersionChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SupplementalLaunchRecipeChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In EarlySupplementalChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SupplementalCandidateVerificationChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In JavaProbeChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            FolderTests()
            For Each label In ExternalLauncherChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In InstalledInstanceChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LocalModChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LocalModUpdateChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LocalModActionFollowupChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LocalModCatalogFollowupChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LocalModManifestChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ForgeChecksumChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ForgeChecksumChecks.RunAsync(TestRoot, ComponentKind.NeoForge).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ForgeVersionChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentVersionChecks.RunAsync().GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentCatalogueChecks.RunAsync().GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In NeoForgeVersionChecks.RunAsync().GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In FolderDiscoveryChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            MetadataTests()
            For Each label In LargeModpackChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            TransferTestsAsync().GetAwaiter().GetResult()
            MetadataRaceTestsAsync().GetAwaiter().GetResult()
            For Each label In MetadataTransferChecks.RunAsync(root:=TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            SlowSourceTestsAsync().GetAwaiter().GetResult()
            InstallTestsAsync().GetAwaiter().GetResult()
            For Each label In BugChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In RateLimitChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In InstallationChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ProfileChecks.RunAsync().GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentCacheChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ProcessorOutputChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SingleResumeChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SourceAlignmentChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In AdaptiveParallelChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In SourcePreferenceChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In NamePreflightChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In DownloadPlanChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ComponentDependencyChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ProcessorStreamChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In NativeLibraryChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In CleanupChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In CommitCancellationChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In TransferCleanupChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ReuseAlignmentChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In ReuseCopyChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LibraryMetadataChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            For Each label In LibrarySelectionChecks.RunAsync(TestRoot).GetAwaiter().GetResult()
                Check(True, label)
            Next
            Console.WriteLine("ALL PASSED: " & _passed & " checks. Fixtures: " & TestRoot)
        Catch ex As Exception
            Console.Error.WriteLine(ex.ToString())
            Environment.ExitCode = 1
        End Try
    End Sub

    Private Sub Check(condition As Boolean, name As String)
        If Not condition Then Throw New Exception("FAIL: " & name)
        _passed += 1
        Console.WriteLine("PASS " & name)
    End Sub
    Private Sub MustThrow(action As Action, name As String)
        Dim failed = False
        Try
            action()
        Catch
            failed = True
        End Try
        Check(failed, name)
    End Sub
    Private Function Hash(bytes As Byte()) As String
        Using sha = SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub FolderTests()
        Dim basePath = Path.Combine(TestRoot, "PCL base 中文")
        Directory.CreateDirectory(Path.Combine(basePath, "PCL"))
        Directory.CreateDirectory(Path.Combine(basePath, ".minecraft", "versions", "test"))
        File.WriteAllText(Path.Combine(basePath, ".minecraft", "versions", "test", "test.json"), "{}")
        File.WriteAllText(Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect:$.minecraft\" & vbCrLf)
        Dim before = File.ReadAllBytes(Path.Combine(basePath, "PCL", "Setup.ini"))
        Dim reader As New PclFolders()
        Dim folders = reader.Discover(basePath, False)
        Check(folders.Any(Function(f) f.IsSelected AndAlso f.Location = Path.Combine(basePath, ".minecraft")), "PCL $ relative current selection")
        Check(folders.First().VersionCount = 1, "existing PCL instance count")
        reader.AddCustomFolders(folders, "副本>$.minecraft\|bad|另一个>" & Path.Combine(TestRoot, "missing") & "\", basePath, "test")
        Check(folders.Where(Function(f) f.Location = Path.Combine(basePath, ".minecraft")).Count() = 1, "custom folder dedup and nonexistent paths")
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(basePath, "PCL", "Setup.ini"))), "PCL configuration stays byte-identical")
        File.AppendAllText(Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect:" & basePath & vbCrLf)
        Check(PclFolders.ReadIniValue(Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect") = basePath, "PCL repeated INI key uses last value")
        MustThrow(Sub() SafeFiles.UnderRoot(basePath, "../escape.jar"), "reject path traversal")
        MustThrow(Sub() SafeFiles.UnderRoot(basePath, "C:\escape.jar"), "reject absolute metadata paths")
        MustThrow(Sub() SafeFiles.ValidateInstanceName("CON"), "reject Windows reserved instance name")
        MustThrow(Sub() SafeFiles.ValidateInstanceName("foo."), "reject ambiguous trailing dot")
    End Sub

    Private Sub MetadataTests()
        Check(MinecraftInstaller.RulesAllow(Nothing), "library without rules allowed")
        Check(Not MinecraftInstaller.RulesAllow(JArray.Parse("[{'action':'allow','os':{'name':'linux'}}]")), "exclude Linux libraries")
        Check(Not MinecraftInstaller.RulesAllow(JArray.Parse("[{'action':'allow'},{'action':'disallow','os':{'name':'windows'}}]")), "ordered disallow overrides allow")
        Check(MinecraftInstaller.MavenPath("org.example:artifact:1.2:natives-windows") = "org/example/artifact/1.2/artifact-1.2-natives-windows.jar", "Maven classifier layout")
        Dim meta = JObject.Parse("{'downloads':{'client':{'url':'https://example.org/client','size':4,'sha1':'abc'}},'libraries':[{'name':'a:b:1','downloads':{'artifact':{'path':'a/b/1/b-1.jar','url':'https://example.org/lib','size':4}},'natives':{'windows':'natives-windows'},'downloads2':{}}]}")
        Dim plan = MinecraftInstaller.BuildBasePlan(meta, "测试实例")
        Check(plan.Any(Function(f) f.RelativePath = "versions/测试实例/测试实例.jar"), "custom instance JAR path")
        Check(plan.Any(Function(f) f.RelativePath = "libraries/a/b/1/b-1-natives-windows.jar"), "legacy natives fallback")
        Dim index As New JObject From {{"virtual", True}, {"objects", New JObject From {{"sounds/test.ogg", New JObject From {{"hash", New String("a"c, 40)}, {"size", 2}}}}}}
        Dim assets = MinecraftInstaller.BuildAssetsPlan(index)
        Check(assets.Count = 2 AndAlso assets.Any(Function(f) f.RelativePath = "assets/virtual/legacy/sounds/test.ogg"), "legacy mapping plus reusable object store")
        Dim sources As New SourcePolicy With {.Preference = SourcePreference.MirrorFirst}
        Check(sources.Candidates("https://resources.download.minecraft.net/aa/file", "asset").First() = "https://bmclapi2.bangbang93.com/assets/aa/file", "PCL assets mirror mapping")
    End Sub

    Private Async Function TransferTestsAsync() As Task
        Using server As New TestServer()
            Dim bytes(10 * 1024 * 1024 - 1) As Byte
            Dim random As New Random(123)
            random.NextBytes(bytes)
            server.Files("/large") = bytes
            server.SlowFirstRange = True
            Dim policy As New SourcePolicy With {.AllowLoopback = True, .Preference = SourcePreference.OfficialFirst}
            Dim item As New DownloadFile With {.RelativePath = "libraries/test.bin", .Url = server.Url & "/large", .Size = bytes.Length, .Sha1 = Hash(bytes)}
            Dim root = Path.Combine(TestRoot, "transfer")
            Using engine As New DownloadEngine(policy, Nothing, {}, 8)
                Await engine.DownloadAsync(root, {item}, CancellationToken.None)
                Check(SafeFiles.Matches(Path.Combine(root, item.RelativePath), item) AndAlso server.RangeRequests >= 2, "PCL adaptive 60/40 splitting accelerates a slow first range and verifies hash")
            End Using
            server.SlowFirstRange = False
            Dim reuseRoot = Path.Combine(TestRoot, "reuse")
            Using engine As New DownloadEngine(policy, Nothing, {root})
                Await engine.DownloadAsync(reuseRoot, {item}, CancellationToken.None)
                Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0, "cross-folder reuse needs zero network")
            End Using
            Dim oldVersion = Path.Combine(TestRoot, "old-game", "versions", "custom-name")
            Directory.CreateDirectory(oldVersion)
            File.WriteAllBytes(Path.Combine(oldVersion, "custom-name.jar"), bytes)
            Using engine As New DownloadEngine(policy, Nothing, {Path.Combine(TestRoot, "old-game")})
                Await engine.DownloadAsync(Path.Combine(TestRoot, "client-reuse"), {New DownloadFile With {.RelativePath = "1.21.1.jar", .Url = item.Url, .Size = item.Size, .Sha1 = item.Sha1}}, CancellationToken.None)
                Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0, "PCL-style client JAR reuse across differently named instances")
            End Using
            server.IgnoreRanges = True
            Using engine As New DownloadEngine(policy, Nothing, {})
                Dim fallbackRoot = Path.Combine(TestRoot, "fallback")
                Await engine.DownloadAsync(fallbackRoot, {item}, CancellationToken.None)
                Check(SafeFiles.Matches(Path.Combine(fallbackRoot, item.RelativePath), item), "unsupported Range falls back to full response")
            End Using
            server.IgnoreRanges = False
            server.Slow = True
            server.SlowFirstRange = True
            Dim cancelRoot = Path.Combine(TestRoot, "cancel")
            Dim canceled = False
            Using cancel As New CancellationTokenSource(350), engine As New DownloadEngine(policy, Nothing, {})
                Try
                    Await engine.DownloadAsync(cancelRoot, {item}, cancel.Token)
                Catch ex As Exception
                    canceled = True
                End Try
            End Using
            Check(canceled AndAlso Not File.Exists(Path.Combine(cancelRoot, item.RelativePath)), "cancel leaves no final partial file")
            Dim journal = Directory.EnumerateFiles(cancelRoot, "segments.json", SearchOption.AllDirectories).Single()
            Check(DirectCast(JObject.Parse(File.ReadAllText(journal))("parts"), JArray).Count >= 2, "dynamic split interval journal survives cancellation")
            server.Slow = False
            server.SlowFirstRange = False
            Using engine As New DownloadEngine(policy, Nothing, {})
                Await engine.DownloadAsync(cancelRoot, {item}, CancellationToken.None)
                Check(engine.NetworkBytes < bytes.Length, "resume reuses verified segment lengths then checks final hash")
            End Using
            server.Corrupt = True
            Dim corruptRoot = Path.Combine(TestRoot, "corrupt")
            Dim rejected = False
            Using engine As New DownloadEngine(policy, Nothing, {})
                Try
                    Await engine.DownloadAsync(corruptRoot, {item}, CancellationToken.None)
                Catch ex As Exception
                    rejected = True
                End Try
            End Using
            Check(rejected AndAlso Not File.Exists(Path.Combine(corruptRoot, item.RelativePath)), "bad hash never reaches final target")
        End Using
    End Function

    Private Async Function MetadataRaceTestsAsync() As Task
        Using slow As New TestServer(), fast As New TestServer()
            Dim json = Encoding.UTF8.GetBytes("{""ok"":true}")
            slow.Files("/meta") = json
            fast.Files("/meta") = json
            slow.HeaderDelayMs = 2500
            Dim policy As New SourcePolicy With {.AllowLoopback = True}
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Dim result = Await policy.RaceJsonAsync({slow.Url & "/meta", fast.Url & "/meta"}, CancellationToken.None, Hash(json), 150)
            Check(CBool(result("ok")) AndAlso clock.ElapsedMilliseconds < 2000, "delayed metadata fallback wins without waiting for slow primary")
            clock.Restart()
            Await policy.RaceJsonAsync({fast.Url & "/missing", fast.Url & "/meta"}, CancellationToken.None, Hash(json), 5000)
            Check(clock.ElapsedMilliseconds < 2000, "failed primary immediately starts delayed fallback")
            Dim badHash = False
            Try
                Await policy.RaceJsonAsync({fast.Url & "/meta"}, CancellationToken.None, New String("0"c, 40), 0)
            Catch ex As AggregateException
                badHash = True
            End Try
            Check(badHash, "metadata race never accepts unverified result")
            Dim canceled = False
            Using cancel As New CancellationTokenSource(100)
                Try
                    Await policy.RaceJsonAsync({slow.Url & "/meta"}, cancel.Token, Hash(json), 0)
                Catch ex As OperationCanceledException
                    canceled = True
                End Try
            End Using
            Check(canceled, "metadata cancellation joins losing requests")
        End Using
    End Function

    Private Async Function SlowSourceTestsAsync() As Task
        Using slow As New TestServer(), fast As New TestServer()
            Dim bytes(1024 * 1024 - 1) As Byte
            Dim random As New Random(45)
            random.NextBytes(bytes)
            slow.Files("/content") = bytes
            fast.Files("/content") = bytes
            slow.Slow = True
            Dim policy As New FixtureSources With {.AllowLoopback = True, .Urls = New List(Of String) From {slow.Url & "/content", fast.Url & "/content"}}
            Using engine As New DownloadEngine(policy, Nothing, {})
                engine.SlowSourceGrace = TimeSpan.FromMilliseconds(50)
                engine.MinimumUsefulSpeed = 100L * 1024 * 1024
                Dim item As New DownloadFile With {.RelativePath = "test/content.bin", .Url = slow.Url & "/content", .Size = bytes.Length, .Sha1 = Hash(bytes)}
                Dim root = Path.Combine(TestRoot, "slow-fallback")
                Await engine.DownloadAsync(root, {item}, CancellationToken.None)
                Check(SafeFiles.Matches(Path.Combine(root, item.RelativePath), item), "slow content source switches to fallback and verifies final file")
            End Using
        End Using
    End Function
    Private Class FixtureSources
        Inherits SourcePolicy
        Public Property Urls As List(Of String)
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return Urls
        End Function
    End Class

    Private Async Function InstallTestsAsync() As Task
        Using server As New TestServer()
            Dim client = Encoding.UTF8.GetBytes("fixture minecraft jar")
            Dim library = Encoding.UTF8.GetBytes("fixture support library")
            Dim index = Encoding.UTF8.GetBytes("{""objects"":{}}")
            server.Files("/client") = client
            server.Files("/library") = library
            server.Files("/index") = index
            Dim metadata As New JObject From {{"id", "fixture-1"}, {"type", "release"}, {"mainClass", "net.minecraft.client.main.Main"},
                {"downloads", New JObject From {{"client", New JObject From {{"url", server.Url & "/client"}, {"size", client.Length}, {"sha1", Hash(client)}}}}},
                {"assetIndex", New JObject From {{"id", "fixture"}, {"url", server.Url & "/index"}, {"size", index.Length}, {"sha1", Hash(index)}}},
                {"libraries", New JArray(New JObject From {{"name", "example:library:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", "example/library/1/library-1.jar"}, {"url", server.Url & "/library"}, {"size", library.Length}, {"sha1", Hash(library)}}}}}})}}
            server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            Dim entry As New VersionEntry With {.Id = "fixture-1", .Url = server.Url & "/version", .Sha1 = Hash(server.Files("/version"))}
            Dim root = Path.Combine(TestRoot, "install 中文", ".minecraft")
            Dim installer As New MinecraftInstaller(New SourcePolicy With {.AllowLoopback = True})
            Dim result = Await installer.InstallAsync(entry, root, "我的原版", {}, Nothing, CancellationToken.None)
            Dim jsonPath = Path.Combine(result.InstancePath, "我的原版.json")
            Dim written = JObject.Parse(File.ReadAllText(jsonPath))
            Check(CStr(written("id")) = "我的原版" AndAlso CStr(written("clientVersion")) = "fixture-1", "install commits PCL custom instance metadata")
            Check(File.Exists(Path.Combine(result.InstancePath, "我的原版.jar")), "client JAR committed beside matching JSON")
            Dim second = Await installer.InstallAsync(entry, root, "同目录另一实例", {}, Nothing, CancellationToken.None)
            Check(second.NetworkBytes = 0 AndAlso File.Exists(Path.Combine(second.InstancePath, "同目录另一实例.jar")), "same game root reuses differently named client JAR without external reuse roots")
            Dim before = File.ReadAllBytes(jsonPath)
            Dim refused = False
            Try
                Await installer.InstallAsync(entry, root, "我的原版", {}, Nothing, CancellationToken.None)
            Catch ex As IOException
                refused = True
            End Try
            Check(refused AndAlso before.SequenceEqual(File.ReadAllBytes(jsonPath)), "same-name install refuses overwrite")
            File.Delete(Path.Combine(root, "libraries", "example", "library", "1", "library-1.jar"))
            Await installer.InstallAsync(entry, root, "我的原版", {}, Nothing, CancellationToken.None, True)
            Check(before.SequenceEqual(File.ReadAllBytes(jsonPath)) AndAlso File.Exists(Path.Combine(root, "libraries", "example", "library", "1", "library-1.jar")), "repair restores library and preserves metadata bytes")
            written("inheritsFrom") = "forge"
            File.WriteAllText(jsonPath, written.ToString())
            Dim rejected = False
            Try
                Await installer.InstallAsync(entry, root, "我的原版", {}, Nothing, CancellationToken.None, True)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected, "repair refuses modded/inherited instances")
            metadata("downloads")("client")("sha1") = New String("0"c, 40)
            server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            entry.Sha1 = Hash(server.Files("/version"))
            Dim failed = False
            Try
                Await installer.InstallAsync(entry, root, "failed-install", {}, Nothing, CancellationToken.None)
            Catch ex As Exception
                failed = True
            End Try
            Check(failed AndAlso Not Directory.Exists(Path.Combine(root, "versions", "failed-install")), "failed client never publishes half-installed instance")
        End Using
    End Function

    Private Async Function LiveAsync(args As String()) As Task
        If args.Length < 3 Then Throw New ArgumentException("--live version absolute-output-root [--repair] [--supplemental]")
        Dim policy As New SourcePolicy With {.Preference = If(args.Contains("--official"), SourcePreference.OfficialFirst, SourcePreference.Automatic)}
        Dim installer As New MinecraftInstaller(policy)
        Using timeout As New CancellationTokenSource(TimeSpan.FromMinutes(15))
            Dim versions = Await installer.VersionsAsync(timeout.Token)
            If args.Contains("--supplemental") Then versions = SupplementalVersions.Merge(versions, Await SupplementalVersions.LoadAsync(policy, timeout.Token))
            Dim entry = versions.Single(Function(v) v.Id = args(1))
            Dim components As New ComponentSelection
            Dim catalog As New ComponentCatalog(policy)
            Dim options = args.Where(Function(a) a.StartsWith("--with=", StringComparison.Ordinal)).ToList()
            For Each value In options
                Dim parts = value.Substring(7).Split("="c)
                Dim kind = CType([Enum].Parse(GetType(ComponentKind), parts(0), True), ComponentKind)
                Dim choices = Await catalog.LoadAsync(kind, entry.Id, timeout.Token)
                Console.WriteLine("CATALOG " & kind.ToString() & " count=" & choices.Count)
                components.Entries(kind) = If(parts.Length > 1, choices.Single(Function(c) c.Version = parts(1)), If(choices.FirstOrDefault(Function(c) c.Stable), choices.First()))
            Next
            Dim instance = entry.Id & If(components.Entries.Count = 0, "", "-" & String.Join("-", components.Entries.Select(Function(v) v.Key.ToString() & "-" & v.Value.Version.Replace(" ", "_"))))
            Dim reuse = args.Where(Function(a) a.StartsWith("--reuse=", StringComparison.Ordinal)).Select(Function(a) a.Substring(8)).ToList()
            Dim last As DateTime = DateTime.MinValue
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Dim progress As New InlineProgress(Sub(p)
                                                   SyncLock versions
                                                       If (DateTime.UtcNow - last).TotalSeconds < 5 Then Return
                                                       last = DateTime.UtcNow
                                                       Console.WriteLine(p.Stage & " " & p.Completed & "/" & p.Total & " bytes=" & p.NetworkBytes & " reuse=" & p.Reused & " speed=" & CLng(p.BytesPerSecond) & " connections=" & p.ActiveConnections & " splits=" & p.DynamicSplits)
                                                   End SyncLock
                                               End Sub)
            Dim result = Await installer.InstallAsync(entry, Path.GetFullPath(args(2)), instance, reuse, progress, timeout.Token, args.Contains("--repair"), 64, components:=components)
            Console.WriteLine("LIVE_OK elapsedMs=" & clock.ElapsedMilliseconds & " " & Newtonsoft.Json.JsonConvert.SerializeObject(result))
        End Using
    End Function
    Private Class InlineProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Public Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
End Module
