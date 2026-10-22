Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module RepairMetadataChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub
    Private Class InlineProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Install As InstallationChecks.InstallFixture
        Friend ReadOnly JsonPath As String
        Friend ReadOnly JarPath As String
        Friend ReadOnly IniPath As String
        Friend OriginalJson As Byte()
        Friend ReadOnly Corrupt As Byte()
        Friend ReadOnly Preferences As Byte() = Encoding.UTF8.GetBytes("VersionArgumentIndie:1" & vbCrLf & "UserValue:保留设置" & vbCrLf)
        Friend Sub New(root As String, name As String)
            Install = New InstallationChecks.InstallFixture(root, "repair-metadata-" & name)
            Dim instance = Path.Combine(Install.Root, "versions", "existing")
            JsonPath = Path.Combine(instance, "existing.json") : JarPath = Path.Combine(instance, "existing.jar")
            IniPath = Path.Combine(instance, "PCL", "Setup.ini")
            Corrupt = DirectCast(Install.Server.Files("/client").Clone(), Byte()) : Corrupt(0) = Corrupt(0) Xor CByte(1)
        End Sub
        Friend Async Function SeedAsync() As Task
            Await Install.InstallAsync("existing")
            OriginalJson = File.ReadAllBytes(JsonPath)
            File.WriteAllBytes(JarPath, Corrupt)
            Directory.CreateDirectory(Path.GetDirectoryName(IniPath)) : File.WriteAllBytes(IniPath, Preferences)
        End Function
        Friend Function PrivateStateReleased() As Boolean
            Dim staging = Path.Combine(Install.Root, ".vanilla-downloader", "staging")
            If Directory.Exists(staging) AndAlso Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any() Then Return False
            Using hold As New FileStream(Path.Combine(Install.Root, ".vanilla-downloader", "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Return True
            End Using
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Install.Dispose()
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-repair-metadata-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-repair-metadata-conflict" Then Await ConflictAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-repair-metadata-encoding" Then Await EncodingAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-repair-metadata-guard" Then Await GuardAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function ConflictAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"loader", "same-length", "formatting", "deleted", "locked"}
            Using fixture As New Fixture(root, mode)
                Await fixture.SeedAsync()
                Dim changed = fixture.OriginalJson
                If mode = "loader" OrElse mode = "same-length" Then
                    Dim metadata = JObject.Parse(Encoding.UTF8.GetString(changed))
                    If mode = "loader" Then metadata("inheritsFrom") = fixture.Install.Entry.Id
                    If mode = "same-length" Then metadata("mainClass") = "vanilla.Test"
                    changed = Encoding.UTF8.GetBytes(metadata.ToString())
                    If mode = "same-length" AndAlso changed.Length <> fixture.OriginalJson.Length Then Throw New InvalidOperationException("Same-length fixture changed length.")
                End If
                If mode = "formatting" Then changed = fixture.OriginalJson.Concat(Encoding.UTF8.GetBytes(vbCrLf)).ToArray()
                Dim reached = False, failure As Exception = Nothing, externalLock As FileStream = Nothing
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase <> "commit" OrElse reached Then Return
                    reached = True
                    If mode = "deleted" Then
                        File.Delete(fixture.JsonPath)
                    ElseIf mode = "locked" Then
                        externalLock = New FileStream(fixture.JsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
                    Else
                        File.WriteAllBytes(fixture.JsonPath, changed)
                    End If
                End Sub)
                Try
                    Await fixture.Install.InstallAsync("existing", repair:=True, progress:=progress)
                Catch ex As Exception
                    failure = ex
                Finally
                    externalLock?.Dispose()
                End Try
                Console.WriteLine("REPAIR_METADATA mode=" & mode & " outcome=" & If(failure?.GetType().Name, "success") & " clientReplaced=" & Not File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Corrupt))
                Check(reached AndAlso If(mode = "deleted" OrElse mode = "locked", TypeOf failure Is IOException, TypeOf failure Is InvalidDataException), "repair rejects changed, removed or write-held metadata before replacing the client: " & mode, passed)
                Check(File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Corrupt) AndAlso If(mode = "deleted", Not File.Exists(fixture.JsonPath), File.ReadAllBytes(fixture.JsonPath).SequenceEqual(changed)), "failed repair preserves the external metadata state and existing client: " & mode, passed)
                Check(File.ReadAllBytes(fixture.IniPath).SequenceEqual(fixture.Preferences) AndAlso fixture.PrivateStateReleased(), "metadata conflict preserves PCL preferences and releases private payloads and root lock: " & mode, passed)
                Dim retryJson = If(mode = "formatting", changed, fixture.OriginalJson)
                File.WriteAllBytes(fixture.JsonPath, retryJson)
                Dim result = Await fixture.Install.InstallAsync("existing", repair:=True)
                Check(result.NetworkBytes = 0 AndAlso fixture.Install.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Install.Server.Files("/client")) AndAlso File.ReadAllBytes(fixture.JsonPath).SequenceEqual(retryJson), "resolved metadata conflict retries with the retained checked client and preserves JSON: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function EncodingAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"utf8-bom", "utf16-le", "utf16-be", "readonly"}
            Using fixture As New Fixture(root, mode)
                Await fixture.SeedAsync()
                Dim codec As Encoding = If(mode = "utf16-le", Encoding.Unicode, If(mode = "utf16-be", Encoding.BigEndianUnicode, New UTF8Encoding(True)))
                Dim bytes = If(mode = "readonly", fixture.OriginalJson, codec.GetPreamble().Concat(codec.GetBytes(Encoding.UTF8.GetString(fixture.OriginalJson))).ToArray())
                File.WriteAllBytes(fixture.JsonPath, bytes)
                If mode = "readonly" Then File.SetAttributes(fixture.JsonPath, FileAttributes.ReadOnly)
                Try
                    Await fixture.Install.InstallAsync("existing", repair:=True)
                    Check(File.ReadAllBytes(fixture.JsonPath).SequenceEqual(bytes) AndAlso File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Install.Server.Files("/client")), "repair preserves the exact original metadata encoding and repairs its client: " & mode, passed)
                    Check(File.ReadAllBytes(fixture.IniPath).SequenceEqual(fixture.Preferences) AndAlso fixture.PrivateStateReleased() AndAlso (mode <> "readonly" OrElse (File.GetAttributes(fixture.JsonPath) And FileAttributes.ReadOnly) <> 0), "metadata guard preserves PCL preferences, attributes and cleanup: " & mode, passed)
                Finally
                    If mode = "readonly" Then File.SetAttributes(fixture.JsonPath, FileAttributes.Normal)
                End Try
            End Using
        Next
    End Function
    Private Async Function GuardAsync(root As String, passed As List(Of String)) As Task
        For Each cancelAtGuard In {False, True}
            Using fixture As New Fixture(root, "guard-" & cancelAtGuard), cancel As New CancellationTokenSource()
                Await fixture.SeedAsync()
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}, previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim armed = False, probed As Integer = 0, blocked = False, readable = False, failure As Exception = Nothing
                Dim progress As New InlineProgress(Sub(value)
                    If value.Phase = "commit" Then armed = True
                End Sub)
                CopyCancellationSha1.OnHash = Sub()
                    If Not armed OrElse Interlocked.CompareExchange(probed, 1, 0) <> 0 Then Return
                    Using reader = File.OpenRead(fixture.JsonPath)
                        readable = True
                    End Using
                    Try
                        Using writer As New FileStream(fixture.JsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
                        End Using
                    Catch ex As IOException
                        blocked = True
                    End Try
                    If cancelAtGuard Then cancel.Cancel()
                End Sub
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Try
                        Await fixture.Install.InstallAsync("existing", repair:=True, progress:=progress, token:=cancel.Token)
                    Catch ex As Exception
                        failure = ex
                    End Try
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each entry In previous
                        CryptoConfig.AddAlgorithm(entry.Value, entry.Key)
                    Next
                End Try
                Check(armed AndAlso probed = 1 AndAlso blocked AndAlso readable, "final repair guard allows readers and blocks writers during the actual client check: " & cancelAtGuard, passed)
                Check(If(cancelAtGuard, TypeOf failure Is OperationCanceledException AndAlso File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Corrupt), failure Is Nothing AndAlso File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Install.Server.Files("/client"))), "final guarded repair preserves cancellation and publication outcomes: " & cancelAtGuard, passed)
                Using writer As New FileStream(fixture.JsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    Check(File.ReadAllBytes(fixture.IniPath).SequenceEqual(fixture.Preferences) AndAlso fixture.PrivateStateReleased(), "success or cancellation releases the metadata guard and preserves PCL preferences: " & cancelAtGuard, passed)
                End Using
                Dim result = Await fixture.Install.InstallAsync("existing", repair:=True)
                Check(result.NetworkBytes = 0 AndAlso fixture.Install.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(fixture.JsonPath).SequenceEqual(fixture.OriginalJson) AndAlso File.ReadAllBytes(fixture.JarPath).SequenceEqual(fixture.Install.Server.Files("/client")), "retry after guarded repair reuses checked content and preserves exact JSON: " & cancelAtGuard, passed)
            End Using
        Next
    End Function
End Module
