Public Class Token
    Public Property Name As String
    Public Property Symbol As String
    Public Property InitialSupply As Decimal ' Changed to Decimal
    Public Property Owner As String ' Public Key of the initial owner

    Public Sub New(name As String, symbol As String, initialSupply As Decimal, owner As String)
        Me.Name = name
        Me.Symbol = symbol
        Me.InitialSupply = initialSupply
        Me.Owner = owner
    End Sub
End Class