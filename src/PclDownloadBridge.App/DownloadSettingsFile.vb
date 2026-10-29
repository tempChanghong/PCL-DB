Imports System.IO
Imports System.Text
Imports System.Security.Cryptography
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module DownloadSettingsFile
    ' The old name is a storage compatibility key, never an assembly/namespace alias.
    Friend Const LegacyDirectoryName As String = "VanillaDownloader"
    Friend Const PreviousDirectoryName As String = "PlainCraftDownloader"

    Friend Function ChooseLegacyPath(previous As String, oldest As String) As String
        Try
            File.GetAttributes(previous)
            Return previous
        Catch ex As FileNotFoundException
            Return oldest
        Catch ex As DirectoryNotFoundException
            Return oldest
        End Try
    End Function

    Friend Function LoadCompatible(path As String, legacyPath As String, Optional report As Action(Of String) = Nothing) As JObject
        Dim target = IO.Path.GetFullPath(path)
        Dim result As JObject = Nothing
        RunWithGate(target, Sub() result = LoadUnlocked(target, legacyPath, report))
        Return result
    End Function

    Private Function LoadUnlocked(target As String, legacyPath As String, Optional report As Action(Of String) = Nothing) As JObject
        If File.Exists(target) Then Return JObject.Parse(File.ReadAllText(target))
        If IO.Directory.Exists(legacyPath) Then Throw New InvalidDataException("旧设置文件路径被文件夹占用。")
        If Not File.Exists(legacyPath) Then Return Nothing
        ' Read once, validate before publishing, and retain unknown settings and the original bytes.
        Dim bytes = File.ReadAllBytes(legacyPath)
        Dim settings As JObject
        Using reader As New StreamReader(New MemoryStream(bytes), Encoding.UTF8, True)
            settings = JObject.Parse(reader.ReadToEnd())
        End Using
        Dim directory = IO.Path.GetDirectoryName(target)
        Dim temp = IO.Path.Combine(directory, ".settings-migrate-" & Guid.NewGuid().ToString("N") & ".tmp")
        Try
            IO.Directory.CreateDirectory(directory)
            File.WriteAllBytes(temp, bytes)
            ' Move publishes atomically and refuses to replace a settings file created concurrently.
            File.Move(temp, target)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            If File.Exists(target) Then Return JObject.Parse(File.ReadAllText(target))
            report?.Invoke("已读取旧版偏好，但无法迁移到 PCL-DB 目录：" & ex.Message)
        Finally
            DeleteTemporary(temp)
        End Try
        Return settings
    End Function

    Friend Sub Save(path As String, settings As JObject)
        Dim target = IO.Path.GetFullPath(path)
        Dim directory = IO.Path.GetDirectoryName(target)
        Dim temp = IO.Path.Combine(directory, ".settings-" & Guid.NewGuid().ToString("N") & ".tmp")
        Try
            IO.Directory.CreateDirectory(directory)
            File.WriteAllText(temp, settings.ToString(), New UTF8Encoding(False))
            SafeFiles.CommitFile(temp, target)
        Finally
            DeleteTemporary(temp)
        End Try
    End Sub

    Friend Function MergeValues(existing As JObject, changes As JObject) As JObject
        If changes Is Nothing Then Throw New ArgumentNullException(NameOf(changes))
        Dim result = If(existing Is Nothing, New JObject, DirectCast(existing.DeepClone(), JObject))
        result.Merge(changes, New JsonMergeSettings With {.MergeArrayHandling = MergeArrayHandling.Replace, .MergeNullValueHandling = MergeNullValueHandling.Merge})
        Return result
    End Function

    Friend Function ChangedValues(baseline As JObject, values As JObject) As JObject
        Dim changes As New JObject
        For Each item In values.Properties()
            Dim previous = If(baseline Is Nothing, Nothing, baseline(item.Name))
            If JToken.DeepEquals(previous, item.Value) Then Continue For
            If TypeOf previous Is JObject AndAlso TypeOf item.Value Is JObject Then
                Dim nested = ChangedValues(DirectCast(previous, JObject), DirectCast(item.Value, JObject))
                If nested.HasValues Then changes(item.Name) = nested
            Else
                changes(item.Name) = item.Value.DeepClone()
            End If
        Next
        Return changes
    End Function

    Friend Sub SaveChanges(path As String, legacyPath As String, changes As JObject, Optional replaceMalformed As Boolean = False)
        Dim target = IO.Path.GetFullPath(path)
        If changes Is Nothing Then Throw New ArgumentNullException(NameOf(changes))
        RunWithGate(target, Sub()
                                Dim existing As JObject = Nothing
                                Try
                                    existing = LoadUnlocked(target, legacyPath)
                                Catch ex As Newtonsoft.Json.JsonException When replaceMalformed
                                    ' Only an explicit settings-button action may replace malformed JSON.
                                End Try
                                Dim merged = MergeValues(existing, changes)
                                ' Recheck under the gate: another version may have upgraded
                                ' the binding format after this window loaded its settings.
                                Dim bindings = existing?("externalLaunchers")
                                If bindings IsNot Nothing AndAlso Not KnownLauncherFormat(bindings) AndAlso
                                    Not JToken.DeepEquals(bindings, merged("externalLaunchers")) Then
                                    Throw New InvalidDataException("启动器绑定格式已变更或无法识别，请重新打开 PCL-DB 后再保存。原配置已保留。")
                                End If
                                Save(target, merged)
                            End Sub)
    End Sub

    Private Function KnownLauncherFormat(value As JToken) As Boolean
        ' Keep the same format boundary as LauncherBindingsState.Restore.
        Dim document = TryCast(value, JObject), format = document?("format")
        Return format IsNot Nothing AndAlso format.Type = JTokenType.Integer AndAlso format.ToString() = "1" AndAlso
            TypeOf document("bindings") Is JObject
    End Function

    Private Sub RunWithGate(target As String, work As Action)
        ' Migration and every new PCL-DB save coordinate through this per-path, cross-process gate.
        Using gate As New Mutex(False, GateName(target))
            Dim acquired = False
            Try
                Try
                    acquired = gate.WaitOne(TimeSpan.FromSeconds(2))
                Catch ex As AbandonedMutexException
                    acquired = True
                End Try
                If Not acquired Then Throw New IOException("另一个 PCL-DB 进程正在保存设置，请稍后重试。")
                work()
            Finally
                If acquired Then gate.ReleaseMutex()
            End Try
        End Using
    End Sub

    Private Function GateName(path As String) As String
        Using hash = SHA256.Create()
            Dim canonical = IO.Path.GetFullPath(path).ToUpperInvariant()
            Return "Local\PclDownloadBridge.Settings." & BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "")
        End Using
    End Function

    Private Sub DeleteTemporary(path As String)
        If Not File.Exists(path) Then Return
        Try
            File.Delete(path)
        Catch ex As IOException
            ' Do not obscure the original load/save failure.
        Catch ex As UnauthorizedAccessException
            ' A blocked temporary file must not obscure the original failure either.
        End Try
    End Sub
End Module
