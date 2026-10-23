Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    ' PCL 5ef81e4 InitializeStackResourceVersions / ResourceVersion.ToListItem:
    ' use filenames for a card with duplicate display names; keep release IDs as identity.
    Private Shared Function PackReleaseTitles(releases As List(Of ModpackRelease)) As String()
        Dim titles = releases.Select(Function(release) PackReleaseFallbackTitle(release)).ToArray()
        If titles.Distinct(StringComparer.OrdinalIgnoreCase).Count() <> titles.Length Then
            titles = releases.Select(Function(release) If(PackReleaseTitle(release.FileName) <> "", PackReleaseTitle(release.FileName), PackReleaseFallbackTitle(release))).ToArray()
        End If
        ' Some projects reuse both the display name and the filename. Preserve the
        ' visible version number, then the platform ID, without changing the selection.
        For Each useId In {False, True}
            Dim duplicates = Enumerable.Range(0, titles.Length).GroupBy(Function(index) titles(index), StringComparer.OrdinalIgnoreCase).Where(Function(group) group.Count() > 1).Select(Function(group) group.ToArray()).ToArray()
            For Each group In duplicates
                For Each index In group
                    Dim suffix = If(useId, releases(index).Id, releases(index).Version)
                    If Not String.IsNullOrWhiteSpace(suffix) Then titles(index) &= " · " & suffix.Trim()
                Next
            Next
        Next
        Dim used As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For index = 0 To titles.Length - 1
            Dim original = titles(index), serial = 1
            While Not used.Add(titles(index))
                serial += 1 : titles(index) = original & " · " & serial.ToString(Globalization.CultureInfo.InvariantCulture)
            End While
        Next
        Return titles
    End Function

    Private Shared Function PackReleaseFallbackTitle(release As ModpackRelease) As String
        For Each value In {release.Name, release.FileName, release.Version}
            Dim title = PackReleaseTitle(value)
            If title <> "" Then Return title
        Next
        Return "整合包"
    End Function

    Private Shared Function PackReleaseInfo(release As ModpackRelease, title As String, Optional now As DateTimeOffset? = Nothing) As String
        Dim parts As New List(Of String)
        If release.Loaders.Length > 0 Then parts.Add(String.Join("、", release.Loaders.Select(AddressOf PackLoaderLabel)))
        Dim fileTitle = PackReleaseTitle(release.FileName)
        If fileTitle <> "" AndAlso Not String.Equals(title, fileTitle, StringComparison.OrdinalIgnoreCase) Then parts.Add(fileTitle)
        If release.GameVersions.Length > 0 AndAlso release.GameVersions.All(Function(game) Not game.Contains(".") OrElse {"w", "snapshot", "rc", "pre", "experimental", "-"}.Any(Function(marker) game.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)) Then parts.Add("游戏版本 " & String.Join("、", release.GameVersions))
        Dim downloads = ModpackReleaseStatistics.DownloadText(release.Downloads)
        If downloads <> "" Then parts.Add(downloads)
        parts.Add("更新于 " & ModpackReleaseStatistics.RelativeTime(release.Published, now.GetValueOrDefault(DateTimeOffset.UtcNow)))
        If release.ReleaseType = "beta" Then parts.Add("测试版")
        If release.ReleaseType = "alpha" Then parts.Add("早期测试版")
        Return String.Join("，", parts)
    End Function

    Private Shared Function PackReleaseToolTip(release As ModpackRelease) As String
        Dim parts As New List(Of String) From {release.Name, "文件：" & release.FileName}
        If Not String.IsNullOrWhiteSpace(release.Version) Then parts.Add("版本号：" & release.Version)
        If release.GameVersions.Length > 0 Then parts.Add("Minecraft：" & String.Join("、", release.GameVersions))
        If release.Loaders.Length > 0 Then parts.Add("加载器：" & String.Join("、", release.Loaders.Select(AddressOf PackLoaderLabel)))
        parts.Add("更新时间：" & release.Published.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"))
        If release.Downloads.GetValueOrDefault() > 0 Then parts.Add("平台下载次数：" & release.Downloads.Value.ToString("N0", Globalization.CultureInfo.InvariantCulture) & " 次")
        If release.Size > 0 Then parts.Add("归档大小：" & FormatBytes(release.Size))
        parts.Add(release.Platform.ToString() & " · " & release.Id)
        Return String.Join(vbCrLf, parts.Where(Function(part) Not String.IsNullOrWhiteSpace(part)))
    End Function

    Private Shared Function PackReleaseConfirmation(release As ModpackRelease, displayedTitle As String) As String
        Return If(String.IsNullOrWhiteSpace(displayedTitle), PackReleaseFallbackTitle(release), displayedTitle) & vbCrLf & "文件：" & release.FileName
    End Function

    Private Shared Function PackLoaderLabel(value As String) As String
        Select Case If(value, "").Trim().ToLowerInvariant()
            Case "forge" : Return "Forge"
            Case "neoforge", "neo-forge" : Return "NeoForge"
            Case "fabric", "fabric-loader" : Return "Fabric"
            Case "quilt", "quilt-loader" : Return "Quilt"
            Case "liteloader" : Return "LiteLoader"
            Case "rift" : Return "Rift"
            Case "minecraft", "vanilla" : Return "原版"
            Case Else : Return value
        End Select
    End Function
End Class
