Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json.Linq

Public Class Block
    Public Property Index As Integer
    Public Property Timestamp As DateTime
    Public Property Data As List(Of JObject) ' Change Data to a list of JObjects
    Public Property PreviousHash As String
    Public Property Hash As String
    Public Property Nonce As Integer

    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String) ' Update constructor
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Nonce = 0
        Me.Hash = CalculateHash()
    End Sub

    Public Function CalculateHash() As String
        ' Concatenate all transaction hashes for the block data
        'Dim transactionHashes As String = String.Join("", Me.Data.Select(Function(t) t("Hash").ToString()))
        Dim dataToHash As String = Me.Index.ToString() & Me.Timestamp.ToString() & Me.Data.ToList.ToString() & Me.PreviousHash & Me.Nonce.ToString()
        'Dim dataToHash As String = Me.Index.ToString() & Me.Timestamp.ToString() & Me.Hash & Me.PreviousHash & Me.Nonce.ToString()
        Return HashString(dataToHash)
    End Function

    'Simple proof of work to "mine"
    Public Sub Mine(difficulty As Integer)
        Dim leadingZeros As String = New String("0"c, difficulty)
        While Not Me.Hash.StartsWith(leadingZeros)
            Me.Nonce += 1
            Me.Hash = CalculateHash()
        End While
    End Sub

    Private Function HashString(input As String) As String
        Using sha256 As SHA256 = SHA256.Create()
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(input)
            Dim hashBytes As Byte() = sha256.ComputeHash(bytes)
            Return BitConverter.ToString(hashBytes).Replace("-", "").ToLower()
        End Using
    End Function
End Class