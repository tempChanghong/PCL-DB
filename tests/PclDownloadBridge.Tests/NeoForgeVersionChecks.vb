Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module NeoForgeVersionChecks
    Friend Async Function RunAsync() As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim versions = New JArray("21.0.1-beta", "21.0.2", "21.1.77", "21.1.78-alpha.1", "26.1.0.0-alpha.1+snapshot-1", "26.1.0.4-alpha.1", "26.1.0.4-beta.2", "26.1.0.4", "26.1.2.5", "0.25w14craftmine.3-beta", "26.1.0.6+snapshot-2")
            server.Files("/api/maven/versions/releases/net/neoforged/neoforge") = Encoding.UTF8.GetBytes(New JObject From {{"versions", versions}}.ToString())
            server.Files("/api/maven/versions/releases/net/neoforged/forge") = Encoding.UTF8.GetBytes(New JObject From {{"versions", New JArray("1.20.1-47.1.99", "47.1.100", "47.1.82", "1.20.1-47.1.82")}}.ToString())
            Dim catalog As New ComponentCatalog(New FixturePolicy(server.Url, SourcePreference.OfficialFirst))
            Dim original = Await catalog.LoadAsync(ComponentKind.NeoForge, "1.21", timeout.Token)
            Check(original.Select(Function(v) v.Version).SequenceEqual({"21.0.2", "21.0.1-beta"}) AndAlso original.All(Function(v) v.Minecraft = "1.21"), "NeoForge 21.0 releases map to Minecraft 1.21 without a fabricated trailing zero", passed)
            Dim patch = Await catalog.LoadAsync(ComponentKind.NeoForge, "1.21.1", timeout.Token)
            Check(patch.Select(Function(v) v.Version).SequenceEqual({"21.1.78-alpha.1", "21.1.77"}) AndAlso Not patch(0).Stable AndAlso patch(1).Stable, "existing NeoForge patch mappings remain separate and correctly classify alpha releases", passed)
            Dim modern = Await catalog.LoadAsync(ComponentKind.NeoForge, "26.1", timeout.Token)
            Check(modern.Count = 3 AndAlso modern.All(Function(v) v.Minecraft = "26.1"), "NeoForge four-part versions map to the new Minecraft numbering", passed)
            Check(modern.Select(Function(v) v.Version).SequenceEqual({"26.1.0.4-beta.2", "26.1.0.4-alpha.1", "26.1.0.4"}), "equal NeoForge numeric versions retain PCL's version-name ordering", passed)
            Check(modern.Single(Function(v) v.Version = "26.1.0.4").Stable AndAlso modern.Where(Function(v) v.Version <> "26.1.0.4").All(Function(v) Not v.Stable), "NeoForge alpha and beta releases are both marked experimental", passed)
            Check(modern.Single(Function(v) v.Stable).Url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/26.1.0.4/neoforge-26.1.0.4-installer.jar", "modern NeoForge installers retain the original Maven version coordinate", passed)
            Dim modernPatch = Await catalog.LoadAsync(ComponentKind.NeoForge, "26.1.2", timeout.Token)
            Check(modernPatch.Count = 1 AndAlso modernPatch(0).Version = "26.1.2.5", "the third new-format Minecraft segment is retained instead of merging different patch releases", passed)
            Dim snapshot = Await catalog.LoadAsync(ComponentKind.NeoForge, "26.1-snapshot-1", timeout.Token)
            Check(snapshot.Count = 1 AndAlso snapshot(0).Version = "26.1.0.0-alpha.1+snapshot-1" AndAlso Not snapshot(0).Stable, "NeoForge snapshot suffixes select the exact Minecraft snapshot", passed)
            Check(snapshot(0).Url.EndsWith("/26.1.0.0-alpha.1+snapshot-1/neoforge-26.1.0.0-alpha.1+snapshot-1-installer.jar", StringComparison.Ordinal), "snapshot installer URLs retain alpha and build metadata", passed)
            Dim nextSnapshot = Await catalog.LoadAsync(ComponentKind.NeoForge, "26.1-snapshot-2", timeout.Token)
            Check(nextSnapshot.Count = 1 AndAlso nextSnapshot(0).Version = "26.1.0.6+snapshot-2" AndAlso Not nextSnapshot(0).Stable, "snapshot build metadata cannot leak into another snapshot or appear stable", passed)
            Dim april = Await catalog.LoadAsync(ComponentKind.NeoForge, "25w14craftmine", timeout.Token)
            Check(april.Count = 1 AndAlso april(0).Version = "0.25w14craftmine.3-beta" AndAlso Not april(0).Stable, "NeoForge April Fools coordinates use the embedded Minecraft version name", passed)
            Dim legacy = Await catalog.LoadAsync(ComponentKind.NeoForge, "1.20.1", timeout.Token)
            Check(legacy.Select(Function(v) v.Version).SequenceEqual({"47.1.100", "47.1.99", "47.1.82"}) AndAlso legacy.Last().Url.Contains("/1.20.1-47.1.82/"), "legacy NeoForge versions retain numeric ordering and apply PCL's exact API-name exclusion", passed)
            Check(legacy(1).Url.EndsWith("/forge/1.20.1-47.1.99/forge-1.20.1-47.1.99-installer.jar", StringComparison.Ordinal), "legacy display names omit the game prefix while installer coordinates retain it", passed)
            server.Files("/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge") = Encoding.UTF8.GetBytes(New JObject From {{"files", New JArray(New JObject From {{"name", "26.1.0.4"}}, New JObject From {{"name", "26.1.0.4"}}, New JObject From {{"name", "26.1.0.4-alpha.1"}})}}.ToString())
            Dim mirror = Await New ComponentCatalog(New FixturePolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.NeoForge, "26.1", timeout.Token)
            Check(mirror.Select(Function(v) v.Version).SequenceEqual({"26.1.0.4-alpha.1", "26.1.0.4"}), "BMCLAPI Maven-detail wrappers use the same mapping and deduplicate version names", passed)
        End Using
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _root As String
        Public Sub New(root As String, preference As SourcePreference)
            _root = root
            Me.Preference = preference : Me.VersionPreference = preference : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _root & New Uri(source).PathAndQuery
        End Function
    End Class
End Module
