Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module ComponentCacheChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-reuse" Then Await ReuseAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-md5" Then Await Md5Async(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-unhashed" Then Await UnhashedAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-damaged" Then Await DamagedAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-cancel" Then Await CancelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-failure" Then Await FailureAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-cache-unavailable" Then Await UnavailableAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown component cache case: " & onlyCase)
        Return passed
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(bytes As Byte(), Optional md5 As Boolean = False) As String
        Using digest = If(md5, DirectCast(Security.Cryptography.MD5.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.SHA1.Create())
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function CacheFiles(root As String) As List(Of String)
        Dim cache = Path.Combine(root, ".vanilla-downloader", "component-cache")
        Return If(Directory.Exists(cache), Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).ToList(), New List(Of String))
    End Function

    Private Function CreateSelection(fixture As InstallationChecks.InstallFixture) As ComponentSelection
        Dim processor = ComponentChecks.ProcessorJar()
        fixture.Server.Files("/cached-processor") = processor
        Dim output = Encoding.UTF8.GetBytes("patched")
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id},
            {"libraries", New JArray(New JObject From {{"name", "fixture:processor:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", "fixture/processor/1/processor-1.jar"}, {"url", fixture.Server.Url & "/cached-processor"}, {"sha1", Hash(processor)}, {"size", processor.Length}}}}}})},
            {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:cached-runtime:1]"}}}}},
            {"processors", New JArray(New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()}, {"args", New JArray("{PATCHED}", "patched")}, {"outputs", New JObject From {{"{PATCHED}", Hash(output)}}}})}}
        Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"inheritsFrom", fixture.Entry.Id},
            {"libraries", New JArray(New JObject From {{"name", "fixture:cached-runtime:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", "fixture/cached-runtime/1/cached-runtime-1.jar"}, {"url", ""}, {"sha1", Hash(output)}, {"size", output.Length}}}}}})}}
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                WriteEntry(zip, "install_profile.json", Encoding.UTF8.GetBytes(profile.ToString()))
                WriteEntry(zip, "version.json", Encoding.UTF8.GetBytes(version.ToString()))
            End Using
            fixture.Server.Files("/cached-installer") = buffer.ToArray()
        End Using
        Dim selection As New ComponentSelection()
        selection.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = fixture.Entry.Id, .Version = "fixture", .Url = fixture.Server.Url & "/cached-installer", .Sha1 = Hash(fixture.Server.Files("/cached-installer"))}
        Return selection
    End Function

    Private Async Function ReuseAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-reuse")
            Dim selected = CreateSelection(fixture)
            Await fixture.InstallAsync("first", selected)
            fixture.Server.Files("/renamed-installer") = fixture.Server.Files("/cached-installer")
            selected.Item(ComponentKind.NeoForge).Url = fixture.Server.Url & "/renamed-installer"
            Dim second = Await fixture.InstallAsync("second", selected)
            Console.WriteLine("COMPONENT_CACHE_REPEAT bytes=" & second.NetworkBytes & " reuse=" & second.Reused)
            Assert(second.NetworkBytes = 0 AndAlso fixture.Server.Requests("/cached-processor") = 1 AndAlso Not fixture.Server.Requests.ContainsKey("/renamed-installer"), "new instance reuses hashed installers and private processor dependencies across URL aliases", passed)
            Assert(Not File.Exists(SafeFiles.UnderRoot(fixture.Root, "libraries/fixture/processor/1/processor-1.jar")), "persistent processor reuse does not publish install-only dependencies into shared libraries", passed)
            Dim otherRoot = Path.Combine(testRoot, "other-root-from-component-cache", ".minecraft")
            Dim installer As New MinecraftInstaller(New SourcePolicy With {.AllowLoopback = True})
            Dim other = Await installer.InstallAsync(fixture.Entry, otherRoot, "from-other-root", {fixture.Root}, Nothing, CancellationToken.None, components:=selected)
            Assert(other.NetworkBytes = 0 AndAlso CacheFiles(otherRoot).Count = 2, "selected reuse roots seed private dependencies; published clients keep no extra cache copy", passed)
        End Using
    End Function

    Private Async Function Md5Async(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-md5")
            Dim selected = CreateSelection(fixture)
            Dim entry = selected.Item(ComponentKind.NeoForge)
            entry.Sha1 = Nothing : entry.Md5 = Hash(fixture.Server.Files("/cached-installer"), True)
            Await fixture.InstallAsync("md5-first", selected)
            Dim second = Await fixture.InstallAsync("md5-second", selected)
            Assert(second.NetworkBytes = 0 AndAlso fixture.Server.Requests("/cached-installer") = 1, "upstream MD5-only installer metadata supports checked private reuse", passed)
        End Using
    End Function

    Private Async Function UnhashedAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-unhashed")
            Dim selected = CreateSelection(fixture)
            selected.Item(ComponentKind.NeoForge).Sha1 = Nothing
            Await fixture.InstallAsync("unhashed-first", selected)
            Dim second = Await fixture.InstallAsync("unhashed-second", selected)
            Assert(fixture.Server.Requests("/cached-installer") = 2 AndAlso second.NetworkBytes = fixture.Server.Files("/cached-installer").Length, "unhashed installers are fetched again while their hashed dependencies remain reusable", passed)
            Assert(CacheFiles(fixture.Root).Count = 1 AndAlso Not CacheFiles(fixture.Root).Any(Function(path) File.ReadAllBytes(path).SequenceEqual(fixture.Server.Files("/cached-installer"))), "an unverified URL or filename alone cannot create a persistent content entry", passed)
        End Using
    End Function

    Private Async Function DamagedAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-damaged")
            Dim selected = CreateSelection(fixture)
            Await fixture.InstallAsync("before-cache-damage", selected)
            Dim processor = fixture.Server.Files("/cached-processor")
            Dim cachedProcessor = CacheFiles(fixture.Root).SingleOrDefault(Function(path) File.ReadAllBytes(path).SequenceEqual(processor))
            Assert(cachedProcessor IsNot Nothing, "verified private processor content persists after the temporary work directory is removed", passed)
            Dim damaged = DirectCast(processor.Clone(), Byte())
            damaged(0) = damaged(0) Xor CByte(1)
            File.WriteAllBytes(cachedProcessor, damaged)
            Dim second = Await fixture.InstallAsync("after-cache-damage", selected)
            Assert(second.NetworkBytes = processor.Length AndAlso fixture.Server.Requests("/cached-processor") = 2 AndAlso File.ReadAllBytes(cachedProcessor).SequenceEqual(processor), "same-size corrupt cache entry is rejected, fetched again, and replaced with checked content", passed)
            Assert(File.ReadAllText(SafeFiles.UnderRoot(fixture.Root, "libraries/fixture/cached-runtime/1/cached-runtime-1.jar")) = "patched", "processor executes successfully only after the damaged cached JAR is repaired", passed)
        End Using
    End Function

    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-cancel"), cancel As New CancellationTokenSource()
            Dim selected = CreateSelection(fixture)
            Dim progress As New CallbackProgress(Sub(value)
                                                     If value.Phase = "processors" AndAlso value.Stage.StartsWith("安装 NeoForge", StringComparison.Ordinal) Then cancel.Cancel()
                                                 End Sub)
            Dim cancelled = False
            Try
                Await fixture.InstallAsync("cancel-and-retry", selected, progress:=progress, token:=cancel.Token)
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Assert(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "cancel-and-retry")), "cancelling before processor execution publishes no partial component instance", passed)
            Assert(CacheFiles(fixture.Root).Count = 3, "cancellation keeps the already checked staged client, installer and dependency entries", passed)
            Dim retry = Await fixture.InstallAsync("cancel-and-retry", selected)
            Assert(retry.NetworkBytes = 0 AndAlso File.Exists(Path.Combine(retry.InstancePath, "cancel-and-retry.json")) AndAlso CacheFiles(fixture.Root).Count = 2, "retry after cancellation reuses checked downloads and releases the client cache after publication", passed)
        End Using
    End Function

    Private Async Function FailureAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-failure")
            Dim selected = CreateSelection(fixture)
            Dim runtime = SafeFiles.UnderRoot(fixture.Root, "libraries/fixture/cached-runtime/1/cached-runtime-1.jar")
            Directory.CreateDirectory(Path.GetDirectoryName(runtime))
            File.WriteAllText(runtime, "keep locked file")
            Dim failed = False
            Using held As New FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read)
                Try
                    Await fixture.InstallAsync("retry-after-failure", selected)
                Catch ex As IOException
                    failed = True
                End Try
                Assert(failed AndAlso File.ReadAllText(runtime) = "keep locked file", "component publication failure preserves the existing locked runtime", passed)
            End Using
            Dim retry = Await fixture.InstallAsync("retry-after-failure", selected)
            Assert(retry.NetworkBytes = 0 AndAlso File.ReadAllText(runtime) = "patched", "retry after publication failure does not fetch the installer or processor dependencies again", passed)
        End Using
    End Function

    Private Async Function UnavailableAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "component-cache-unavailable")
            Dim selected = CreateSelection(fixture)
            Dim blocker = Path.Combine(fixture.Root, ".vanilla-downloader", "component-cache")
            Directory.CreateDirectory(Path.GetDirectoryName(blocker))
            File.WriteAllText(blocker, "preserve cache blocker")
            Dim result = Await fixture.InstallAsync("without-cache", selected)
            Assert(File.Exists(Path.Combine(result.InstancePath, "without-cache.json")) AndAlso File.ReadAllText(blocker) = "preserve cache blocker", "an unavailable optional cache cannot break a valid installation or remove the blocking file", passed)
            Assert(Not Directory.EnumerateFiles(fixture.Root, "*.cache-*", SearchOption.AllDirectories).Any(), "optional cache failures leave no partial cache copies in the game directory", passed)
        End Using
    End Function

    Private Sub WriteEntry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub

    Private Class CallbackProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _callback As Action(Of TransferProgress)
        Friend Sub New(callback As Action(Of TransferProgress))
            _callback = callback
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _callback(value)
        End Sub
    End Class
End Module
