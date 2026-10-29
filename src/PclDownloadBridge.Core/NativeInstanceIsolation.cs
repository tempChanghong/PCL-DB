using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // HMCL HMCLGameRepository.computeRunDirectory / LegacyGameSettingsMigrator and
    // CE McInstance.PathIndie. Existing instance bytes are never initialized by repair.
    internal sealed partial class NativeInstanceIsolation : IInstanceIsolationPlan
    {
        private readonly LauncherInstallTarget _target;
        private readonly string _root;
        private readonly string _name;
        private readonly string _instance;
        private readonly InstanceConfigurationInputs _inputs;
        private readonly Dictionary<string, byte[]> _written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private bool? _folderSignal;
        private bool _hmclModern;
        private JObject _legacyGlobal;
        internal bool Isolated { get; private set; }
        bool IInstanceIsolationPlan.Isolated { get => Isolated; }
        internal string RunDirectory { get; private set; }
        internal IEnumerable<string> WrittenPaths
        {
            get
            {
                return _written.Keys;
            }
        }
        private NativeInstanceIsolation(LauncherInstallTarget target, string name, VersionEntry entry, JObject metadata, bool repair, bool components, InstanceIsolationMode mode, bool modable, int? strategy, InstanceConfigurationInputs inputs = null)
        {
            _inputs = inputs ?? new InstanceConfigurationInputs();
            _target = target;
            _root = target.Root;
            _name = name;
            _instance = SafeFiles.UnderRoot(_root, "versions/" + name);
            if (strategy.HasValue && (strategy.Value < 0 || strategy.Value > 4))
                throw new ArgumentOutOfRangeException(nameof(strategy));
            var choice = default(bool?);
            string custom = null;
            if (target.Kind == ExternalLauncherKind.PclCe)
            {
                var local = CeReader(target.ConfigPath ?? Path.Combine(target.Workspace, "PCL/config.v1.yml"));
                if (repair)
                {
                    var read = CeReader(Path.Combine(_instance, "PCL/config.v1.yml"));
                    string value = read("VersionArgumentIndieV2");
                    // CE counts API-classified instances as modable when inspecting
                    // an undecided existing instance. Installation Resolve keeps its
                    // established input semantics; this addition is read-only Inspect.
                    if (inputs is not null)
                        modable = modable || ConfigurationInterpreter.ParseInt(read("DisplayType"), 0, -1, 6) == 2;
                    if (value is not null)
                    {
                        choice = ConfigurationInterpreter.ParseBool(value);
                    }
                    else
                    {
                        int old = ConfigurationInterpreter.ParseInt(read("VersionArgumentIndie"), -1, -1, 2);
                        if (old > 0)
                        {
                            choice = old == 1;
                        }
                        else
                        {
                            _folderSignal = HasUserData();
                            if (_folderSignal.Value)
                                choice = true;
                        }
                    }
                }
                if (!choice.HasValue)
                {
                    bool nonRelease = entry.IsSupplemental || (string)metadata["type"] == "old_alpha" || (string)metadata["type"] == "old_beta" || !components && !string.Equals((string)metadata["type"], "release", StringComparison.OrdinalIgnoreCase);
                    choice = !repair && mode != InstanceIsolationMode.FollowPcl ? mode == InstanceIsolationMode.Enabled : ConfigurationInterpreter.StrategyValue(!repair && strategy.HasValue ? strategy.Value : ConfigurationInterpreter.ParseInt(local("LaunchArgumentIndieV2"), 4, 0, 4), modable, nonRelease);
                }
            }
            else if (target.Kind == ExternalLauncherKind.Hmcl)
            {
                string primary = target.ConfigPath;
                JObject newDirectories = null;
                if (primary is null)
                    newDirectories = ReadJson(Path.Combine(target.Workspace, ".hmcl/config/game-directories.json"));
                _hmclModern = primary is not null ? Path.GetFileName(primary).Equals("game-directories.json", StringComparison.OrdinalIgnoreCase) : newDirectories is not null;
                JObject globalSettings = null;
                JObject instanceSettings = null;
                if (_hmclModern)
                {
                    string configDirectory = primary is null ? Path.Combine(target.Workspace, ".hmcl/config") : Path.GetDirectoryName(primary);
                    if (primary is not null)
                        newDirectories = ReadJson(primary);
                    var launcher = ReadJson(Path.Combine(configDirectory, "launcher-settings.json"));
                    var presets = ReadJson(Path.Combine(configDirectory, "game-settings.json"));
                    ConfigurationInterpreter.CheckSchema(presets, "game-settings");
                    if (repair)
                    {
                        instanceSettings = ReadJson(Path.Combine(_instance, ".hmcl/config/instance-game-settings.json"));
                        ConfigurationInterpreter.CheckSchema(instanceSettings, "instance-game-settings");
                    }
                    JArray values = presets?["presets"] as JArray;
                    if (presets?["presets"] is not null && values is null)
                        throw new InvalidDataException("HMCL 预设列表格式无效。");
                    if (values is not null && values.OfType<JObject>().Select(item => ConfigurationInterpreter.Scalar(item["id"])).GroupBy(id => id).Any(@group => @group.Count() > 1))
                        throw new InvalidDataException("HMCL 预设标识不唯一。");
                    string parent = ConfigurationInterpreter.Scalar(instanceSettings?["parent"]);
                    string defaultParent = ConfigurationInterpreter.Scalar(launcher?["defaultGameSettingsPreset"]);
                    string legacyParent = null;
                    if (repair && instanceSettings is null)
                    {
                        string sharedPath = target.SharedConfigPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".hmcl/config/user-game-directories.json");
                        var sharedDirectories = ReadJson(sharedPath);
                        bool matchedParent = false;
                        JObject[] directorySources = new[] { newDirectories, sharedDirectories };
                        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var source in directorySources)
                        {
                            JArray directories = source?["directories"] as JArray;
                            if (directories is not null)
                            {
                                foreach (var item in directories.OfType<JObject>())
                                {
                                    string id = ConfigurationInterpreter.Scalar(item["id"]);
                                    if (id is not null && !seenIds.Add(id))
                                        continue;
                                    string directory = ConfigurationInterpreter.Scalar(item["path"]);
                                    if (directory is null || !Same(LauncherDirectoryReader.AbsolutePath(directory, target.Workspace), _root))
                                        continue;
                                    string value = ConfigurationInterpreter.Scalar(item["legacyGameSettings"]);
                                    if (matchedParent && (legacyParent ?? "") != (value ?? ""))
                                        throw new InvalidDataException("HMCL 同目录的历史设置来源不唯一。");
                                    legacyParent = value;
                                    matchedParent = true;
                                }
                            }
                        }
                        parent = legacyParent;
                    }
                    bool legacyPresetResolved = false;
                    if (values is not null)
                    {
                        globalSettings = values.OfType<JObject>().FirstOrDefault(item => (ConfigurationInterpreter.Scalar(item["id"]) ?? "") == (parent ?? "") && parent is not null);
                        legacyPresetResolved = legacyParent is not null && globalSettings is not null;
                        if (globalSettings is null)
                            globalSettings = values.OfType<JObject>().FirstOrDefault(item => (ConfigurationInterpreter.Scalar(item["id"]) ?? "") == (defaultParent ?? "") && defaultParent is not null);
                        if (globalSettings is null)
                            globalSettings = values.OfType<JObject>().FirstOrDefault();
                    }
                    if (repair)
                    {
                        if (instanceSettings is not null)
                        {
                            JArray overrideNames = instanceSettings["overrideProperties"] as JArray;
                            if (instanceSettings["overrideProperties"] is not null && overrideNames is null)
                                throw new InvalidDataException("HMCL 实例覆盖属性格式无效。");
                            if (overrideNames is not null && overrideNames.Values<string>().Contains("runningDirectory", StringComparer.Ordinal))
                            {
                                custom = ConfigurationInterpreter.Scalar(instanceSettings["runningDirectory"]);
                                choice = string.IsNullOrWhiteSpace(custom);
                            }
                        }
                        else
                        {
                            var old = ReadJson(Path.Combine(_instance, "hmclversion.cfg"));
                            if (old is not null && _inputs.Read(Path.Combine(_instance, ".hmcl/state/instance-game-settings.migration-receipt.json")) is not null)
                                throw new InvalidDataException("HMCL 旧配置迁移状态不明确，请先用 HMCL 保存实例设置后重试。");
                            if (old is not null && !ConfigurationInterpreter.BooleanValue(old["usesGlobal"], false))
                            {
                                int oldMode = ConfigurationInterpreter.HmclMode(old["gameDirType"]);
                                choice = oldMode == 1;
                                if (oldMode == 2)
                                {
                                    custom = ConfigurationInterpreter.Scalar(old["gameDir"]);
                                    choice = string.IsNullOrWhiteSpace(custom);
                                }
                            }
                            else if (old is not null && legacyPresetResolved && ConfigurationInterpreter.Scalar(globalSettings?["defaultIsolationType"]) == "ALWAYS" && string.IsNullOrWhiteSpace(ConfigurationInterpreter.Scalar(globalSettings?["runningDirectory"])))
                            {
                                choice = true;
                            }
                        }
                        // A modern instance inherits the preset running directory when
                        // it does not override that property; installation defaults do
                        // not retroactively isolate already installed vanilla versions.
                        if (!choice.HasValue)
                        {
                            choice = false;
                            custom = ConfigurationInterpreter.Scalar(globalSettings?["runningDirectory"]);
                        }
                    }
                    else
                    {
                        string policy = ConfigurationInterpreter.Scalar(globalSettings?["defaultIsolationType"]) ?? "MODDED";
                        if (strategy.HasValue || mode != InstanceIsolationMode.FollowPcl)
                            policy = "NEVER";
                        switch (policy ?? "")
                        {
                            case "ALWAYS":
                                {
                                    choice = true;
                                    break;
                                }
                            case "NEVER":
                                {
                                    choice = false;
                                    break;
                                }
                            case "MODDED":
                                {
                                    choice = modable;
                                    break;
                                }

                            default:
                                {
                                    throw new InvalidDataException("HMCL 默认隔离策略无法识别。");
                                }
                        }
                        if (!choice.Value)
                            custom = ConfigurationInterpreter.Scalar(globalSettings?["runningDirectory"]);
                    }
                }
                else
                {
                    string legacy = primary ?? FirstLegacyHmcl();
                    var config = legacy is null ? null : ReadJson(legacy);
                    JObject profiles = config?["configurations"] as JObject;
                    if (config is not null && profiles is null)
                        throw new InvalidDataException("HMCL 旧配置格式无法识别。");
                    if (profiles is not null)
                    {
                        bool matchedProfile = false;
                        foreach (var profile in profiles.Properties().Select(item => item.Value as JObject).Where(item => item is not null))
                        {
                            string directory = ConfigurationInterpreter.Scalar(profile["gameDir"]);
                            if (directory is null || !Same(LauncherDirectoryReader.AbsolutePath(directory, target.Workspace), _root))
                                continue;
                            JObject value = profile["global"] as JObject;
                            if (matchedProfile && !JToken.DeepEquals(globalSettings, value))
                                throw new InvalidDataException("多个 HMCL 配置使用同一目录但隔离规则不同，请明确选择配置后重试。");
                            globalSettings = value;
                            matchedProfile = true;
                        }
                    }
                    _legacyGlobal = globalSettings ?? new JObject();
                    var effective = globalSettings;
                    if (repair)
                    {
                        instanceSettings = ReadJson(Path.Combine(_instance, "hmclversion.cfg"));
                        if (instanceSettings is not null && !ConfigurationInterpreter.BooleanValue(instanceSettings["usesGlobal"], false))
                            effective = instanceSettings;
                    }
                    int directoryType = ConfigurationInterpreter.HmclMode(effective?["gameDirType"]);
                    choice = directoryType == 1;
                    if (directoryType == 2)
                        custom = ConfigurationInterpreter.Scalar(effective?["gameDir"]);
                }
                if (!repair && strategy.HasValue)
                {
                    choice = ConfigurationInterpreter.StrategyValue(strategy.Value, modable, entry.IsSupplemental || !string.Equals((string)metadata["type"], "release", StringComparison.OrdinalIgnoreCase));
                    custom = null;
                }
                if (repair && _inputs.Read(Path.Combine(_instance, "modpack.cfg")) is not null)
                {
                    choice = true;
                    custom = null;
                }
            }
            else
            {
                throw new ArgumentException("未知的原生隔离目标。");
            }
            if (!repair && mode != InstanceIsolationMode.FollowPcl)
            {
                choice = mode == InstanceIsolationMode.Enabled;
                custom = null;
            }
            string run = choice.GetValueOrDefault() ? _instance : string.IsNullOrWhiteSpace(custom) ? _root : LauncherDirectoryReader.AbsolutePath(custom, target.Workspace);
            RunDirectory = run;
            Isolated = Same(run, _instance);
        }

        internal static NativeInstanceIsolation Resolve(LauncherInstallTarget target, string name, VersionEntry entry, JObject metadata, bool repair, bool components, InstanceIsolationMode mode, bool modable, int? strategy)
        {
            return new NativeInstanceIsolation(target, name, entry, metadata, repair, components, mode, modable, strategy);
        }
        // Inspection shares the repair-time read rules and never calls WriteNewSettings.
        internal static NativeInstanceIsolation Inspect(LauncherInstallTarget target, string name, VersionEntry entry, JObject metadata, bool components, bool modable, InstanceConfigurationInputs inputs)
        {
            return new NativeInstanceIsolation(target, name, entry, metadata, true, components, InstanceIsolationMode.FollowPcl, modable, default, inputs);
        }
        internal void ValidateDataLocation(bool needsRunFiles)
        {
            if (needsRunFiles && !Same(RunDirectory, _root) && !Same(RunDirectory, _instance))
                throw new NotSupportedException("此实例使用自定义运行目录；为保留原设置，暂不向该目录自动写入 Mods 或旧资源。");
        }
        private string FirstLegacyHmcl()
        {
            foreach (var relative in new[] { ".hmcl/hmcl.json", "hmcl.json", ".hmcl.json" })
            {
                string full = Path.Combine(_target.Workspace, relative);
                if (_inputs.Read(full) is not null)
                    return full;
            }
            return null;
        }
        private JObject ReadJson(string full)
        {
            string text = InstanceConfigurationInputs.Text(_inputs.Read(full));
            if (text is null)
                return null;
            return JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        private Func<string, string> CeReader(string preferred)
        {
            string ext = Path.GetExtension(preferred).ToLowerInvariant();
            byte[] bytes = _inputs.Read(preferred);
            if (bytes is not null)
            {
                string text = InstanceConfigurationInputs.Text(bytes);
                if (ext == ".yml" || ext == ".yaml")
                    return new Func<string, string>(key => LauncherDirectoryReader.ReadYamlScalar(text, key));
                if (ext == ".json")
                {
                    var json = JObject.Parse(text, new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    return new Func<string, string>(key => ConfigurationInterpreter.Scalar(json[key]));
                }
                return new Func<string, string>(key => ConfigurationInterpreter.IniValue(text, key));
            }
            if (ext == ".yml" || ext == ".yaml")
            {
                string jsonPath = Path.ChangeExtension(preferred, ".json");
                var json = ReadJson(jsonPath);
                if (json is not null)
                    return new Func<string, string>(key => ConfigurationInterpreter.Scalar(json[key]));
                string ini = InstanceConfigurationInputs.Text(_inputs.Read(Path.Combine(Path.GetDirectoryName(preferred), "Setup.ini")));
                if (ini is not null)
                    return new Func<string, string>(key => ConfigurationInterpreter.IniValue(ini, key));
            }
            return new Func<string, string>(key => null);
        }
        private static bool Same(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        private bool HasUserData()
        {
            string mods = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" + _name + "/mods/entry"));
            string saves = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" + _name + "/saves/entry"));
            return Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any() || Directory.Exists(saves) && Directory.EnumerateDirectories(saves).Any();
        }

        public void WriteNewSettings(string stage)
        {
            if (_target.Kind == ExternalLauncherKind.PclCe)
            {
                string yaml = SafeFiles.UnderRoot(stage, "PCL/config.v1.yml");
                string jsonPath = SafeFiles.UnderRoot(stage, "PCL/config.v1.json");
                if (!File.Exists(yaml) && File.Exists(jsonPath))
                {
                    var json = JObject.Parse(StageText(jsonPath), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    json["VersionArgumentIndie"] = Isolated ? 1 : 2;
                    json["VersionArgumentIndieV2"] = Isolated;
                    Save(stage, "PCL/config.v1.json", Encoding.UTF8.GetBytes(json.ToString()));
                }
                else
                {
                    string text = StageText(yaml);
                    LauncherDirectoryReader.ReadYamlScalar(text, "VersionArgumentIndie");
                    LauncherDirectoryReader.ReadYamlScalar(text, "VersionArgumentIndieV2");
                    // Only the two isolation keys are replaced; every other archive value
                    // and comment remains in its original order.
                    var lines = text.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n').Where(line => !line.StartsWith("VersionArgumentIndie:", StringComparison.Ordinal) && !line.StartsWith("VersionArgumentIndieV2:", StringComparison.Ordinal));
                    Save(stage, "PCL/config.v1.yml", Encoding.UTF8.GetBytes(string.Join(Constants.vbLf, lines) + Constants.vbLf + "VersionArgumentIndie: " + (Isolated ? "1" : "2") + Constants.vbLf + "VersionArgumentIndieV2: " + (Isolated ? "true" : "false") + Constants.vbLf));
                }
                string iniPath = SafeFiles.UnderRoot(stage, "PCL/Setup.ini");
                string ini = StageText(iniPath);
                var iniLines = ini.Replace(Constants.vbCrLf, Constants.vbLf).Split('\n').Where(line => !Regex.IsMatch(line, @"^\s*VersionArgumentIndie(?:V2)?\s*:", RegexOptions.IgnoreCase));
                Save(stage, "PCL/Setup.ini", Encoding.UTF8.GetBytes(string.Join(Constants.vbCrLf, iniLines) + Constants.vbCrLf + "VersionArgumentIndie:" + (Isolated ? "1" : "2") + Constants.vbCrLf + "VersionArgumentIndieV2:" + Isolated.ToString() + Constants.vbCrLf));
            }
            else if (_hmclModern)
            {
                string relative = ".hmcl/config/instance-game-settings.json";
                string filePath = SafeFiles.UnderRoot(stage, relative);
                var json = File.Exists(filePath) ? JObject.Parse(StageText(filePath), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) : new JObject();
                ConfigurationInterpreter.CheckSchema(json, "instance-game-settings");
                json["$schema"] = "https://schemas.glavo.site/hmcl/instance-game-settings/1.0.1";
                JArray overrideNames = json["overrideProperties"] as JArray;
                if (json["overrideProperties"] is not null && overrideNames is null)
                    throw new InvalidDataException("整合包的 HMCL 覆盖属性无效。");
                if (overrideNames is null)
                    overrideNames = new JArray();
                if (!overrideNames.Values<string>().Contains("runningDirectory"))
                    overrideNames.Add("runningDirectory");
                json["overrideProperties"] = overrideNames;
                json["runningDirectory"] = Isolated ? "" : RunDirectory;
                Save(stage, relative, Encoding.UTF8.GetBytes(json.ToString()));
            }
            else
            {
                string relative = "hmclversion.cfg";
                string filePath = SafeFiles.UnderRoot(stage, relative);
                var json = File.Exists(filePath) ? JObject.Parse(StageText(filePath), new JsonLoadSettings() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) : (JObject)_legacyGlobal.DeepClone();
                json["usesGlobal"] = false;
                json["gameDirType"] = Isolated ? 1 : Same(RunDirectory, _root) ? 0 : 2;
                if (!Isolated && !Same(RunDirectory, _root))
                    json["gameDir"] = RunDirectory;
                Save(stage, relative, Encoding.UTF8.GetBytes(json.ToString()));
            }
        }
        private static string StageText(string full)
        {
            if (!File.Exists(full))
                return "";
            if (new FileInfo(full).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("整合包实例配置超过大小限制。");
            return File.ReadAllText(full);
        }
        private void Save(string stage, string relative, byte[] bytes)
        {
            string full = SafeFiles.UnderRoot(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllBytes(full, bytes);
            _written[relative] = bytes;
        }
        internal void ValidateStaged(string stage, CancellationToken token)
        {
            foreach (var item in _written)
            {
                token.ThrowIfCancellationRequested();
                string full = SafeFiles.UnderRoot(stage, item.Key);
                if (!File.ReadAllBytes(full).SequenceEqual(item.Value))
                    throw new InvalidDataException("原生实例配置在发布前发生变化，请重试。");
            }
        }
        public IInstanceConfigurationGuard LockForRepair()
        {
            return _inputs.Hold(() => { if (_folderSignal.HasValue && HasUserData() != _folderSignal.Value) throw new InvalidDataException("实例数据目录在补全期间发生变化，请重试。"); });
        }
    }
}