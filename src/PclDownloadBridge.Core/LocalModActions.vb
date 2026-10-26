Imports System.ComponentModel
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Microsoft.Win32.SafeHandles

Public NotInheritable Class LocalModActionResult
    Public ReadOnly Property ModsDirectory As String
    Public ReadOnly Property ChangedCount As Integer
    Public ReadOnly Property SkippedCount As Integer
    Public ReadOnly Property Warnings As IReadOnlyList(Of String)
    Public ReadOnly Property BackupDirectory As String
    Public Sub New(directory As String, changed As Integer, skipped As Integer, Optional messages As IEnumerable(Of String) = Nothing, Optional backup As String = Nothing)
        ModsDirectory = directory : ChangedCount = changed : SkippedCount = skipped
        BackupDirectory = backup
        Warnings = If(messages, Enumerable.Empty(Of String)()).Distinct(StringComparer.Ordinal).ToList().AsReadOnly()
    End Sub
End Class

' PCL 5ef81e4 PageInstanceMod.EDMods / InstallMods: PathIndie/mods, terminal
' .disabled/.old and import-to-enabled names. Imports remain create-only;
' explicit updates verify and back up originals; launcher settings are read-only.
Partial Public NotInheritable Class LocalModActions
    Private Sub New()
    End Sub

    Public Shared Function SetEnabled(instance As InstalledInstance, entry As LocalModEntry, enabled As Boolean, token As CancellationToken) As LocalModActionResult
        token.ThrowIfCancellationRequested()
        If entry Is Nothing OrElse Not entry.CanToggle Then Throw New InvalidDataException("无法读取这个 Mod 的文件内容，请刷新后重试。")
        Dim directory = LocalModCatalog.ModsDirectory(instance, token)
        Dim source = Child(directory, entry.FileName)
        If Not Same(source, entry.FilePath) Then Throw New InvalidDataException("Mod 不属于当前实例的实际 Mods 目录。")
        Using gate As New OperationGate(instance, directory, token)
            InstalledInstanceCatalog.EnsureUnchanged(instance, token)
            If Not IO.Directory.Exists(directory) Then Throw New DirectoryNotFoundException("Mods 文件夹已不存在，请刷新。")
            gate.HoldModsDirectory(directory)
            Using lease = NativeLease.Open(source, True)
                CheckDigest(lease.Stream, entry.Length, entry.Sha256, token)
                If entry.Enabled = enabled Then Return New LocalModActionResult(directory, 0, 1)
                Dim destination = Child(directory, If(enabled, entry.EnabledName, entry.EnabledName & ".disabled"))
                EnsureAliases(directory, entry.EnabledName, source)
                If File.Exists(destination) OrElse IO.Directory.Exists(destination) Then Throw Conflict(destination)
                InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                token.ThrowIfCancellationRequested()
                lease.Rename(destination)
                Try
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                    EnsureAliases(directory, entry.EnabledName, destination)
                    token.ThrowIfCancellationRequested()
                Catch ex As Exception
                    Try
                        lease.Rename(source)
                    Catch rollback As Exception
                        Throw New IOException("Mod 状态已变化，但原名称被占用，无法自动恢复；现有文件均已保留，请刷新后检查。", New AggregateException(ex, rollback))
                    End Try
                    Throw
                End Try
                Return New LocalModActionResult(directory, 1, 0)
            End Using
        End Using
    End Function

    Public Shared Function ImportFiles(instance As InstalledInstance, sourcePaths As IEnumerable(Of String), token As CancellationToken) As LocalModActionResult
        Return ImportCore(instance, sourcePaths, token, Nothing)
    End Function

    Friend Shared Function ImportResource(instance As InstalledInstance, source As String, release As ModpackRelease, token As CancellationToken, Optional validate As Action = Nothing) As LocalModActionResult
        If release.Kind <> CommunityResourceKind.Mod OrElse IO.Path.GetFileName(source) <> release.FileName Then Throw New InvalidDataException("下载文件与所选 Mod 不符。")
        Return ImportCore(instance, {source}, token, Nothing, release.Copy(), validate)
    End Function

    Friend Shared Function ImportFilesForChecks(instance As InstalledInstance, sourcePaths As IEnumerable(Of String), token As CancellationToken, beforePublish As Action(Of Integer, String)) As LocalModActionResult
        Return ImportCore(instance, sourcePaths, token, beforePublish)
    End Function

    Private NotInheritable Class Input
        Friend Entry As LocalModEntry
        Friend Source As NativeLease
        Friend Destination As String
        Friend Staged As NativeLease
    End Class

    Private Shared Function ImportCore(instance As InstalledInstance, sourcePaths As IEnumerable(Of String), token As CancellationToken, beforePublish As Action(Of Integer, String), Optional resource As ModpackRelease = Nothing, Optional validateResource As Action = Nothing) As LocalModActionResult
        token.ThrowIfCancellationRequested()
        If sourcePaths Is Nothing Then Throw New ArgumentNullException(NameOf(sourcePaths))
        Dim paths = sourcePaths.ToList()
        If paths.Count = 0 Then Throw New ArgumentException("请选择要导入的 Mod 文件。")
        Dim directory = LocalModCatalog.ModsDirectory(instance, token)
        Using gate As New OperationGate(instance, directory, token)
            validateResource?.Invoke()
            Dim inputs As New List(Of Input), sources As New List(Of NativeLease), retained As New List(Of NativeLease)
            Dim owned As New List(Of NativeLease), warnings As New List(Of String), seen As New Dictionary(Of String, LocalModEntry)(StringComparer.OrdinalIgnoreCase)
            Dim stage As String = Nothing, createdDirectory As Boolean = False, complete As Boolean = False, skipped As Integer = 0
            Try
                ' Every source is held read-only through the batch. A later source
                ' failure therefore happens before any destination is published.
                For Each sourcePath In paths
                    token.ThrowIfCancellationRequested()
                    Dim full = IO.Path.GetFullPath(sourcePath), extension = IO.Path.GetExtension(full)
                    Dim enabledExtension = IO.Path.GetExtension(LocalModCatalog.EnabledFileName(full))
                    If Not {".jar", ".litemod", ".disabled", ".old"}.Contains(extension, StringComparer.OrdinalIgnoreCase) OrElse
                        Not {".jar", ".litemod"}.Contains(enabledExtension, StringComparer.OrdinalIgnoreCase) OrElse Not LocalModCatalog.IsSupportedFile(full) Then
                        Throw New InvalidDataException("请选择 JAR、LiteMod 或带 .disabled/.old 后缀的 Mod；ZIP 整合包请使用整合包导入。")
                    End If
                    ValidatePath(full, False)
                    Dim held = NativeLease.Open(full, False)
                    sources.Add(held)
                    If resource IsNot Nothing Then CommunityResourceDownload.VerifySource(held.Stream, resource, token)
                    Dim entry = LocalModCatalog.Read(full, token)
                    If Not entry.IsArchiveValid OrElse Not String.IsNullOrEmpty(entry.Problem) OrElse entry.Sha256 Is Nothing Then
                        Throw New InvalidDataException("无法导入 " & IO.Path.GetFileName(full) & "：" & If(entry.Problem, "文件不是有效的 Mod 归档。"))
                    End If
                    CheckDigest(held.Stream, entry.Length, entry.Sha256, token)
                    warnings.AddRange(entry.Warnings)
                    Dim previous As LocalModEntry = Nothing
                    If seen.TryGetValue(entry.EnabledName, previous) Then
                        If previous.Length <> entry.Length OrElse Not String.Equals(previous.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) Then Throw Conflict(entry.EnabledName)
                        skipped += 1 : Continue For
                    End If
                    seen.Add(entry.EnabledName, entry)
                    Dim destination = Child(directory, entry.EnabledName)
                    EnsureAliases(directory, entry.EnabledName, destination)
                    If IO.Directory.Exists(destination) Then Throw Conflict(destination)
                    If File.Exists(destination) Then
                        Dim existing = NativeLease.Open(destination, False)
                        retained.Add(existing)
                        Try
                            CheckDigest(existing.Stream, entry.Length, entry.Sha256, token)
                        Catch ex As InvalidDataException
                            Throw Conflict(destination)
                        End Try
                        skipped += 1 : Continue For
                    End If
                    inputs.Add(New Input With {.Entry = entry, .Source = held, .Destination = destination})
                Next
                InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                token.ThrowIfCancellationRequested()
                If inputs.Count = 0 Then
                    EnsureBatchAliases(directory, seen.Keys, token)
                    complete = True
                    Return New LocalModActionResult(directory, 0, skipped, warnings)
                End If
                ValidatePath(directory, True)
                If Not IO.Directory.Exists(directory) Then
                    IO.Directory.CreateDirectory(directory) : createdDirectory = True
                End If
                gate.HoldModsDirectory(directory)
                stage = Child(directory, ".pcl-db-mod-stage-" & Guid.NewGuid().ToString("N"))
                IO.Directory.CreateDirectory(stage)
                gate.HoldModsDirectory(stage)
                For Each item In inputs
                    token.ThrowIfCancellationRequested()
                    Dim temporary = Child(stage, item.Entry.EnabledName)
                    Dim output = NativeLease.Create(temporary)
                    owned.Add(output) : item.Staged = output
                    CopyChecked(item.Source.Stream, output.Stream, item.Entry, token)
                Next
                InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                For i = 0 To inputs.Count - 1
                    Dim item = inputs(i)
                    If resource IsNot Nothing Then EnsureResourceIds(directory, item.Entry, item.Destination, token)
                    token.ThrowIfCancellationRequested()
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                    EnsureAliases(directory, item.Entry.EnabledName, Nothing)
                    beforePublish?.Invoke(i, item.Destination)
                    validateResource?.Invoke()
                    token.ThrowIfCancellationRequested()
                    InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                    ValidatePath(directory, True) : ValidatePath(stage, True)
                    EnsureAliases(directory, item.Entry.EnabledName, Nothing)
                    ' Native rename refuses an already-created destination, and
                    ' the source handle blocks replacement between hash and move.
                    item.Staged.Rename(item.Destination)
                    EnsureAliases(directory, item.Entry.EnabledName, item.Destination)
                    If resource IsNot Nothing Then EnsureResourceIds(directory, item.Entry, item.Destination, token)
                Next
                InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                ' Recheck earlier publications and identical existing targets as
                ' well: an unrelated process may have created a state alias while
                ' a later file was being copied. Rollback still owns only our files.
                EnsureBatchAliases(directory, seen.Keys, token)
                validateResource?.Invoke()
                token.ThrowIfCancellationRequested()
                complete = True
                Return New LocalModActionResult(directory, inputs.Count, skipped, warnings)
            Finally
                ' Owned handles still identify the exact files we created, even
                ' after publication. No path-based delete can remove a replacement.
                For Each item In owned
                    If Not complete Then
                        Try
                            item.DeleteOnClose()
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                            InstallLog.Append(instance.Target.Root, "Mod 导入回收失败，保留新文件：" & item.CurrentPath & " | " & ex.Message)
                        End Try
                    End If
                    item.Dispose()
                Next
                For Each item In retained
                    item.Dispose()
                Next
                For Each item In sources
                    item.Dispose()
                Next
                If stage IsNot Nothing Then gate.ReleaseModsDirectory(stage)
                RemoveEmpty(stage)
                If Not complete AndAlso createdDirectory Then
                    gate.ReleaseModsDirectory(directory)
                    RemoveEmpty(directory)
                End If
            End Try
        End Using
    End Function

    Private Shared Sub EnsureResourceIds(directory As String, entry As LocalModEntry, destination As String, token As CancellationToken)
        If entry.ModIds.Count = 0 Then Return
        For Each file In IO.Directory.EnumerateFiles(directory)
            token.ThrowIfCancellationRequested()
            If String.Equals(IO.Path.GetFullPath(file), destination, StringComparison.OrdinalIgnoreCase) OrElse Not LocalModCatalog.IsSupportedFile(file) Then Continue For
            Dim existing = LocalModCatalog.Read(file, token)
            If existing.ModIds.Intersect(entry.ModIds, StringComparer.Ordinal).Any() Then Throw New IOException("已存在相同 Mod ID 的文件，请先在 Mod 管理中处理：" & existing.FileName)
        Next
    End Sub

    Private Shared Sub CopyChecked(source As FileStream, destination As FileStream, entry As LocalModEntry, token As CancellationToken)
        source.Position = 0
        Using digest = SHA256.Create()
            Dim buffer(65535) As Byte, total As Long = 0
            Do
                token.ThrowIfCancellationRequested()
                Dim count = source.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                destination.Write(buffer, 0, count) : digest.TransformBlock(buffer, 0, count, buffer, 0) : total += count
            Loop
            digest.TransformFinalBlock(New Byte() {}, 0, 0)
            destination.Flush(True)
            If total <> entry.Length OrElse Not String.Equals(Hex(digest.Hash), entry.Sha256, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Mod 副本校验失败：" & entry.FileName)
        End Using
        token.ThrowIfCancellationRequested()
    End Sub
    Private Shared Sub CheckDigest(stream As FileStream, length As Long, expected As String, token As CancellationToken)
        If stream.Length <> length OrElse expected Is Nothing Then Throw New InvalidDataException("Mod 文件已变化，请刷新后重试。")
        stream.Position = 0
        Using digest = SHA256.Create()
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = stream.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                digest.TransformBlock(buffer, 0, count, buffer, 0)
            Loop
            digest.TransformFinalBlock(New Byte() {}, 0, 0)
            If Not String.Equals(Hex(digest.Hash), expected, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Mod 文件内容与扫描结果不同，请刷新后重试。")
        End Using
        stream.Position = 0 : token.ThrowIfCancellationRequested()
    End Sub
    Private Shared Function Hex(bytes As Byte()) As String
        Return BitConverter.ToString(bytes).Replace("-", "")
    End Function
    Private Shared Function Child(directory As String, name As String) As String
        If String.IsNullOrWhiteSpace(name) OrElse IO.Path.GetFileName(name) <> name OrElse name.EndsWith(".", StringComparison.Ordinal) OrElse name.EndsWith(" ", StringComparison.Ordinal) Then Throw New InvalidDataException("Mod 文件名无效。")
        Return SafeFiles.UnderRoot(directory, name)
    End Function
    Friend Shared Sub EnsureAliases(directory As String, enabledName As String, exceptPath As String)
        For Each name In {enabledName, enabledName & ".disabled", enabledName & ".old"}
            Dim candidate = Child(directory, name)
            If exceptPath IsNot Nothing AndAlso Same(candidate, exceptPath) Then Continue For
            If File.Exists(candidate) OrElse IO.Directory.Exists(candidate) Then Throw Conflict(candidate)
        Next
    End Sub
    Private Shared Sub EnsureBatchAliases(directory As String, enabledNames As IEnumerable(Of String), token As CancellationToken)
        For Each name In enabledNames
            token.ThrowIfCancellationRequested()
            EnsureAliases(directory, name, Child(directory, name))
        Next
    End Sub
    Private Shared Function Conflict(path As String) As IOException
        Return New IOException("存在同名或启停状态冲突，不会覆盖已有文件：" & IO.Path.GetFileName(path))
    End Function
    Private Shared Function Same(first As String, second As String) As Boolean
        Return String.Equals(IO.Path.GetFullPath(first), IO.Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase)
    End Function
    Private Shared Sub ValidatePath(path As String, directory As Boolean)
        Dim current = IO.Path.GetFullPath(path)
        If Not directory AndAlso IO.Path.GetFileName(current).IndexOfAny(IO.Path.GetInvalidFileNameChars()) >= 0 Then Throw New InvalidDataException("Mod 路径无效。")
        While current IsNot Nothing
            If File.Exists(current) OrElse IO.Directory.Exists(current) Then
                Dim attributes = File.GetAttributes(current)
                If (attributes And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("Mods 路径包含链接，请选择真实目录。")
                If (directory OrElse Not Same(current, path)) AndAlso (attributes And FileAttributes.Directory) = 0 Then Throw New IOException("文件占用了 Mods 目录。")
            End If
            current = IO.Path.GetDirectoryName(current)
        End While
    End Sub
    Private Shared Sub RemoveEmpty(path As String)
        If path Is Nothing Then Return
        Try
            ValidatePath(path, True)
            If IO.Directory.Exists(path) Then IO.Directory.Delete(path, False)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Keep unexpected/locked files. Cleanup must never replace the result.
        End Try
    End Sub

    Friend NotInheritable Class OperationGate
        Implements IDisposable
        Private Shared ReadOnly Active As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _identity As String
        Private _mutex As Mutex
        Private _ownsMutex As Boolean
        Private _rootLock As FileStream
        Private _runDirectory As SafeFileHandle
        Private ReadOnly _directories As New Dictionary(Of String, SafeFileHandle)(StringComparer.OrdinalIgnoreCase)
        Friend Sub New(instance As InstalledInstance, directory As String, token As CancellationToken, Optional suffix As String = "MODS")
            token.ThrowIfCancellationRequested()
            Dim folderKind = If(suffix = "MODS", "Mods", "资源")
            Dim runDirectory = If(instance?.RunDirectory, IO.Path.GetDirectoryName(directory))
            If instance Is Nothing AndAlso Not IO.Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("共享 Mods 目录无效。")
            If Not IO.Directory.Exists(runDirectory) Then Throw New DirectoryNotFoundException("实际运行目录尚不存在，请先在启动器中确认目录。")
            Dim identity As String = Nothing
            _runDirectory = NativeLease.OpenDirectory(runDirectory, identity)
            _identity = identity & "/" & suffix
            SyncLock Active
                If Not Active.Add(_identity) Then
                    _runDirectory.Dispose() : _runDirectory = Nothing
                    Throw New IOException("另一个任务正在修改这个 " & folderKind & " 文件夹，请稍后重试。")
                End If
            End SyncLock
            Try
                Dim key As String
                Using digest = SHA256.Create()
                    key = Hex(digest.ComputeHash(Encoding.UTF8.GetBytes(_identity)))
                End Using
                _mutex = New Mutex(False, "Local\PclDbMods-" & key)
                Try
                    _ownsMutex = _mutex.WaitOne(0)
                Catch ex As AbandonedMutexException
                    _ownsMutex = True
                End Try
                If Not _ownsMutex Then Throw New IOException("另一个进程正在修改这个 " & folderKind & " 文件夹，请稍后重试。")
                token.ThrowIfCancellationRequested()
                If instance IsNot Nothing Then InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                _directories.Add(IO.Path.GetFullPath(runDirectory), _runDirectory)
                HoldDirectoryChain(runDirectory)
                If instance IsNot Nothing Then HoldDirectoryChain(instance.Target.Root)
                If IO.Directory.Exists(directory) Then HoldModsDirectory(directory)
                If instance IsNot Nothing Then
                    Dim lockPath = SafeFiles.UnderRoot(instance.Target.Root, ".vanilla-downloader/install.lock")
                    IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(lockPath))
                    Try
                        _rootLock = New FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    Catch ex As IOException
                        Throw New IOException("这个游戏目录正在安装或被其他任务占用，请稍后重试。", ex)
                    End Try
                End If
                token.ThrowIfCancellationRequested()
            Catch
                Dispose() : Throw
            End Try
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            _rootLock?.Dispose() : _rootLock = Nothing
            For Each handle In _directories.Values
                handle.Dispose()
            Next
            _directories.Clear()
            _runDirectory?.Dispose() : _runDirectory = Nothing
            If _ownsMutex Then _mutex.ReleaseMutex() : _ownsMutex = False
            _mutex?.Dispose() : _mutex = Nothing
            SyncLock Active
                Active.Remove(_identity)
            End SyncLock
        End Sub
        Friend Sub HoldModsDirectory(directory As String)
            HoldDirectoryChain(directory)
        End Sub
        Friend Sub ReleaseModsDirectory(directory As String)
            Dim full = IO.Path.GetFullPath(directory), handle As SafeFileHandle = Nothing
            If _directories.TryGetValue(full, handle) Then
                handle.Dispose() : _directories.Remove(full)
            End If
        End Sub
        Private Sub HoldDirectoryChain(directory As String)
            Dim current = IO.Path.GetFullPath(directory)
            While current IsNot Nothing
                If Not _directories.ContainsKey(current) Then
                    Dim identity As String = Nothing
                    _directories.Add(current, NativeLease.OpenDirectory(current, identity))
                End If
                current = IO.Path.GetDirectoryName(current)
            End While
        End Sub
    End Class

    ' A rename is performed on the same read-leased file that was hashed. This
    ' prevents a different file being substituted after validation on Windows.
    Friend NotInheritable Class NativeLease
        Implements IDisposable
        Friend ReadOnly Stream As FileStream
        Friend Property CurrentPath As String
        Private Sub New(path As String, create As Boolean, rename As Boolean)
            ValidatePath(path, False)
            Dim access As UInteger = If(create, &HC0010000UI, If(rename, &H80010000UI, &H80000000UI))
            Dim handle = CreateFile(NativePath(path), access, 1UI, IntPtr.Zero, If(create, 1UI, 3UI), &H200080UI, IntPtr.Zero)
            If handle.IsInvalid Then
                Dim errorCode = Marshal.GetLastWin32Error()
                handle.Dispose()
                Throw New IOException("无法访问 Mod 文件：" & IO.Path.GetFileName(path), New Win32Exception(errorCode))
            End If
            Try
                Dim info As FileInformation
                If Not GetInformation(handle, info) Then Throw New IOException("无法确认 Mod 文件身份。", New Win32Exception(Marshal.GetLastWin32Error()))
                If (info.Attributes And CUInt(FileAttributes.ReparsePoint Or FileAttributes.Directory)) <> 0 Then Throw New IOException("Mod 文件是链接或目录。")
                Stream = New FileStream(handle, If(create, FileAccess.ReadWrite, FileAccess.Read), 65536, False)
                handle = Nothing
                CurrentPath = path
            Finally
                handle?.Dispose()
            End Try
        End Sub
        Friend Shared Function Open(path As String, rename As Boolean) As NativeLease
            Return New NativeLease(path, False, rename)
        End Function
        Friend Shared Function Create(path As String) As NativeLease
            Return New NativeLease(path, True, True)
        End Function
        Friend Shared Function OpenDirectory(path As String, ByRef identity As String) As SafeFileHandle
            ValidatePath(path, True)
            Dim handle = CreateFile(NativePath(path), &H80UI, 1UI, IntPtr.Zero, 3UI, &H2200000UI, IntPtr.Zero)
            If handle.IsInvalid Then
                Dim errorCode = Marshal.GetLastWin32Error()
                handle.Dispose()
                Throw New IOException("无法锁定实际运行目录，请关闭占用该目录的操作后重试。", New Win32Exception(errorCode))
            End If
            Try
                Dim info As FileInformation
                If Not GetInformation(handle, info) Then Throw New IOException("无法确认运行目录身份。", New Win32Exception(Marshal.GetLastWin32Error()))
                If (info.Attributes And CUInt(FileAttributes.ReparsePoint)) <> 0 OrElse (info.Attributes And CUInt(FileAttributes.Directory)) = 0 Then Throw New IOException("运行目录是链接或文件。")
                identity = info.VolumeSerial.ToString("X8", Globalization.CultureInfo.InvariantCulture) & ":" & info.IndexHigh.ToString("X8", Globalization.CultureInfo.InvariantCulture) & info.IndexLow.ToString("X8", Globalization.CultureInfo.InvariantCulture)
                Return handle
            Catch
                handle.Dispose() : Throw
            End Try
        End Function
        Friend Sub Rename(destination As String)
            ValidatePath(destination, False)
            Dim bytes = Encoding.Unicode.GetBytes(NativePath(IO.Path.GetFullPath(destination)))
            Dim lengthOffset = IntPtr.Size * 2, nameOffset = lengthOffset + 4, size = nameOffset + bytes.Length + 2
            Dim buffer = Marshal.AllocHGlobal(size)
            Try
                Marshal.Copy(New Byte(size - 1) {}, 0, buffer, size)
                Marshal.WriteInt32(buffer, 0, 0) ' ReplaceIfExists = False.
                Marshal.WriteIntPtr(buffer, IntPtr.Size, IntPtr.Zero)
                Marshal.WriteInt32(buffer, lengthOffset, bytes.Length)
                Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length)
                If Not SetInformation(Stream.SafeFileHandle, 3, buffer, CUInt(size)) Then Throw New IOException("无法重命名 Mod，不会覆盖已有文件：" & IO.Path.GetFileName(destination), New Win32Exception(Marshal.GetLastWin32Error()))
                CurrentPath = destination
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
        End Sub
        Friend Sub DeleteOnClose()
            Dim buffer = Marshal.AllocHGlobal(4)
            Try
                Marshal.WriteInt32(buffer, 1)
                If Not SetInformation(Stream.SafeFileHandle, 4, buffer, 4UI) Then Throw New IOException("无法回收本次新增的 Mod 文件。", New Win32Exception(Marshal.GetLastWin32Error()))
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            Stream.Dispose()
        End Sub
        Private Shared Function NativePath(path As String) As String
            Dim full = IO.Path.GetFullPath(path)
            If full.StartsWith("\\?\", StringComparison.Ordinal) Then Return full
            If full.StartsWith("\\", StringComparison.Ordinal) Then Return "\\?\UNC\" & full.Substring(2)
            Return "\\?\" & full
        End Function
        <StructLayout(LayoutKind.Sequential)>
        Private Structure FileInformation
            Public Attributes As UInteger
            Public CreationTime As Runtime.InteropServices.ComTypes.FILETIME
            Public LastAccessTime As Runtime.InteropServices.ComTypes.FILETIME
            Public LastWriteTime As Runtime.InteropServices.ComTypes.FILETIME
            Public VolumeSerial As UInteger
            Public SizeHigh As UInteger
            Public SizeLow As UInteger
            Public Links As UInteger
            Public IndexHigh As UInteger
            Public IndexLow As UInteger
        End Structure
        <DllImport("kernel32.dll", EntryPoint:="CreateFileW", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateFile(path As String, access As UInteger, share As UInteger, security As IntPtr, creation As UInteger, attributes As UInteger, template As IntPtr) As SafeFileHandle
        End Function
        <DllImport("kernel32.dll", EntryPoint:="GetFileInformationByHandle", SetLastError:=True)>
        Private Shared Function GetInformation(handle As SafeFileHandle, ByRef info As FileInformation) As Boolean
        End Function
        <DllImport("kernel32.dll", EntryPoint:="SetFileInformationByHandle", SetLastError:=True)>
        Private Shared Function SetInformation(handle As SafeFileHandle, informationClass As Integer, information As IntPtr, size As UInteger) As Boolean
        End Function
    End Class
End Class
