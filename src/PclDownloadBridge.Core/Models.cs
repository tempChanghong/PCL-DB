using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PclDownloadBridge.Core
{

    public class GameFolder
    {
        public string LauncherBindingId { get; set; }
        public ExternalLauncherKind? LauncherKind { get; set; }
        public string LauncherWorkspace { get; set; }
        public string LauncherConfigPath { get; set; }
        public string LauncherSharedConfigPath { get; set; }
        public string Name { get; set; }
        public string Location { get; set; }
        public string Origin { get; set; }
        public bool IsSelected { get; set; }
        public string DisplayName
        {
            get
            {
                return (LauncherKind.HasValue ? "[" + (LauncherKind.Value == ExternalLauncherKind.Pcl ? "PCL" : LauncherKind.Value == ExternalLauncherKind.Hmcl ? "HMCL" : "PCL-CE") + "] " : "") + Name + (string.IsNullOrEmpty(LauncherWorkspace) ? "" : " · " + LauncherWorkspace);
            }
        }
        public int VersionCount
        {
            get
            {
                string versions = Path.Combine(Location, "versions");
                if (!Directory.Exists(versions))
                    return 0;
                try
                {
                    return Directory.EnumerateDirectories(versions).Count(p => File.Exists(Path.Combine(p, Path.GetFileName(p) + ".json")));
                }
                catch (IOException)
                {
                    return 0;
                }
                catch (UnauthorizedAccessException)
                {
                    return 0;
                }
            }
        }
        public override string ToString()
        {
            return Name + " · " + Location;
        }
    }

    public class VersionEntry
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Url { get; set; }
        public string Sha1 { get; set; }
        public DateTimeOffset Released { get; set; }
        // Original UVMC catalogue ID. Id is the verified canonical Minecraft metadata ID.
        public string SupplementalId { get; set; }
        public VersionEntry Copy()
        {
            // Every stored field is a value or immutable string. Keep the identity,
            // origin and checksum bound to this request across asynchronous stages.
            return (VersionEntry)MemberwiseClone();
        }
        public bool IsSupplemental
        {
            get
            {
                return SupplementalId is not null;
            }
        }
        public string Subtitle
        {
            get
            {
                return (IsSupplemental ? SupplementalVersions.Label(this) + " · 补充来源 UVMC" : Kind == "release" ? "正式版" : Kind == "snapshot" ? "快照版" : "历史版本") + " · " + Released.ToString("yyyy/MM/dd");
            }
        }
    }

    public class DownloadFile
    {
        public string RelativePath { get; set; }
        public string Url { get; set; }
        public long Size { get; set; } = -1;
        public string[] AlternateUrls { get; set; }
        // Modrinth fileSize is a progress estimate, never a hard integrity check.
        public long EstimatedSize { get; set; } = -1;
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public string Md5 { get; set; }
        // Only the one-part multipart S3 ETag form (MD5 of the binary MD5, then -1).
        // Ordinary ETags and unknown multipart boundaries are never checksums.
        public string S3SinglePartETag { get; set; }
        public string Category { get; set; } = "meta";
    }

    public class TransferProgress
    {
        public Dictionary<string, double> PhaseFractions { get; set; }
        public int PhaseTotal { get; set; } = -1;
        public int PhaseCompleted { get; set; }
        public string Phase { get; set; } = "files";
        public double StageFraction { get; set; }
        public string CurrentFile { get; set; }
        public string Source { get; set; }
        public int Retries { get; set; }
        public long KnownBytes { get; set; }
        public long VerifiedBytes { get; set; }
        public int ActiveConnections { get; set; }
        public int DynamicSplits { get; set; }
        public string Stage { get; set; }
        public string Detail { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public long NetworkBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public int Reused { get; set; }
        public double Fraction { get; set; }
    }

    public class InstallResult
    {
        public int DynamicSplits { get; set; }
        public string InstancePath { get; set; }
        public int FileCount { get; set; }
        public int Reused { get; set; }
        public long NetworkBytes { get; set; }
    }

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    public sealed class SafeFiles
    {
        private SafeFiles() { }
        public static string UnderRoot(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException("文件路径无效：" + relative);
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件路径超出游戏目录：" + relative);
            // Do not follow junctions/symlinks out of the selected game directory.
            string current = Path.GetDirectoryName(target);
            while (current is not null && current.Length >= basePath.TrimEnd(Path.DirectorySeparatorChar).Length)
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录包含链接，请选择真实游戏目录：" + current);
                current = Path.GetDirectoryName(current);
            }
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("目标文件是链接：" + target);
            return target;
        }

        public static void ValidateInstanceName(string value)
        {
            ValidateName(value, 100, "实例");
        }

        internal static void ValidatePathSegment(string value)
        {
            ValidateName(value, 255, "文件或文件夹");
        }

        private static void ValidateName(string value, int maximum, string description)
        {
            if (string.IsNullOrWhiteSpace(value) || (value ?? "") != (value.Trim() ?? "") || value.EndsWith(".", StringComparison.Ordinal) || value.Length > maximum || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value == "." || value == "..")
                throw new ArgumentException(description + "名称无效，请使用普通文件夹名称。");
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException(description + "名称是 Windows 保留名称。");
        }

        public static bool Matches(string path, DownloadFile item)
        {
            return Matches(path, item, CancellationToken.None);
        }

        internal static bool Matches(string path, DownloadFile item, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                return false;
            var info = new FileInfo(path);
            if (item.Size >= 0L && info.Length != item.Size)
                return false;
            // An explicitly declared or hashed empty file is legitimate; unknown empty
            // downloads still must not be mistaken for completed reusable files.
            if (info.Length == 0L && item.Size != 0L && string.IsNullOrWhiteSpace(item.Sha1) && string.IsNullOrWhiteSpace(item.Sha512) && string.IsNullOrWhiteSpace(item.Md5))
                return false;
            if (!string.IsNullOrWhiteSpace(item.Sha512))
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA512.Create())
                {
                    if (!string.Equals(BitConverter.ToString(HashStream(sha, stream, token)).Replace("-", ""), item.Sha512, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Sha1))
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA1.Create())
                {
                    if (!string.Equals(BitConverter.ToString(HashStream(sha, stream, token)).Replace("-", ""), item.Sha1, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Md5))
            {
                using (var stream = File.OpenRead(path))
                using (var hash = MD5.Create())
                {
                    if (!string.Equals(BitConverter.ToString(HashStream(hash, stream, token)).Replace("-", ""), item.Md5, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(item.S3SinglePartETag))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(item.S3SinglePartETag, @"\A[0-9a-fA-F]{32}-1\z"))
                    return false;
                using (var stream = File.OpenRead(path))
                using (var hash = MD5.Create())
                {
                    byte[] digest = hash.ComputeHash(HashStream(hash, stream, token));
                    token.ThrowIfCancellationRequested();
                    if (!string.Equals(BitConverter.ToString(digest).Replace("-", "") + "-1", item.S3SinglePartETag, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            token.ThrowIfCancellationRequested();
            return true;
        }

        private static byte[] HashStream(HashAlgorithm hash, Stream stream, CancellationToken token)
        {
            // Preserve the original non-cancellable entry's provider behavior. PCL
            // keeps size/hash checks before publication; CE also passes the caller's
            // token through stream hashing. Use net48-compatible blocks for that path.
            if (!token.CanBeCanceled)
                return hash.ComputeHash(stream);
            var buffer = new byte[65536];
            do
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;
                hash.TransformBlock(buffer, 0, count, buffer, 0);
            }
            while (true);
            token.ThrowIfCancellationRequested();
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] digest = hash.Hash;
            // ComputeHash resets its algorithm before returning. S3's single-part
            // ETag then reuses this same MD5 instance for the raw inner digest.
            hash.Initialize();
            token.ThrowIfCancellationRequested();
            return digest;
        }

        public static void CommitFile(string temp, string target)
        {
            if (File.Exists(target))
            {
                File.Replace(temp, target, null);
            }
            else
            {
                File.Move(temp, target);
            }
        }
    }
}
