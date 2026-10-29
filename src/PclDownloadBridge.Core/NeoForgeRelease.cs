using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModDownload.DlNeoForgeListEntry: keep Maven identity separate
    // from the Minecraft version and the numeric loader version used for sorting.
    internal sealed class NeoForgeRelease
    {
        internal const string Pattern = @"(?:1\.20\.1-)?\d+\.[A-Za-z0-9_]+\.\d+(?:\.\d+)?(?:-(?:beta|alpha)(?:\.\d+)?)?(?:\+snapshot-\d+)?";
        internal string ApiName { get; set; }
        internal string VersionName { get; set; }
        internal string Minecraft { get; set; }
        internal Version NumericVersion { get; set; }
        internal bool Stable { get; set; }

        internal static NeoForgeRelease Parse(string value, bool legacy)
        {
            if (value is null || !Regex.IsMatch(value, @"\A" + Pattern + @"\z", RegexOptions.IgnoreCase))
                return null;
            string display = value.StartsWith("1.20.1-", StringComparison.Ordinal) ? value.Substring("1.20.1-".Length) : value;
            if (value == "47.1.82")
                return null; // PCL excludes this exact API name; display names are not Maven identities.
            string core = display.Split('-', '+')[0];
            string[] parts = core.Split('.');
            Version numeric = null;
            string game;
            if (!legacy && value.StartsWith("0.", StringComparison.Ordinal))
            {
                int build;
                if (parts.Length < 3 || !int.TryParse(parts.Last(), out build))
                    return null;
                numeric = new Version(0, 0, build);
                game = parts[1];
            }
            else
            {
                if (!Version.TryParse(core, out numeric) || numeric.Build < 0)
                    return null;
                if (legacy)
                {
                    if (numeric.Revision >= 0)
                        return null;
                    numeric = new Version(19, numeric.Major, numeric.Minor, numeric.Build);
                    game = "1.20.1";
                }
                else
                {
                    game = numeric.Major >= 24 ? numeric.Major + "." + numeric.Minor + "." + numeric.Build : "1." + numeric.Major + "." + numeric.Minor;
                    if (game.EndsWith(".0", StringComparison.Ordinal))
                        game = game.Substring(0, game.Length - 2);
                    int plus = value.IndexOf('+');
                    if (plus >= 0)
                        game += "-" + value.Substring(plus + 1);
                }
            }
            return new NeoForgeRelease()
            {
                ApiName = value,
                VersionName = display,
                Minecraft = game,
                NumericVersion = numeric,
                Stable = value.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) < 0 && value.IndexOf("beta", StringComparison.OrdinalIgnoreCase) < 0 && value.IndexOf("+snapshot-", StringComparison.OrdinalIgnoreCase) < 0
            };
        }
    }
}