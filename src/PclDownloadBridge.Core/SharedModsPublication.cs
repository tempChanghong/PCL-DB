using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PclDownloadBridge.Core
{

    // PCL ModDownloadLib installs add-ons into PathIndie/mods. Shared add-ons use
    // the same owned-file rollback as legacy resources, inside the instance commit.
    internal sealed class SharedModsPublication
    {
        private SharedModsPublication()
        {
        }
        internal static void Preflight(string root, List<DownloadFile> files, bool modable)
        {
            PreflightForInstallation(root, files, modable, CancellationToken.None);
        }
        internal static void PreflightForInstallation(string root, List<DownloadFile> files, bool modable, CancellationToken token)
        {
            ValidateFolders(root, modable);
            LegacyResourcePublication.ValidateForInstallation(root, files, true, token, "共享 Mods");
            foreach (var item in files)
            {
                string target = SafeFiles.UnderRoot(root, item.RelativePath);
                if (File.Exists(target) && (!string.IsNullOrWhiteSpace(item.Sha1) || !string.IsNullOrWhiteSpace(item.Md5)) && !SafeFiles.Matches(target, item, token))
                    throw new IOException("已有共享 Mods 内容不同，不会覆盖：" + item.RelativePath);
            }
        }

        private static void ValidateFolders(string root, bool modable)
        {
            foreach (var folder in modable ? (new[] { "mods", "resourcepacks" }) : (new[] { "resourcepacks" }))
            {
                string path = SafeFiles.UnderRoot(root, folder + "/.pcl-db-directory");
                if (File.Exists(Path.GetDirectoryName(path)))
                    throw new IOException("文件占用了共享目录：" + folder);
            }
        }

        internal static List<DownloadFile> CheckedFiles(string stage, List<DownloadFile> expected, CancellationToken token)
        {
            var result = new List<DownloadFile>();
            foreach (var item in expected)
            {
                token.ThrowIfCancellationRequested();
                string path = SafeFiles.UnderRoot(stage, item.RelativePath);
                // OptiFine may have no remote digest. Freeze the verified staged bytes,
                // so an existing same-name shared mod is compared by content, not size.
                // Keep one read lease across both checks: a second open must not turn
                // changed bytes into the new trusted publication digest.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!SafeFiles.Matches(path, item, token))
                        throw new InvalidDataException("待发布的组件 Mods 校验失败：" + item.RelativePath);
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                    {
                        result.Add(new DownloadFile()
                        {
                            RelativePath = item.RelativePath,
                            Size = input.Length,
                            Sha1 = BitConverter.ToString(HashStream(sha, input, token)).Replace("-", "").ToLowerInvariant(),
                            Category = "component"
                        });
                    }
                    token.ThrowIfCancellationRequested();
                }
            }
            return result;
        }

        private static byte[] HashStream(System.Security.Cryptography.HashAlgorithm hash, Stream input, CancellationToken token)
        {
            // Keep the existing provider behavior for non-cancellable callers. The
            // caller's read lease spans remote checks and this frozen local digest.
            if (!token.CanBeCanceled)
                return hash.ComputeHash(input);
            var buffer = new byte[65536];
            do
            {
                token.ThrowIfCancellationRequested();
                int count = input.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;
                hash.TransformBlock(buffer, 0, count, buffer, 0);
            }
            while (true);
            token.ThrowIfCancellationRequested();
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] digest = hash.Hash;
            hash.Initialize(); // ComputeHash resets its provider before returning.
            token.ThrowIfCancellationRequested();
            return digest;
        }

        internal static void FinishFolders(string root, string stage, bool modable)
        {
            ValidateFolders(root, modable);
            foreach (var folder in modable ? (new[] { "mods", "resourcepacks" }) : (new[] { "resourcepacks" }))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SafeFiles.UnderRoot(root, folder + "/.pcl-db-directory")));
                string privateFolder = Path.GetDirectoryName(SafeFiles.UnderRoot(stage, folder + "/.pcl-db-directory"));
                // Remove empty private folders only. In particular no mod may remain in
                // versions/name/mods, where PCL could infer a different isolation value.
                if (!Directory.Exists(privateFolder))
                    continue;
                RemoveEmpty(stage, folder);
            }
        }
        private static void RemoveEmpty(string stage, string relative)
        {
            string target = SafeFiles.UnderRoot(stage, relative);
            foreach (var child in Directory.EnumerateDirectories(target))
                RemoveEmpty(stage, relative + "/" + Path.GetFileName(child));
            Directory.Delete(target, false);
        }
    }
}