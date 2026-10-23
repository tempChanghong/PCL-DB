Imports System.IO
Imports System.Text
Imports Newtonsoft.Json.Linq

' Real Mojang wrapper; client/index bytes remain explicitly synthetic fixtures.
Friend NotInheritable Class SupplementalInstallFixture
    Friend Shared ReadOnly Ids As String() = {"2.0_blue", "2.0_red", "2.0_purple", "c0.0.12a_03", "a1.1.1", "b1.9-pre6", "13w12~", "1.0.0-rc1", "inf-20100630-1", "13w10b", "13w10a", "13w09c", "13w09b", "11w47a"}

    Friend Shared Function UsesAlpha(id As String) As Boolean
        Return id = "c0.0.12a_03" OrElse id = "1.0.0-rc1" OrElse id = "inf-20100630-1"
    End Function

    Friend Shared Function Kind(id As String) As String
        If id = "c0.0.12a_03" OrElse id = "a1.1.1" OrElse id = "inf-20100630-1" Then Return "old_alpha"
        Return If(id = "b1.9-pre6" OrElse id = "1.0.0-rc1", "old_beta", "snapshot")
    End Function

    Friend Shared Function Arguments(id As String) As String
        Return "${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}" &
            If(UsesAlpha(id), " --tweakClass net.minecraft.launchwrapper.AlphaVanillaTweaker", "")
    End Function

    Friend Shared Function Entry(id As String) As PclDownloadBridge.Core.VersionEntry
        Dim slug = id.Replace("2.0_", "2point0_")
        If id = "b1.9-pre6" Then slug = "b1_9-pre6"
        If id = "13w12~" Then slug = "13w12-"
        Return New PclDownloadBridge.Core.VersionEntry With {.Id = id, .SupplementalId = If(id.StartsWith("2.0_", StringComparison.Ordinal), slug, id), .Kind = Kind(id),
            .Url = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/" & slug & "/" & slug & ".json"}
    End Function

    Friend Shared Function Resource(name As String) As Byte()
        Using input = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name), output As New MemoryStream()
            input.CopyTo(output) : Return output.ToArray()
        End Using
    End Function

    Friend Shared Function Profile(fixture As InstallationChecks.InstallFixture, id As String, Optional source As JObject = Nothing) As JObject
        Dim metadata = If(source Is Nothing, JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version"))), DirectCast(source.DeepClone(), JObject))
        metadata("id") = id : metadata("type") = Kind(id)
        metadata("mainClass") = "net.minecraft.launchwrapper.Launch"
        metadata("minecraftArguments") = Arguments(id) : metadata.Remove("arguments")
        Dim version = If(UsesAlpha(id), "1.6", "1.5"), bytes = Resource("LegacyLaunch.launchwrapper-" & version & ".jar")
        Dim route = "/fixture-wrapper/" & version & ".jar", hash As String
        Using sha = Security.Cryptography.SHA1.Create()
            hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
        fixture.Server.Files(route) = bytes
        metadata("libraries") = New JArray(New JObject From {{"name", "net.minecraft:launchwrapper:" & version}, {"downloads", New JObject From {
            {"artifact", New JObject From {{"path", "net/minecraft/launchwrapper/" & version & "/launchwrapper-" & version & ".jar"}, {"url", fixture.Server.Url & route}, {"size", bytes.Length}, {"sha1", hash}}}}}})
        Return metadata
    End Function

    Friend Shared Function Apply(fixture As InstallationChecks.InstallFixture, id As String) As JObject
        Dim metadata = Profile(fixture, id)
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
        Return metadata
    End Function
End Class
