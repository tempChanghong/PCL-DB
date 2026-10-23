Imports System.Globalization
Imports PclDownloadBridge.Core

Friend Module ModpackProjectStatisticsChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Friend Sub Verify(passed As List(Of String))
        Dim counts = {Tuple.Create(-1L, "未知"), Tuple.Create(0L, "0"), Tuple.Create(99999L, "99999"), Tuple.Create(100000L, "100000"),
            Tuple.Create(100001L, "10 万"), Tuple.Create(109999L, "10 万"), Tuple.Create(19194999L, "1919 万"), Tuple.Create(100000000L, "10000 万"),
            Tuple.Create(100000001L, "1 亿"), Tuple.Create(3000000000L, "30 亿"), Tuple.Create(Long.MaxValue, "92233720368.55 亿")}
        For index = 0 To counts.Length - 1
            Check(ModpackProjectStatistics.DownloadText(counts(index).Item1) = counts(index).Item2, "project counters preserve PCL's separate compact thresholds and 64-bit bounds: " & index, passed)
        Next
        Dim now As New DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)
        Dim times = {Tuple.Create(0.0, "1 秒"), Tuple.Create(0.5, "1 秒"), Tuple.Create(1.0, "1 秒"), Tuple.Create(59.0, "59 秒"),
            Tuple.Create(60.0, "1 分钟"), Tuple.Create(3599.0, "59 分钟"), Tuple.Create(3600.0, "1 小时"), Tuple.Create(86399.0, "23 小时"),
            Tuple.Create(86400.0, "24 小时"), Tuple.Create(90000.0, "25 小时"), Tuple.Create(172799.0, "47 小时"), Tuple.Create(172800.0, "2 天"),
            Tuple.Create(2592000.0, "30 天"), Tuple.Create(5183999.0, "59 天"), Tuple.Create(5184000.0, "2 个月"),
            Tuple.Create(31103999.0, "11 个月"), Tuple.Create(31104000.0, "1 年"), Tuple.Create(62208000.0, "2 年")}
        For Each future In {False, True}
            For index = 0 To times.Length - 1
                Dim published = now.AddSeconds(If(future, times(index).Item1, -times(index).Item1))
                Check(ModpackReleaseStatistics.ShortRelativeTime(published, now) = times(index).Item2 & If(future AndAlso times(index).Item1 > 0, "后", "前"),
                    "short project dates preserve boundaries without wrapping 24-47 hours: " & future & " / " & index, passed)
            Next
        Next
        Dim versions As Tuple(Of String(), String)() = {
            Tuple.Create(Of String(), String)(Nothing, "版本未知"), Tuple.Create(New String() {}, "版本未知"),
            Tuple.Create(New String() {Nothing, "", " "}, "版本未知"), Tuple.Create({"1.20.1"}, "1.20"),
            Tuple.Create({"1.9", "1.20.1", "1.20.2", "1.21.1", "1.9"}, "1.21, 1.20"),
            Tuple.Create({"26.3", "1.21.1", "26.1", "26.2", "26.1.1"}, "26.3, 26.2, 26.1, 1.21"),
            Tuple.Create({"1.12", "1.20.1"}, "1.20, 1.12"), Tuple.Create({"1.7.10", "1.11.2", "1.9"}, "1.11, 1.9, 1.7"),
            Tuple.Create({"26w14a", "24w10b"}, "仅快照版本"), Tuple.Create({"1.21-pre1"}, "其他版本"),
            Tuple.Create({"fabric", "1.20.1", "1.99999999999", "1.21.1"}, "1.21, 1.20"),
            Tuple.Create({"26.10", "26.2", "26.10.1", "27.1"}, "27.1, 26.10, 26.2")}
        For index = 0 To versions.Length - 1
            Dim original = If(versions(index).Item1 Is Nothing, Nothing, DirectCast(versions(index).Item1.Clone(), String()))
            Check(ModpackProjectStatistics.VersionText(versions(index).Item1) = versions(index).Item2,
                "project families sort numerically, deduplicate and never infer unverified ranges: " & index, passed)
            Check(original Is Nothing OrElse original.SequenceEqual(versions(index).Item1), "formatting retains the exact compatibility array without mutation: " & index, passed)
        Next
        Dim culture = CultureInfo.CurrentCulture
        Try
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR")
            Check(ModpackProjectStatistics.DownloadText(123456789) = "1.23 亿" AndAlso ModpackProjectStatistics.VersionText({"1.9", "1.20.1"}) = "1.20" AndAlso
                ModpackReleaseStatistics.ShortRelativeTime(now.AddHours(-25), now.ToOffset(TimeSpan.FromHours(8))) = "25 小时前", "project metadata is independent of local numeric culture and time-zone offsets", passed)
        Finally
            CultureInfo.CurrentCulture = culture
        End Try
    End Sub
End Module
