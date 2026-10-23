Imports System.Globalization

' Download rounding: PCL ResourceVersion.ToListItem at 5ef81e4.
' Relative time: VB adaptation of MeloongCore StringUtils.FormatTimeSpan(false)
' at c774ae4c85fd94e0eaa171db21e78a65870df19b, Apache-2.0.
' Changes: explicit DateTimeOffset inputs, invariant formatting, 64-bit counters;
' Short and detailed branches are adapted. See THIRD_PARTY_NOTICES.md.
Public NotInheritable Class ModpackReleaseStatistics
    Private Sub New()
    End Sub

    Public Shared Function DownloadText(count As Long?) As String
        If Not count.HasValue OrElse count.Value <= 0 Then Return ""
        Dim number = If(count.Value > 100000,
            Math.Round(CDec(count.Value) / 10000D, 0, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture) & " 万",
            count.Value.ToString(CultureInfo.InvariantCulture) & " ")
        Return "下载 " & number & "次"
    End Function

    Public Shared Function ShortRelativeTime(published As DateTimeOffset, now As DateTimeOffset) As String
        Dim span = published - now, suffix = If(span > TimeSpan.Zero, "后", "前")
        If span < TimeSpan.Zero Then span = -span
        Dim months = span.Days \ 30
        If months >= 12 Then Return Number(months \ 12) & " 年" & suffix
        If months >= 2 Then Return Number(months) & " 个月" & suffix
        If span.TotalDays >= 2 Then Return Number(span.Days) & " 天" & suffix
        ' PCL's span.Hours wraps after 24h; keep the full 24-47h interval.
        If span.TotalHours >= 1 Then Return Number(CInt(Math.Floor(span.TotalHours))) & " 小时" & suffix
        If span.TotalMinutes >= 1 Then Return Number(span.Minutes) & " 分钟" & suffix
        Return Number(If(span.TotalSeconds >= 1, span.Seconds, 1)) & " 秒" & suffix
    End Function

    Public Shared Function RelativeTime(published As DateTimeOffset, now As DateTimeOffset) As String
        Dim span = published - now, suffix = If(span > TimeSpan.Zero, "后", "前")
        If span < TimeSpan.Zero Then span = -span
        Dim months = span.Days \ 30
        If months >= 61 Then Return Number(months \ 12) & " 年" & suffix
        If months >= 12 Then Return Number(months \ 12) & " 年" & If(months Mod 12 > 0, " " & Number(months Mod 12) & " 个月", "") & suffix
        If months >= 4 Then Return Number(months) & " 个月" & suffix
        If months >= 1 Then Return Number(months) & " 月" & If(span.Days Mod 30 > 0, " " & Number(span.Days Mod 30) & " 天", "") & suffix
        If span.TotalDays >= 4 Then Return Number(span.Days) & " 天" & suffix
        If span.TotalDays >= 1 Then Return Number(span.Days) & " 天" & If(span.Hours > 0, " " & Number(span.Hours) & " 小时", "") & suffix
        If span.TotalHours >= 10 Then Return Number(span.Hours) & " 小时" & suffix
        If span.TotalHours >= 1 Then Return Number(span.Hours) & " 小时" & If(span.Minutes > 0, " " & Number(span.Minutes) & " 分钟", "") & suffix
        If span.TotalMinutes >= 10 Then Return Number(span.Minutes) & " 分钟" & suffix
        If span.TotalMinutes >= 1 Then Return Number(span.Minutes) & " 分" & If(span.Seconds > 0, " " & Number(span.Seconds) & " 秒", "") & suffix
        Return Number(If(span.TotalSeconds >= 1, span.Seconds, 1)) & " 秒" & suffix
    End Function

    Private Shared Function Number(value As Integer) As String
        Return value.ToString(CultureInfo.InvariantCulture)
    End Function
End Class
