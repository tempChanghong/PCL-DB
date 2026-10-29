using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModLaunch splits legacy arguments before replacing variables.
    // LaunchWrapper 1.5/1.6 use jopt-simple 4.5: known single-dash long options and
    // unambiguous abbreviations are options even after an unknown game argument.
    internal sealed class SupplementalLaunchRecipe
    {
        private const string LaunchClass = "net.minecraft.launchwrapper.Launch";
        private const string Vanilla = "net.minecraft.launchwrapper.VanillaTweaker";
        private const string Alpha = "net.minecraft.launchwrapper.AlphaVanillaTweaker";
        private static readonly string[] OptionNames = new[] { "gameDir", "assetsDir", "tweakClass", "version" };

        private class Recipe
        {
            internal string Version;
            internal string Sha1;
            internal long Size;
            internal string Tweaker;
        }

        private static Recipe ForVersion(string id)
        {
            switch (id ?? "")
            {
                case "c0.0.12a_03":
                case "1.0.0-rc1":
                case "inf-20100630-1":
                    {
                        return new Recipe() { Version = "1.6", Sha1 = "4ea0aca9c022a234ebaf14b51fb119055955fc9d", Size = 27583L, Tweaker = Alpha };
                    }
                // These archived Alpha profiles use 1.5 with an explicit Alpha tweaker.
                // Neither old_alpha classification nor the neighboring revision implies this recipe.
                case "a1.1.0-1":
                case "a1.0.17_03":
                case "a1.0.16_02":
                case "a1.0.14-1":
                case "a1.0.14-2":
                case "a1.0.13_01-1":
                case "a1.0.13_01-2":
                    {
                        return new Recipe() { Version = "1.5", Sha1 = "5150b9c2951f0fde987ce9c33496e26add1de224", Size = 27787L, Tweaker = Alpha };
                    }
            }
            if (SupplementalVersions.IsHistoricalSnapshot(id) || SupplementalVersions.IsHistoricalBeta(id))
                return VanillaRecipe();
            switch (id ?? "")
            {
                case "2.0_blue":
                case "2.0_red":
                case "2.0_purple":
                case "a1.1.1":
                case "a1.1.0-2":
                    {
                        return VanillaRecipe();
                    }

                default:
                    {
                        throw new InvalidDataException("该补充版本尚未配置经过验证的启动配方。");
                    }
            }
        }

        private static Recipe VanillaRecipe()
        {
            return new Recipe() { Version = "1.5", Sha1 = "5150b9c2951f0fde987ce9c33496e26add1de224", Size = 27787L, Tweaker = Vanilla };
        }

        private static string Text(JToken value)
        {
            return value is not null && value.Type == JTokenType.String ? (string)value : null;
        }

        private static void Reject(string @field, bool existing)
        {
            throw new InvalidDataException(existing ? "已有实例的启动配方不兼容（" + @field + "）。请先在 PCL 中确认，或使用新名称安装；不会改写已有设置。" : "补充版本启动配方无效（" + @field + "）。请刷新列表后重试。");
        }

        internal static void ValidateSource(string id, JObject profile)
        {
            ValidateArguments(ForVersion(id), profile, false);
        }

        internal static void ValidateExisting(string id, JObject profile)
        {
            // An installed JSON uses the user's instance name, not the catalogue ID.
            // Identity, clientVersion, libraries and index equality remain the installer's job.
            if (profile["inheritsFrom"] is not null || profile["jar"] is not null)
                Reject("继承版本或自定义 JAR", true);
            ValidateArguments(ForVersion(id), profile, true);
        }

        private static void ValidateArguments(Recipe expected, JObject profile, bool existing)
        {
            if ((Text(profile["mainClass"]) ?? "") != LaunchClass)
                Reject("mainClass", existing);
            // PCL appends arguments.game after minecraftArguments; it can override a
            // correct directory or add a second tweaker. Known legacy recipes use neither branch.
            if (profile.Property("arguments") is not null)
                Reject("混合的新旧 arguments", existing);
            string arguments = Text(profile["minecraftArguments"]);
            if (string.IsNullOrWhiteSpace(arguments) || arguments.Length > (existing ? 65536 : 4096))
                Reject("minecraftArguments 类型或长度", existing);
            var rawTokens = new List<string>();
            var tokens = Tokenize(arguments, existing, rawTokens);
            if (tokens.Count < 2 || tokens[0] != "${auth_player_name}" || !new[] { "${auth_session}", "${auth_access_token}", "${access_token}" }.Contains(tokens[1], StringComparer.Ordinal))
                Reject("玩家名或会话参数", existing);
            ValidateQuotedVariable(rawTokens[0], tokens[0], existing);
            ValidateQuotedVariable(rawTokens[1], tokens[1], existing);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var rawValues = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 2;
            while (i < tokens.Count)
            {
                string token = tokens[i];
                if (token == "--")
                    Reject("提前结束选项解析", existing);
                int prefix = token.StartsWith("--", StringComparison.Ordinal) ? 2 : token.StartsWith("-", StringComparison.Ordinal) ? 1 : 0;
                string key = token.Substring(prefix);
                string value = null;
                string rawValue = rawTokens[i];
                int @equals = key.IndexOf('=');
                if (equals >= 0)
                {
                    value = key.Substring(equals + 1);
                    key = key.Substring(0, equals);
                }
                string known = null;
                if (prefix > 0 && key.Length > 0)
                    known = OptionNames.FirstOrDefault(name => name.StartsWith(key, StringComparison.Ordinal));
                if (known is null)
                {
                    if (!existing)
                        Reject("未知的游戏参数", false);
                    // Unknown options do not consume the next token in jopt-simple.
                    // Scan it independently so -gameDir=... cannot hide as their value.
                    i += 1;
                    continue;
                }
                if (!existing && (prefix != 2 || (key ?? "") != (known ?? "") || known == "version"))
                    Reject("非标准选项 " + key, false);
                if (values.ContainsKey(known))
                    Reject("重复选项 " + known, existing);
                if (value is null)
                {
                    i += 1;
                    if (i >= tokens.Count)
                        Reject("缺少选项值 " + known, existing);
                    value = tokens[i];
                    rawValue = rawTokens[i];
                }
                if (string.IsNullOrWhiteSpace(value))
                    Reject("空选项值 " + known, existing);
                values.Add(known, value);
                rawValues.Add(known, rawValue);
                i += 1;
            }
            string game = null;
            string assets = null;
            string tweaker = null;
            if (!values.TryGetValue("gameDir", out game) || game != "${game_directory}")
                Reject("gameDir 目录变量", existing);
            if (!values.TryGetValue("assetsDir", out assets) || assets != "${game_assets}")
                Reject("assetsDir 目录变量", existing);
            ValidateQuotedVariable(rawValues["gameDir"], game, existing);
            ValidateQuotedVariable(rawValues["assetsDir"], assets, existing);
            if (!values.TryGetValue("tweakClass", out tweaker))
                tweaker = Vanilla;
            if ((tweaker ?? "") != (expected.Tweaker ?? ""))
                Reject("tweakClass", existing);
        }

        private static void ValidateQuotedVariable(string raw, string variable, bool existing)
        {
            // PCL only adds outer quotes when neither edge is already quoted. Empty
            // quote prefixes/suffixes can otherwise leave a substituted path unquoted.
            if (!raw.StartsWith("\"", StringComparison.Ordinal) && !raw.EndsWith("\"", StringComparison.Ordinal))
            {
                if (raw.Contains("\""))
                    Reject("变量中的歧义引号", existing);
                return;
            }
            int position = raw.IndexOf(variable, StringComparison.Ordinal);
            bool quoted = false;
            int i = 0;
            if (position < 0)
                Reject("变量引号", existing);
            while (i < position)
            {
                if (raw[i] == '\\' && i + 1 < position && raw[i + 1] == '"')
                {
                    i += 1;
                }
                else if (raw[i] == '"')
                {
                    quoted = !quoted;
                }
                i += 1;
            }
            if (!quoted)
                Reject("未包围变量的引号", existing);
        }

        private static List<string> Tokenize(string arguments, bool existing, List<string> rawTokens)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            var raw = new StringBuilder();
            bool quoted = false;
            bool started = false;
            int i = 0;
            while (i < arguments.Length)
            {
                char c = arguments[i];
                if (char.IsControl(c) && c != '\r' && c != '\n')
                    Reject("参数控制字符", existing);
                if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == '"')
                {
                    current.Append('"');
                    raw.Append(c).Append('"');
                    started = true;
                    i += 1;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                    raw.Append(c);
                    started = true;
                }
                else if ((c == ' ' || c == '\r' || c == '\n') && !quoted)
                {
                    AddToken(result, current, rawTokens, raw, ref started, existing);
                }
                else
                {
                    if (c == '\r' || c == '\n')
                        c = ' ';
                    current.Append(c);
                    raw.Append(c);
                    started = true;
                }
                i += 1;
            }
            if (quoted)
                Reject("未闭合的引号", existing);
            AddToken(result, current, rawTokens, raw, ref started, existing);
            return result;
        }

        private static void AddToken(List<string> tokens, StringBuilder current, List<string> rawTokens, StringBuilder raw, ref bool started, bool existing)
        {
            if (!started)
                return;
            if (tokens.Count >= (existing ? 1024 : 64))
                Reject("参数数量", existing);
            tokens.Add(current.ToString());
            rawTokens.Add(raw.ToString());
            current.Clear();
            raw.Clear();
            started = false;
        }

        internal static void ValidateLibraries(string id, JObject profile)
        {
            var expected = ForVersion(id);
            JArray libraries = profile["libraries"] as JArray;
            if (libraries is null)
                Reject("libraries", false);
            var wrappers = libraries.OfType<JObject>().Where(item => (Text(item["name"]) ?? "").StartsWith("net.minecraft:launchwrapper:", StringComparison.Ordinal)).ToList();
            if (wrappers.Count != 1)
                Reject("LaunchWrapper 数量", false);
            var wrapper = wrappers[0];
            JObject artifact = wrapper.SelectToken("downloads.artifact") as JObject;
            if ((Text(wrapper["name"]) ?? "") != ("net.minecraft:launchwrapper:" + expected.Version ?? "") || wrapper.Property("rules") is not null || wrapper.Property("natives") is not null || artifact is null)
                Reject("LaunchWrapper 声明", false);
            var sizeToken = artifact["size"];
            long size;
            if (sizeToken is null || sizeToken.Type != JTokenType.Integer && sizeToken.Type != JTokenType.String || !long.TryParse(sizeToken.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size != expected.Size || !string.Equals(Text(artifact["sha1"]), expected.Sha1, StringComparison.OrdinalIgnoreCase))
                Reject("LaunchWrapper 工件校验", false);
        }
    }
}