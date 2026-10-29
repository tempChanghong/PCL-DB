using System.IO;
using System.Linq;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL PageSetupSystem / ToolDownloadMod, ToolDownloadTranslateV2 and
    // ToolModLocalNameStyle. These are our own settings; never write PCL's settings.
    public sealed class CommunityResourcePreferences
    {
        public int Source { get; set; } = 2;
        public int FileNameStyle { get; set; } = 1;
        public int LocalModStyle { get; set; }
        public static CommunityResourcePreferences Read(JToken value)
        {
            var result = new CommunityResourcePreferences();
            JObject json = value as JObject;
            if (json is null)
                return result;
            result.Source = ReadIndex(json["source"], 2, 2);
            result.FileNameStyle = ReadIndex(json["fileNameStyle"], 1, 4);
            result.LocalModStyle = ReadIndex(json["localModStyle"], 0, 1);
            return result;
        }
        private static int ReadIndex(JToken value, int fallback, int maximum)
        {
            int number;
            if (value is null || value.Type != JTokenType.Integer || !int.TryParse(value.ToString(), out number) || number < 0 || number > maximum)
                return fallback;
            return number;
        }
        public JObject ToJson()
        {
            return new JObject() { { "source", Source }, { "fileNameStyle", FileNameStyle }, { "localModStyle", LocalModStyle } };
        }
        public string SuggestedFileName(ModpackProject project, ModpackRelease release)
        {
            CommunityResources.ValidateFileName(release.Kind, release.FileName);
            string original = release.Kind == CommunityResourceKind.Mod ? release.FileName.Replace("~", "-") : release.FileName;
            string translated = ResourceProjectNames.GetTranslation(project);
            if (translated is null || FileNameStyle == 4)
                return original;
            string name = ResourceProjectNames.GetTitle(project).Title;
            string fromChars = @"\/:|<>*?" + '"';
            string toChars = "＼／：｜＜＞＊？" + Strings.ChrW(0xFF02);
            for (int i = 0, loopTo = fromChars.Length - 1; i <= loopTo; i++)
                name = name.Replace(fromChars[i], toChars[i]);
            name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim().TrimEnd('.');
            string ext = Path.GetExtension(original);
            string stem = Path.GetFileNameWithoutExtension(original);
            int room = 255 - original.Length - (FileNameStyle <= 1 ? 3 : 1);
            if (room <= 0)
                return original;
            if (name.Length > room)
            {
                if (char.IsHighSurrogate(name[room - 1]))
                    room -= 1;
                name = name.Substring(0, room);
            }
            if (string.IsNullOrEmpty(name))
                return original;
            string result;
            switch (FileNameStyle)
            {
                case 0:
                    {
                        result = "【" + name + "】" + stem + ext;
                        break;
                    }
                case 1:
                    {
                        result = "[" + name + "] " + stem + ext;
                        break;
                    }
                case 2:
                    {
                        result = name + "-" + stem + ext;
                        break;
                    }
                case 3:
                    {
                        result = stem + "-" + name + ext;
                        break;
                    }

                default:
                    {
                        return original;
                    }
            }
            CommunityResources.ValidateFileName(release.Kind, result);
            return result;
        }
    }
}