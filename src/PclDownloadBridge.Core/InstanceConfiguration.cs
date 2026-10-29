using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PclDownloadBridge.Core
{

    internal interface IInstanceConfigurationGuard : IDisposable
    {
        void Verify();
    }

    internal interface IInstanceIsolationPlan
    {
        bool Isolated { get; }
        void WriteNewSettings(string stage);
        IInstanceConfigurationGuard LockForRepair();
    }

    // Snapshot external preferences read-only, then hold their exact bytes (including
    // absence) through the short filesystem commit. No migration or initialization.
    internal sealed class InstanceConfigurationInputs
    {
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        internal string Fingerprint()
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                foreach (var pair in _files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.Write(pair.Key.ToUpperInvariant());
                    writer.Write(pair.Value is null ? -1 : pair.Value.Length);
                    if (pair.Value is not null)
                        writer.Write(pair.Value);
                }
                writer.Flush();
                using (var hash = System.Security.Cryptography.SHA256.Create())
                {
                    return BitConverter.ToString(hash.ComputeHash(buffer.ToArray())).Replace("-", "");
                }
            }
        }
        internal byte[] Read(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (_files.ContainsKey(full))
                return _files[full];
            byte[] bytes;
            using (var stream = Open(full))
            {
                bytes = ReadBytes(stream);
            }
            _files[full] = bytes;
            return bytes;
        }
        internal static string Text(byte[] bytes)
        {
            if (bytes is null)
                return null;
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }
        private static FileStream Open(string full)
        {
            SafeFiles.UnderRoot(Path.GetDirectoryName(full), Path.GetFileName(full));
            string parent = Path.GetDirectoryName(full);
            while (parent is not null)
            {
                if (File.Exists(parent))
                    throw new IOException("文件占用了实例配置的父目录：" + parent);
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("实例配置的父目录包含链接：" + parent);
                parent = Path.GetDirectoryName(parent);
            }
            if (Directory.Exists(full))
                throw new IOException("文件夹占用了实例配置文件：" + full);
            try
            {
                return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            if (stream.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("实例配置超过大小限制。");
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                if (buffer.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("实例配置超过大小限制。");
                return buffer.ToArray();
            }
        }
        internal IInstanceConfigurationGuard Hold(Action extraCheck = null)
        {
            return new Guard(this, extraCheck);
        }
        private sealed class Guard : IInstanceConfigurationGuard
        {
            private readonly InstanceConfigurationInputs _owner;
            private readonly List<FileStream> _streams = new List<FileStream>();
            private readonly Action _extra;
            internal Guard(InstanceConfigurationInputs owner, Action extra)
            {
                _owner = owner;
                _extra = extra;
                try
                {
                    foreach (var item in owner._files)
                    {
                        var stream = Open(item.Key);
                        if (stream is not null)
                            _streams.Add(stream);
                        byte[] current = ReadBytes(stream);
                        if (current is null != item.Value is null || current is not null && !current.SequenceEqual(item.Value))
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
                foreach (var item in _owner._files.Where(pair => pair.Value is null))
                {
                    using (var stream = Open(item.Key))
                    {
                        if (stream is not null)
                            throw Changed();
                    }
                }
                _extra?.Invoke();
            }
            private static InvalidDataException Changed()
            {
                return new InvalidDataException("启动器或实例配置在安装期间发生变化，请确认设置后重试。");
            }
            public void Dispose()
            {
                foreach (var stream in _streams)
                    stream.Dispose();
                _streams.Clear();
            }
        }
    }
}
