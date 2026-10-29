using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadBridge.Core
{

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    public sealed class InstallPreflight
    {
        private InstallPreflight() { }
        public static void ValidatePaths(string root, string instanceName)
        {
            ValidateName(instanceName);
            // PCL PageSelectLeft also rejects ! and ;: Java expands these in launch paths.
            if (root.IndexOfAny(new[] { '!', ';' }) >= 0 || instanceName.IndexOfAny(new[] { '!', ';' }) >= 0)
                throw new ArgumentException("为兼容 PCL 与 Java，游戏路径和实例名称不能含有 ! 或 ;。");
            SafeFiles.UnderRoot(root, "versions/" + instanceName + "/" + instanceName + ".json");
        }

        public static void ValidateName(string instanceName)
        {
            // PCL ValidateFolderName, with the existing Windows reserved-name checks.
            if (string.IsNullOrWhiteSpace(instanceName))
                throw new ArgumentException("版本名称不能为空。");
            if (instanceName.StartsWith(" ", StringComparison.Ordinal) || instanceName.EndsWith(" ", StringComparison.Ordinal))
                throw new ArgumentException("版本名称不能以空格开头或结尾。");
            if (instanceName.EndsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("版本名称不能以小数点结尾。");
            if (instanceName.Length > 100)
                throw new ArgumentException("版本名称不能超过 100 个字符。");
            if (instanceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException(@"版本名称不能包含 /、\、:、*、?、""、<、>、| 等字符。");
            if (instanceName.IndexOfAny(new[] { '!', ';' }) >= 0)
                throw new ArgumentException("为兼容 PCL 与 Java，版本名称不能含有 ! 或 ;。");
            if (Regex.IsMatch(instanceName, @"^(CLOCK\$|COM0|LPT0)(\.|$)", RegexOptions.IgnoreCase))
                throw new ArgumentException("版本名称是 PCL 不支持的保留名称。");
            if (Regex.IsMatch(instanceName, @".{2,}~\d"))
                throw new ArgumentException("版本名称不能包含 NTFS 短文件名格式（如 ABC~1）。");
            SafeFiles.ValidateInstanceName(instanceName);
        }

        public static void ValidateDestination(string root, string instanceName, bool repairExisting)
        {
            ValidatePaths(root, instanceName);
            string instancePath = SafeFiles.UnderRoot(root, "versions/" + instanceName);
            string jsonTarget = SafeFiles.UnderRoot(root, "versions/" + instanceName + "/" + instanceName + ".json");
            if (File.Exists(instancePath))
                throw new IOException("已有同名文件占用了版本目录，请选择新名称。");
            if (Directory.Exists(instancePath) && !repairExisting)
                throw new IOException("该实例已存在。请选择新名称，或明确选择「校验补全现有原版」。");
            if (repairExisting && !File.Exists(jsonTarget))
                throw new IOException("未找到待补全实例的版本 JSON。");
        }

        public static void VerifyJava(ComponentSelection selection, JObject metadata)
        {
            if (selection is null)
                return;
            var forge = selection.Item(ComponentKind.Forge);
            int forgeMajor;
            bool modernForge = forge is not null && (!int.TryParse((forge.Version ?? "").Split('.')[0], out forgeMajor) || forgeMajor >= 20);
            if (modernForge || selection.Has(ComponentKind.NeoForge))
            {
                int minimum = Math.Max(8, (int)(metadata.SelectToken("javaVersion.majorVersion") ?? new JValue(8L)));
                selection.JavaPath = JavaRuntime.Find(selection.JavaPath, minimum);
            }
            else if (selection.Has(ComponentKind.OptiFine) && !selection.Has(ComponentKind.Forge) && !selection.Has(ComponentKind.Fabric) && !selection.Has(ComponentKind.LiteLoader))
            {
                // Detect a missing runtime early. OptiFine's class version is checked again
                // after fetching its installer, so automatic selection can choose a newer Java.
                JavaRuntime.Find(selection.JavaPath, 8);
            }
        }
    }
}