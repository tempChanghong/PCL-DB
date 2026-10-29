using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 McLibListGetWithJson: rules first, ordinary/native identities,
    // then compare the saved file's parent folder using MCUtils.CompareVersion.
    internal sealed class PclLibrarySelection
    {
        private PclLibrarySelection()
        {
        }

        internal static string Identity(JObject libraryInfo, bool native)
        {
            string coordinate = (string)libraryInfo["name"];
            MinecraftInstaller.MavenPath(coordinate);
            string[] artifact = coordinate.Split('@');
            string[] parts = artifact[0].Split(':');
            // Retain this tool's existing classifier/extension separation.
            return parts[0] + ":" + parts[1] + ":" + (parts.Length > 3 ? parts[3] : "") + "@" + (artifact.Length > 1 ? artifact[1] : "jar") + (native ? ":native" : ":java");
        }

        internal static string ProfileVersion(JObject libraryInfo)
        {
            string classifier = (string)libraryInfo.SelectToken("natives.windows");
            JObject data;
            if (libraryInfo["natives"] is null)
            {
                data = libraryInfo.SelectToken("downloads.artifact") as JObject;
            }
            else
            {
                if (classifier is not null)
                    classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                data = classifier is null ? null : libraryInfo["downloads"]?["classifiers"]?[classifier] as JObject;
            }
            string relative = (string)data?["path"];
            if (relative is null)
                relative = MinecraftInstaller.MavenPath((string)libraryInfo["name"], classifier);
            return FileVersion("libraries/" + relative);
        }

        private static string FileVersion(string relative)
        {
            return Path.GetFileName(Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        internal static void Add(Dictionary<string, List<DownloadFile>> selected, JObject libraryInfo, DownloadFile item, bool native)
        {
            string key = Identity(libraryInfo, native);
            List<DownloadFile> existing = null;
            if (!selected.TryGetValue(key, out existing))
            {
                selected.Add(key, new List<DownloadFile>() { item });
            }
            else if (string.Equals(Path.GetFullPath(existing[0].RelativePath), Path.GetFullPath(item.RelativePath), StringComparison.OrdinalIgnoreCase))
            {
                // One actual file must still merge all compatible sizes/hashes and reject
                // conflicts through DownloadPlan, rather than losing a stronger checker.
                existing.Add(item);
            }
            else if (CompareVersion(FileVersion(item.RelativePath), FileVersion(existing[0].RelativePath)) >= 0)
            {
                selected[key] = new List<DownloadFile>() { item };
            }
        }

        internal static int CompareVersion(string left, string right)
        {
            if ((left ?? "") == (right ?? ""))
                return 0;
            if (left == "未知版本" || right == "未知版本")
                return left == "未知版本" ? 1 : -1;
            left = left.ToLowerInvariant();
            right = right.ToLowerInvariant();
            var leftParts = Regex.Matches(left.Replace("快照", "snapshot").Replace("预览版", "pre"), "[a-z]+|[0-9]+").Cast<Match>().Select(item => item.Value).ToList();
            var rightParts = Regex.Matches(right.Replace("快照", "snapshot").Replace("预览版", "pre"), "[a-z]+|[0-9]+").Cast<Match>().Select(item => item.Value).ToList();
            var index = default(int);
            do
            {
                if (index >= leftParts.Count && index >= rightParts.Count)
                    return string.Compare(left, right, StringComparison.Ordinal);
                string leftValue = index < leftParts.Count ? leftParts[index] : "0";
                string rightValue = index < rightParts.Count ? rightParts[index] : "0";
                index += 1;
                if ((leftValue ?? "") == (rightValue ?? ""))
                    continue;
                int leftNumber = VersionToken(leftValue);
                int rightNumber = VersionToken(rightValue);
                if (leftNumber == 0 && rightNumber == 0)
                    return string.Compare(leftValue, rightValue, StringComparison.Ordinal);
                if (leftNumber != rightNumber)
                    return leftNumber.CompareTo(rightNumber);
            }
            while (true);
        }

        private static int VersionToken(string value)
        {
            switch (value ?? "")
            {
                case "rc":
                    {
                        return -1;
                    }
                case "pre":
                    {
                        return -2;
                    }
                case "snapshot":
                    {
                        return -3;
                    }
                case "experimental":
                    {
                        return -4;
                    }
            }
            int number;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }
    }
}