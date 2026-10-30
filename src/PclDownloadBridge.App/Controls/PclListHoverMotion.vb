Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyListItem.RefreshColor at PCL 5ef81e4. StateLast avoids restarting identical states.
    Friend NotInheritable Class PclListHoverMotion
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.ListHoverMotion

        Friend Sub New(background As Border, wholeScale As ScaleTransform)
            _implementation = New Global.PclDownloadBridge.Presentation.ListHoverMotion(background, wholeScale)
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _implementation.Transition
            End Get
        End Property
        Friend Sub RefreshPalette(color As Color)
            _implementation.RefreshPalette(color)
        End Sub

        Friend Sub Update(active As Boolean, pressed As Boolean, sidebar As Boolean, color As Color, animate As Boolean)
            _implementation.Update(active, pressed, sidebar, color, animate)
        End Sub
    End Class
End Namespace
