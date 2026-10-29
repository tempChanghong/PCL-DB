using System;
using System.Globalization;

namespace PclDownloadBridge.Core
{

    // Download rounding: PCL ResourceVersion.ToListItem at 5ef81e4.
    // Relative time: VB adaptation of MeloongCore StringUtils.FormatTimeSpan(false)
    // at c774ae4c85fd94e0eaa171db21e78a65870df19b, Apache-2.0.
    // Changes: explicit DateTimeOffset inputs, invariant formatting, 64-bit counters;
    // Short and detailed branches are adapted. See THIRD_PARTY_NOTICES.md.
    public sealed class ModpackReleaseStatistics
    {
        private ModpackReleaseStatistics()
        {
        }

        public static string DownloadText(long? count)
        {
            if (!count.HasValue || count.Value <= 0L)
                return "";
            string number = count.Value > 100000L ? Math.Round(count.Value / 10000m, 0, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture) + " 万" : count.Value.ToString(CultureInfo.InvariantCulture) + " ";
            return "下载 " + number + "次";
        }

        public static string ShortRelativeTime(DateTimeOffset published, DateTimeOffset now)
        {
            var span = published - now;
            string suffix = span > TimeSpan.Zero ? "后" : "前";
            if (span < TimeSpan.Zero)
                span = -span;
            int months = span.Days / 30;
            if (months >= 12)
                return Number(months / 12) + " 年" + suffix;
            if (months >= 2)
                return Number(months) + " 个月" + suffix;
            if (span.TotalDays >= 2d)
                return Number(span.Days) + " 天" + suffix;
            // PCL's span.Hours wraps after 24h; keep the full 24-47h interval.
            if (span.TotalHours >= 1d)
                return Number((int)Math.Round(Math.Floor(span.TotalHours))) + " 小时" + suffix;
            if (span.TotalMinutes >= 1d)
                return Number(span.Minutes) + " 分钟" + suffix;
            return Number(span.TotalSeconds >= 1d ? span.Seconds : 1) + " 秒" + suffix;
        }

        public static string RelativeTime(DateTimeOffset published, DateTimeOffset now)
        {
            var span = published - now;
            string suffix = span > TimeSpan.Zero ? "后" : "前";
            if (span < TimeSpan.Zero)
                span = -span;
            int months = span.Days / 30;
            if (months >= 61)
                return Number(months / 12) + " 年" + suffix;
            if (months >= 12)
                return Number(months / 12) + " 年" + (months % 12 > 0 ? " " + Number(months % 12) + " 个月" : "") + suffix;
            if (months >= 4)
                return Number(months) + " 个月" + suffix;
            if (months >= 1)
                return Number(months) + " 月" + (span.Days % 30 > 0 ? " " + Number(span.Days % 30) + " 天" : "") + suffix;
            if (span.TotalDays >= 4d)
                return Number(span.Days) + " 天" + suffix;
            if (span.TotalDays >= 1d)
                return Number(span.Days) + " 天" + (span.Hours > 0 ? " " + Number(span.Hours) + " 小时" : "") + suffix;
            if (span.TotalHours >= 10d)
                return Number(span.Hours) + " 小时" + suffix;
            if (span.TotalHours >= 1d)
                return Number(span.Hours) + " 小时" + (span.Minutes > 0 ? " " + Number(span.Minutes) + " 分钟" : "") + suffix;
            if (span.TotalMinutes >= 10d)
                return Number(span.Minutes) + " 分钟" + suffix;
            if (span.TotalMinutes >= 1d)
                return Number(span.Minutes) + " 分" + (span.Seconds > 0 ? " " + Number(span.Seconds) + " 秒" : "") + suffix;
            return Number(span.TotalSeconds >= 1d ? span.Seconds : 1) + " 秒" + suffix;
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}