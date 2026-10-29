using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.CompilerServices;
using Microsoft.Win32;

namespace PclDownloadBridge.Core
{

    // Behavioral adaptation of PCL 5ef81e4, ModMinecraft.McFolderListLoadSub / SetMcFolder
    // and Pages/PageSetup/Settings.vb. See SOURCE_REFERENCES.md for exact provenance.
    public class PclFolders
    {
        public List<string> Warnings { get; private set; } = new List<string>();

        public List<GameFolder> Discover(string pclDirectory, bool readRegistry = true, string configurationFile = null)
        {
            Warnings.Clear();
            var result = new List<GameFolder>();
            string basePath = Path.GetFullPath(pclDirectory);
            string selected = null;
            try
            {
                string stored = ReadIniValue(configurationFile ?? Path.Combine(basePath, "PCL", "Setup.ini"), "LaunchFolderSelect");
                if (!string.IsNullOrWhiteSpace(stored))
                    selected = NormalizeFolderPath(ExpandPath(stored, basePath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                Warnings.Add("无法读取 PCL 当前目录设置：" + ex.Message);
            }
            AddFolder(result, basePath, "当前游戏目录", "PCL 所在目录", false);
            if (Directory.Exists(basePath))
            {
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(basePath))
                        AddFolder(result, child, Path.GetFileName(child) == ".minecraft" ? "PCL 游戏目录" : Path.GetFileName(child), "PCL 旁的目录", Path.GetFileName(child) == ".minecraft");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Warnings.Add("部分目录无法读取：" + ex.Message);
                }
            }
            if (readRegistry)
            {
                // PCLDebug is the published source build; PCL is the regular distribution.
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    foreach (var keyName in new[] { "PCL", "PCLDebug" })
                    {
                        try
                        {
                            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                            using (var key = hive.OpenSubKey(@"Software\" + keyName, false))
                            {
                                if (key is null)
                                    continue;
                                AddCustomFolders(result, Conversions.ToString(key.GetValue("LaunchFolders", "")), basePath, "PCL 自定义目录");
                            }
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        {
                            Warnings.Add("无法读取 PCL 目录设置：" + ex.Message);
                        }
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(selected))
            {
                AddFolder(result, selected, "PCL 当前选择", @"PCL\Setup.ini", true);
                foreach (var folder in result)
                    folder.IsSelected = SamePath(folder.Location, selected);
            }
            string official = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            AddFolder(result, official, "官方启动器目录", "AppData", false);
            return result.OrderByDescending(f => f.IsSelected).ToList();
        }

        public void AddCustomFolders(List<GameFolder> result, string raw, string basePath, string origin)
        {
            if (string.IsNullOrEmpty(raw))
                return;
            foreach (var entry in raw.Split('|'))
            {
                int split = entry.IndexOf('>');
                if (split < 1)
                    continue;
                string location = ExpandPath(entry.Substring(split + 1), basePath);
                AddFolder(result, location, entry.Substring(0, split), origin, true, true);
            }
        }

        private void AddFolder(List<GameFolder> result, string location, string name, string origin, bool allowEmpty, bool customName = false)
        {
            try
            {
                string full = NormalizeFolderPath(location);
                if (!Directory.Exists(full))
                    return;
                if (!allowEmpty && !Directory.Exists(Path.Combine(full, "versions")))
                    return;
                var existing = result.FirstOrDefault(f => SamePath(f.Location, full));
                if (existing is null)
                {
                    result.Add(new GameFolder() { Name = name, Location = full, Origin = origin });
                }
                else if (customName)
                {
                    // PCL renames an already scanned directory instead of dropping its custom alias.
                    existing.Name = name;
                    existing.Origin = origin;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                Warnings.Add("忽略无效目录：" + location);
            }
        }

        public static string ExpandPath(string value, string basePath)
        {
            return Environment.ExpandEnvironmentVariables(value.Trim().Replace("$", Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(NormalizeFolderPath(a), NormalizeFolderPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFolderPath(string value)
        {
            string full = Path.GetFullPath(value);
            string root = Path.GetPathRoot(full);
            string trimmed = full.TrimEnd('\\', '/');
            // C:\ is fully qualified; C: instead resolves against that drive's working directory.
            return trimmed.Length < root.Length ? root : trimmed;
        }

        public static string ReadIniValue(string path, string name)
        {
            if (!File.Exists(path))
                return null;
            string value = null;
            foreach (var line in File.ReadLines(path))
            {
                int index = line.IndexOf(':');
                int @equals = line.IndexOf('=');
                if (equals >= 0 && (index < 0 || equals < index))
                    index = equals;
                if (index < 0)
                    continue;
                // PCL IniGetContent overwrites repeated keys: the last occurrence wins.
                if ((line.Substring(0, index).Trim() ?? "") == (name ?? ""))
                    value = line.Substring(index + 1).Trim();
            }
            return value;
        }
    }
}