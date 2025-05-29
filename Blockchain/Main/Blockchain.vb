Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization
Imports System.Data
Imports System.Diagnostics ' For Debug.WriteLine if you prefer over Console

Public Class Blockchain

    Public Property Chain As List(Of Block)
    Private _dbConnection As SqliteConnection
    Public Property _difficulty As Integer = 4 ' Default difficulty
    Public _mempool As New Mempool()

    Public Sub New(dbFilePath As String)
        Chain = New List(Of Block)
        _dbConnection = New SqliteConnection($"Data Source={dbFilePath};")
        CreateDatabaseIfNotExists()
        LoadChainFromDatabase()
    End Sub

#Region "DB"
    Private Sub CreateDatabaseIfNotExists()
        Try
            _dbConnection.Open()
            Dim createTableQuery As String = "CREATE TABLE IF NOT EXISTS Blocks (
                                                [Index] INTEGER PRIMARY KEY,
                                                Timestamp TEXT,
                                                Data TEXT, 
                                                PreviousHash TEXT,
                                                Hash TEXT,
                                                Nonce INTEGER,
                                                Difficulty INTEGER,
                                                BlockSize INTEGER
                                                );"
            Dim command As New SqliteCommand(createTableQuery, _dbConnection)
            command.ExecuteNonQuery()
        Catch ex As Exception
            Console.WriteLine($"Error creating database: {ex.Message}")
        Finally
            If _dbConnection.State = ConnectionState.Open Then
                _dbConnection.Close()
            End If
        End Try
    End Sub

    Private Sub LoadChainFromDatabase()
        Chain.Clear()
        Try
            _dbConnection.Open()
            Dim selectQuery As String = "SELECT * FROM Blocks ORDER BY [Index]"
            Dim command As New SqliteCommand(selectQuery, _dbConnection)
            Using reader As SqliteDataReader = command.ExecuteReader()
                While reader.Read()
                    Dim dataAsJsonString As String = reader.GetString(2)
                    Dim data As List(Of JObject) = JsonConvert.DeserializeObject(Of List(Of JObject))(dataAsJsonString)

                    Dim timestampString = reader.GetString(1)
                    Dim timestamp As DateTime
                    If Not DateTime.TryParseExact(timestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, timestamp) Then
                        Console.WriteLine($"Warning: Could not parse timestamp '{timestampString}' in 'o' format for block index {reader.GetInt32(0)}. Attempting general parse.")
                        If Not DateTime.TryParse(timestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, timestamp) Then
                            timestamp = DateTime.MinValue.ToUniversalTime()
                            Console.WriteLine($"Error: Timestamp parsing failed completely for block {reader.GetInt32(0)}. Using MinValue.")
                        Else
                            timestamp = timestamp.ToUniversalTime()
                        End If
                    End If

                    Dim difficultyFromDb As Integer = reader.GetInt32(6)
                    Dim blockSizeFromDb As Integer = 0
                    If Not reader.IsDBNull(7) Then
                        blockSizeFromDb = reader.GetInt32(7)
                    End If

                    Dim block As New Block(
                        reader.GetInt32(0),
                        timestamp,
                        data,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt32(5),
                        difficultyFromDb,
                        blockSizeFromDb
                    )
                    If blockSizeFromDb = 0 Then
                        block.BlockSize = block.CalculateBlockSize()
                    End If

                    Chain.Add(block)
                End While
            End Using

            If Chain.Any() Then
                _difficulty = Chain.Last().Difficulty
            Else
                _difficulty = 4
            End If

        Catch ex As Exception
            Console.WriteLine($"Error loading chain from database: {ex.Message}{vbCrLf}{ex.StackTrace}")
        Finally
            If _dbConnection.State = ConnectionState.Open Then
                _dbConnection.Close()
            End If
        End Try

        If Chain.Count = 0 Then
            AddGenesisBlock()
        End If
    End Sub

    Public Sub SaveBlockToDatabase(block As Block)
        Try
            _dbConnection.Open()
            Dim insertQuery As String = "INSERT INTO Blocks ([Index], Timestamp, Data, PreviousHash, Hash, Nonce, Difficulty, BlockSize) 
                                            VALUES (@Index, @Timestamp, @Data, @PreviousHash, @Hash, @Nonce, @Difficulty, @BlockSize)"
            Using command As New SqliteCommand(insertQuery, _dbConnection)
                command.Parameters.AddWithValue("@Index", block.Index)
                command.Parameters.AddWithValue("@Timestamp", block.Timestamp.ToString("o", CultureInfo.InvariantCulture))
                Dim jsonData As String = JsonConvert.SerializeObject(block.Data)
                command.Parameters.AddWithValue("@Data", jsonData)
                command.Parameters.AddWithValue("@PreviousHash", block.PreviousHash)
                command.Parameters.AddWithValue("@Hash", block.Hash)
                command.Parameters.AddWithValue("@Nonce", block.Nonce)
                command.Parameters.AddWithValue("@Difficulty", block.Difficulty)
                command.Parameters.AddWithValue("@BlockSize", block.BlockSize)
                command.ExecuteNonQuery()
            End Using
        Catch ex As Exception
            Console.WriteLine($"Error saving block to database: {ex.Message}")
        Finally
            If _dbConnection.State = ConnectionState.Open Then
                _dbConnection.Close()
            End If
        End Try
    End Sub
