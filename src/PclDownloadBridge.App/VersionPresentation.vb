Imports PclDownloadBridge.Core
Imports System.Globalization
Imports System.Text.RegularExpressions

' Adapted from fixed PCL ShowMinecraftVersionList, McDownloadListItem and GetMcFoolName.
' Keep display corrections separate from the manifest identity used for metadata verification.
Friend NotInheritable Class VersionPresentation
    Public ReadOnly Property Entry As VersionEntry
    Public ReadOnly Property Kind As String
    Public ReadOnly Property DisplayId As String
    Public ReadOnly Property Lore As String

    ' PCL McVersion.IsStandardFormat, including its prefix-based pre-release handling.
    Friend Shared Function IsStandardFormat(value As String) As Boolean
        If value Is Nothing Then Return False
        If Regex.IsMatch(value, "^1\.\d") Then Return True
        Dim number As Double
        Return Double.TryParse(Regex.Match(value, "^[2-9]\d\.\d+").Value, NumberStyles.Float, CultureInfo.InvariantCulture, number) AndAlso number >= 26
    End Function

    Public Sub New(value As VersionEntry)
        Entry = value
        Kind = value.Kind
        DisplayId = value.Id
        If value.IsSupplemental Then
            If SupplementalVersions.Label(value) = "愚人节版" Then Kind = "special"
            DisplayId = SupplementalVersions.DisplayName(value)
            Dim detail = SupplementalVersions.VariantDescription(value)
            Lore = "[PCL-DB特供] " & If(detail IsNot Nothing, "2013 | " & detail, If(Kind = "special", AprilFoolsLore(value.Id), value.Released.ToString("yyyy/MM/dd HH:mm")))
            Return
        End If
        If Kind <> "snapshot" Then Return
        Dim id = value.Id.ToLowerInvariant()
        If id.StartsWith("1.", StringComparison.Ordinal) AndAlso Not {"combat", "rc", "experimental", "pre"}.Any(Function(marker) id.Contains(marker)) Then Kind = "release"
        If id = "20w14infinite" OrElse id = "20w14∞" Then DisplayId = "20w14∞"
        Lore = AprilFoolsLore(id)
        Dim releaseDate = value.Released.ToOffset(TimeSpan.FromHours(2))
        If Lore IsNot Nothing OrElse (releaseDate.Month = 4 AndAlso releaseDate.Day = 1) Then Kind = "special"
    End Sub

    Private Shared Function AprilFoolsLore(id As String) As String
        Dim Lore As String = Nothing
        If id.StartsWith("2.0", StringComparison.Ordinal) Then id = "2.0"
        Select Case id
            Case "20w14infinite", "20w14∞"
                Lore = "2020 | 我们加入了 20 亿个新的维度，让无限的想象变成了现实！"
            Case "3d shareware v1.34"
                Lore = "2019 | 我们从地下室的废墟里找到了这个开发于 1994 年的杰作！"
            Case "1.rv-pre1"
                Lore = "2016 | 是时候将现代科技带入 Minecraft 了！"
            Case "15w14a"
                Lore = "2015 | 作为一款全年龄向的游戏，我们需要和平，需要爱与拥抱。"
            Case "2.0"
                Lore = "2013 | 这个秘密计划了两年的更新将游戏推向了一个新高度！"
            Case "22w13oneblockatatime"
                Lore = "2022 | 一次一个方块更新！迎接全新的挖掘、合成与骑乘玩法吧！"
            Case "23w13a_or_b"
                Lore = "2023 | 研究表明：玩家喜欢作出选择——越多越好！"
            Case "24w14potato"
                Lore = "2024 | 毒马铃薯一直都被大家忽视和低估，于是我们超级加强了它！"
            Case "25w14craftmine"
                Lore = "2025 | 你可以合成任何东西——包括合成你的世界！"
            Case "26w14a"
                Lore = "2026 | 为什么需要物品栏？让方块们跟着你走吧！"
        End Select
        Return Lore
    End Function

    Public ReadOnly Property Category As String
        Get
            Select Case Kind
                Case "release" : Return "正式版"
                Case "snapshot" : Return "预览版"
                Case "special" : Return "愚人节版"
                Case Else : Return "远古版"
            End Select
        End Get
    End Property

    Public ReadOnly Property Icon As String
        Get
            Dim block = If(Kind = "special", "GoldBlock", If(Kind = "release", "Grass", If(Kind = "snapshot", "CommandBlock", "CobbleStone")))
            Return "pack://application:,,,/PclDownloadBridge;component/Images/Blocks/" & block & ".png"
        End Get
    End Property

    Public Function Description(latest As Boolean) As String
        If latest Then Return If(Kind = "release", "最新正式版，发布于 ", "最新预览版，发布于 ") & Entry.Released.ToString("yyyy/MM/dd HH:mm")
        Return If(Lore, Entry.Released.ToString("yyyy/MM/dd HH:mm"))
    End Function
End Class
