Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading

' PCL's Forge entries carry MD5/SHA1 into their download checks. The Maven XML
' catalogue used here omits hashes, so resolve sidecars for the selected file only.
Friend NotInheritable Class ForgeInstallerChecksum
    Friend Shared Function IsChecksum(value As String) As Boolean
        Return value IsNot Nothing AndAlso Regex.IsMatch(value, "\A(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{40})\z")
    End Function

    Friend Shared Async Function ResolveAsync(policy As SourcePolicy, file As DownloadFile, token As CancellationToken) As Task
        token.ThrowIfCancellationRequested()
        If Not String.IsNullOrWhiteSpace(file.Sha1) AndAlso Not Regex.IsMatch(file.Sha1, "\A[0-9a-fA-F]{40}\z") Then Throw New InvalidDataException("Forge 安装器 SHA1 校验信息格式无效。")
        If Not String.IsNullOrWhiteSpace(file.Md5) AndAlso Not Regex.IsMatch(file.Md5, "\A[0-9a-fA-F]{32}\z") Then Throw New InvalidDataException("Forge 安装器 MD5 校验信息格式无效。")
        If Not String.IsNullOrWhiteSpace(file.Sha1) OrElse Not String.IsNullOrWhiteSpace(file.Md5) Then Return
        Dim sha1Failure As Exception
        Try
            file.Sha1 = Await policy.GetArtifactChecksumAsync(file.Url & ".sha1", 40, token).ConfigureAwait(False)
            Return
        Catch ex As AggregateException
            sha1Failure = ex
        End Try
        token.ThrowIfCancellationRequested()
        Try
            file.Md5 = Await policy.GetArtifactChecksumAsync(file.Url & ".md5", 32, token).ConfigureAwait(False)
        Catch ex As AggregateException
            Throw New InvalidDataException("无法获取有效的 Forge 安装器校验信息，请重试或切换下载源。", New AggregateException(sha1Failure, ex))
        End Try
    End Function
End Class
