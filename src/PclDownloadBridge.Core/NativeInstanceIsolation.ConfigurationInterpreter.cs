using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{
    internal sealed partial class NativeInstanceIsolation
    {
        // Interpret supplied values only. Configuration discovery, snapshots, isolation
        // decisions, staged writes, path checks and publication guards stay in the plan.
        private static class ConfigurationInterpreter
        {
            internal static string IniValue(string text, string key)
            {
                string value = null;
                foreach (var line in text.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n'))
                {
                    int split = line.IndexOf(':');
                    if (split > 0 && (line.Substring(0, split) ?? "") == (key ?? ""))
                        value = line.Substring(split + 1).Trim();
                }
                return value;
            }
            internal static string Scalar(JToken value)
            {
                if (value is null || value.Type == JTokenType.Null)
                    return null;
                if (value.Type != JTokenType.String && value.Type != JTokenType.Boolean && value.Type != JTokenType.Integer)
                    throw new InvalidDataException("实例配置值类型无效。");
                return (string)value;
            }
            internal static bool ParseBool(string value)
            {
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
                    return true;
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0")
                    return false;
                throw new InvalidDataException("实例隔离开关无效。");
            }
            internal static bool BooleanValue(JToken value, bool fallback)
            {
                return value is null ? fallback : ParseBool(Scalar(value));
            }
            internal static int ParseInt(string value, int fallback, int minimum, int maximum)
            {
                if (value is null)
                    return fallback;
                int result;
                if (!int.TryParse(value, out result) || result < minimum || result > maximum)
                    throw new InvalidDataException("实例隔离策略无效。");
                return result;
            }
            internal static int HmclMode(JToken value)
            {
                string text = Scalar(value);
                switch (text ?? "")
                {
                    case var @case when @case == "":
                    case "ROOT_FOLDER":
                        {
                            return 0;
                        }
                    case "VERSION_FOLDER":
                        {
                            return 1;
                        }
                    case "CUSTOM":
                        {
                            return 2;
                        }

                    default:
                        {
                            return ParseInt(text, 0, 0, 2);
                        }
                }
            }
            internal static bool StrategyValue(int value, bool modable, bool nonRelease)
            {
                switch (value)
                {
                    case 0:
                        {
                            return false;
                        }
                    case 1:
                        {
                            return modable;
                        }
                    case 2:
                        {
                            return nonRelease;
                        }
                    case 3:
                        {
                            return modable || nonRelease;
                        }

                    default:
                        {
                            return true;
                        }
                }
            }
            internal static void CheckSchema(JObject json, string name)
            {
                if (json is null || json["$schema"] is null)
                    return;
                if (!Regex.IsMatch(Scalar(json["$schema"]), @"\Ahttps://schemas\.glavo\.site/hmcl/" + name + @"/1\.\d+(?:\.\d+)?\z"))
                    throw new InvalidDataException("HMCL 实例配置版本暂不支持。");
            }
        }
    }
}
