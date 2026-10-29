using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL McVersion.IsFoolName / jsonType and McInstance.Load. Presentation only;
    // no cached State, launcher configuration or isolation setting is rewritten.
    public sealed class MinecraftVersionCategory
    {
        private MinecraftVersionCategory()
        {
        }
        public static bool IsAprilFools(string id)
        {
            string name = (id ?? "").ToLowerInvariant();
            return name.StartsWith("2.0", StringComparison.Ordinal) || name.StartsWith("20w14inf", StringComparison.Ordinal) || new[] { "15w14a", "1.rv-pre1", "3d shareware v1.34", "20w14∞", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a" }.Contains(name);
        }
        public static string Classify(string id, JObject metadata)
        {
            var typeToken = metadata?["type"];
            string kind = (typeToken?.Type) is { } arg1 && arg1 == JTokenType.String ? (string)typeToken : "";
            if (IsAprilFools(id) || kind == "fool")
                return "special";
            if (kind == "old_alpha" || kind == "old_beta")
                return kind;
            if (Regex.IsMatch(id ?? "", @"\A(?:rd-|in-|inf-|c0\.|[ab]\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "old_alpha";
            if (kind == "snapshot")
            {
                DateTimeOffset released;
                var time = metadata?["releaseTime"];
                string stamp = (time?.Type) is { } arg2 && arg2 == JTokenType.Date ? time.ToString(Newtonsoft.Json.Formatting.None).Trim('"') : (time?.ToString());
                if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out released))
                {
                    var fixedTime = released.ToUniversalTime().AddHours(2d);
                    if (fixedTime.Month == 4 && fixedTime.Day == 1)
                        return "special";
                }
                return "snapshot";
            }
            if (kind == "pending" || Regex.IsMatch(id ?? "", @"\A\d{2}w\d{2}[a-z]|(?:-pre\d| pre-release | release candidate |-rc\d|experimental)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "snapshot";
            return kind == "release" ? "release" : "unknown";
        }
        public static string Label(string kind)
        {
            switch (kind ?? "")
            {
                case "special":
                    {
                        return "愚人节版本";
                    }
                case "old_alpha":
                case "old_beta":
                    {
                        return "远古版本";
                    }
                case "snapshot":
                    {
                        return "快照版";
                    }
                case "release":
                    {
                        return "正式版";
                    }

                default:
                    {
                        return "原版";
                    }
            }
        }
    }
}