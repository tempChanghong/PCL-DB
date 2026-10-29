using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{
    public sealed partial class LocalModCatalog
    {
        // Text-only interpretation. ZIP/stream I/O, cancellation, limits, icon
        // bytes and LocalModEntry publication remain in LocalModCatalog.
        private static class MetadataParser
        {
            private static string IconPath(JToken value)
            {
                if ((value?.Type) is { } arg1 && arg1 == JTokenType.String)
                    return (string)value;
                JObject sizes = value as JObject;
                if (sizes is null)
                    return null;
                var candidates = new List<KeyValuePair<int, string>>();
                foreach (var item in sizes.Properties())
                {
                    int size;
                    if (int.TryParse(item.Name, out size) && size > 0 && item.Value.Type == JTokenType.String)
                        candidates.Add(new KeyValuePair<int, string>(size, (string)item.Value));
                }
                return candidates.OrderBy(item => item.Key >= 64 ? 0 : 1).ThenBy(item => Math.Abs(item.Key - 64L)).Select(item => item.Value).FirstOrDefault();
            }
            internal static JObject ParseObject(string text)
            {
                return JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }
            private static string Scalar(JToken value, bool required = false)
            {
                if (value is null || value.Type == JTokenType.Null)
                {
                    if (required)
                        throw new InvalidDataException("缺少 Mod 标识。");
                    return null;
                }
                if (value.Type != JTokenType.String)
                    throw new InvalidDataException("Mod 文本字段类型无效。");
                string text = (string)value;
                if (required && string.IsNullOrWhiteSpace(text))
                    throw new InvalidDataException("Mod 标识为空。");
                return text;
            }
            internal static Identity ReadFabric(JObject json)
            {
                return new Identity() { Id = Scalar(json["id"], true), Name = Scalar(json["name"]), Version = Scalar(json["version"]), Description = Scalar(json["description"]), Loader = "Fabric", Icon = IconPath(json["icon"]) };
            }
            internal static Identity ReadQuilt(JObject json)
            {
                JObject loader = json["quilt_loader"] as JObject;
                if (loader is null)
                    throw new InvalidDataException("缺少 Quilt 加载器信息。");
                JObject metadata = loader["metadata"] as JObject;
                return new Identity() { Id = Scalar(loader["id"], true), Name = Scalar(metadata?["name"]), Version = Scalar(loader["version"]), Description = Scalar(metadata?["description"]), Loader = "Quilt", Icon = IconPath(metadata?["icon"]) };
            }
            internal static Identity ReadLite(JObject json)
            {
                return new Identity() { Id = Scalar(json["name"], true), Name = Scalar(json["displayName"]) ?? Scalar(json["name"]), Version = Scalar(json["version"]), Description = Scalar(json["description"]), Loader = "LiteLoader", Icon = IconPath(json["logoFile"]) };
            }
            internal static IEnumerable<Identity> ReadLegacy(string text)
            {
                var json = JToken.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                JArray items = json as JArray;
                if (items is null && json is JObject)
                    items = json["modList"] as JArray;
                if (items is null || items.Count == 0 || items.Any(item => !(item is JObject)))
                    throw new InvalidDataException("旧版 Mod 信息列表无效。");
                return items.OfType<JObject>().Select(item => new Identity() { Id = Scalar(item["modid"], true), Name = Scalar(item["name"]), Version = Scalar(item["version"]), Description = Scalar(item["description"]), Loader = "Forge", Icon = IconPath(item["logoFile"]) }).ToList();
            }
            internal static string ResolveVersion(string value, Dictionary<string, string> manifest)
            {
                if (value == "${file.jarVersion}")
                {
                    string resolved = null;
                    if (manifest.TryGetValue("Implementation-Version", out resolved))
                        return resolved;
                    return null;
                }
                return value;
            }
            internal static Dictionary<string, string> ReadManifest(string text)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string key = null;
                foreach (var line in text.Replace(Constants.vbCrLf, Constants.vbLf).Replace(Constants.vbCr, Constants.vbLf).Split('\n'))
                {
                    if (line.Length == 0)
                        break; // Main attributes, never a named entry section.
                    if (line.StartsWith(" ", StringComparison.Ordinal))
                    {
                        if (key is null)
                            throw new InvalidDataException("JAR 清单续行缺少字段。");
                        result[key] += line.Substring(1);
                    }
                    else
                    {
                        int colon = line.IndexOf(':');
                        if (colon <= 0)
                            throw new InvalidDataException("JAR 清单字段无效。");
                        key = line.Substring(0, colon);
                        if (result.ContainsKey(key))
                            throw new InvalidDataException("JAR 清单字段重复。");
                        result.Add(key, line.Substring(colon + 1).TrimStart(' '));
                    }
                }
                return result;
            }

            // Bounded TOML scalar/table reader used only for [[mods]] metadata. Values in
            // other tables are lexed (so strings/comments cannot fabricate a mods table)
            // but are never interpreted as dependency or compatibility declarations.
            private sealed class LocalModToml
            {
                internal static List<Identity> Read(string text, string loader)
                {
                    var result = new List<Identity>();
                    Dictionary<string, string> current = null;
                    foreach (var statement in Statements(text))
                    {
                        string line = statement.Trim();
                        if (line.Length == 0)
                            continue;
                        if (line.StartsWith("[", StringComparison.Ordinal))
                        {
                            if (current is not null)
                                result.Add(ToIdentity(current, loader));
                            current = null;
                            string table = line.Trim('[', ']').Trim();
                            if (table == "mods" || table == "\"mods\"" || table == "'mods'")
                                current = new Dictionary<string, string>(StringComparer.Ordinal);
                        }
                        else if (current is not null)
                        {
                            int @equals = line.IndexOf('=');
                            if (equals < 1)
                                throw new InvalidDataException("Mod TOML 字段无效。");
                            string key = line.Substring(0, equals).Trim();
                            if (key.StartsWith("\"", StringComparison.Ordinal) || key.StartsWith("'", StringComparison.Ordinal))
                                key = StringValue(key);
                            if (!new[] { "modId", "displayName", "version", "description", "logoFile" }.Contains(key, StringComparer.Ordinal))
                                continue;
                            if (key == "logoFile")
                            {
                                try
                                {
                                    if (current.ContainsKey(key))
                                        current[key] = null;
                                    else
                                        current.Add(key, StringValue(line.Substring(equals + 1).Trim()));
                                }
                                catch (InvalidDataException)
                                {
                                    current[key] = null;
                                }
                                continue;
                            }
                            if (current.ContainsKey(key))
                                throw new InvalidDataException("Mod TOML 字段重复：" + key);
                            current.Add(key, StringValue(line.Substring(equals + 1).Trim()));
                        }
                    }
                    if (current is not null)
                        result.Add(ToIdentity(current, loader));
                    if (result.Count == 0)
                        throw new InvalidDataException("TOML 中没有有效的 mods 信息。");
                    return result;
                }
                private static Identity ToIdentity(Dictionary<string, string> values, string loader)
                {
                    string id = null;
                    string name = null;
                    string version = null;
                    string description = null;
                    string icon = null;
                    if (!values.TryGetValue("modId", out id) || string.IsNullOrWhiteSpace(id))
                        throw new InvalidDataException("TOML 中缺少 Mod ID。");
                    values.TryGetValue("displayName", out name);
                    values.TryGetValue("version", out version);
                    values.TryGetValue("description", out description);
                    values.TryGetValue("logoFile", out icon);
                    return new Identity() { Id = id, Name = name, Version = version, Description = description, Loader = loader, Icon = icon };
                }
                private static List<string> Statements(string text)
                {
                    var result = new List<string>();
                    var buffer = new StringBuilder();
                    char quote = '\0';
                    bool triple = false;
                    bool comment = false;
                    bool escaped = false;
                    int depth = 0;
                    int i = 0;
                    while (i < text.Length)
                    {
                        char ch = text[i];
                        if (comment)
                        {
                            if (ch != '\n' && ch != '\r')
                            {
                                i += 1;
                                continue;
                            }
                            comment = false;
                        }
                        if (quote != '\0')
                        {
                            if (escaped)
                            {
                                buffer.Append(ch);
                                escaped = false;
                            }
                            else if (quote == '"' && ch == '\\')
                            {
                                buffer.Append(ch);
                                escaped = true;
                            }
                            else if (ch == quote && (!triple || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                            {
                                int quoteCount = 1;
                                if (triple)
                                {
                                    quoteCount = 3;
                                    while (i + quoteCount < text.Length && text[i + quoteCount] == quote)
                                        quoteCount += 1;
                                    // TOML permits one or two literal quotes immediately
                                    // before a multiline string's three closing quotes.
                                    if (quoteCount > 5)
                                        throw new InvalidDataException("TOML 多行字符串引号无效。");
                                }
                                buffer.Append(ch, quoteCount);
                                i += quoteCount - 1;
                                quote = '\0';
                                triple = false;
                            }
                            else
                            {
                                if (!triple && (ch == '\n' || ch == '\r'))
                                    throw new InvalidDataException("TOML 单行字符串未结束。");
                                buffer.Append(ch);
                            }
                        }
                        else if (ch == '#')
                        {
                            comment = true;
                        }
                        else if (ch == '"' || ch == '\'')
                        {
                            quote = ch;
                            triple = i + 2 < text.Length && text[i + 1] == ch && text[i + 2] == ch;
                            buffer.Append(ch);
                            if (triple)
                            {
                                buffer.Append(ch).Append(ch);
                                i += 2;
                            }
                        }
                        else if (ch == '\n' || ch == '\r')
                        {
                            if (depth == 0)
                            {
                                result.Add(buffer.ToString());
                                buffer.Clear();
                            }
                            else
                            {
                                buffer.Append(ch);
                            }
                        }
                        else
                        {
                            if (ch == '[' || ch == '{')
                                depth += 1;
                            if (ch == ']' || ch == '}')
                                depth -= 1;
                            if (depth < 0)
                                throw new InvalidDataException("TOML 括号无效。");
                            buffer.Append(ch);
                        }
                        i += 1;
                    }
                    if (quote != '\0' || depth != 0)
                        throw new InvalidDataException("TOML 字符串或数组未结束。");
                    if (buffer.Length > 0)
                        result.Add(buffer.ToString());
                    return result;
                }
                private static string StringValue(string value)
                {
                    if (value.Length < 2)
                        throw new InvalidDataException("TOML Mod 信息必须是字符串。");
                    char quote = value[0];
                    bool triple = value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal);
                    if (quote != '"' && quote != '\'')
                        throw new InvalidDataException("TOML Mod 信息必须是字符串。");
                    int edge = triple ? 3 : 1;
                    if (value.Length < edge * 2 || (value.Substring(value.Length - edge) ?? "") != (new string(quote, edge) ?? ""))
                        throw new InvalidDataException("TOML 字符串未结束。");
                    string inner = value.Substring(edge, value.Length - edge * 2).Replace(Constants.vbCrLf, Constants.vbLf).Replace(Constants.vbCr, Constants.vbLf);
                    if (triple && inner.StartsWith(Constants.vbLf, StringComparison.Ordinal))
                        inner = inner.Substring(1);
                    if (quote == '\'')
                        return inner;
                    var result = new StringBuilder();
                    int i = 0;
                    while (i < inner.Length)
                    {
                        char ch = inner[i];
                        if (ch != '\\')
                        {
                            result.Append(ch);
                        }
                        else
                        {
                            i += 1;
                            if (i >= inner.Length)
                                throw new InvalidDataException("TOML 转义未结束。");
                            switch (inner[i])
                            {
                                case 'b':
                                    {
                                        result.Append('\b');
                                        break;
                                    }
                                case 't':
                                    {
                                        result.Append('\t');
                                        break;
                                    }
                                case 'n':
                                    {
                                        result.Append(Constants.vbLf);
                                        break;
                                    }
                                case 'f':
                                    {
                                        result.Append('\f');
                                        break;
                                    }
                                case 'r':
                                    {
                                        result.Append(Constants.vbCr);
                                        break;
                                    }
                                case '"':
                                    {
                                        result.Append('"');
                                        break;
                                    }
                                case '\\':
                                    {
                                        result.Append('\\');
                                        break;
                                    }
                                case 'u':
                                case 'U':
                                    {
                                        int count = inner[i] == 'u' ? 4 : 8;
                                        if (i + count >= inner.Length)
                                            throw new InvalidDataException("TOML Unicode 转义不完整。");
                                        int point;
                                        if (!int.TryParse(inner.Substring(i + 1, count), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out point))
                                            throw new InvalidDataException("TOML Unicode 转义无效。");
                                        if (point < 0 || point > 0x10FFFF || point >= 0xD800 && point <= 0xDFFF)
                                            throw new InvalidDataException("TOML Unicode 码点无效。");
                                        result.Append(char.ConvertFromUtf32(point));
                                        i += count;
                                        break;
                                    }

                                case '\n':
                                case '\r':
                                case ' ':
                                case '\t':
                                    {
                                        if (!triple)
                                            throw new InvalidDataException("TOML 单行字符串包含换行。");
                                        // A continuation backslash may be followed by spaces
                                        // or tabs, but must reach a newline before other text.
                                        bool hasNewline = inner[i] == '\n' || inner[i] == '\r';
                                        while (i + 1 < inner.Length && (inner[i + 1] == ' ' || inner[i + 1] == '\t' || inner[i + 1] == '\n' || inner[i + 1] == '\r'))
                                        {
                                            i += 1;
                                            hasNewline = hasNewline || inner[i] == '\n' || inner[i] == '\r';
                                        }
                                        if (!hasNewline)
                                            throw new InvalidDataException("TOML 字符串转义无效。");
                                        break;
                                    }

                                default:
                                    {
                                        throw new InvalidDataException("TOML 字符串转义无效。");
                                    }
                            }
                        }
                        i += 1;
                    }
                    return result.ToString();
                }
            }
            internal static List<Identity> ReadToml(string text, string loader)
            {
                return LocalModToml.Read(text, loader);
            }
        }
    }
}
