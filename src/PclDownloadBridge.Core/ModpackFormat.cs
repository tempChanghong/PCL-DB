using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PclDownloadBridge.Core
{

    public enum ModpackFormat
    {
        CurseForge,
        Modrinth
    }

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    public sealed class ModpackFormats
    {
        private ModpackFormats() { }
        // PCL detects the manifest inside the archive, not merely its extension.
        public static ModpackFormat Detect(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > 20000)
                    throw new InvalidDataException("整合包条目过多。");
                string[] names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
                string[] roots = names.Where(name => new[] { "manifest.json", "modrinth.index.json" }.Contains(name, StringComparer.Ordinal)).ToArray();
                // PCL gives the root manifest precedence over one-folder candidates.
                // An ordinary overrides/manifest.json is not another pack identity.
                string[] manifests = roots.Length > 0 ? roots : names.Where(name => name.Count(ch => ch == '/') == 1 && new[] { "manifest.json", "modrinth.index.json" }.Contains(name.Split('/').Last(), StringComparer.Ordinal)).ToArray();
                if (manifests.Length != 1)
                    throw new InvalidDataException("未找到唯一的 CurseForge / Modrinth 整合包清单。");
                return manifests[0].EndsWith("modrinth.index.json", StringComparison.Ordinal) ? ModpackFormat.Modrinth : ModpackFormat.CurseForge;
            }
        }
    }
}