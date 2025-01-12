Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Class Blockchain

    Public Property Chain As List(Of Block)
    Private _dbConnection As SqliteConnection
    Private _difficulty As Integer = 4 ' You can adjust difficulty
    Public _mempool As New Mempool() ' Initialize the mempool

    Private _validationThread As Thread
    Private _stopValidation As Boolean = False ' Flag to signal thread termination

    Public Sub New(dbFilePath As String)
        Chain = New List(Of Block)
        _dbConnection = New SqliteConnection($"Data Source={dbFilePath};")
        CreateDatabaseIfNotExists()
        LoadChainFromDatabase()
    End Sub

#Region "DB"
    ' Initializes the database if it does not exist
    Private Sub CreateDatabaseIfNotExists()
        Try
            _dbConnection.Open()
            Dim createTableQuery As String = "CREATE TABLE IF NOT EXISTS Blocks (
                                                [Index] INTEGER PRIMARY KEY,
                                                Timestamp TEXT,
                                                Data TEXT, 
                                                PreviousHash TEXT,
                                                Hash TEXT,
                                                Nonce INTEGER
                                                );"
            Dim command As New SqliteCommand(createTableQuery, _dbConnection)
            command.ExecuteNonQuery()
        Catch ex As Exception
            Console.WriteLine($"Error creating database: {ex.Message}")
        Finally
            _dbConnection.Close()
        End Try
    End Sub

    Private Sub LoadChainFromDatabase()
        Chain.Clear() ' Clear the chain before loading from the database
        Try
            _dbConnection.Open()
            Dim selectQuery As String = "SELECT * FROM Blocks ORDER BY [Index]"
            Dim command As New SqliteCommand(selectQuery, _dbConnection)
            Using reader As SqliteDataReader = command.ExecuteReader()
                While reader.Read()
                    ' Deserialize the list of transactions from the Data column
                    Dim data As List(Of JObject) = JsonConvert.DeserializeObject(Of List(Of JObject))(reader.GetString(2))

                    Dim block As New Block(
                        reader.GetInt32(0),
                        DateTime.Parse(reader.GetString(1)),
                        data, ' Use the deserialized data
                        reader.GetString(3)
                    )
                    block.Hash = reader.GetString(4)
                    block.Nonce = reader.GetInt32(5)
                    Chain.Add(block)
                End While
            End Using
        Catch ex As Exception
            Console.WriteLine($"Error loading chain from database: {ex.Message}")
        Finally
            _dbConnection.Close()
        End Try

        If Chain.Count = 0 Then
            AddGenesisBlock() ' Create the first block if the chain is empty
        End If
    End Sub

    Private Sub SaveBlockToDatabase(block As Block)
        Try
            _dbConnection.Open()
            Dim insertQuery As String = "INSERT INTO Blocks ([Index], Timestamp, Data, PreviousHash, Hash, Nonce) 
                                            VALUES (@Index, @Timestamp, @Data, @PreviousHash, @Hash, @Nonce)"
            Using command As New SqliteCommand(insertQuery, _dbConnection)
                command.Parameters.AddWithValue("@Index", block.Index)
                command.Parameters.AddWithValue("@Timestamp", block.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))

                ' Serialize the list of transactions to JSON
                Dim jsonData As String = JsonConvert.SerializeObject(block.Data)
                command.Parameters.AddWithValue("@Data", jsonData)

                command.Parameters.AddWithValue("@PreviousHash", block.PreviousHash)
                command.Parameters.AddWithValue("@Hash", block.Hash)
                command.Parameters.AddWithValue("@Nonce", block.Nonce)
                command.ExecuteNonQuery()
            End Using
        Catch ex As Exception
            Console.WriteLine($"Error saving block to database: {ex.Message}")
        Finally
            _dbConnection.Close()
        End Try
    End Sub
#End Region

#Region "Block related"
    ' Creates the initial block of the blockchain
    Private Sub AddGenesisBlock()
        ' Create an empty list of transactions for the genesis block
        Dim genesisData As New List(Of JObject)

        Dim genesisBlock As New Block(0, DateTime.Now, genesisData, "0") ' Use the empty list
        genesisBlock.Mine(_difficulty)
        Chain.Add(genesisBlock)
        SaveBlockToDatabase(genesisBlock)
    End Sub
#End Region