#End Region

#Region "Block related"
    Private Sub AddGenesisBlock()
        Dim tokenCreationData = JObject.FromObject(New With {
            .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            .type = "tokenCreation",
            .name = "Beancoin",
            .symbol = "BEAN",
            .initialSupply = CObj(0D),
            .owner = "genesis",
            .txId = Guid.NewGuid().ToString("N")
        })

        Dim genesisTransactionWrapper As New JObject From {
            {"transaction", tokenCreationData}
        }

        Dim genesisBlockData As New List(Of JObject) From {genesisTransactionWrapper}

        Dim genesisBlock As New Block(0, DateTime.UtcNow, genesisBlockData, "0", _difficulty)
        genesisBlock.Mine()
        Chain.Add(genesisBlock)
        SaveBlockToDatabase(genesisBlock)
        Console.WriteLine($"Genesis block created with difficulty {genesisBlock.Difficulty}.")
    End Sub

    Public Function GetLatestBlock() As Block
        If Chain.Any() Then
            Return Chain.Last()
        End If
        Return Nothing
    End Function
#End Region

#Region "Mempool"
    Public Function AddTransactionToMempool(transactionJsonString As String) As String
        Dim txDataAsJObject As JObject
        Try
            txDataAsJObject = JObject.Parse(transactionJsonString)
        Catch ex As JsonReaderException
            Console.WriteLine($"Error parsing transaction JSON for mempool: {ex.Message}. JSON: {transactionJsonString}")
            Throw New ArgumentException("Invalid transaction JSON format.", ex)
        End Try

        Dim txId As String
        If txDataAsJObject.ContainsKey("txId") AndAlso txDataAsJObject("txId")?.Type = JTokenType.String Then
            txId = txDataAsJObject("txId").ToString()
        Else
            txId = Guid.NewGuid().ToString("N")
            txDataAsJObject.Add("txId", txId)
        End If

        Dim transactionWrapper As New JObject From {
            {"transaction", txDataAsJObject}
        }

        _mempool.AddTransaction(transactionWrapper)
        Console.WriteLine($"Transaction {txId} added to the mempool.")
        Return txId
    End Function
#End Region

