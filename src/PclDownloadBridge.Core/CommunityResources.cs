using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ResourceTypes / ResourceSearcher / ResourceVersion. The existing
    // modpack API remains the default; resource context belongs to each request.
    public enum CommunityResourceKind
    {
        Modpack,
        Mod,
        DataPack,
        ResourcePack,
        Shader,
        Any
    }

    public sealed class ResourceDependency
    {
        public string ProjectId { get; set; }
        public string VersionId { get; set; }
    }

    public sealed class CommunityResources
    {
        private CommunityResources()
        {
        }
        public static string Name(CommunityResourceKind kind)
        {
            switch (kind)
            {
                case CommunityResourceKind.Modpack:
                    {
                        return "整合包";
                    }
                case CommunityResourceKind.Mod:
                    {
                        return "Mod";
                    }
                case CommunityResourceKind.DataPack:
                    {
                        return "数据包";
                    }
                case CommunityResourceKind.ResourcePack:
                    {
                        return "资源包";
                    }
                case CommunityResourceKind.Shader:
                    {
                        return "光影包";
                    }

                default:
                    {
                        throw new ArgumentException("资源类型无效。");
                    }
            }
        }
        public static int ClassId(CommunityResourceKind kind)
        {
            switch (kind)
            {
                case CommunityResourceKind.Modpack:
                    {
                        return 4471;
                    }
                case CommunityResourceKind.Mod:
                    {
                        return 6;
                    }
                case CommunityResourceKind.DataPack:
                    {
                        return 6945;
                    }
                case CommunityResourceKind.ResourcePack:
                    {
                        return 12;
                    }
                case CommunityResourceKind.Shader:
                    {
                        return 6552;
                    }

                default:
                    {
                        throw new ArgumentException("资源类型无效。");
                    }
            }
        }
        public static string ProjectType(CommunityResourceKind kind)
        {
            Name(kind);
            return kind.ToString().ToLowerInvariant();
        }
        internal static bool Matches(ModpackPlatform platform, JObject data, CommunityResourceKind kind)
        {
            if (kind == CommunityResourceKind.Any)
                return Enumerable.Range(0, 5).Any(value => Matches(platform, data, (CommunityResourceKind)value));
            if (platform == ModpackPlatform.CurseForge)
                return ((string)data["classId"] ?? "") == (ClassId(kind).ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
            JArray types = data["all_project_types"] as JArray;
            if (types is not null)
                return types.Values<string>().Contains(ProjectType(kind), StringComparer.Ordinal);
            if (((string)data["project_type"] ?? "") == (ProjectType(kind) ?? ""))
                return true;
            // v2 project metadata may report a datapack/mod hybrid only as "mod".
            return kind == CommunityResourceKind.DataPack && (string)data["project_type"] == "mod";
        }
        internal static CommunityResourceKind Infer(ModpackPlatform platform, JObject data)
        {
            foreach (var kind in new[] { CommunityResourceKind.Modpack, CommunityResourceKind.Mod, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader, CommunityResourceKind.DataPack })
            {
                if (Matches(platform, data, kind))
                    return kind;
            }
            throw new InvalidDataException("暂不支持这个资源类型。");
        }
        internal static CommunityResourceKind? FileKind(ModpackProject project, string[] loaders)
        {
            if (project.Platform != ModpackPlatform.Modrinth || project.Kind != CommunityResourceKind.Mod && project.Kind != CommunityResourceKind.DataPack)
                return project.Kind;
            // PCL explicitly excludes plugin builds from mixed projects.
            bool mods = loaders.Any(value => new[] { "fabric", "forge", "neoforge", "quilt", "liteloader", "rift", "modloader" }.Contains(value, StringComparer.OrdinalIgnoreCase));
            if (project.Kind == CommunityResourceKind.DataPack)
                return loaders.Contains("datapack", StringComparer.OrdinalIgnoreCase) ? CommunityResourceKind.DataPack : default(CommunityResourceKind?);
            return mods ? CommunityResourceKind.Mod : default(CommunityResourceKind?);
        }
        public static bool AcceptsFile(CommunityResourceKind kind, string fileName)
        {
            string extension = Path.GetExtension(fileName);
            switch (kind)
            {
                case CommunityResourceKind.Modpack:
                    {
                        return new[] { ".zip", ".mrpack" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
                    }
                case CommunityResourceKind.Mod:
                    {
                        return new[] { ".jar", ".litemod" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
                    }
                case CommunityResourceKind.DataPack:
                case CommunityResourceKind.ResourcePack:
                case CommunityResourceKind.Shader:
                    {
                        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
                    }

                default:
                    {
                        return false;
                    }
            }
        }
        public static void ValidateFileName(CommunityResourceKind kind, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || (fileName ?? "") != (fileName.Trim() ?? "") || (fileName ?? "") != (Path.GetFileName(fileName) ?? "") || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !AcceptsFile(kind, fileName) || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(?:\.|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("资源文件名无效。");
        }
    }
}