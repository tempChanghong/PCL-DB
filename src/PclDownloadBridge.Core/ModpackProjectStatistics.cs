using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ResourceProject.ToResourceItem. Keep project statistics distinct
    // from per-file statistics: project downloads use floor(10k) / rounded(100m).
    public sealed class ModpackProjectStatistics
    {
        private ModpackProjectStatistics()
        {
        }

        public static string DownloadText(long count)
        {
            if (count < 0L)
                return "未知";
            if (count > 100000000L)
                return Math.Round(count / 100000000m, 2, MidpointRounding.ToEven).ToString("0.##", CultureInfo.InvariantCulture) + " 亿";
            if (count > 100000L)
                return decimal.Floor(count / 10000m).ToString(CultureInfo.InvariantCulture) + " 万";
            return count.ToString(CultureInfo.InvariantCulture);
        }

        public static string VersionText(IEnumerable<string> versions, IEnumerable<string> knownVersions = null)
        {
            string[] values = (versions ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length == 0)
                return "版本未知";
            var families = values.Select(Family).Where(value => value is not null);
            Version[] ordered = families.Distinct().OrderByDescending(value => value).ToArray();
            if (ordered.Length == 0)
                return values.All(value => Regex.IsMatch(value, @"\A[0-9]{2}w[0-9]{2}[a-z]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ? "仅快照版本" : "其他版本";
            var known = (knownVersions ?? Enumerable.Empty<string>()).Select(Family).Where(value => value is not null).Distinct().OrderByDescending(value => value).ToList();
            if (known.Count > 0)
                return Ranges(ordered, known);
            // PCL's AllDrops=Nothing fallback: no trustworthy global family list.
            // Like PCL, omit pre-1.12 families when modern families are available.
            if (ordered[0] >= new Version(1, 12))
                ordered = ordered.Where(value => value >= new Version(1, 12)).ToArray();
            return string.Join(", ", ordered.Select(value => value.ToString(2)));
        }
        private static Version Family(string value)
        {
            if (value is null)
                return null;
            var match = Regex.Match(value, @"\A(?<major>1|[2-9][0-9])\.(?<minor>[0-9]{1,3})(?:\.[0-9]{1,3})?\z", RegexOptions.CultureInvariant);
            if (!match.Success)
                return null;
            return new Version(int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture));
        }
        private static string Ranges(Version[] values, List<Version> known)
        {
            var segments = new List<string>();
            var threshold = new Version(1, 12);
            bool old = false;
            int i = 0;
            while (i < values.Length)
            {
                var first = values[i];
                var last = first;
                int startIndex = known.IndexOf(first);
                int count = 1;
                if (first < threshold)
                {
                    if (segments.Count > 0 && !old)
                        break;
                    old = true;
                }
                while (i + 1 < values.Length && startIndex >= 0 && known.IndexOf(values[i + 1]) == startIndex + count)
                {
                    i += 1;
                    last = values[i];
                    count += 1;
                }
                if (count == 1)
                {
                    segments.Add(first.ToString(2));
                }
                else if (startIndex == 0)
                {
                    if (last < threshold)
                        return "全版本";
                    segments.Add(last.ToString(2) + "+");
                }
                else if (last < threshold)
                {
                    segments.Add(first.ToString(2) + "-");
                    break;
                }
                else if (count == 2)
                {
                    segments.Add(first.ToString(2) + ", " + last.ToString(2));
                }
                else
                {
                    segments.Add(first.ToString(2) + "~" + last.ToString(2));
                }
                i += 1;
            }
            return string.Join(", ", segments);
        }
    }
}