#Region "POST Actions"
    Public Function CreateToken(name As String, symbolText As String, initialSupply As Decimal, ownerPublicKey As String, signature As String) As String
        Try
            If TokenNameExists(name) Then
                Throw New Exception("A token with this name already exists.")
            End If
            If TokenSymbolExists(symbolText) Then
                Throw New Exception("A token with this symbol already exists.")
            End If

            Dim dataToSign As String = $"{name}:{symbolText}:{initialSupply.ToString(CultureInfo.InvariantCulture)}:{ownerPublicKey}"
            If Not Wallet.VerifySignature(ownerPublicKey, signature, dataToSign) Then
                Throw New Exception("Invalid signature. Token creation canceled.")
            End If
            Console.WriteLine("Signature verified successfully for token creation.")

            Dim tokenTransactionJObject = JObject.FromObject(New With {
                .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                .type = "tokenCreation",
                .name = name,
                .symbol = symbolText,
                .initialSupply = initialSupply,
                .owner = ownerPublicKey,
                .txId = Guid.NewGuid().ToString("N")
            })

            Dim txId As String = AddTransactionToMempool(tokenTransactionJObject.ToString(Formatting.None))
            Console.WriteLine($"Token creation transaction {txId} for {symbolText} added to the mempool.")
            Return txId
        Catch ex As Exception
            Console.WriteLine($"Error in CreateToken: {ex.Message}{vbCrLf}{ex.StackTrace}")
            Throw
        End Try
    End Function

    Public Function TransferTokens(toAddress As String, amount As Decimal, tokenSymbol As String, signature As String, fromAddressPublicKey As String) As String
        Try
            If String.Equals(fromAddressPublicKey, toAddress, StringComparison.Ordinal) Then
                Throw New Exception("Cannot transfer tokens to the same address.")
            End If

            Dim dataToSign As String = $"{fromAddressPublicKey}:{toAddress}:{amount.ToString(CultureInfo.InvariantCulture)}:{tokenSymbol}"

            If Not Wallet.VerifySignature(fromAddressPublicKey, signature, dataToSign) Then
                Throw New Exception($"Invalid signature for transfer. Token transfer canceled.")
            End If
            Console.WriteLine("Signature verified successfully for token transfer.")

            Dim tokenBalances = GetTokensOwned(fromAddressPublicKey)
            If Not tokenBalances.ContainsKey(tokenSymbol) OrElse tokenBalances(tokenSymbol) < amount Then
                Throw New Exception($"Insufficient balance for token transfer of {amount} {tokenSymbol} from {fromAddressPublicKey}. Available: {tokenBalances.GetValueOrDefault(tokenSymbol, 0D)}")
            End If
            Console.WriteLine("Balance check passed for transfer.")

            If Not amount > 0.00000000D Then
                Throw New Exception("Cannot process transaction with zero or negative amount.")
            End If

            Dim transferTransactionJObject = JObject.FromObject(New With {
                .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                .type = "transfer",
                .from = fromAddressPublicKey,
                .to = toAddress,
                .amount = amount,
                .token = tokenSymbol,
                .txId = Guid.NewGuid().ToString("N")
            })

            Dim txId As String = AddTransactionToMempool(transferTransactionJObject.ToString(Formatting.None))
            Console.WriteLine($"Transfer transaction {txId} for {amount} {tokenSymbol} from {fromAddressPublicKey} to {toAddress} added to mempool.")
            Return txId
        Catch ex As Exception
            Console.WriteLine($"Error in TransferTokens: {ex.Message}{vbCrLf}{ex.StackTrace}")
            Throw
        End Try
    End Function
#End Region

