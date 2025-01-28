Imports System.Security.Claims
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public Class Validation
    Private _validationThread As Thread
    Private _stopValidation As Boolean = False ' Flag to signal thread termination
    Public _blockchain As Blockchain



    Private Function IsDoubleSpend(transaction As JObject) As Boolean
        ' Check if the transaction object has the "Data" property
        If transaction.ContainsKey("Data") Then
            ' Parse the 'Data' field into a JObject
            Dim dataObj As JObject = JObject.Parse(transaction("Data").ToString())

            ' Check if the "inputs" field exists and is an array within "Data"
            If dataObj IsNot Nothing AndAlso dataObj.TryGetValue("inputs", New JArray) AndAlso dataObj("inputs").GetType().IsAssignableFrom(GetType(JArray)) Then
                Dim inputs As JArray = dataObj("inputs")
                Dim spentOutputs As New HashSet(Of String) ' Keep track of spent outputs

                For Each input As JObject In inputs
                    ' Check if the input has the required fields
                    If Not input.ContainsKey("txHash") OrElse Not input.ContainsKey("outputIndex") Then
                        Continue For ' Skip this input if it doesn't have the required fields
                    End If

                    Dim txHash As String = input("txHash").ToString()
                    Dim outputIndex As Integer = CInt(input("outputIndex"))

                    Dim outputId = $"{txHash}:{outputIndex}" ' Unique identifier for the output

                    ' Check if this output has already been spent
                    If spentOutputs.Contains(outputId) Then
                        Return True ' Double-spend detected
                    End If

                    spentOutputs.Add(outputId) ' Add the output to the spent outputs set
                Next

                Return False ' No double-spend detected
            Else
                Return False ' No inputs or invalid input format, so no double-spend
            End If
        Else
            Return False ' "Data" property not found in the transaction object
        End If
    End Function

    ' Helper function to check for sufficient balance
    Private Function HasSufficientBalance(transaction As JObject) As Boolean
        ' Check if the transaction object has the "Data" property
        If transaction.ContainsKey("Data") Then
            ' Parse the 'Data' field into a JObject
            Dim dataObj As JObject = JObject.Parse(transaction("Data").ToString())

            If dataObj IsNot Nothing AndAlso dataObj("type").ToString() = "transfer" Then
                Dim fromAddress As String = dataObj("from").ToString()
                Dim amount As Decimal = CDec(dataObj("amount"))
                Dim tokenSymbol As String = dataObj("token").ToString()

                ' Use GetTokensOwned instead of GetTokenBalance
                Dim tokenBalances = _blockchain.GetTokensOwned(fromAddress)

                ' Check if the sender has enough balance for the specific token
                If tokenBalances.ContainsKey(tokenSymbol) Then
                    Return tokenBalances(tokenSymbol) >= amount
                Else
                    Return False ' Token not found in the sender's balance
                End If
            Else
                Return True ' Not a transfer or invalid data format, so no balance check needed
            End If
        Else
            Return True ' "Data" property not found, so no balance check needed
        End If
    End Function

    ' Start the validation thread
    Public Sub StartValidationThread()
        _stopValidation = False ' Reset the flag
        _validationThread = New Thread(AddressOf ValidateTransactions)
        _validationThread.Start()
        Console.WriteLine("Transaction validation thread started.")
    End Sub

    ' Stop the validation thread
    Public Sub StopValidationThread()
        _stopValidation = True
        _validationThread.Join() ' Wait for the thread to finish
        Console.WriteLine("Transaction validation thread stopped.")
    End Sub

    ' Method to validate transactions periodically
    Private Sub ValidateTransactions()
        While Not _stopValidation
            ' Validate and process transactions from the mempool in batches
            If _blockchain._mempool.Count() > 0 Then
                Console.WriteLine("Validating transactions...")

                ' Create a new block with validated transactions
                Dim newBlockData As New List(Of JObject)

                ' Create a copy of the transactions list to avoid modification issues
                Dim transactionsToValidate = _blockchain._mempool.GetTransactions().ToList()

                For Each transaction In transactionsToValidate
                    Try
                        Dim isValid = True ' Flag to track transaction validity

                        ' Parse the 'Data' field into a JObject
                        Dim dataObj As JObject = JObject.Parse(transaction("transaction").ToString())

                        ' Now you can access properties of dataObj directly
                        If dataObj IsNot Nothing Then
                            ' 1. Double-Spending Check
                            If IsDoubleSpend(dataObj) Then ' Pass dataObj directly
                                Console.WriteLine($"Invalid transaction: Double-spending detected - {transaction}")
                                isValid = False
                            End If

                            ' 2. Sufficient Balance Check
                            If Not HasSufficientBalance(dataObj) Then ' Pass dataObj directly
                                Console.WriteLine($"Invalid transaction: Insufficient balance - {transaction}")
                                isValid = False
                            End If

                            ' 3. Other Validation Checks (Add more as needed)
                            ' ...

                            ' If validation is successful, add this transaction to the block
                            If isValid Then
                                ' Wrap the transaction data in a JObject with a "Data" property
                                Dim transactionWrapper As New JObject()
                                transactionWrapper("transaction") = dataObj
                                newBlockData.Add(transactionWrapper)
                                Console.WriteLine($"Transaction added to block: {dataObj.ToString()}")
                            End If
                        Else
                            Console.WriteLine($"Invalid transaction: Invalid data format - {transaction}")
                            isValid = False
                        End If

                    Catch ex As Exception
                        Console.WriteLine($"Error validating transaction: {ex.Message}")
                    Finally
                        ' Remove the processed transaction from the mempool (whether valid or invalid)
                        _blockchain._mempool.RemoveTransaction(transaction)
                    End Try
                Next

                ' Create the new block with the list of validated transactions
                If newBlockData.Count > 0 Then
                    Dim newIndex As Integer = _blockchain.Chain.Count
                    Dim previousHash As String = If(_blockchain.Chain.Count > 0, _blockchain.Chain.Last().Hash, "0")
                    Dim newBlock As New Block(newIndex, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), newBlockData, previousHash) ' Create block with the list
                    newBlock.Mine(_blockchain._difficulty)

                    ' Add the new block to the chain and database
                    _blockchain.Chain.Add(newBlock)
                    _blockchain.SaveBlockToDatabase(newBlock)

                    Console.WriteLine("Block added to the chain.")
                End If

                Console.WriteLine("Transactions validated and processed.")
            End If

            Thread.Sleep(10000) ' Wait for 10 seconds
        End While
    End Sub
End Class
