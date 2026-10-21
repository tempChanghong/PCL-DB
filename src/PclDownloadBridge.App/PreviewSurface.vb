Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows

' A hidden presentation source makes WPF's IsVisible and viewport events meaningful
' during isolated checks and rendering. It never shows or activates a desktop window.
Friend Module PreviewSurface
    Friend Function Attach(content As Visual, width As Integer, height As Integer) As HwndSource
        Dim element = TryCast(content, FrameworkElement)
        If element IsNot Nothing AndAlso Not element.Resources.MergedDictionaries.Contains(Application.Current.Resources) Then
            ' Keep dynamic resource invalidation connected when this test/render root
            ' is temporarily detached from Application.Windows.
            element.Resources.MergedDictionaries.Add(Application.Current.Resources)
        End If
        Dim parameters As New HwndSourceParameters("PclDownloadBridge isolated preview") With {
            .Width = width, .Height = height, .PositionX = -32000, .PositionY = -32000,
            .WindowStyle = &H80000000, .ExtendedWindowStyle = &H8000080}
        Dim source As New HwndSource(parameters)
        source.RootVisual = content
        Return source
    End Function
End Module
