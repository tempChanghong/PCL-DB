Imports System.Globalization
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModpackReleaseStatisticsChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Friend Sub VerifyParsing(project As ModpackProject, original As JObject, passed As List(Of String))
        Dim field = If(project.Platform = ModpackPlatform.Modrinth, "downloads", "downloadCount")
        Dim valid = {Tuple.Create(Of JToken, Long)(New JValue(0), 0), Tuple.Create(Of JToken, Long)(New JValue(42), 42),
            Tuple.Create(Of JToken, Long)(New JValue(3000000000L), 3000000000L), Tuple.Create(Of JToken, Long)(New JValue(Long.MaxValue), Long.MaxValue),
            Tuple.Create(Of JToken, Long)(New JValue(42D), 42), Tuple.Create(Of JToken, Long)(JToken.Parse("1e3"), 1000)}
        For index = 0 To valid.Length - 1
            Dim data = DirectCast(original.DeepClone(), JObject) : data(field) = valid(index).Item1
            Dim release = ModpackCatalogClient.ParseRelease(project, data)
            Check(release.Downloads.HasValue AndAlso release.Downloads.Value = valid(index).Item2,
                "release statistics retain nonnegative integral platform counters: " & project.Platform.ToString() & " / " & index, passed)
        Next
        Dim invalid As JToken() = {JValue.CreateNull(), New JValue(-1), New JValue(42.5), New JValue("42"), New JValue(True), New JObject(), New JArray(), JToken.Parse("9223372036854775808"), JToken.Parse("1e100")}
        For index = -1 To invalid.Length - 1
            Dim data = DirectCast(original.DeepClone(), JObject)
            If index = -1 Then data.Remove(field) Else data(field) = invalid(index)
            Dim release = ModpackCatalogClient.ParseRelease(project, data), baseline = ModpackCatalogClient.ParseRelease(project, original)
            Check(Not release.Downloads.HasValue AndAlso release.Id = baseline.Id AndAlso release.ProjectId = baseline.ProjectId AndAlso release.Url = baseline.Url AndAlso
                release.Size = baseline.Size AndAlso release.Sha1 = baseline.Sha1 AndAlso release.Md5 = baseline.Md5 AndAlso release.Sha512 = baseline.Sha512,
                "unknown or invalid advisory counters preserve the verified archive identity: " & project.Platform.ToString() & " / " & index, passed)
        Next
        Dim source = DirectCast(original.DeepClone(), JObject) : source(field) = 42
        Dim owned = ModpackCatalogClient.ParseRelease(project, source), snapshot = owned.Copy()
        owned.Downloads = 43
        Check(snapshot.Downloads.HasValue AndAlso snapshot.Downloads.Value = 42 AndAlso snapshot.Id = owned.Id,
            "release snapshots retain their own statistics without changing platform identity: " & project.Platform.ToString(), passed)
    End Sub

    Friend Sub VerifyFormatting(passed As List(Of String))
        Dim counts = {Tuple.Create(Of Long?, String)(Nothing, ""), Tuple.Create(Of Long?, String)(-1, ""), Tuple.Create(Of Long?, String)(0, ""),
            Tuple.Create(Of Long?, String)(99999, "下载 99999 次"), Tuple.Create(Of Long?, String)(100000, "下载 100000 次"), Tuple.Create(Of Long?, String)(100001, "下载 10 万次"),
            Tuple.Create(Of Long?, String)(105000, "下载 10 万次"), Tuple.Create(Of Long?, String)(115000, "下载 12 万次"), Tuple.Create(Of Long?, String)(1234567, "下载 123 万次"),
            Tuple.Create(Of Long?, String)(3000000000L, "下载 300000 万次"), Tuple.Create(Of Long?, String)(Long.MaxValue, "下载 922337203685478 万次")}
        For index = 0 To counts.Length - 1
            Check(ModpackReleaseStatistics.DownloadText(counts(index).Item1) = counts(index).Item2, "release download labels retain PCL rounding and 64-bit bounds: " & index, passed)
        Next
        Dim times = {Tuple.Create(0.0, "1 秒"), Tuple.Create(0.5, "1 秒"), Tuple.Create(1.0, "1 秒"), Tuple.Create(59.0, "59 秒"), Tuple.Create(60.0, "1 分"),
            Tuple.Create(61.0, "1 分 1 秒"), Tuple.Create(599.0, "9 分 59 秒"), Tuple.Create(600.0, "10 分钟"), Tuple.Create(3599.0, "59 分钟"),
            Tuple.Create(3600.0, "1 小时"), Tuple.Create(3660.0, "1 小时 1 分钟"), Tuple.Create(35999.0, "9 小时 59 分钟"), Tuple.Create(36000.0, "10 小时"),
            Tuple.Create(86399.0, "23 小时"), Tuple.Create(86400.0, "1 天"), Tuple.Create(90000.0, "1 天 1 小时"), Tuple.Create(345599.0, "3 天 23 小时"),
            Tuple.Create(345600.0, "4 天"), Tuple.Create(2591999.0, "29 天"), Tuple.Create(2592000.0, "1 月"), Tuple.Create(2678400.0, "1 月 1 天"),
            Tuple.Create(10281600.0, "3 月 29 天"), Tuple.Create(10368000.0, "4 个月"), Tuple.Create(31017600.0, "11 个月"), Tuple.Create(31104000.0, "1 年"),
            Tuple.Create(33696000.0, "1 年 1 个月"), Tuple.Create(155520000.0, "5 年"), Tuple.Create(158025600.0, "5 年"), Tuple.Create(158112000.0, "5 年")}
        Dim now As New DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)
        For Each future In {False, True}
            For index = 0 To times.Length - 1
                Dim published = now.AddSeconds(If(future, times(index).Item1, -times(index).Item1))
                Check(ModpackReleaseStatistics.RelativeTime(published, now) = times(index).Item2 & If(future AndAlso times(index).Item1 > 0, "后", "前"),
                    "release relative time follows fixed PCL detailed-unit boundaries: " & future & " / " & index, passed)
            Next
        Next
        Check(ModpackReleaseStatistics.RelativeTime(now.ToOffset(TimeSpan.FromHours(8)), now) = "1 秒前", "relative timestamps compare instants rather than local offset clock values", passed)
        Dim culture = CultureInfo.CurrentCulture
        Try
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR")
            Check(ModpackReleaseStatistics.DownloadText(105000) = "下载 10 万次" AndAlso ModpackReleaseStatistics.RelativeTime(now.AddMinutes(-2).AddSeconds(-3), now) = "2 分 3 秒前",
                "release presentation keeps PCL units and integer rounding under a different numeric culture", passed)
        Finally
            CultureInfo.CurrentCulture = culture
        End Try
    End Sub
End Module