#Region "GET Actions"
    Public Function GetTokensOwned(address As String) As Dictionary(Of String, Decimal)
        Dim tokensOwned As New Dictionary(Of String, Decimal)

        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then
                        Console.WriteLine($"Warning (GetTokensOwned - Chain): 'transaction' field in block {block.Index} is not a JObject or is null. Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                        Continue For
                    End If
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    Dim txType = transactionData("type")?.ToString()

                    If txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol = transactionData("symbol")?.ToString()
                        Dim initialSupplyToken As JToken = transactionData("initialSupply")
                        Dim initialSupply As Decimal = 0D

                        If initialSupplyToken IsNot Nothing Then
                            Try
                                initialSupply = initialSupplyToken.ToObject(Of Decimal)()
                            Catch ex As Exception
                                Console.WriteLine($"Warning (GetTokensOwned - Chain): Could not convert initialSupply JToken '{initialSupplyToken.ToString(Formatting.None)}' to Decimal for tokenCreation. Owner '{owner}', Symbol '{symbol}'. Block {block.Index}. Error: {ex.Message}")
                                initialSupply = 0D
                            End Try
                        End If

                        If String.Equals(owner, address, StringComparison.Ordinal) AndAlso symbol IsNot Nothing AndAlso initialSupply > 0D Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + initialSupply
                        End If

                    ElseIf txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim to_addr = transactionData("to")?.ToString()
                        Dim symbol = transactionData("token")?.ToString()
                        Dim amountValToken As JToken = transactionData("amount")
                        Dim amountVal As Decimal = 0D

                        If amountValToken IsNot Nothing Then
                            Try
                                amountVal = amountValToken.ToObject(Of Decimal)()
                            Catch ex As Exception
                                Console.WriteLine($"Warning (GetTokensOwned - Chain): Could not convert transfer amount JToken '{amountValToken.ToString(Formatting.None)}' to Decimal. From '{from}', To '{to_addr}', Symbol '{symbol}'. Block {block.Index}. Error: {ex.Message}")
                                amountVal = 0D
                            End Try
                        End If

                        If symbol IsNot Nothing AndAlso amountVal > 0D Then
                            If String.Equals(from, address, StringComparison.Ordinal) Then
                                tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) - amountVal
                            End If
                            If String.Equals(to_addr, address, StringComparison.Ordinal) Then
                                tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + amountVal
                            End If
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for balance in block {block.Index}: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}, Stack: {ex.StackTrace}")
                End Try
            Next
        Next
        'Console.WriteLine($"Tokens owned after chain processing for {address}: {JsonConvert.SerializeObject(tokensOwned)}")

        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionToken As JToken = mempoolTxWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then
                    Console.WriteLine($"Warning (GetTokensOwned - Mempool): 'transaction' field is not a JObject or is null. Wrapper: {mempoolTxWrapper.ToString(Formatting.None)}")
                    Continue For
                End If
                Dim transactionData As JObject = CType(transactionToken, JObject)

                Dim txType = transactionData("type")?.ToString()
                If txType = "transfer" Then
                    Dim from = transactionData("from")?.ToString()
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amountValToken As JToken = transactionData("amount")
                    Dim amountVal As Decimal = 0D

                    If amountValToken IsNot Nothing Then
                        Try
                            amountVal = amountValToken.ToObject(Of Decimal)()
                        Catch ex As Exception
                            Console.WriteLine($"Warning (GetTokensOwned - Mempool): Could not convert transfer amount JToken '{amountValToken.ToString(Formatting.None)}' to Decimal. From '{from}', Symbol '{symbol}'. Error: {ex.Message}")
                            amountVal = 0D
                        End Try
                    End If

                    If symbol IsNot Nothing AndAlso amountVal > 0D Then
                        If String.Equals(from, address, StringComparison.Ordinal) Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) - amountVal
                        End If
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for balance: {ex.Message} - Wrapper: {mempoolTxWrapper.ToString(Formatting.None)}, Stack: {ex.StackTrace}")
            End Try
        Next
        'Console.WriteLine($"Tokens owned after mempool processing for {address}: {JsonConvert.SerializeObject(tokensOwned)}")

        Dim keysToRemove = tokensOwned.Where(Function(kvp) kvp.Value <= 0.00000000D).Select(Function(kvp) kvp.Key).ToList()
        For Each key In keysToRemove
            tokensOwned.Remove(key)
        Next

        Return tokensOwned
    End Function

    Public Function GetTransactionHistory(address As String) As List(Of Object)
        Dim historyItems As New List(Of Object)
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    Dim blockDateTime = block.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    Dim txType = transactionData("type")?.ToString()
                    Dim txId = transactionData("txId")?.ToString()
                    Dim txTimestampString = If(transactionData.ContainsKey("timestamp") AndAlso transactionData("timestamp").Type = JTokenType.String,
                                               transactionData("timestamp").ToString(),
                                               block.Timestamp.ToString("o", CultureInfo.InvariantCulture))
                    Dim txTimestampDateTime As DateTime
                    If Not DateTime.TryParseExact(txTimestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime) Then
                        DateTime.TryParse(txTimestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, txTimestampDateTime)
                    End If


                    If txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim to_addr = transactionData("to")?.ToString()
                        Dim symbol = transactionData("token")?.ToString()
                        Dim amountVal As Decimal = 0D
                        If transactionData("amount") IsNot Nothing Then
                            Try : amountVal = transactionData("amount").ToObject(Of Decimal)() : Catch : End Try
                        End If

                        If String.Equals(from, address, StringComparison.Ordinal) OrElse String.Equals(to_addr, address, StringComparison.Ordinal) Then
                            Dim itemType = If(String.Equals(from, "miningReward", StringComparison.OrdinalIgnoreCase), "Mining Reward", "Transfer")
                            Dim amountString = ""
                            If String.Equals(from, address, StringComparison.Ordinal) Then amountString = $"-{amountVal} {symbol}"
                            If String.Equals(to_addr, address, StringComparison.Ordinal) Then amountString = $"+{amountVal} {symbol}"
                            If String.Equals(from, "miningReward", StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(to_addr, address, StringComparison.Ordinal) Then amountString = $"+{amountVal} {symbol}"


                            historyItems.Add(New With {
                                .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                .BlockTimestamp = blockDateTime,
                                .Type = itemType, .From = from, .To = to_addr, .AmountString = amountString, .Token = symbol, .Value = amountVal,
                                .BlockHash = block.Hash, .TxId = txId, .Status = "Completed"
                            })
                        End If
                    ElseIf txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol = transactionData("symbol")?.ToString()
                        Dim name = transactionData("name")?.ToString()
                        Dim initialSupply As Decimal = 0D
                        If transactionData("initialSupply") IsNot Nothing Then
                            Try : initialSupply = transactionData("initialSupply").ToObject(Of Decimal)() : Catch : End Try
                        End If

                        If String.Equals(owner, address, StringComparison.Ordinal) Then
                            historyItems.Add(New With {
                                .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                .BlockTimestamp = blockDateTime, .Type = "Token Creation", .From = "N/A", .To = owner,
                                .AmountString = $"+{initialSupply} {symbol} ({name})", .Token = symbol, .Value = initialSupply,
                                .BlockHash = block.Hash, .TxId = txId, .Status = "Completed"
                            })
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for history: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next

        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionToken As JToken = mempoolTxWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                Dim transactionData As JObject = CType(transactionToken, JObject)

                Dim txType = transactionData("type")?.ToString()
                Dim txId = transactionData("txId")?.ToString()
                Dim txTimestampString = If(transactionData.ContainsKey("timestamp") AndAlso transactionData("timestamp").Type = JTokenType.String,
                                           transactionData("timestamp").ToString(),
                                           DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                Dim txTimestampDateTime As DateTime
                If Not DateTime.TryParseExact(txTimestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime) Then
                    DateTime.TryParse(txTimestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, txTimestampDateTime)
                End If


                If txType = "transfer" Then
                    Dim from = transactionData("from")?.ToString()
                    Dim to_addr = transactionData("to")?.ToString()
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amountVal As Decimal = 0D
                    If transactionData("amount") IsNot Nothing Then
                        Try : amountVal = transactionData("amount").ToObject(Of Decimal)() : Catch : End Try
                    End If

                    If String.Equals(from, address, StringComparison.Ordinal) OrElse String.Equals(to_addr, address, StringComparison.Ordinal) Then
                        Dim amountString = ""
                        If String.Equals(from, address, StringComparison.Ordinal) Then amountString = $"-{amountVal} {symbol}"
                        If String.Equals(to_addr, address, StringComparison.Ordinal) Then amountString = $"+{amountVal} {symbol}"
                        historyItems.Add(New With {
                            .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .BlockTimestamp = "Pending", .Type = "Transfer", .From = from, .To = to_addr,
                            .AmountString = amountString,
                            .Token = symbol, .Value = amountVal, .BlockHash = "Pending", .TxId = txId, .Status = "Pending"
                        })
                    End If
                ElseIf txType = "tokenCreation" Then
                    Dim owner = transactionData("owner")?.ToString()
                    Dim symbol = transactionData("symbol")?.ToString()
                    Dim name = transactionData("name")?.ToString()
                    Dim initialSupply As Decimal = 0D
                    If transactionData("initialSupply") IsNot Nothing Then
                        Try : initialSupply = transactionData("initialSupply").ToObject(Of Decimal)() : Catch : End Try
                    End If

                    If String.Equals(owner, address, StringComparison.Ordinal) Then
                        historyItems.Add(New With {
                            .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .BlockTimestamp = "Pending", .Type = "Token Creation", .From = "N/A", .To = owner,
                            .AmountString = $"+{initialSupply} {symbol} ({name})", .Token = symbol, .Value = initialSupply,
                            .BlockHash = "Pending", .TxId = txId, .Status = "Pending"
                        })
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for history: {ex.Message} - Wrapper: {mempoolTxWrapper.ToString(Formatting.None)}")
            End Try
        Next
        Return historyItems.OrderByDescending(Function(h) If(h.Status = "Pending", DateTime.MaxValue, DateTime.ParseExact(h.BlockTimestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))) _
                           .ThenByDescending(Function(h) DateTime.ParseExact(h.TxTimestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) _
                           .ToList()
    End Function

    Public Function GetTransactionByTxId(txId As String) As Object
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot
            For Each transactionWrapper As JObject In block.Data
                Dim transactionToken As JToken = transactionWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                Dim transactionData As JObject = CType(transactionToken, JObject)

                If String.Equals(transactionData("txId")?.ToString(), txId, StringComparison.Ordinal) Then
                    Return New With {
                        .status = "completed", .timestamp = block.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                        .blockIndex = block.Index, .blockHash = block.Hash, .transaction = transactionData
                    }
                End If
            Next
        Next
        Dim mempoolTransactionWrapper = _mempool.GetTransactionByTxId(txId)
        If mempoolTransactionWrapper IsNot Nothing Then
            Dim transactionToken As JToken = mempoolTransactionWrapper("transaction")
            If transactionToken IsNot Nothing AndAlso transactionToken.Type = JTokenType.Object Then
                Dim transactionData As JObject = CType(transactionToken, JObject)
                Dim txTimestamp As String = If(transactionData.ContainsKey("timestamp") AndAlso transactionData("timestamp").Type = JTokenType.String,
                                               transactionData("timestamp").ToString(),
                                               "N/A")
                Return New With {
                    .status = "pending", .timestamp = txTimestamp,
                    .blockIndex = -1, .blockHash = "N/A", .transaction = transactionData
                }
            End If
        End If
        Return Nothing
    End Function

    Public Function GetTokenNames() As Dictionary(Of String, String)
        Dim tokenNames As New Dictionary(Of String, String)
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    If transactionData("type")?.ToString() = "tokenCreation" Then
                        Dim symbol As String = transactionData("symbol")?.ToString()
                        Dim name As String = transactionData("name")?.ToString()
                        If symbol IsNot Nothing AndAlso name IsNot Nothing Then
                            tokenNames(symbol) = name
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction for token names: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return tokenNames
    End Function

#End Region

#Region "Helper Functions and Validation Support"
    Public Function IsChainValid() As Boolean
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        If chainSnapshot Is Nothing OrElse chainSnapshot.Count = 0 Then Return True

        For i As Integer = 0 To chainSnapshot.Count - 1
            Dim currentBlock = chainSnapshot(i)

            If currentBlock.Hash <> currentBlock.CalculateHash() Then
                Console.WriteLine($"Chain invalid: Block {currentBlock.Index} (Hash: {currentBlock.Hash.Substring(0, 8)}) hash mismatch. Calculated: {currentBlock.CalculateHash().Substring(0, 8)}")
                Return False
            End If

            If i > 0 Then
                Dim previousBlock = chainSnapshot(i - 1)
                If currentBlock.PreviousHash <> previousBlock.Hash Then
                    Console.WriteLine($"Chain invalid: Block {currentBlock.Index} previous hash mismatch.")
                    Return False
                End If
            Else
                If currentBlock.Index <> 0 OrElse currentBlock.PreviousHash <> "0" Then
                    Console.WriteLine($"Chain invalid: Genesis block (Index {currentBlock.Index}) structure error (PrevHash: {currentBlock.PreviousHash}).")
                    Return False
                End If
            End If

            If Not currentBlock.Hash.StartsWith(New String("0"c, currentBlock.Difficulty)) Then
                Console.WriteLine($"Chain invalid: Block {currentBlock.Index} PoW not met (difficulty {currentBlock.Difficulty}). Hash: {currentBlock.Hash}")
                Return False
            End If
        Next
        Return True
    End Function

    Public Function TokenNameExists(name As String) As Boolean
        Dim lastIndex As Integer = -1
        SyncLock Chain
            If Chain.Any() Then lastIndex = Chain.Last().Index
        End SyncLock
        If TokenNameExistsOnChain_Private(name, lastIndex) Then Return True

        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionToken As JToken = mempoolTxWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                Dim transactionData As JObject = CType(transactionToken, JObject)

                If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                   String.Equals(transactionData("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction in TokenNameExists: {ex.Message}")
            End Try
        Next
        Return False
    End Function

    Public Function TokenSymbolExists(symbolText As String) As Boolean
        Dim lastIndex As Integer = -1
        SyncLock Chain
            If Chain.Any() Then lastIndex = Chain.Last().Index
        End SyncLock
        If TokenSymbolExistsOnChain_Private(symbolText, lastIndex) Then Return True

        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionToken As JToken = mempoolTxWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                Dim transactionData As JObject = CType(transactionToken, JObject)

                If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                   String.Equals(transactionData("symbol")?.ToString(), symbolText, StringComparison.Ordinal) Then
                    Return True
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction in TokenSymbolExists: {ex.Message}")
            End Try
        Next
        Return False
    End Function

    Friend Function GetBalanceAtBlock_Private(address As String, tokenSymbol As String, upToBlockIndex As Integer) As Decimal
        Dim balance As Decimal = 0D
        Dim chainToCheck As List(Of Block)
        SyncLock Chain
            chainToCheck = Chain.Where(Function(b) b.Index <= upToBlockIndex).ToList()
        End SyncLock

        If Not chainToCheck.Any() Then Return 0D

        For Each block As Block In chainToCheck
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    Dim txType = transactionData("type")?.ToString()

                    If txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol_val = transactionData("symbol")?.ToString()
                        Dim initialSupply As Decimal = 0D
                        If transactionData("initialSupply") IsNot Nothing Then
                            Try : initialSupply = transactionData("initialSupply").ToObject(Of Decimal)() : Catch : End Try
                        End If

                        If String.Equals(owner, address, StringComparison.Ordinal) AndAlso String.Equals(symbol_val, tokenSymbol, StringComparison.Ordinal) Then
                            balance += initialSupply
                        End If
                    ElseIf txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim to_addr = transactionData("to")?.ToString()
                        Dim symbol_val = transactionData("token")?.ToString()
                        Dim amountVal As Decimal = 0D
                        If transactionData("amount") IsNot Nothing Then
                            Try : amountVal = transactionData("amount").ToObject(Of Decimal)() : Catch : End Try
                        End If

                        If String.Equals(symbol_val, tokenSymbol, StringComparison.Ordinal) Then
                            If String.Equals(from, address, StringComparison.Ordinal) Then
                                balance -= amountVal
                            End If
                            If String.Equals(to_addr, address, StringComparison.Ordinal) Then
                                balance += amountVal
                            End If
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for balance (GetBalanceAtBlock_Private): {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return If(balance < 0D, 0D, balance)
    End Function

    Friend Function TokenNameExistsOnChain_Private(name As String, upToBlockIndex As Integer) As Boolean
        Dim chainToCheck As List(Of Block)
        SyncLock Chain
            chainToCheck = Chain.Where(Function(b) b.Index <= upToBlockIndex).ToList()
        End SyncLock

        If Not chainToCheck.Any() Then Return False

        For Each block As Block In chainToCheck
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                       String.Equals(transactionData("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase) Then
                        Return True
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction in TokenNameExistsOnChain_Private: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return False
    End Function

    Friend Function TokenSymbolExistsOnChain_Private(symbolValText As String, upToBlockIndex As Integer) As Boolean
        Dim chainToCheck As List(Of Block)
        SyncLock Chain
            chainToCheck = Chain.Where(Function(b) b.Index <= upToBlockIndex).ToList()
        End SyncLock

        If Not chainToCheck.Any() Then Return False

        For Each block As Block In chainToCheck
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                       String.Equals(transactionData("symbol")?.ToString(), symbolValText, StringComparison.Ordinal) Then
                        Return True
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction in TokenSymbolExistsOnChain_Private: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return False
    End Function

    Public Function GetTotalSupply(tokenSymbol As String) As Decimal
        Dim totalSupply As Decimal = 0D
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    Dim txType = transactionData("type")?.ToString()
                    Dim currentSymbolForCreation = transactionData("symbol")?.ToString()
                    Dim currentTokenForTransfer = transactionData("token")?.ToString()

                    If txType = "tokenCreation" AndAlso String.Equals(currentSymbolForCreation, tokenSymbol, StringComparison.Ordinal) Then
                        Dim supply As Decimal = 0D
                        If transactionData("initialSupply") IsNot Nothing Then
                            Try : supply = transactionData("initialSupply").ToObject(Of Decimal)() : Catch : End Try
                        End If
                        totalSupply += supply
                    ElseIf txType = "transfer" AndAlso
                           String.Equals(currentTokenForTransfer, tokenSymbol, StringComparison.Ordinal) AndAlso
                           String.Equals(transactionData("from")?.ToString(), "miningReward", StringComparison.OrdinalIgnoreCase) Then
                        Dim amount As Decimal = 0D
                        If transactionData("amount") IsNot Nothing Then
                            Try : amount = transactionData("amount").ToObject(Of Decimal)() : Catch : End Try
                        End If
                        totalSupply += amount
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction for total supply: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return totalSupply
    End Function
#End Region
End Class