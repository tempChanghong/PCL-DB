using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // Adapted from fixed PCL 5ef81e4 McVersion.NameToVersion / VersionToDrop.
    public sealed class MinecraftVersionRules
    {
        private MinecraftVersionRules()
        {
        }

        // PCL McVersion.NameToVersion / VersionToDrop; card visibility uses release families,
        // while component compatibility uses the full pre-release-aware ordering.
        public static int ReleaseFamily(string value)
        {
            if (value is null)
                return 209;
            string name = value.ToLowerInvariant().Replace("_unobfuscated", "").Replace(" unobfuscated", "");
            if (name.StartsWith("2.0", StringComparison.Ordinal))
            {
                name = "1.5.1";
            }
            else if (name == "15w14a")
            {
                name = "1.8.3";
            }
            else if (name.Contains(".rv-pre"))
            {
                name = "1.9.2";
            }
            else if (name.Contains("shareware"))
            {
                name = "1.13.2";
            }
            else if (name.StartsWith("20w14", StringComparison.Ordinal) && name != "20w14a")
            {
                name = "1.15.2";
            }
            else if (name.Contains("oneblockatatime"))
            {
                name = "1.18.2";
            }
            else if (name.Contains("23w13a"))
            {
                name = "1.19.4";
            }
            else if (name == "24w14potato")
            {
                name = "1.20.4";
            }
            else if (name == "25w14craftmine")
            {
                name = "1.21.4";
            }
            else if (name == "26w14a")
            {
                name = "26.1.1";
            }
            string[] parts = name.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.None);
            long drop;
            if (name.StartsWith("1.", StringComparison.Ordinal))
            {
                drop = VersionSegment(parts, 1) * 10L;
            }
            else if (Regex.IsMatch(name, @"^[2-9][0-9]\."))
            {
                drop = VersionSegment(parts, 0) * 10L + VersionSegment(parts, 1);
            }
            else
            {
                return 209;
            }
            return drop > int.MaxValue ? 209 : (int)drop;
        }

        private static int VersionSegment(string[] parts, int index)
        {
            if (index >= parts.Length)
                return 0;
            int number;
            return int.TryParse(Regex.Match(parts[index], @"^\d+").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

    }
}