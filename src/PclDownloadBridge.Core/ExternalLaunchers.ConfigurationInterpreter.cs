using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{
    public sealed partial class LauncherDirectoryReader
    {
        // Interpret text only. Emit candidates as they are read so the outer
        // reader retains filesystem timing, duplicate IDs and partial results.
        private static class DirectoryConfigurationInterpreter
        {
            internal static JObject ParseConfiguration(string text)
            {
                // Match install-time configuration snapshots: ambiguous duplicate keys
                // must not silently choose a different game directory.
                return JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }

            // Read only the flat scalar used by CE's YamlFileProvider. Unsupported YAML
            // constructs fail visibly instead of guessing or migrating the foreign file.
            internal static string ReadYamlScalar(string text, string key)
            {
                if (text.TrimStart().StartsWith("{", StringComparison.Ordinal) || text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                    throw new InvalidDataException("此 YAML 配置写法暂不支持。");
                bool found = false;
                string value = null;
                foreach (var line in text.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n'))
                {
                    if (!line.StartsWith(key + ":", StringComparison.Ordinal))
                    {
                        if (Regex.IsMatch(line, @"^\s*[""']?" + Regex.Escape(key) + @"[""']?\s*:", RegexOptions.IgnoreCase))
                            throw new InvalidDataException("此 YAML 配置键写法暂不支持，请使用普通顶层键。");
                        continue;
                    }
                    if (found)
                        throw new InvalidDataException("PCL-CE 目录配置包含重复键。");
                    found = true;
                    value = line.Substring(key.Length + 1).Trim();
                    if (value.StartsWith("\"", StringComparison.Ordinal))
                    {
                        value = JToken.Parse(value).Value<string>();
                    }
                    else if (value.StartsWith("'", StringComparison.Ordinal))
                    {
                        if (!value.EndsWith("'", StringComparison.Ordinal) || value.Length < 2)
                            throw new InvalidDataException("PCL-CE 目录字符串不完整。");
                        value = value.Substring(1, value.Length - 2).Replace("''", "'");
                    }
                    else
                    {
                        if (value.StartsWith("|", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal) || value.StartsWith("*", StringComparison.Ordinal) || value.StartsWith("&", StringComparison.Ordinal) || value.StartsWith("!", StringComparison.Ordinal) || value.StartsWith("{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal))
                            throw new InvalidDataException("此 YAML 目录写法暂不支持，请手动选择游戏目录。");
                        int comment = value.IndexOf(" #", StringComparison.Ordinal);
                        if (comment >= 0)
                            value = value.Substring(0, comment).TrimEnd();
                        if (value == "null" || value == "~")
                            value = null;
                    }
                }
                return value;
            }

            internal static void ReadHmclDirectories(JObject json, string selected, HashSet<string> ids, Action<string, string, bool> emit)
            {
                JArray directories = json["directories"] as JArray;
                if (directories is null)
                    throw new InvalidDataException("HMCL 目录列表缺失。");
                if (directories.Count > 2048)
                    throw new InvalidDataException("HMCL 目录条目过多。");
                var schema = json["$schema"];
                if (schema is not null && (schema.Type != JTokenType.String || !Regex.IsMatch((string)schema, @"\Ahttps://schemas\.glavo\.site/hmcl/game-directories/1\.\d+(?:\.\d+)?\z")))
                    throw new InvalidDataException("HMCL 目录配置版本暂不支持。");
                foreach (var item in directories.OfType<JObject>())
                {
                    if ((item["path"]?.Type) is { } arg2 && arg2 != JTokenType.String)
                        continue;
                    string id = (item["id"]?.Type) is { } arg3 && arg3 == JTokenType.String ? (string)item["id"] : null;
                    if (id is not null && !ids.Add(id))
                        continue;
                    string label = (item["name"]?.Type) is { } arg4 && arg4 == JTokenType.String ? (string)item["name"] : "HMCL 游戏目录";
                    emit((string)item["path"], label, selected is not null && (id ?? "") == (selected ?? ""));
                }
            }

            internal static void ReadHmclProfiles(JObject json, Action<string, string, bool> emit)
            {
                if (((JObject)json["configurations"]).Count > 2048)
                    throw new InvalidDataException("HMCL 目录条目过多。");
                string selected = (string)json["last"];
                foreach (var item in ((JObject)json["configurations"]).Properties())
                {
                    JObject profile = item.Value as JObject;
                    if (profile is null || ((profile["gameDir"]?.Type) is { } arg1 ? arg1 != JTokenType.String : (bool?)null).GetValueOrDefault())
                        continue;
                    emit((string)profile["gameDir"], item.Name, (item.Name ?? "") == (selected ?? ""));
                }
            }

            internal static string CeYamlSelection(string content)
            {
                string version = ReadYamlScalar(content, "LocalFileVersion");
                if (version is not null && version != "1")
                    throw new InvalidDataException("PCL-CE 本地配置版本暂不支持。");
                return ReadYamlScalar(content, "LaunchFolderSelect");
            }

            internal static string CeJsonSelection(string content)
            {
                var json = ParseConfiguration(content);
                if (json["LocalFileVersion"] is not null && (string)json["LocalFileVersion"] != "1")
                    throw new InvalidDataException("PCL-CE 本地配置版本暂不支持。");
                return (string)json["LaunchFolderSelect"];
            }

            internal static void ReadCeShared(JObject json, Action<string, string> emit)
            {
                if (json["FileVersion"] is not null && (string)json["FileVersion"] != "1")
                    throw new InvalidDataException("PCL-CE 共享配置版本暂不支持。");
                string raw = (string)json["LaunchFolders"];
                if (raw is not null)
                {
                    if (raw.Split('|').Length > 2048)
                        throw new InvalidDataException("PCL-CE 目录条目过多。");
                    foreach (var item in raw.Split('|'))
                    {
                        int split = item.IndexOf('>');
                        if (split < 1)
                            continue;
                        emit(item.Substring(split + 1), item.Substring(0, split));
                    }
                }
            }
        }
    }
}
