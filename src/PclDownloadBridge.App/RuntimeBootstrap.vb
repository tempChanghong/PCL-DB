Imports System.Collections.Concurrent
Imports System.IO
Imports System.Reflection
Imports System.Threading

' PCL Application.New installs its resource resolver before touching dependency types.
' Keep this bootstrap limited to framework APIs for the same reason.
Friend NotInheritable Class RuntimeBootstrap
    Private Shared ReadOnly Loaded As New ConcurrentDictionary(Of String, Lazy(Of Assembly))(StringComparer.OrdinalIgnoreCase)
    Private Shared _initialized As Integer

    Friend Shared ReadOnly Property IsStandalone As Boolean
        Get
#If PCD_SINGLE_EXE Then
            Return True
#Else
            Return False
#End If
        End Get
    End Property

    Friend Shared Sub Initialize()
        If Interlocked.Exchange(_initialized, 1) <> 0 Then Return
        ' These switches used to depend only on the adjacent exe.config.
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", False)
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", False)
        If IsStandalone Then AddHandler AppDomain.CurrentDomain.AssemblyResolve, Function(sender, args) Resolve(args.Name)
    End Sub

    Friend Shared Function Resolve(displayName As String) As Assembly
        If Not IsStandalone Then Return Nothing
        Dim requested As New AssemblyName(displayName)
        Dim canonical As String
        If String.Equals(requested.Name, "PclDownloadBridge.Core", StringComparison.OrdinalIgnoreCase) Then
            canonical = "PclDownloadBridge.Core"
        ElseIf String.Equals(requested.Name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) Then
            canonical = "Newtonsoft.Json"
        ElseIf String.Equals(requested.Name, "PclDownloadBridge.Presentation", StringComparison.OrdinalIgnoreCase) Then
            canonical = "PclDownloadBridge.Presentation"
        Else
            Return Nothing
        End If
        Dim resolved = Loaded.GetOrAdd(canonical, Function(name) New Lazy(Of Assembly)(Function() LoadEmbedded(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value
        Dim actual = resolved.GetName()
        If Not String.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase) OrElse
           (requested.Version IsNot Nothing AndAlso requested.Version <> actual.Version) OrElse
           Not String.Equals(If(requested.CultureName, ""), If(actual.CultureName, ""), StringComparison.OrdinalIgnoreCase) OrElse
           Not If(requested.GetPublicKeyToken(), New Byte() {}).SequenceEqual(If(actual.GetPublicKeyToken(), New Byte() {})) Then Return Nothing
        Return resolved
    End Function

    Private Shared Function LoadEmbedded(name As String) As Assembly
        Using source = GetType(RuntimeBootstrap).Assembly.GetManifestResourceStream("PclDownloadBridge.Dependencies." & name & ".dll")
            If source Is Nothing Then Throw New FileNotFoundException("程序缺少内嵌依赖：" & name)
            Using buffer As New MemoryStream()
                source.CopyTo(buffer)
                Return Assembly.Load(buffer.ToArray())
            End Using
        End Using
    End Function
End Class
