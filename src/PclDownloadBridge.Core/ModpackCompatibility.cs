using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PclDownloadBridge.Core
{

    // PCL 5ef81e4 ModModpack selects Forge / NeoForge / Fabric from the archive.
    // Catalogue tags are supported alternatives, never the source of dependencies.
    public sealed class ModpackCompatibility
    {
        private ModpackCompatibility()
        {
        }

        public static string GetInstallBlockReason(ModpackRelease release)
        {
            if (release.GameVersionMetadataError is not null)
                return release.GameVersionMetadataError;
            var declared = KnownLoaders(release.Loaders);
            // Missing or unfamiliar tags do not prove incompatibility. Parse the archive.
            if (declared is null || declared.Count == 0 || declared.Any(loader => new[] { "Forge", "NeoForge", "Fabric", "原版" }.Contains(loader, StringComparer.Ordinal)))
                return null;
            return "此版本使用 " + string.Join("、", declared) + "，当前暂不支持自动安装。请选择 Forge、NeoForge、Fabric 或原版版本。";
        }

        public static void ValidateManifest(ModpackRelease release, string minecraft, ComponentKind? loader)
        {
            if (release.GameVersionMetadataError is not null)
                throw new InvalidDataException(release.GameVersionMetadataError);
            if (release.GameVersions.Length > 0 && !release.GameVersions.Contains(minecraft, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包清单与所选版本声明的 Minecraft 版本不一致。");
            var declared = KnownLoaders(release.Loaders);
            if (declared is null || declared.Count == 0)
                return;
            string actual = loader.HasValue ? loader.GetValueOrDefault().ToString() : "原版";
            if (!declared.Contains(actual, StringComparer.Ordinal))
                throw new InvalidDataException("整合包清单中的加载器（" + actual + "）与所选版本标注（" + string.Join("、", declared) + "）不一致。");
        }

        private static List<string> KnownLoaders(IEnumerable<string> values)
        {
            var result = new List<string>();
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                string canonical;
                switch ((value ?? "").Trim().ToLowerInvariant() ?? "")
                {
                    case "forge":
                        {
                            canonical = "Forge";
                            break;
                        }
                    case "neoforge":
                    case "neo-forge":
                        {
                            canonical = "NeoForge";
                            break;
                        }
                    case "fabric":
                    case "fabric-loader":
                        {
                            canonical = "Fabric";
                            break;
                        }
                    case "minecraft":
                    case "vanilla":
                        {
                            canonical = "原版";
                            break;
                        }
                    case "quilt":
                    case "quilt-loader":
                        {
                            canonical = "Quilt";
                            break;
                        }
                    case "liteloader":
                        {
                            canonical = "LiteLoader";
                            break;
                        }
                    case "rift":
                        {
                            canonical = "Rift";
                            break;
                        }

                    default:
                        {
                            return null;
                        }
                }
                if (!result.Contains(canonical, StringComparer.Ordinal))
                    result.Add(canonical);
            }
            return result;
        }
    }
}