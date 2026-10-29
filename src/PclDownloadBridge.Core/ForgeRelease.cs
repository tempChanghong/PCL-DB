using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModDownload.DlForgeVersionEntry: display VersionName and file
    // FileVersion are different identities; historical branches are repaired here.
    internal sealed class ForgeRelease
    {
        internal string VersionName { get; set; }
        internal string Branch { get; set; }
        internal string FileVersion
        {
            get
            {
                return VersionName + (Branch is null ? "" : "-" + Branch);
            }
        }

        internal static ForgeRelease Parse(string value, string branch, string mc)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            int split = value.IndexOf('-');
            string embedded = null;
            if (split >= 0)
            {
                embedded = value.Substring(split + 1);
                value = value.Substring(0, split);
            }
            branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
            if (embedded is not null)
            {
                if (branch is not null && (branch ?? "") != (embedded ?? ""))
                    return null;
                branch = embedded;
            }
            Version numeric = null;
            if (!Regex.IsMatch(value, @"\A[0-9]+(?:\.[0-9]+){1,3}\z") || !Version.TryParse(value, out numeric))
                return null;
            if (new[] { "11.15.1.2318", "11.15.1.1902", "11.15.1.1890" }.Contains(value))
                branch = "1.8.9";
            if (branch is null && mc == "1.7.10" && numeric.Revision >= 1300)
                branch = "1.7.10";
            if (branch is not null && !Regex.IsMatch(branch, @"\A[0-9A-Za-z_.-]+\z"))
                return null;
            return new ForgeRelease() { VersionName = value, Branch = branch };
        }
    }
}