#Region "Validation"
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

                Dim balance As Decimal = GetTokenBalance(fromAddress, tokenSymbol)

                ' Check if the sender has enough balance, considering the transaction fee
                Return balance >= amount
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
            If _mempool.Count() > 0 Then
                Console.WriteLine("Validating transactions...")

                ' Create a new block with validated transactions
                Dim newBlockData As New List(Of JObject)

                ' Create a copy of the transactions list to avoid modification issues
                Dim transactionsToValidate = _mempool.GetTransactions().ToList()

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
                        _mempool.RemoveTransaction(transaction)
                    End Try
                Next

                ' Create the new block with the list of validated transactions
                If newBlockData.Count > 0 Then
                    Dim newIndex As Integer = Chain.Count
                    Dim previousHash As String = If(Chain.Count > 0, Chain.Last().Hash, "0")
                    Dim newBlock As New Block(newIndex, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), newBlockData, previousHash) ' Create block with the list
                    newBlock.Mine(_difficulty)

                    ' Add the new block to the chain and database
                    Chain.Add(newBlock)
                    SaveBlockToDatabase(newBlock)

                    Console.WriteLine("Block added to the chain.")
                End If

                Console.WriteLine("Transactions validated and processed.")
            End If

            Thread.Sleep(10000) ' Wait for 10 seconds
        End While
    End Sub
#End Region

#Region "Mempool"
    Public Function AddTransactionToMempool(transactionData As String) As String
        ' Create a new JObject to hold the transaction data
        Dim transaction As New JObject

        Dim txData As JObject = JObject.Parse(transactionData)
        Dim txId As String = Guid.NewGuid().ToString("N")
        txData.Add("txId", txId)
        ' Add the original transaction data
        transaction.Add("transaction", txData.ToString()) 'transactionData)
        ' Add the transaction to the mempool
        _mempool.AddTransaction(transaction)
        Console.WriteLine("Transaction added to the mempool.")
        ' Return the generated txId
        Return txId
    End Function
#End Region

#Region "POST Actions"
    ' Token creation
    Public Function CreateToken(name As String, symbol As String, initialSupply As Decimal, fromPublicKey As String, signature As String) As String ' Add signature parameter
        ' Check for duplicate token name (case-insensitive)
        Try


            If TokenNameExists(name) Then
                Throw New Exception("A token with this name already exists.")
            End If

            ' Check for duplicate symbol (case-sensitive)
            If TokenSymbolExists(symbol) Then
                Throw New Exception("A token with this symbol already exists.")
            End If

            ' Construct the data string that was signed on the client-side (consistent with client-side)
            Dim dataToSign As String = $"{name}:{symbol}:{initialSupply}:{fromPublicKey}"

            ' Verify the signature using the provided ownerPublicKey and dataToSign
            If Not Wallet.VerifySignature(fromPublicKey, signature, dataToSign) Then
                Throw New Exception("Invalid signature. Token creation canceled.")
            End If
            Console.WriteLine("Signature verified successfully.")

            ' Create token data including the signature
            Dim tokenData = JObject.FromObject(New With {
            .timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            .type = "tokenCreation",
            .name = name,
            .symbol = symbol,
            .initialSupply = initialSupply,
            .owner = fromPublicKey
        }).ToString()

            ' Add the transaction to the mempool and get the txId
            Dim txId As String = AddTransactionToMempool(tokenData)

            Console.WriteLine("Transfer transaction added to the mempool.")

            ' Return the txId
            Return txId
        Catch ex As Exception
            Console.WriteLine($"Error in TransferTokens: {ex.Message}")
            Throw
        End Try
    End Function

    Public Function TransferTokens(toAddress As String, amount As Decimal, tokenSymbol As String, signature As String, fromAddress As String) As String ' Add return type String
        Try
            ' Prevent transfers to the same address
            If fromAddress = toAddress Then
                Throw New Exception("Cannot transfer tokens to the same address.")
            End If

            ' Get sender's public key
            Dim fromAddressPublicKey As String = GetPublicKeyForAddress(fromAddress)
            Console.WriteLine($"Retrieved public key from sender: {fromAddress}")

            ' Construct the data string that was signed on the client-side (consistent with client-side)
            Dim dataToSign As String = $"{fromAddress}:{toAddress}:{amount}:{tokenSymbol}"

            ' Verify the signature
            If Not Wallet.VerifySignature(fromAddressPublicKey, signature, dataToSign) Then
                Throw New Exception($"Invalid signature. Token transfer canceled.")
            End If
            Console.WriteLine("Signature verified successfully.")

            ' Validate balance
            If GetTokenBalance(fromAddress, tokenSymbol) < amount Then
                Throw New Exception("Insufficient balance for token transfer.")
            End If
            Console.WriteLine("Balance check passed.")

            ' Validate amount (ensure it's greater than zero with tolerance)
            If Not amount > 0.00000001D Then
                Throw New Exception("Cannot process transaction with zero amount")
            End If



            ' Create transfer data
            Dim transferData = JObject.FromObject(New With {
            .timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            .type = "transfer",
            .from = fromAddress,
            .to = toAddress,
            .amount = amount,
            .token = tokenSymbol
        }).ToString()

            ' Add the transaction to the mempool and get the txId
            Dim txId As String = AddTransactionToMempool(transferData)

            Console.WriteLine("Transfer transaction added to the mempool.")

            ' Return the txId
            Return txId

        Catch ex As Exception
            ' Log the error and rethrow it for the caller to handle
            Console.WriteLine($"Error in TransferTokens: {ex.Message}")
            Throw
        End Try
    End Function
