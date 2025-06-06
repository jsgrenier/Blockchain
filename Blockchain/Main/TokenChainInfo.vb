Public Class TokenChainInfo
    Public Property Name As String
    Public Property Symbol As String
    Public Property TotalSupply As Decimal ' For altcoins, this is their initial supply
    Public Property CreatorAddress As String
    Public Property CreationTxId As String
    Public Property CreationBlockIndex As Integer
    Public Property CreationTimestamp As DateTime
End Class