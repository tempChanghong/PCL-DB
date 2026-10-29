using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PclDownloadBridge.Core
{

    // PCL's Forge entries carry MD5/SHA1 into their download checks. Resolve sidecars
    // for selected Forge/NeoForge installers whose Maven catalogues omit hashes.
    internal sealed class ForgelikeInstallerChecksum
    {
        internal static bool IsChecksum(string value)
        {
            return value is not null && Regex.IsMatch(value, @"\A(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{40})\z");
        }

        internal static async Task ResolveAsync(SourcePolicy policy, DownloadFile file, CancellationToken token, string label)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.Sha1) && !Regex.IsMatch(file.Sha1, @"\A[0-9a-fA-F]{40}\z"))
                throw new InvalidDataException(label + " 安装器 SHA1 校验信息格式无效。");
            if (!string.IsNullOrWhiteSpace(file.Md5) && !Regex.IsMatch(file.Md5, @"\A[0-9a-fA-F]{32}\z"))
                throw new InvalidDataException(label + " 安装器 MD5 校验信息格式无效。");
            if (!string.IsNullOrWhiteSpace(file.Sha1) || !string.IsNullOrWhiteSpace(file.Md5))
                return;
            Exception sha1Failure;
            try
            {
                file.Sha1 = await policy.GetArtifactChecksumAsync(file.Url + ".sha1", 40, token).ConfigureAwait(false);
                return;
            }
            catch (AggregateException ex)
            {
                sha1Failure = ex;
            }
            token.ThrowIfCancellationRequested();
            try
            {
                file.Md5 = await policy.GetArtifactChecksumAsync(file.Url + ".md5", 32, token).ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                throw new InvalidDataException("无法获取有效的 " + label + " 安装器校验信息，请重试或切换下载源。", new AggregateException(sha1Failure, ex));
            }
        }
    }
}