#End Region

#Region "GET Actions"
    Public Function GetTokenBalance(address As String, tokenSymbol As String) As Decimal
        Dim balance As Decimal = 0

        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions in the block
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                    ' Check for token creation
                    If transactionData("type").ToString() = "tokenCreation" AndAlso transactionData("symbol").ToString() = tokenSymbol Then
                        If transactionData("owner").ToString() = address Then
                            balance += CDec(transactionData("initialSupply"))
                        End If
                    End If

                    ' Check for token transfers
                    If transactionData("type").ToString() = "transfer" AndAlso transactionData("token").ToString() = tokenSymbol Then
                        If transactionData("from").ToString() = address Then
                            balance -= CDec(transactionData("amount"))
                        ElseIf transactionData("to").ToString() = address Then
                            balance += CDec(transactionData("amount"))
                        End If
                    End If

                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        Return balance
    End Function


    Public Function GetTokensOwned(address As String) As Dictionary(Of String, Decimal)
        Dim tokensOwned As New Dictionary(Of String, Decimal)
        Dim tokenNames As New Dictionary(Of String, String)

        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions in the block
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                    If transactionData("type").ToString() = "tokenCreation" Then
                        Dim owner = transactionData("owner").ToString()
                        Dim symbol = transactionData("symbol").ToString()
                        Dim name = transactionData("name").ToString()
                        Dim initialSupply = CDec(transactionData("initialSupply"))

                        If Not tokenNames.ContainsKey(symbol) Then
                            tokenNames(symbol) = name
                        End If

                        If owner = address Then
                            tokensOwned(symbol) = initialSupply
                        End If

                    ElseIf transactionData("type").ToString() = "transfer" Then
                        Dim from = transactionData("from").ToString()
                        Dim too = transactionData("to").ToString()
                        Dim symbol = transactionData("token").ToString()
                        Dim amount = CDec(transactionData("amount"))

                        If Not tokenNames.ContainsKey(symbol) Then
                            tokenNames(symbol) = symbol
                        End If

                        If from = address Then
                            If tokensOwned.ContainsKey(symbol) Then
                                tokensOwned(symbol) -= amount
                            End If
                        ElseIf too = address Then
                            If tokensOwned.ContainsKey(symbol) Then
                                tokensOwned(symbol) += amount
                            Else
                                tokensOwned(symbol) = amount
                            End If
                        End If
                    End If

                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        ' Adjust balances based on mempool transactions
        For Each transaction As JObject In _mempool.GetTransactions()
            Try
                Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                If transactionData("type").ToString() = "transfer" Then
                    Dim from = transactionData("from").ToString()
                    Dim too = transactionData("to").ToString()
                    Dim symbol = transactionData("token").ToString()
                    Dim amount = CDec(transactionData("amount"))

                    If from = address Then
                        If tokensOwned.ContainsKey(symbol) Then
                            tokensOwned(symbol) -= amount
                        End If
                    End If

                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction: {ex.Message}")
            End Try
        Next

        Return tokensOwned
    End Function

    Public Function GetTransactionHistory(address As String) As List(Of Object)
        Dim historyItems As New List(Of Object)

        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions in the block
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                    Dim blockDateTime = $"{block.Timestamp:MM/dd/yyyy} {block.Timestamp:HH:mm:ss}"
                    'Dim transactionId = transaction("txId").ToString() ' Get the transaction ID

                    If transactionData("type").ToString() = "transfer" Then
                        Dim from = transactionData("from").ToString()
                        Dim too = transactionData("to").ToString()
                        Dim symbol = transactionData("token").ToString()
                        Dim amount = CDec(transactionData("amount"))

                        If from = address OrElse too = address Then
                            Dim historyItem = New With {
                            .Timestamp = block.Timestamp,
                            .DateTime = blockDateTime,
                            .Type = "Transfer",
                            .Amount = If(from = address, $"-{amount} {symbol}", $"+{amount} {symbol}"),
                            .Hash = block.Hash ' Use the transaction ID
                        }
                            historyItems.Add(historyItem)
                        End If

                    ElseIf transactionData("type").ToString() = "tokenCreation" Then
                        Dim owner = transactionData("owner").ToString()
                        Dim symbol = transactionData("symbol").ToString()
                        Dim initialSupply = CDec(transactionData("initialSupply"))

                        If owner = address Then
                            Dim historyItem = New With {
                            .Timestamp = block.Timestamp,
                            .DateTime = blockDateTime,
                            .Type = "TokenCreation",
                            .Amount = $"+{initialSupply} {symbol}",
                            .Hash = block.Hash'transactionId ' Use the transaction ID
                        }
                            historyItems.Add(historyItem)
                        End If
                    End If

                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        Return historyItems
    End Function

    Public Function GetTransactionByHash(hash As String) As Object
        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions
                Try
                    If transaction("Hash").ToString() = hash Then ' Check transaction hash
                        Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                        Return New With {
                        .Timestamp = block.Timestamp,
                        .DateTime = $"{block.Timestamp:MM/dd/yyyy} {block.Timestamp:HH:mm:ss}",
                        .Data = transactionData,
                        .Hash = transaction("Hash").ToString()
                    }
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        Return Nothing ' Transaction not found
    End Function

    Public Function GetTokenNames() As Dictionary(Of String, String)
        Dim tokenNames As New Dictionary(Of String, String)

        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                    If transactionData("type").ToString() = "tokenCreation" Then
                        Dim symbol As String = transactionData("symbol").ToString()
                        Dim name As String = transactionData("name").ToString()
                        tokenNames.Add(symbol, name)
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        Return tokenNames
    End Function

    Private Function GetPublicKeyForAddress(address As String) As String
        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                    If transactionData("type").ToString() = "tokenCreation" AndAlso transactionData("owner").ToString() = address Then
                        Return transactionData("owner").ToString()

                    ElseIf transactionData("type").ToString() = "transfer" Then
                        If transactionData("from").ToString() = address Then
                            Return transactionData("from").ToString()
                        ElseIf transactionData("to").ToString() = address Then
                            Return transactionData("to").ToString()
                        End If
                    End If

                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next

        Throw New Exception($"Public key not found for address: {address}")
    End Function
