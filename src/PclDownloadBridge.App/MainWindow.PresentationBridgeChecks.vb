Imports System.Reflection
Imports System.Runtime.Versioning
Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Microsoft.VisualBasic.CompilerServices
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyPresentationBridgeUi(passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim app = GetType(MainWindow).Assembly, bridge = GetType(Global.PclDownloadBridge.Presentation.MotionCurves).Assembly
        Dim framework = DirectCast(Attribute.GetCustomAttribute(bridge, GetType(TargetFrameworkAttribute)), TargetFrameworkAttribute)
        check(bridge.GetName().Name = "PclDownloadBridge.Presentation" AndAlso framework.FrameworkName = ".NETFramework,Version=v4.8",
            "the C# presentation bridge has its own net48 assembly identity")
        check(bridge.GetExportedTypes().Length = 0 AndAlso bridge.GetReferencedAssemblies().All(Function(item) {"mscorlib", "System", "System.Core", "WindowsBase", "PresentationCore", "PresentationFramework"}.Contains(item.Name)),
            "the presentation bridge exposes no public types and references only Framework and WPF lifecycle assemblies")
        Dim facade = app.GetType("PclDownloadBridge.Controls.PclMotionCurves", True)
        check(facade.Assembly Is app AndAlso facade.IsDefined(GetType(StandardModuleAttribute), False),
            "the original VB motion module remains a standard module in the App assembly")
        Dim flags = BindingFlags.Static Or BindingFlags.NonPublic Or BindingFlags.Public
        For Each signature In {New With {.Name = "Fluent", .Count = 2}, New With {.Name = "Back", .Count = 2}, New With {.Name = "PageY", .Count = 1}}
            Dim method = facade.GetMethod(signature.Name, flags), parameters = method.GetParameters()
            check(method.ReturnType Is GetType(Double) AndAlso parameters.Length = signature.Count AndAlso parameters.All(Function(item) item.ParameterType Is GetType(Double)),
                "the VB motion facade retains its original Double signature: " & signature.Name)
        Next
        Dim power = facade.GetMethod("Back", flags).GetParameters()(1)
        check(power.IsOptional AndAlso CDbl(power.DefaultValue) = 3,
            "the VB Back facade retains its optional power default of three")
        For Each kind In {GetType(PclPageYAnimation), GetType(PclDialogYAnimation), GetType(PclWheelAnimation)}
            check(kind.Assembly Is app AndAlso kind.IsSealed AndAlso kind.BaseType Is GetType(DoubleAnimationBase),
                "the WPF animation type retains its original App identity: " & kind.Name)
        Next
        Dim wheelType = GetType(PclWheelAnimation)
        Dim origin = DirectCast(wheelType.GetField("OriginProperty", BindingFlags.NonPublic Or BindingFlags.Static).GetValue(Nothing), DependencyProperty)
        Dim inputs = DirectCast(wheelType.GetField("InputsProperty", BindingFlags.NonPublic Or BindingFlags.Static).GetValue(Nothing), DependencyProperty)
        check(origin.OwnerType Is wheelType AndAlso inputs.OwnerType Is wheelType AndAlso origin.Name = "Origin" AndAlso inputs.Name = "Inputs",
            "the existing wheel dependency properties retain their App owner and names")
        Dim animations As DoubleAnimationBase() = {New PclPageYAnimation(), New PclDialogYAnimation(), New PclWheelAnimation(70, {New Point(50, 120), New Point(0, -40)})}
        check(animations.All(Function(item)
                                 Dim clone = DirectCast(item.Clone(), DoubleAnimationBase)
                                 clone.Freeze()
                                 Return clone.IsFrozen AndAlso clone.GetType() Is item.GetType() AndAlso clone.Duration = item.Duration
                             End Function), "WPF cloning and freezing preserve the original VB animation factories")
        check(PclMotionCurves.Fluent(-1, 3) = 0 AndAlso PclMotionCurves.Fluent(2, 3) = 1 AndAlso PclMotionCurves.Back(-1) = 0 AndAlso PclMotionCurves.Back(2) = 1,
            "the C# bridge retains the original PCL progress clamping")
        check(Double.IsNaN(PclMotionCurves.Fluent(Double.NaN, 3)) AndAlso Double.IsNaN(PclMotionCurves.Back(Double.NaN)) AndAlso
              PclMotionCurves.PageY(Double.NegativeInfinity) = -16 AndAlso PclMotionCurves.PageY(Double.PositiveInfinity) = 0,
            "the C# bridge retains the original nonfinite progress behavior")
    End Sub
End Class
