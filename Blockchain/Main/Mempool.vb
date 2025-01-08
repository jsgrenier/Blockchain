Imports Newtonsoft.Json.Linq

Public Class Mempool
    Private _transactions As New List(Of JObject)

    ' Add a transaction to the mempool
    Public Sub AddTransaction(transaction As JObject)
        _transactions.Add(transaction)
    End Sub

    ' Retrieve all transactions in the mempool
    Public Function GetTransactions() As List(Of JObject)
        Return _transactions
    End Function

    ' Clear the mempool after transactions are added to a block
    Public Sub Clear()
        _transactions.Clear()
    End Sub

    ' Remove a specific transaction from the mempool
    Public Sub RemoveTransaction(transaction As JObject)
        _transactions.Remove(transaction)
    End Sub

    ' Get the count of transactions in the mempool
    Public Function Count() As Integer
        Return _transactions.Count
    End Function

    Public Function GetTransactionByTxId(txId As String) As JObject
        For Each transaction As JObject In _transactions
            Dim transactionData = JObject.Parse(transaction("transaction").ToString())
            If transactionData("txId").ToString() = txId Then
                Return transaction
            End If
        Next
        Return Nothing
    End Function
End Class