#End Region

#Region "Helper Functions"
    ' Helper function to check if chain is valid
    Public Function IsChainValid() As Boolean
        For i As Integer = 1 To Chain.Count - 1
            Dim currentBlock = Chain(i)
            Dim previousBlock = Chain(i - 1)

            ' Check if the current block's hash is correctly calculated.
            If currentBlock.Hash <> currentBlock.CalculateHash() Then
                Return False
            End If

            ' Check if the previous hash matches the hash of the previous block.
            If currentBlock.PreviousHash <> previousBlock.Hash Then
                Return False
            End If

            ' Check if the Proof of Work is valid (hash difficulty)
            If Not currentBlock.Hash.StartsWith(New String("0", _difficulty)) Then
                Return False
            End If
        Next

        Return True
    End Function



    ' Helper function to check if a token name exists (case-insensitive)
    Public Function TokenNameExists(name As String) As Boolean
        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                    If transactionData("type").ToString() = "tokenCreation" AndAlso
                   transactionData("name").ToString().ToLower() = name.ToLower() Then
                        Return True ' Token with this name found
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next
        Return False ' Token name not found
    End Function

    ' Helper function to check if a token symbol exists (case-sensitive)
    Public Function TokenSymbolExists(symbol As String) As Boolean
        For Each block As Block In Chain
            For Each transaction As JObject In block.Data ' Iterate through transactions
                Try
                    Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                    If transactionData("type").ToString() = "tokenCreation" AndAlso
                   transactionData("symbol").ToString() = symbol Then
                        Return True ' Token with this symbol found
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction: {ex.Message}")
                End Try
            Next
        Next
        Return False ' Token symbol not found
    End Function
#End Region
















End Class