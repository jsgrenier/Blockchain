Imports Newtonsoft.Json.Linq
Imports System.Collections.Generic

Public Class Mempool
    Private _transactions As New List(Of JObject)
    Private ReadOnly _lock As New Object() ' Dedicated lock object for thread safety

    ' Add a transaction to the mempool
    Public Sub AddTransaction(transaction As JObject)
        SyncLock _lock
            _transactions.Add(transaction)
        End SyncLock
    End Sub

    ' Retrieve a copy of all transactions in the mempool
    Public Function GetTransactions() As List(Of JObject)
        SyncLock _lock
            Return New List(Of JObject)(_transactions) ' Return a copy
        End SyncLock
    End Function

    ' Clear the mempool after transactions are added to a block
    Public Sub Clear()
        SyncLock _lock
            _transactions.Clear()
        End SyncLock
    End Sub

    ' Remove a specific transaction from the mempool
    Public Sub RemoveTransaction(transaction As JObject)
        SyncLock _lock
            ' For JObject, direct removal might be tricky if it's not the exact same instance.
            ' It's often better to remove by a unique identifier like txId.
            Dim txIdToRemove As String = Nothing
            Dim transactionDataToRemove = JObject.Parse(transaction("transaction").ToString())
            If transactionDataToRemove.ContainsKey("txId") Then
                txIdToRemove = transactionDataToRemove("txId").ToString()
            End If

            If txIdToRemove IsNot Nothing Then
                _transactions.RemoveAll(Function(t)
                                            Dim currentTxData = JObject.Parse(t("transaction").ToString())
                                            Return currentTxData.ContainsKey("txId") AndAlso currentTxData("txId").ToString() = txIdToRemove
                                        End Function)
            Else
                _transactions.Remove(transaction) ' Fallback if txId not found, might not work as expected
            End If
        End SyncLock
    End Sub

    ' Remove transactions that are now in a block
    Public Sub RemoveTransactions(transactionsInBlock As List(Of JObject))
        SyncLock _lock
            Dim txIdsInBlock As New HashSet(Of String)
            For Each txWrapper As JObject In transactionsInBlock
                Dim txData = JObject.Parse(txWrapper("transaction").ToString())
                If txData.ContainsKey("txId") Then
                    txIdsInBlock.Add(txData("txId").ToString())
                End If
            Next

            _transactions.RemoveAll(Function(memTxWrapper)
                                        Dim memTxData = JObject.Parse(memTxWrapper("transaction").ToString())
                                        Return memTxData.ContainsKey("txId") AndAlso txIdsInBlock.Contains(memTxData("txId").ToString())
                                    End Function)
        End SyncLock
    End Sub


    ' Get the count of transactions in the mempool
    Public Function Count() As Integer
        SyncLock _lock
            Return _transactions.Count
        End SyncLock
    End Function

    Public Function GetTransactionByTxId(txId As String) As JObject
        SyncLock _lock
            For Each transaction As JObject In _transactions
                Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                If transactionData("txId").ToString() = txId Then
                    Return transaction
                End If
            Next
            Return Nothing
        End SyncLock
    End Function
End Class