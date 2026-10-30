Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyLocalModItem.Checked: the two height deltas run in parallel.
    ' Selection owns its marker and title ink independently of hover/action motion.
    Friend NotInheritable Class PclLocalModSelectionMotion
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.LocalModSelectionMotion

        Friend Sub New(marker As Border, title As TextBlock)
            _implementation = New Global.PclDownloadBridge.Presentation.LocalModSelectionMotion(marker, title,
                AddressOf PclAnimation.Brush, Function(origin, target) New PclSelectedHeightAnimation(origin, target),
                Function(origin, target, milliseconds) New PclRadioColorAnimation(origin, target, milliseconds))
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _implementation.Transition
            End Get
        End Property
        Friend Sub RefreshPalette(key As String)
            _implementation.RefreshPalette(key)
        End Sub
        Friend Sub Update(selected As Boolean, key As String, animate As Boolean, pause As Boolean)
            _implementation.Update(selected, key, animate, pause)
        End Sub
    End Class
End Namespace
