Imports System.Reflection

Friend Module ProductInfo
    Friend Const DisplayName As String = "PCL-DB"
    Friend Const FullName As String = "Plain Craft Launcher - Download Bridge"
    Friend Const PclReference As String = "5ef81e490ae46b1100d61e33d0183094c7a80ca8"

    Friend ReadOnly Property VersionText As String
        Get
            Dim assembly = Reflection.Assembly.GetExecutingAssembly()
            Dim release = assembly.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)().InformationalVersion.Split("+"c)(0)
            Return If(release.EndsWith("-beta.1", StringComparison.Ordinal), "Beta " & assembly.GetName().Version.ToString(2), release)
        End Get
    End Property

    Friend Function VersionReport() As String
        Return String.Join(Environment.NewLine, {
            DisplayName & " " & Assembly.GetExecutingAssembly().GetName().Version.ToString(3) & " (" & VersionText & ")",
            FullName,
            "构建标识：" & Assembly.GetExecutingAssembly().GetCustomAttribute(Of AssemblyInformationalVersionAttribute)().InformationalVersion,
            "Minecraft Java 原版下载补全工具",
            "运行环境：.NET Framework 4.8 / CLR " & Environment.Version.ToString(),
            "PCL 代码参照：" & PclReference,
            "独立项目，非 PCL 官方发行版。"})
    End Function
End Module
