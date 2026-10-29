using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public enum ExternalLauncherKind
    {
        Pcl,
        Hmcl,
        PclCe
    }

    // Directory discovery is read-only. Paths are resolved against an explicitly
    // bound workspace, never against this process's current working directory.
    public sealed class ExternalLauncherBinding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public ExternalLauncherKind Kind { get; set; }
        public string Workspace { get; set; }
        public string ConfigPath { get; set; }
        public string SharedConfigPath { get; set; }
        public string DisplayName
        {
            get
            {
                return Kind == ExternalLauncherKind.Pcl ? "PCL" : Kind == ExternalLauncherKind.Hmcl ? "HMCL" : "PCL-CE";
            }
        }
        public override string ToString()
        {
            return Workspace + (string.IsNullOrEmpty(ConfigPath) ? "" : " · " + ConfigPath) + (string.IsNullOrEmpty(SharedConfigPath) ? "" : " · " + SharedConfigPath);
        }
        public ExternalLauncherBinding Copy()
        {
            return new ExternalLauncherBinding() { Id = Id, Kind = Kind, Workspace = Workspace, ConfigPath = ConfigPath, SharedConfigPath = SharedConfigPath };
        }
        public JObject ToJson()
        {
            return new JObject() { { "kind", Kind.ToString() }, { "workspace", Workspace }, { "configPath", ConfigPath ?? "" }, { "sharedConfigPath", SharedConfigPath ?? "" } };
        }
        public static ExternalLauncherBinding FromJson(string id, JObject value)
        {
            ExternalLauncherKind kind;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || value is null || !Enum.TryParse((string)value["kind"], true, out kind) || !Enum.IsDefined(typeof(ExternalLauncherKind), kind))
                throw new InvalidDataException("启动器绑定类型无效。");
            var result = new ExternalLauncherBinding()
            {
                Id = id,
                Kind = kind,
                Workspace = LauncherDirectoryReader.AbsolutePath((string)value["workspace"], null),
                ConfigPath = OptionalPath((string)value["configPath"]),
                SharedConfigPath = OptionalPath((string)value["sharedConfigPath"])
            };
            result.ValidatePclConfiguration();
            return result;
        }
        public void ValidatePclConfiguration()
        {
            if (Kind == ExternalLauncherKind.Pcl && ConfigPath is not null && !string.Equals(LauncherDirectoryReader.AbsolutePath(ConfigPath, null), LauncherDirectoryReader.AbsolutePath(Path.Combine(Workspace, "PCL/Setup.ini"), null), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PCL 配置与工作目录不匹配，请选择该位置下的 PCL/Setup.ini。");
            }
        }
        private static string OptionalPath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : LauncherDirectoryReader.AbsolutePath(value, null);
        }
    }

    public sealed class LauncherDirectoryResult
    {
        public readonly List<GameFolder> Folders = new List<GameFolder>();
        public readonly List<string> Warnings = new List<string>();
        public bool Succeeded { get; set; }
    }

    public sealed class LauncherDirectoryReader
    {
        public static string AbsolutePath(string value, string workspace)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || value.Contains("://"))
                throw new InvalidDataException("目录路径无效。");
            if (value.Length >= 2 && value[1] == ':' && (value.Length < 3 || value[2] != '\\' && value[2] != '/'))
                throw new InvalidDataException("不接受依赖当前盘符目录的路径。");
            if (!Path.IsPathRooted(value))
            {
                if (workspace is null)
                    throw new InvalidDataException("启动器位置必须为绝对路径。");
                value = Path.Combine(workspace, value);
            }
            if (Path.GetPathRoot(value).Length == 1)
                throw new InvalidDataException("不接受依赖当前盘符的根相对路径。");
            string full = Path.GetFullPath(value);
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd('\\', '/');
        }

        public LauncherDirectoryResult Read(ExternalLauncherBinding binding, bool readUserConfiguration = true)
        {
            var result = new LauncherDirectoryResult();
            try
            {
                var source = binding.Copy();
                source.Workspace = AbsolutePath(source.Workspace, null);
                if (!Directory.Exists(source.Workspace))
                    throw new DirectoryNotFoundException("启动器工作目录不存在。");
                if (source.Kind == ExternalLauncherKind.Pcl)
                {
                    source.ValidatePclConfiguration();
                    var reader = new PclFolders();
                    if (source.ConfigPath is not null && !File.Exists(source.ConfigPath))
                        throw new FileNotFoundException("绑定的 PCL 配置不存在。");
                    result.Folders.AddRange(reader.Discover(source.Workspace, readUserConfiguration, source.ConfigPath));
                    result.Warnings.AddRange(reader.Warnings);
                }
                else if (source.Kind == ExternalLauncherKind.Hmcl)
                {
                    ReadHmcl(source, result, readUserConfiguration);
                }
                else if (source.Kind == ExternalLauncherKind.PclCe)
                {
                    ReadCe(source, result, readUserConfiguration);
                }
                else
                {
                    throw new InvalidDataException("未知的启动器类型。");
                }
                foreach (var folder in result.Folders)
                {
                    folder.LauncherBindingId = source.Id;
                    folder.LauncherKind = source.Kind;
                    folder.LauncherWorkspace = source.Workspace;
                    folder.LauncherConfigPath = source.ConfigPath;
                    folder.LauncherSharedConfigPath = source.SharedConfigPath;
                    folder.Origin = source.DisplayName + " · " + source.Workspace + " · " + folder.Origin;
                }
                result.Succeeded = true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is Newtonsoft.Json.JsonException || ex is System.Security.SecurityException || ex is InvalidCastException || ex is FormatException)
            {
                result.Warnings.Add("读取启动器目录失败：" + ex.Message);
            }
            return result;
        }

        private static JObject ParseConfiguration(string text)
        {
            // Match install-time configuration snapshots: ambiguous duplicate keys
            // must not silently choose a different game directory.
            return JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }

        private static string ReadText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("目录配置超过大小限制。");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var chars = new char[2097153];
                    int count = 0;
                    while (count < chars.Length)
                    {
                        int read = reader.Read(chars, count, chars.Length - count);
                        if (read == 0)
                            break;
                        count += read;
                    }
                    if (count == chars.Length)
                        throw new InvalidDataException("目录配置超过大小限制。");
                    return new string(chars, 0, count);
                }
            }
        }

        private static string FirstFile(params string[] paths)
        {
            return paths.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
        }

        private static void Add(LauncherDirectoryResult result, ExternalLauncherBinding binding, string raw, string name, string origin, bool selected = false, bool warnMissing = true)
        {
            try
            {
                string full = AbsolutePath(raw, binding.Workspace);
                if (!Directory.Exists(full))
                {
                    if (warnMissing)
                        result.Warnings.Add("已跳过不存在的目录：" + full);
                    return;
                }
                var prior = result.Folders.FirstOrDefault(folder => string.Equals(folder.Location, full, StringComparison.OrdinalIgnoreCase));
                if (prior is null)
                {
                    result.Folders.Add(new GameFolder() { Name = string.IsNullOrWhiteSpace(name) ? binding.DisplayName + " 游戏目录" : name, Location = full, Origin = origin, IsSelected = selected });
                }
                else
                {
                    prior.IsSelected = prior.IsSelected || selected;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("忽略无效的游戏目录。");
            }
        }

        private static void ReadHmcl(ExternalLauncherBinding binding, LauncherDirectoryResult result, bool user)
        {
            string work = binding.Workspace;
            string primary = binding.ConfigPath ?? FirstFile(Path.Combine(work, ".hmcl/config/game-directories.json"), Path.Combine(work, ".hmcl/hmcl.json"), Path.Combine(work, "hmcl.json"), Path.Combine(work, ".hmcl.json"));
            string selectedId = null;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string localSettings = primary is not null && Path.GetFileName(primary).Equals("game-directories.json", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Path.GetDirectoryName(primary), "launcher-settings.json") : Path.Combine(work, ".hmcl/config/launcher-settings.json");
            if (File.Exists(localSettings))
            {
                try
                {
                    selectedId = (string)ParseConfiguration(ReadText(localSettings))["selectedGameDirectory"];
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
                {
                    result.Warnings.Add("无法读取 HMCL 当前目录选择。");
                }
            }
            if (primary is not null)
            {
                var json = ParseConfiguration(ReadText(primary));
                if (json["directories"] is JArray)
                {
                    ReadHmclDirectories(json, binding, result, primary, selectedId, ids);
                }
                else if (json["configurations"] is JObject)
                {
                    if (((JObject)json["configurations"]).Count > 2048)
                        throw new InvalidDataException("HMCL 目录条目过多。");
                    string selected = (string)json["last"];
                    foreach (var item in ((JObject)json["configurations"]).Properties())
                    {
                        JObject profile = item.Value as JObject;
                        if (profile is null || ((profile["gameDir"]?.Type) is { } arg1 ? arg1 != JTokenType.String : (bool?)null).GetValueOrDefault())
                            continue;
                        Add(result, binding, (string)profile["gameDir"], item.Name, primary, (item.Name ?? "") == (selected ?? ""));
                    }
                }
                else
                {
                    throw new InvalidDataException("无法识别 HMCL 目录配置格式。");
                }
            }
            string sharedFile = binding.SharedConfigPath;
            if (sharedFile is not null && !File.Exists(sharedFile))
                result.Warnings.Add("绑定的 HMCL 共享目录配置不存在，继续保留本地目录。");
            if (string.IsNullOrEmpty(sharedFile) && user)
                sharedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".hmcl/config/user-game-directories.json");
            if (!string.IsNullOrEmpty(sharedFile) && File.Exists(sharedFile))
            {
                try
                {
                    ReadHmclDirectories(ParseConfiguration(ReadText(sharedFile)), binding, result, sharedFile, selectedId, ids);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
                {
                    result.Warnings.Add("HMCL 共享目录配置读取失败：" + ex.Message);
                }
            }
            Add(result, binding, ".minecraft", "HMCL 默认游戏目录", "工作目录", selected: selectedId == "7105bc1f-490e-5e8c-878c-f5844c3d4bc3", warnMissing: false);
            if (user)
                Add(result, binding, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"), "HMCL 用户游戏目录", "用户目录", selected: selectedId == "f3eafde8-506e-5a77-bc88-f24b4728dfb2", warnMissing: false);
        }

        private static void ReadHmclDirectories(JObject json, ExternalLauncherBinding binding, LauncherDirectoryResult result, string origin, string selected, HashSet<string> ids)
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
                Add(result, binding, (string)item["path"], label, origin, selected is not null && (id ?? "") == (selected ?? ""));
            }
        }

        private static void ReadCe(ExternalLauncherBinding binding, LauncherDirectoryResult result, bool user)
        {
            string work = binding.Workspace;
            string selected = null;
            string primary = binding.ConfigPath ?? FirstFile(Path.Combine(work, "PCL/config.v1.yml"), Path.Combine(work, "PCL/config.v1.json"), Path.Combine(work, "PCL/Setup.ini"));
            if (primary is not null)
            {
                string content = ReadText(primary);
                switch (Path.GetExtension(primary).ToLowerInvariant() ?? "")
                {
                    case ".yml":
                    case ".yaml":
                        {
                            string version = ReadYamlScalar(content, "LocalFileVersion");
                            if (version is not null && version != "1")
                                throw new InvalidDataException("PCL-CE 本地配置版本暂不支持。");
                            selected = ReadYamlScalar(content, "LaunchFolderSelect");
                            break;
                        }
                    case ".json":
                        {
                            var json = ParseConfiguration(content);
                            if (json["LocalFileVersion"] is not null && (string)json["LocalFileVersion"] != "1")
                                throw new InvalidDataException("PCL-CE 本地配置版本暂不支持。");
                            selected = (string)json["LaunchFolderSelect"];
                            break;
                        }
                    case ".ini":
                        {
                            selected = PclFolders.ReadIniValue(primary, "LaunchFolderSelect");
                            break;
                        }

                    default:
                        {
                            throw new InvalidDataException("不支持的 PCL-CE 配置格式。");
                        }
                }
            }
            string sharedFile = binding.SharedConfigPath;
            if (sharedFile is not null && !File.Exists(sharedFile))
                result.Warnings.Add("绑定的 PCL-CE 共享目录配置不存在，继续保留本地目录。");
            if (string.IsNullOrEmpty(sharedFile) && user)
            {
                string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                sharedFile = FirstFile(Path.Combine(app, "PCLCE/config.v1.json"), Path.Combine(app, "PCLCE/config.json"), Path.Combine(app, ".PCLCE/Config.json"));
            }
            if (sharedFile is not null && File.Exists(sharedFile))
            {
                try
                {
                    var json = ParseConfiguration(ReadText(sharedFile));
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
                            Add(result, binding, PclFolders.ExpandPath(item.Substring(split + 1), work), item.Substring(0, split), sharedFile);
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
                {
                    result.Warnings.Add("PCL-CE 共享目录配置读取失败：" + ex.Message);
                }
            }
            if (!string.IsNullOrWhiteSpace(selected))
            {
                string full = AbsolutePath(PclFolders.ExpandPath(selected, work), work);
                Add(result, binding, full, "PCL-CE 当前选择", primary, true);
                foreach (var folder in result.Folders)
                    folder.IsSelected = string.Equals(folder.Location, full, StringComparison.OrdinalIgnoreCase);
            }
            Add(result, binding, ".minecraft", "PCL-CE 默认游戏目录", "启动器目录", warnMissing: false);
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
    }

    // Immutable task context: discovery and preference changes cannot retarget work.
    public sealed class LauncherInstallTarget
    {
        public readonly string Root;
        public readonly string BindingId;
        public readonly ExternalLauncherKind Kind;
        public readonly string Workspace;
        public readonly string ConfigPath;
        public readonly string SharedConfigPath;
        public string PclDirectory
        {
            get
            {
                return Kind == ExternalLauncherKind.Pcl ? Workspace : null;
            }
        }
        public LauncherInstallTarget(GameFolder folder, string fallbackPcl)
        {
            if (folder is null)
                throw new ArgumentNullException(nameof(folder));
            Root = LauncherDirectoryReader.AbsolutePath(folder.Location, null);
            BindingId = folder.LauncherBindingId;
            Kind = folder.LauncherKind.GetValueOrDefault(ExternalLauncherKind.Pcl);
            Workspace = folder.LauncherWorkspace ?? fallbackPcl;
            ConfigPath = folder.LauncherConfigPath;
            SharedConfigPath = folder.LauncherSharedConfigPath;
        }
        public void ValidateDestination(string destination)
        {
            if (!Enum.IsDefined(typeof(ExternalLauncherKind), Kind) || !string.Equals(LauncherDirectoryReader.AbsolutePath(destination, null), Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装目录与冻结的启动器目标不一致。");
        }
    }

    // Stored inside the bridge's preferences only; the original object is retained
    // so future fields and unrecognized binding records survive a current save.
    public sealed class LauncherBindingsState
    {
        private JToken _original;
        public readonly List<ExternalLauncherBinding> Bindings = new List<ExternalLauncherBinding>();
        public readonly Dictionary<ExternalLauncherKind, string> SelectedRows = new Dictionary<ExternalLauncherKind, string>();
        public readonly List<string> Warnings = new List<string>();
        public string TargetBindingId { get; set; }
        public string TargetRoot { get; set; }
        public bool ReadOnlyFormat { get; set; }

        private static bool TryPreferenceText(JToken value, out string text)
        {
            try
            {
                text = (string)value;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
            {
                text = null;
                return false;
            }
        }

        public static LauncherBindingsState Restore(JObject settings, string fallbackPcl)
        {
            var result = new LauncherBindingsState();
            bool legacyRootValid = TryPreferenceText(settings?["gameRoot"], out string legacyRoot);
            result.TargetRoot = legacyRoot ?? "";
            if (!legacyRootValid)
                result.Warnings.Add("已忽略无效的游戏目录设置，请重新选择。");
            var raw = settings?["externalLaunchers"];
            if (raw is null)
            {
                if (!TryPreferenceText(settings?["pclBase"], out string workspace))
                {
                    result.TargetRoot = "";
                    result.Warnings.Add("已忽略无效的 PCL 位置，请重新选择游戏目录。");
                }
                var binding = new ExternalLauncherBinding()
                {
                    Id = "legacy-pcl",
                    Kind = ExternalLauncherKind.Pcl,
                    Workspace = LauncherDirectoryReader.AbsolutePath(workspace ?? fallbackPcl, null)
                };
                result.Bindings.Add(binding);
                result.SelectedRows[binding.Kind] = binding.Id;
                result.TargetBindingId = binding.Id;
                return result;
            }
            result._original = raw.DeepClone();
            if (result._original is not JObject document || document["format"]?.Type != JTokenType.Integer ||
                document["format"].ToString() != "1" || document["bindings"] is not JObject)
            {
                result.ReadOnlyFormat = true;
                result.Warnings.Add("启动器绑定格式无法识别，已保留原设置，请手动选择游戏目录。");
                return result;
            }
            bool rootValid = TryPreferenceText(document["targetRoot"], out string targetRoot);
            bool bindingValid = TryPreferenceText(document["targetBindingId"], out string targetBinding);
            if (!rootValid || !bindingValid || targetRoot is null && !legacyRootValid)
            {
                // A corrupt half must not attach another launcher's saved root to this ID.
                result.TargetRoot = "";
                result.TargetBindingId = null;
                result.Warnings.Add("安装目标信息无效，请重新选择游戏目录。");
            }
            else
            {
                result.TargetRoot = targetRoot ?? result.TargetRoot;
                result.TargetBindingId = targetBinding;
            }
            foreach (var @record in ((JObject)result._original["bindings"]).Properties())
            {
                try
                {
                    result.Bindings.Add(ExternalLauncherBinding.FromJson(@record.Name, @record.Value as JObject));
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
                {
                    result.Warnings.Add("保留但跳过无效的启动器绑定：" + @record.Name);
                }
            }
            JObject rows = document["selectedRows"] as JObject;
            if (document["selectedRows"] is not null && document["selectedRows"].Type != JTokenType.Null && rows is null)
                result.Warnings.Add("启动器选择信息无效，使用各类首个有效绑定。");
            foreach (ExternalLauncherKind kind in Enum.GetValues(typeof(ExternalLauncherKind)))
            {
                if (!TryPreferenceText(rows?[kind.ToString()], out string id))
                    result.Warnings.Add("已忽略无效的启动器选择：" + kind);
                var selected = result.Bindings.FirstOrDefault(binding => binding.Kind == kind && (binding.Id ?? "") == (id ?? ""));
                if (selected is null)
                    selected = result.Bindings.FirstOrDefault(binding => binding.Kind == kind);
                if (selected is not null)
                    result.SelectedRows[kind] = selected.Id;
            }
            return result;
        }

        public JToken ToJson()
        {
            if (ReadOnlyFormat)
                return (_original?.DeepClone()) ?? JValue.CreateNull();
            var result = _original?.DeepClone() as JObject ?? new JObject();
            result["format"] = 1;
            JObject records = result["bindings"] as JObject;
            if (records is null)
            {
                records = new JObject();
                result["bindings"] = records;
            }
            foreach (var binding in Bindings)
            {
                JObject item = records[binding.Id] as JObject;
                if (item is null)
                    item = new JObject();
                item.Merge(binding.ToJson());
                records[binding.Id] = item;
            }
            var rows = result["selectedRows"] as JObject ?? new JObject();
            foreach (var row in SelectedRows)
                rows[row.Key.ToString()] = row.Value;
            result["selectedRows"] = rows;
            result["targetBindingId"] = TargetBindingId ?? "";
            result["targetRoot"] = TargetRoot ?? "";
            return result;
        }
    }
}