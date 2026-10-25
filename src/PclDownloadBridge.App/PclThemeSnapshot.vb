Imports System.Globalization
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

' Read only the ordinary setting used by PCL 5ef81e4 Settings/Get and
' ModBase/IniGetContent. Never load PCL.exe or its settings API: a failed
' encrypted setting read there can write defaults back to the source.
Friend NotInheritable Class PclThemeSnapshot
    Friend Const MaximumBytes As Integer = 1024 * 1024
    Private Shared ReadOnly Names As String() = {"龙猫蓝", "甜柠青", "小草绿", "菠萝黄", "橡木棕", "玄素黑", "铁杆粉", "神秘紫", "秋仪金", "活跃橙", "跳票红", "极客蓝", "滑稽彩", "欧皇彩", "自定义"}
    Private Shared ReadOnly Presets As String() = {"blue", "cyan", "green", "yellow", "brown", "black"}
    Friend ReadOnly Success As Boolean
    Friend ReadOnly ThemeId As Integer
    Friend ReadOnly IsDefault As Boolean
    Friend ReadOnly ErrorText As String
    Friend ReadOnly Fingerprint As String

    Private Sub New(ok As Boolean, id As Integer, defaulted As Boolean, message As String, fingerprint As String)
        Success = ok : ThemeId = id : IsDefault = defaulted : ErrorText = message : Me.Fingerprint = fingerprint
    End Sub
    Friend ReadOnly Property Name As String
        Get
            Return If(ThemeId >= 0 AndAlso ThemeId < Names.Length, Names(ThemeId), "未知主题 " & ThemeId.ToString(CultureInfo.InvariantCulture))
        End Get
    End Property
    Friend ReadOnly Property Preset As String
        Get
            If Success AndAlso ThemeId = 12 Then Return "custom"
            Return If(Success AndAlso ThemeId >= 0 AndAlso ThemeId < Presets.Length, Presets(ThemeId), Nothing)
        End Get
    End Property
    Friend ReadOnly Property StateKey As String
        Get
            Return If(Success, "ok:" & ThemeId.ToString(CultureInfo.InvariantCulture) & ":" & IsDefault.ToString(), "error:" & ErrorText)
        End Get
    End Property
    Friend Shared Function Failure(message As String) As PclThemeSnapshot
        Return New PclThemeSnapshot(False, 0, False, message, "error:" & message)
    End Function
    Friend Shared Function NormalizePath(path As String) As String
        If String.IsNullOrWhiteSpace(path) OrElse path.Length > 4096 OrElse Not IO.Path.IsPathRooted(path) OrElse IO.Path.GetPathRoot(path).Length < 3 OrElse
            path.StartsWith("\\.\", StringComparison.Ordinal) OrElse path.StartsWith("\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("请选择明确的 PCL 程序或配置路径。")
        End If
        Dim full = IO.Path.GetFullPath(path.Trim())
        If Not String.Equals(IO.Path.GetFileName(full), "Setup.ini", StringComparison.OrdinalIgnoreCase) Then Throw New ArgumentException("请选择 PCL 的 Setup.ini 配置。")
        Return full
    End Function
    Friend Shared Function FromSelection(path As String) As String
        If String.IsNullOrWhiteSpace(path) OrElse Not IO.Path.IsPathRooted(path) OrElse IO.Path.GetPathRoot(path).Length < 3 Then Throw New ArgumentException("请选择明确的 PCL 路径。")
        If String.Equals(IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) Then
            path = IO.Path.Combine(IO.Path.GetDirectoryName(IO.Path.GetFullPath(path)), "PCL", "Setup.ini")
        End If
        Return NormalizePath(path)
    End Function
    Friend Shared Function Read(path As String) As PclThemeSnapshot
        Try
            path = NormalizePath(path)
            Dim bytes As Byte()
            Using input As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete), buffer As New MemoryStream()
                Dim originalLength = input.Length
                If originalLength = 0 OrElse originalLength > MaximumBytes Then Return Failure("配置为空或超过读取上限。")
                Dim block(8191) As Byte
                Do
                    Dim count = input.Read(block, 0, block.Length)
                    If count = 0 Then Exit Do
                    buffer.Write(block, 0, count)
                    If buffer.Length > MaximumBytes Then Return Failure("配置超过读取上限。")
                Loop
                If input.Length <> originalLength OrElse buffer.Length <> originalLength Then Return Failure("配置正在写入，请稍候。")
                bytes = buffer.ToArray()
            End Using
            Return Parse(bytes)
        Catch ex As Exception When TypeOf ex Is FileNotFoundException OrElse TypeOf ex Is DirectoryNotFoundException
            Return Failure("找不到所选配置。")
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Security.SecurityException
            Return Failure("配置暂时无法读取，将自动重试。")
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
            Return Failure("配置路径无效，请重新选择 PCL。")
        End Try
    End Function
    Friend Shared Function Parse(bytes As Byte()) As PclThemeSnapshot
        If bytes Is Nothing OrElse bytes.Length = 0 OrElse bytes.Length > MaximumBytes Then Return Failure("配置为空或超过读取上限。")
        Try
            Dim text As String
            Using reader As New StreamReader(New MemoryStream(bytes), New UTF8Encoding(False, True), True)
                text = reader.ReadToEnd()
            End Using
            If text.IndexOf(ChrW(0)) >= 0 OrElse text.IndexOf(ChrW(&HFFFD)) >= 0 Then Return Failure("配置文本损坏或编码不受支持。")
            Dim found As String = Nothing, records = 0
            For Each raw In text.Split(ChrW(10))
                Dim line = raw.Trim(), split = line.IndexOf(":"c)
                If line.Length = 0 OrElse line.StartsWith("#", StringComparison.Ordinal) OrElse line.StartsWith(";", StringComparison.Ordinal) Then Continue For
                If split <= 0 Then
                    If line.StartsWith("UiLauncherTheme", StringComparison.Ordinal) Then Return Failure("配置内容不完整或格式无效。")
                    Continue For ' IniGetContent also ignores unrelated lines without a colon.
                End If
                records += 1
                ' PCL keeps the last occurrence of each key. This is its flat
                ' colon-separated format, not Windows INI sections or '=' pairs.
                If line.Substring(0, split).Trim() = "UiLauncherTheme" Then found = line.Substring(split + 1).Trim()
            Next
            If records = 0 Then Return Failure("配置内容为空。")
            Dim id = 0
            If found IsNot Nothing AndAlso Not Integer.TryParse(found, NumberStyles.Integer, CultureInfo.InvariantCulture, id) Then Return Failure("主题编号无效。")
            Dim fingerprint As String
            Using hash = SHA256.Create()
                fingerprint = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "")
            End Using
            Return New PclThemeSnapshot(True, id, found Is Nothing, "", fingerprint)
        Catch ex As DecoderFallbackException
            Return Failure("配置文本损坏或编码不受支持。")
        End Try
    End Function
End Class
