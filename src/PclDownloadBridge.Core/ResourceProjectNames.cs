using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PclDownloadBridge.Core
{

    // Fixed PCL WikiEntry.cs / ResourceProject.GetControlTitle. Platform slug is
    // the only lookup key; translated text never replaces install/project identity.
    public sealed class ResourceProjectTitle
    {
        public string Title { get; private set; }
        public string Subtitle { get; private set; }
        internal ResourceProjectTitle(string title, string subtitle)
        {
            Title = title;
            Subtitle = subtitle;
        }
    }

    public sealed class ResourceProjectNames
    {
        private ResourceProjectNames()
        {
        }
        private static readonly Lazy<Dictionary<string, string>> Entries = new Lazy<Dictionary<string, string>>(ReadEntries);
        private static Dictionary<string, string> ReadEntries()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(ResourceProjectNames).Assembly.GetManifestResourceStream("PclWikiEntries.txt"))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true))
            {
                string[] lines = reader.ReadToEnd().Split('\n');
                // Final nonempty line encodes popularity, not a project.
                int last = lines.Length - 1;
                while (last >= 0 && string.IsNullOrWhiteSpace(lines[last]))
                    last -= 1;
                for (int i = 0, loopTo = last - 1; i <= loopTo; i++)
                {
                    foreach (var entry in lines[i].TrimEnd('\r').Split('¨'))
                    {
                        string[] parts = entry.Split('|');
                        if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]))
                            continue;
                        string[] slugs = parts[0].Split('@');
                        string name = parts[parts.Length - 1];
                        string englishSlug = slugs.FirstOrDefault(slug => !string.IsNullOrEmpty(slug));
                        if (englishSlug is null || string.IsNullOrEmpty(name))
                            continue;
                        name = name.Replace("*", " (" + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(englishSlug.Replace("-", " ")) + ")");
                        if (!string.IsNullOrEmpty(slugs[0]))
                            Add(result, ModpackPlatform.CurseForge, slugs[0], name);
                        if (slugs.Length == 2)
                            Add(result, ModpackPlatform.Modrinth, string.IsNullOrEmpty(slugs[1]) ? slugs[0] : slugs[1], name);
                    }
                }
            }
            return result;
        }
        private static void Add(Dictionary<string, string> result, ModpackPlatform platform, string slug, string name)
        {
            string key = platform.ToString() + ":" + slug;
            if (!result.ContainsKey(key))
                result.Add(key, name);
        }
        public static ResourceProjectTitle GetTitle(ModpackProject project)
        {
            if (project is null)
                return new ResourceProjectTitle("", "");
            string raw = project.Name ?? "";
            string translated = GetTranslation(project);
            if (translated is not null && (translated ?? "") != (raw ?? ""))
            {
                int opening = translated.IndexOf(" (", StringComparison.Ordinal);
                int closing = translated.LastIndexOf(')');
                if (opening > 0 && closing > opening)
                {
                    string title = translated.Substring(0, opening);
                    string english = translated.Substring(opening + 2, closing - opening - 2);
                    string suffix = translated.Substring(closing + 1).Trim();
                    if (suffix.StartsWith("- ", StringComparison.Ordinal))
                        english += "，" + suffix.Substring(2);
                    return new ResourceProjectTitle(title, english);
                }
                return new ResourceProjectTitle(translated, raw);
            }
            return UntranslatedTitle(raw);
        }
        public static string GetTranslation(ModpackProject project)
        {
            if (project is null)
                return null;
            string translated = null;
            if ((project.Kind == CommunityResourceKind.Mod || project.Kind == CommunityResourceKind.DataPack) && !string.IsNullOrEmpty(project.Slug))
            {
                Entries.Value.TryGetValue(project.Platform.ToString() + ":" + project.Slug, out translated);
            }
            return translated;
        }
        private static ResourceProjectTitle UntranslatedTitle(string raw)
        {
            // PCL separates abbreviations and loader editions from untranslated names.
            string[] parts = Regex.Split(raw, @" \| | - |[()\[\]{}]").Select(value => value.Trim(' ', '/', '\\')).Where(value => !string.IsNullOrEmpty(value)).ToArray();
            var normal = new List<string>();
            var subtitles = new List<string>();
            foreach (var part in parts)
            {
                if ((part ?? "") == (part.ToUpperInvariant() ?? "") && part != "FPS" && part != "HUD" || Regex.IsMatch(part, @"\A(?:forge|neoforge|fabric|quilt)(?:[ /-]*(?:forge|neoforge|fabric|quilt))*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    subtitles.Add(part);
                }
                else
                {
                    normal.Add(part);
                }
            }
            if (normal.Count == 0 || subtitles.Count == 0)
                return new ResourceProjectTitle(raw, "");
            return new ResourceProjectTitle(string.Join(" - ", normal), string.Join("，", subtitles.Distinct(StringComparer.Ordinal)));
        }
    }
}