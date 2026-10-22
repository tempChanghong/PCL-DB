Imports System.IO
Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Imaging

Partial Public Class App
    Inherits System.Windows.Application
    <STAThread>
    Public Shared Sub Main()
        RuntimeBootstrap.Initialize()
        StartApplication()
    End Sub
    <Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.NoInlining)>
    Private Shared Sub StartApplication()
        Dim app As New App()
        app.InitializeComponent()
        app.Run()
    End Sub
    Protected Overrides Sub OnStartup(e As StartupEventArgs)
        MyBase.OnStartup(e)
        Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
        Dim standaloneChecks = Array.IndexOf(e.Args, "--standalone-checks")
        If standaloneChecks >= 0 AndAlso e.Args.Length > standaloneChecks + 1 Then
            Shutdown(SingleExecutableChecks.Run(e.Args(standaloneChecks + 1)))
            Return
        End If
        AddHandler DispatcherUnhandledException, Sub(sender, args)
                                                    Dim snapshotIndex = Array.IndexOf(e.Args, "--snapshot")
                                                    If snapshotIndex >= 0 AndAlso e.Args.Length > snapshotIndex + 1 Then
                                                        File.WriteAllText(e.Args(snapshotIndex + 1) & ".error.log", args.Exception.ToString())
                                                        args.Handled = True
                                                        Shutdown(1)
                                                        Return
                                                    End If
                                                    MessageBox.Show(args.Exception.Message, ProductInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error)
                                                    args.Handled = True
                                                End Sub
        Dim preview = Array.IndexOf(e.Args, "--snapshot")
        Dim checks = Array.IndexOf(e.Args, "--ui-checks")
        Dim appearanceChecks = Array.IndexOf(e.Args, "--appearance-checks")
        Dim cardChecks = Array.IndexOf(e.Args, "--card-checks")
        Dim navigationChecks = Array.IndexOf(e.Args, "--navigation-checks")
        Dim motionChecks = Array.IndexOf(e.Args, "--motion-checks")
        Dim compatibilityChecks = Array.IndexOf(e.Args, "--compatibility-checks")
        Dim developerChecks = Array.IndexOf(e.Args, "--developer-checks")
        Dim window As New MainWindow(preview >= 0 OrElse checks >= 0 OrElse appearanceChecks >= 0 OrElse cardChecks >= 0 OrElse navigationChecks >= 0 OrElse motionChecks >= 0 OrElse compatibilityChecks >= 0 OrElse developerChecks >= 0)
        MainWindow = window
        Dim checkIndex = {checks, appearanceChecks, cardChecks, navigationChecks, motionChecks, compatibilityChecks, developerChecks}.Where(Function(value) value >= 0).DefaultIfEmpty(-1).First()
        If checkIndex >= 0 AndAlso e.Args.Length > checkIndex + 1 Then
            Dim scope = If(checks >= 0, "all", If(appearanceChecks >= 0, "appearance", If(cardChecks >= 0, "cards", If(navigationChecks >= 0, "navigation", If(compatibilityChecks >= 0, "compatibility", "developer")))))
            If checkIndex = motionChecks Then scope = "motion"
            Shutdown(UiCheckRunner.Run(e.Args(checkIndex + 1), scope,
                Sub(directory, passed)
                    If scope = "all" Then
                        window.VerifyUi(directory, passed)
                    ElseIf scope = "appearance" Then
                        window.VerifyAppearanceUi(directory, passed)
                    ElseIf scope = "cards" Then
                        window.VerifyCardUi(directory, passed)
                    ElseIf scope = "navigation" Then
                        window.VerifyNavigationUi(directory, passed)
                    ElseIf scope = "motion" Then
                        window.VerifyMotionUi(directory, passed)
                    ElseIf scope = "developer" Then
                        window.VerifyDeveloperUi(directory, passed)
                    Else
                        window.VerifyCompatibilityUi(directory, passed)
                    End If
                End Sub))
            Return
        End If
        If preview >= 0 AndAlso e.Args.Length > preview + 1 Then
            Dim pageIndex = Array.IndexOf(e.Args, "--page")
            If pageIndex >= 0 AndAlso e.Args.Length > pageIndex + 1 Then window.PreviewPage(e.Args(pageIndex + 1))
            Dim packageIndex = Array.IndexOf(e.Args, "--package")
            If packageIndex >= 0 AndAlso e.Args.Length > packageIndex + 1 Then window.PreviewCurseForge(e.Args(packageIndex + 1))
            Dim width = 850
            Dim height = 500
            Dim sizeIndex = Array.IndexOf(e.Args, "--size")
            If sizeIndex >= 0 AndAlso e.Args.Length > sizeIndex + 1 Then
                Dim sizes = e.Args(sizeIndex + 1).Split("x"c)
                width = Integer.Parse(sizes(0))
                height = Integer.Parse(sizes(1))
            End If
            Dim content = DirectCast(window.Content, FrameworkElement)
            window.Content = Nothing
            Dim canvas As New System.Windows.Controls.Border With {.Width = width, .Height = height, .Child = content}
            Using previewSource = PreviewSurface.Attach(canvas, width, height)
                canvas.Measure(New Size(width, height))
                canvas.Arrange(New Rect(0, 0, width, height))
                canvas.UpdateLayout()
                Global.PclDownloadBridge.MainWindow.FlushControlLayout()
                canvas.UpdateLayout()
                If pageIndex >= 0 AndAlso e.Args(pageIndex + 1) = "developer" Then
                    DirectCast(window.FindName("DeveloperCard"), FrameworkElement).BringIntoView()
                    canvas.UpdateLayout()
                End If
                Dim bitmap As New RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32)
                bitmap.Render(canvas)
                Dim encoder As New PngBitmapEncoder()
                encoder.Frames.Add(BitmapFrame.Create(bitmap))
                Using stream = File.Create(Path.GetFullPath(e.Args(preview + 1)))
                    encoder.Save(stream)
                End Using
                previewSource.RootVisual = Nothing
            End Using
            Shutdown()
        Else
            window.Show()
        End If
    End Sub
End Class
