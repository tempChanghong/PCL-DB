using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    public partial class MinecraftInstaller
    {
        // HMCL stores vanilla metadata as its game patch. Compare that patch without
        // flattening or rewriting the user's manifest; loaders/inheritance stay refused.
        private static JObject HmclVanillaMetadata(JObject existing, string version)
        {
            if (existing["patches"] is null)
                return existing;
            JArray patches = existing["patches"] as JArray;
            if (patches is null || patches.Count != 1 || !(patches[0] is JObject) || existing["inheritsFrom"] is not null)
                throw new InvalidDataException("只支持补全 HMCL 的独立原版实例。");
            JObject game = (JObject)patches[0];
            if ((string)game["id"] != "game" || ((string)game["version"] ?? "") != (version ?? "") || game["inheritsFrom"] is not null)
                throw new InvalidDataException("HMCL 实例版本与所选原版不一致。");
            return (JObject)game.DeepClone();
        }

        // PCL McAssetsGetIndexName and ${assets_index_name} prefer assetIndex.id.
        // Supplemental releases can reuse one upstream ID for different index contents;
        // keep their content-addressed names separate from the ordinary shared index.
        private static bool PrepareSupplementalAssetIndex(JObject metadata, JObject existing)
        {
            JObject index = (JObject)metadata["assetIndex"];
            SafeFiles.ValidateInstanceName((string)index["id"]);
            string isolated = "pcldb-" + ((string)index["sha1"]).ToLowerInvariant();
            // Repair preserves existing JSON bytes. Keep legacy names, but the caller
            // refuses to overwrite an existing index that no longer matches this version.
            if (existing is not null && ((string)existing.SelectToken("assetIndex.id") ?? "") != (isolated ?? ""))
                return true;
            index["id"] = isolated;
            return false;
        }

        private static DownloadFile ToFile(JObject data, string relative, string category)
        {
            return new DownloadFile() { RelativePath = relative, Url = (string)data["url"], Size = data["size"] is null || data["size"].Type == JTokenType.Null ? -1 : (long)data["size"], Sha1 = (string)data["sha1"], Category = category };
        }

    }
}
