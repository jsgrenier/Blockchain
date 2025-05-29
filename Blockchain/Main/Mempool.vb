Imports Newtonsoft.Json.Linq
Imports System.Collections.Generic

Public Class Mempool
    Private _transactions As New List(Of JObject)
    Private ReadOnly _lock As New Object()

    Public Sub AddTransaction(transaction As JObject)
        SyncLock _lock
            _transactions.Add(transaction)
        End SyncLock
    End Sub

    Public Function GetTransactions() As List(Of JObject)
        SyncLock _lock
            Return New List(Of JObject)(_transactions)
        End SyncLock
    End Function

    Public Sub Clear()
        SyncLock _lock
            _transactions.Clear()
        End SyncLock
    End Sub

    Public Sub RemoveTransaction(transaction As JObject)
        SyncLock _lock
            Dim txIdToRemove As String = Nothing
            Try
                Dim transactionDataToRemove = JObject.Parse(transaction("transaction").ToString())
                If transactionDataToRemove.ContainsKey("txId") Then
                    txIdToRemove = transactionDataToRemove("txId").ToString()
                End If
            Catch ex As Exception
                Console.WriteLine($"Mempool.RemoveTransaction: Error parsing txId from JObject: {ex.Message}")
            End Try


            If txIdToRemove IsNot Nothing Then
                _transactions.RemoveAll(Function(t)
                                            Try
                                                Dim currentTxData = JObject.Parse(t("transaction").ToString())
                                                Return currentTxData.ContainsKey("txId") AndAlso currentTxData("txId").ToString() = txIdToRemove
                                            Catch
                                                Return False ' If parsing fails, don't remove
                                            End Try
                                        End Function)
            Else
                ' Fallback to reference equality, less reliable for JObjects if not same instance
                _transactions.Remove(transaction)
            End If
        End SyncLock
    End Sub

    Public Sub RemoveTransactions(transactionsInBlock As List(Of JObject))
        SyncLock _lock
            Dim txIdsInBlock As New HashSet(Of String)
            For Each txWrapper As JObject In transactionsInBlock
                Try
                    Dim txData = JObject.Parse(txWrapper("transaction").ToString())
                    If txData.ContainsKey("txId") Then
                        txIdsInBlock.Add(txData("txId").ToString())
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Mempool.RemoveTransactions: Error parsing txId from block transaction: {ex.Message}")
                End Try
            Next

            _transactions.RemoveAll(Function(memTxWrapper)
                                        Try
                                            Dim memTxData = JObject.Parse(memTxWrapper("transaction").ToString())
                                            Return memTxData.ContainsKey("txId") AndAlso txIdsInBlock.Contains(memTxData("txId").ToString())
                                        Catch
                                            Return False
                                        End Try
                                    End Function)
        End SyncLock
    End Sub


    Public Function Count() As Integer
        SyncLock _lock
            Return _transactions.Count
        End SyncLock
    End Function

    Public Function GetTransactionByTxId(txId As String) As JObject
        SyncLock _lock
            For Each transactionWrapper As JObject In _transactions
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    If transactionData("txId")?.ToString() = txId Then
                        Return transactionWrapper
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Mempool.GetTransactionByTxId: Error parsing txId: {ex.Message}")
                End Try
            Next
            Return Nothing
        End SyncLock
    End Function
End Class