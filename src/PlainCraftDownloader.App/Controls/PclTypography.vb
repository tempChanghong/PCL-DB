Imports System.Windows.Media

Namespace Controls
    Public NotInheritable Class PclTypography
        ' Keep the URI separate from the fallback list: pack URIs contain commas themselves.
        Public Shared ReadOnly Property Font As FontFamily = New FontFamily(New Uri("pack://application:,,,/PlainCraftDownloader;component/"), "Resources/#PCL English, Microsoft YaHei UI")
    End Class
End Namespace
