using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public enum InstanceIsolationMode
    {
        FollowPcl,
        Enabled,
        Disabled
    }

    // Read-only adaptation of pinned PCL McInstance.PathIndie. Existing preferences
    // are never initialized or migrated by a repair. New components, remapped assets
    // or explicit choices freeze their value with the private instance payload.
    internal sealed class PclIsolationPlan : IInstanceIsolationPlan
    {
        internal readonly bool Isolated;
        private bool PlanIsolated
        {
            get
            {
                return Isolated;
            }
        }

        bool IInstanceIsolationPlan.Isolated { get => PlanIsolated; }
        private void WritePlanSettings(string stage)
        {
            WriteNewSettings(stage);
        }

        void IInstanceIsolationPlan.WriteNewSettings(string stage) => WritePlanSettings(stage);
        private IInstanceConfigurationGuard HoldPlan()
        {
            return LockForRepair();
        }

        IInstanceConfigurationGuard IInstanceIsolationPlan.LockForRepair() => HoldPlan();
        private readonly List<Input> _inputs = new List<Input>();
        private readonly string _root;
        private readonly string _name;
        private bool? _folderSignal;
        private class Input
        {
            internal string Root;
            internal string Relative;
            internal byte[] Bytes;
        }
        private PclIsolationPlan(string root, string name, VersionEntry entry, JObject metadata, string pclDirectory, bool repair, bool withComponents, InstanceIsolationMode mode, bool modable, int? newInstanceIsolationStrategy)
        {
            _root = root;
            _name = name;
            string displayValue = null;
            if (!Enum.IsDefined(typeof(InstanceIsolationMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (newInstanceIsolationStrategy.HasValue && (newInstanceIsolationStrategy.Value < 0 || newInstanceIsolationStrategy.Value > 4))
                throw new ArgumentOutOfRangeException(nameof(newInstanceIsolationStrategy));
            if (!repair && mode != InstanceIsolationMode.FollowPcl)
            {
                Isolated = mode == InstanceIsolationMode.Enabled;
                return;
            }
            if (repair)
            {
                var settings = ReadInput(root, "versions/" + name + "/PCL/Setup.ini");
                string value = ReadValue(settings.Bytes, "VersionArgumentIndieV2");
                bool enabled;
                if (value is not null)
                {
                    if (!bool.TryParse(value, out enabled))
                        throw new InvalidDataException("已有实例的版本隔离设置无效，请先在 PCL 中确认。");
                    Isolated = enabled;
                    return;
                }
                value = ReadValue(settings.Bytes, "VersionArgumentIndie");
                if (value is not null)
                {
                    int legacy;
                    if (!int.TryParse(value, out legacy))
                        throw new InvalidDataException("已有实例的旧版隔离设置无效，请先在 PCL 中确认。");
                    if (legacy > 0)
                    {
                        Isolated = legacy == 1;
                        return;
                    }
                }
                _folderSignal = HasUserData();
                if (_folderSignal.Value)
                {
                    Isolated = true;
                    return;
                }
                displayValue = ReadValue(settings.Bytes, "DisplayType");
            }
            int strategy = 4; // Pinned PCL LaunchArgumentIndieV2 default.
                              // The bridge default applies only to a new instance. Repairs retain the
                              // original PCL decision, including legacy, data-folder and global fallbacks.
            if (!repair && newInstanceIsolationStrategy.HasValue)
            {
                strategy = newInstanceIsolationStrategy.Value;
            }
            else if (!string.IsNullOrWhiteSpace(pclDirectory))
            {
                var settings = ReadInput(Path.GetFullPath(pclDirectory), "PCL/Setup.ini");
                string value = ReadValue(settings.Bytes, "LaunchArgumentIndieV2");
                if (value is not null && !int.TryParse(value, out strategy))
                    throw new InvalidDataException("PCL 的默认版本隔离设置无效，请先在 PCL 中确认。");
            }
            if (repair && (strategy == 1 || strategy == 3) && displayValue is not null)
            {
                int display;
                if (!int.TryParse(displayValue, out display))
                    throw new InvalidDataException("已有实例的 PCL 显示分类无效，请先在 PCL 中确认。");
                // Pinned McInstance.Modable also includes DisplayType = API (2),
                // even for an otherwise vanilla instance with no loader flags.
                modable = modable || display == 2;
            }
            string kind = (string)metadata["type"] ?? entry.Kind;
            // PCL Load() assigns a component state after snapshot/fool detection.
            // OptiFine is a component state, but is not a ModLoaders flag (Modable).
            bool nonRelease = entry.IsSupplemental || kind == "old_beta" || kind == "old_alpha" || !withComponents && !string.Equals(kind, "release", StringComparison.OrdinalIgnoreCase);
            switch (strategy)
            {
                case 0:
                    {
                        Isolated = false;
                        break;
                    }
                case 1:
                    {
                        Isolated = modable;
                        break;
                    }
                case 2:
                    {
                        Isolated = nonRelease;
                        break;
                    }
                case 3:
                    {
                        Isolated = nonRelease || modable;
                        break;
                    }

                default:
                    {
                        Isolated = true;
                        break;
                    }
            }
        }
        internal static PclIsolationPlan Resolve(string root, string name, VersionEntry entry, JObject metadata, string pclDirectory, bool repair, bool withComponents, InstanceIsolationMode mode = InstanceIsolationMode.FollowPcl, bool modable = false, int? newInstanceIsolationStrategy = default)
        {
            return new PclIsolationPlan(root, name, entry, metadata, pclDirectory, repair, withComponents, mode, modable, newInstanceIsolationStrategy);
        }
        internal void WriteNewSettings(string stage)
        {
            string target = SafeFiles.UnderRoot(stage, "PCL/Setup.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, "VersionArgumentIndie:" + (Isolated ? "1" : "2") + Constants.vbCrLf + "VersionArgumentIndieV2:" + Isolated.ToString() + Constants.vbCrLf, new UTF8Encoding(false));
        }
        internal static void ValidatePackSettings(string stage, CancellationToken token)
        {
            // CopyOverrideDirectory in pinned PCL forces both old and V2 isolation.
            // A generated INI needs this check even when the pack supplies no INI to hash.
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            using (var input = new FileStream(SafeFiles.UnderRoot(stage, "PCL/Setup.ini"), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var buffer = new MemoryStream())
            {
                if (input.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("整合包隔离设置过大，请重试安装。");
                input.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            token.ThrowIfCancellationRequested();
            int legacy;
            bool enabled;
            if (!int.TryParse(ReadValue(bytes, "VersionArgumentIndie"), out legacy) || legacy != 1 || !bool.TryParse(ReadValue(bytes, "VersionArgumentIndieV2"), out enabled) || !enabled)
                throw new InvalidDataException("整合包隔离设置在发布前发生变化，请重试安装。");
        }
        private Input ReadInput(string root, string relative)
        {
            string path = SafeFiles.UnderRoot(root, relative);
            var prior = _inputs.FirstOrDefault(item => string.Equals(SafeFiles.UnderRoot(item.Root, item.Relative), path, StringComparison.OrdinalIgnoreCase));
            if (prior is not null)
                return prior;
            var result = new Input() { Root = root, Relative = relative };
            using (var stream = OpenInput(result))
            {
                result.Bytes = ReadBytes(stream);
            }
            _inputs.Add(result);
            return result;
        }
        private static FileStream OpenInput(Input input)
        {
            string path = SafeFiles.UnderRoot(input.Root, input.Relative);
            string parent = Path.GetDirectoryName(path);
            while (parent is not null)
            {
                if (File.Exists(parent))
                    throw new IOException("文件占用了 PCL 设置目录：" + parent);
                parent = Path.GetDirectoryName(parent);
            }
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
        private static byte[] ReadBytes(FileStream stream)
        {
            if (stream is null)
                return null;
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        private static string ReadValue(byte[] bytes, string key)
        {
            if (bytes is null)
                return null;
            string result = null;
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    int split = line.IndexOf(':');
                    // Pinned IniGetContent accepts colon-delimited, case-sensitive keys.
                    if (split > 0 && string.Equals(line.Substring(0, split), key, StringComparison.Ordinal))
                        result = line.Substring(split + 1);
                }
            }
            return result;
        }
        private bool HasUserData()
        {
            string mods = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" + _name + "/mods/entry"));
            string saves = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" + _name + "/saves/entry"));
            return Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any() || Directory.Exists(saves) && Directory.EnumerateDirectories(saves).Any();
        }
        internal RepairGuard LockForRepair()
        {
            return new RepairGuard(this);
        }
        internal sealed class RepairGuard : IInstanceConfigurationGuard
        {
            private readonly PclIsolationPlan _plan;
            private readonly List<FileStream> _held = new List<FileStream>();
            internal RepairGuard(PclIsolationPlan plan)
            {
                _plan = plan;
                try
                {
                    foreach (var input in plan._inputs)
                    {
                        var stream = OpenInput(input);
                        if (stream is not null)
                            _held.Add(stream);
                        byte[] current = ReadBytes(stream);
                        if (current is null != input.Bytes is null || current is not null && !current.SequenceEqual(input.Bytes))
                            throw Changed();
                    }
                    Verify();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
            public void Verify()
            {
                // Missing files cannot be locked without creating user preferences.
                // Recheck their absence and directory-based inference at every commit.
                foreach (var input in _plan._inputs.Where(item => item.Bytes is null))
                {
                    using (var stream = OpenInput(input))
                    {
                        if (stream is not null)
                            throw Changed();
                    }
                }
                if (_plan._folderSignal.HasValue && _plan.HasUserData() != _plan._folderSignal.Value)
                    throw Changed();
            }
            private static InvalidDataException Changed()
            {
                return new InvalidDataException("版本隔离配置在补全期间发生变化，请确认 PCL 设置后重试。");
            }
            public void Dispose()
            {
                foreach (var stream in _held)
                    stream.Dispose();
                _held.Clear();
            }
        }
    }
}
