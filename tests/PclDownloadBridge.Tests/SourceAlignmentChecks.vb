Imports System.IO
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module SourceAlignmentChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "source-routing" Then Routing(passed)
        If onlyCase Is Nothing OrElse onlyCase = "source-download" Then Await DownloadAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown source alignment case: " & onlyCase)
        Return passed
    End Function
    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Sub Routing(passed As List(Of String))
        For Each host In {"piston-data.mojang.com", "piston-meta.mojang.com"}
            Dim official = "https://" & host & "/objects/example/file?fixture=1%202"
            For Each category In {"asset", "library", "meta"}
                Dim mirror = "https://bmclapi2.bangbang93.com"
                Dim suffix = "/objects/example/file?fixture=1%202"
                Dim expected As New List(Of String)
                If category = "library" Then
                    expected.Add(mirror & "/maven" & suffix)
                    expected.Add(mirror & "/libraries" & suffix)
                Else
                    expected.Add(mirror & If(category = "asset", "/assets", "") & suffix)
                End If
                expected.Add(official)
                For Each preference In {SourcePreference.Automatic, SourcePreference.MirrorFirst, SourcePreference.OfficialFirst}
                    Dim policy As New SourcePolicy With {.Preference = preference}
                    Dim ordered = If(preference = SourcePreference.OfficialFirst, {official}.Concat(expected.Take(expected.Count - 1)), expected)
                    Assert(policy.Candidates(official, category).SequenceEqual(ordered), host & " " & category & " " & preference.ToString() & " preserves PCL category paths, source order and escaped query", passed)
                Next
            Next
        Next
        Dim sources As New SourcePolicy()
        Assert(sources.Candidates("http://resources.download.minecraft.net/ab/hash", "asset")(0) = "https://bmclapi2.bangbang93.com/assets/ab/hash", "legacy asset HTTP upgrade keeps the existing asset mirror path", passed)
        Assert(sources.Candidates("https://libraries.minecraft.net/a/b.jar", "library").Take(2).SequenceEqual({"https://bmclapi2.bangbang93.com/maven/a/b.jar", "https://bmclapi2.bangbang93.com/libraries/a/b.jar"}), "existing library host retains both PCL mirror candidates", passed)
        Assert(sources.Candidates("https://example.org/file", "asset").SequenceEqual({"https://example.org/file"}), "unknown asset hosts do not acquire a fabricated Mojang mirror", passed)
    End Sub
    Private Async Function DownloadAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim bytes = Encoding.UTF8.GetBytes("category-specific checked download")
            Dim sha1 As String
            Using digest = Security.Cryptography.SHA1.Create()
                sha1 = BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
            End Using
            server.Files("/assets/objects/asset") = bytes
            server.Files("/libraries/objects/library") = bytes
            server.Files("/objects/meta") = bytes
            For Each category In {"asset", "library", "meta"}
                Dim root = Path.Combine(testRoot, "source-category-" & category)
                Dim item As New DownloadFile With {.RelativePath = category & ".bin", .Url = "https://piston-data.mojang.com/objects/" & category, .Category = category, .Size = bytes.Length, .Sha1 = sha1}
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)), engine As New DownloadEngine(New RoutedPolicy(server.Url), Nothing, {}, 1, root)
                    Await engine.DownloadAsync(root, {item}, timeout.Token)
                    Assert(SafeFiles.Matches(SafeFiles.UnderRoot(root, item.RelativePath), item), category & " downloads from the category-specific mirror and passes its declared hash", passed)
                    If category = "library" Then Assert(server.Requests("/maven/objects/library") = 2 AndAlso server.Requests("/libraries/objects/library") = 1, "library failure falls back from maven to libraries before official", passed)
                End Using
            Next
        End Using
    End Function
    Private Class RoutedPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return MyBase.Candidates(url, category).Select(Function(candidate) _server & New Uri(candidate).PathAndQuery).ToList()
        End Function
    End Class
End Module
