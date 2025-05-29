Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization
Imports System.Data

Public Class Blockchain

    Public Property Chain As List(Of Block)
    Private _dbConnection As SqliteConnection
    Public Property _difficulty As Integer = 4
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
                    Dim data As List(Of JObject) = JsonConvert.DeserializeObject(Of List(Of JObject))(reader.GetString(2))
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
            ' Set current difficulty based on the last block loaded, or default if chain is empty
            If Chain.Any() Then
                _difficulty = Chain.Last().Difficulty ' Could also be adjusted for next block, but this is a starting point
                ' Or, more accurately, if difficulty adjustment logic runs on load, _difficulty would be set.
                ' For now, let's assume last block's difficulty is a reasonable start for the *next* block.
                ' The MiningServer.AdjustDifficulty will correct it after new blocks.
            Else
                _difficulty = 4 ' Initial default difficulty if blockchain is new
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
            .initialSupply = CObj(0D), ' No premine for BEAN, only through mining
            .owner = "genesis",
            .txId = Guid.NewGuid.ToString("N")
        })

        Dim genesisData As New List(Of JObject) From {
            JObject.FromObject(New With {
                .transaction = tokenCreationData
            })
        }
        Dim genesisBlock As New Block(0, DateTime.UtcNow, genesisData, "0", _difficulty) ' Use current _difficulty
        genesisBlock.Mine()
        Chain.Add(genesisBlock)
        SaveBlockToDatabase(genesisBlock)
        Console.WriteLine($"Genesis block created with difficulty {genesisBlock.Difficulty}.")
    End Sub

    Public Function GetLatestBlock() As Block
        If Chain.Count > 0 Then
            Return Chain.Last()
        End If
        Return Nothing
    End Function
#End Region

#Region "Mempool"
    Public Function AddTransactionToMempool(transactionData As String) As String
        Dim txData As JObject = JObject.Parse(transactionData)
        Dim txId As String
        If txData.ContainsKey("txId") Then
            txId = txData("txId").ToString()
        Else
            txId = Guid.NewGuid().ToString("N")
            txData.Add("txId", txId)
        End If

        Dim transactionWrapper As New JObject
        transactionWrapper.Add("transaction", txData)
        _mempool.AddTransaction(transactionWrapper)
        Console.WriteLine($"Transaction {txId} added to the mempool.")
        Return txId
    End Function
#End Region

#Region "POST Actions"
    Public Function CreateToken(name As String, symbol As String, initialSupply As Decimal, ownerPublicKey As String, signature As String) As String
        Try
            If TokenNameExists(name) Then
                Throw New Exception("A token with this name already exists.")
            End If
            If TokenSymbolExists(symbol) Then
                Throw New Exception("A token with this symbol already exists.")
            End If

            Dim dataToSign As String = $"{name}:{symbol}:{initialSupply.ToString(CultureInfo.InvariantCulture)}:{ownerPublicKey}"
            If Not Wallet.VerifySignature(ownerPublicKey, signature, dataToSign) Then
                Throw New Exception("Invalid signature. Token creation canceled.")
            End If
            Console.WriteLine("Signature verified successfully for token creation.")

            Dim tokenData = JObject.FromObject(New With {
                .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                .type = "tokenCreation",
                .name = name,
                .symbol = symbol,
                .initialSupply = initialSupply,
                .owner = ownerPublicKey,
                .txId = Guid.NewGuid().ToString("N")
            }).ToString(Formatting.None)

            Dim txId As String = AddTransactionToMempool(tokenData)
            Console.WriteLine($"Token creation transaction {txId} for {symbol} added to the mempool.")
            Return txId
        Catch ex As Exception
            Console.WriteLine($"Error in CreateToken: {ex.Message}")
            Throw
        End Try
    End Function

    Public Function TransferTokens(toAddress As String, amount As Decimal, tokenSymbol As String, signature As String, fromAddressPublicKey As String) As String
        Try
            If fromAddressPublicKey = toAddress Then
                Throw New Exception("Cannot transfer tokens to the same address.")
            End If

            Dim dataToSign As String = $"{fromAddressPublicKey}:{toAddress}:{amount.ToString(CultureInfo.InvariantCulture)}:{tokenSymbol}"

            If Not Wallet.VerifySignature(fromAddressPublicKey, signature, dataToSign) Then
                Throw New Exception($"Invalid signature for transfer. Token transfer canceled.")
            End If
            Console.WriteLine("Signature verified successfully for token transfer.")

            Dim tokenBalances = GetTokensOwned(fromAddressPublicKey) ' Checks chain + mempool for current spendable balance
            If Not tokenBalances.ContainsKey(tokenSymbol) OrElse tokenBalances(tokenSymbol) < amount Then
                Throw New Exception($"Insufficient balance for token transfer of {amount} {tokenSymbol} from {fromAddressPublicKey}.")
            End If
            Console.WriteLine("Balance check passed for transfer.")

            If Not amount > 0.00000000D Then
                Throw New Exception("Cannot process transaction with zero or negative amount.")
            End If

            Dim transferData = JObject.FromObject(New With {
                .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                .type = "transfer",
                .from = fromAddressPublicKey,
                .to = toAddress,
                .amount = amount,
                .token = tokenSymbol,
                .txId = Guid.NewGuid().ToString("N")
            }).ToString(Formatting.None)

            Dim txId As String = AddTransactionToMempool(transferData)
            Console.WriteLine($"Transfer transaction {txId} for {amount} {tokenSymbol} from {fromAddressPublicKey} to {toAddress} added to mempool.")
            Return txId
        Catch ex As Exception
            Console.WriteLine($"Error in TransferTokens: {ex.Message}")
            Throw
        End Try
    End Function
#End Region

#Region "GET Actions"
    Public Function GetTokensOwned(address As String) As Dictionary(Of String, Decimal)
        Dim tokensOwned As New Dictionary(Of String, Decimal)

        For Each block As Block In Chain
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    Dim txType = transactionData("type")?.ToString()

                    If txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol = transactionData("symbol")?.ToString()
                        Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                        If owner = address AndAlso symbol IsNot Nothing Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + initialSupply
                        End If
                    ElseIf txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim too = transactionData("to")?.ToString()
                        Dim symbol = transactionData("token")?.ToString()
                        Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()

                        If symbol IsNot Nothing Then
                            If from = address Then
                                tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) - amountVal
                            End If
                            If too = address Then
                                tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + amountVal
                            End If
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for balance: {ex.Message} - {transactionWrapper.ToString()}")
                End Try
            Next
        Next

        For Each mempoolTxWrapper As JObject In _mempool.GetTransactions()
            Try
                Dim transactionData = JObject.Parse(mempoolTxWrapper("transaction").ToString())
                Dim txType = transactionData("type")?.ToString()
                If txType = "transfer" Then ' Only transfers affect balance from mempool perspective for this function
                    Dim from = transactionData("from")?.ToString()
                    Dim too = transactionData("to")?.ToString() ' Consider incoming from mempool too for spendable balance
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()

                    If symbol IsNot Nothing Then
                        If from = address Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) - amountVal
                        End If
                        ' For GetTokensOwned, usually we don't count incoming mempool tx as "owned" yet until confirmed.
                        ' However, for *spendable* balance check before adding to mempool, it might be considered.
                        ' Sticking to current logic: only outgoing mempool tx reduces "owned" for this query.
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for balance: {ex.Message} - {mempoolTxWrapper.ToString()}")
            End Try
        Next

        Dim keysToRemove = tokensOwned.Where(Function(kvp) kvp.Value <= 0.00000000D).Select(Function(kvp) kvp.Key).ToList()
        For Each key In keysToRemove
            tokensOwned.Remove(key)
        Next

        Return tokensOwned
    End Function

    Public Function GetTransactionHistory(address As String) As List(Of Object)
        Dim historyItems As New List(Of Object)

        For Each block As Block In Chain
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    Dim blockDateTime = block.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    Dim txType = transactionData("type")?.ToString()
                    Dim txId = transactionData("txId")?.ToString()
                    Dim txTimestampString = If(transactionData.ContainsKey("timestamp"), transactionData("timestamp").ToString(), block.Timestamp.ToString("o", CultureInfo.InvariantCulture))
                    Dim txTimestampDateTime As DateTime
                    DateTime.TryParseExact(txTimestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime)


                    If txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim too = transactionData("to")?.ToString()
                        Dim symbol = transactionData("token")?.ToString()
                        Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()
                        If from = address OrElse too = address Then
                            Dim itemType = If(from = "miningReward", "Mining Reward", "Transfer")
                            Dim amountString = ""
                            If from = address Then amountString = $"-{amountVal} {symbol}"
                            If too = address Then amountString = $"+{amountVal} {symbol}"
                            If from = "miningReward" AndAlso too = address Then amountString = $"+{amountVal} {symbol}"


                            historyItems.Add(New With {
                                .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                .BlockTimestamp = blockDateTime,
                                .Type = itemType, .From = from, .To = too, .AmountString = amountString, .Token = symbol, .Value = amountVal,
                                .BlockHash = block.Hash, .TxId = txId, .Status = "Completed"
                            })
                        End If
                    ElseIf txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol = transactionData("symbol")?.ToString()
                        Dim name = transactionData("name")?.ToString()
                        Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                        If owner = address Then
                            historyItems.Add(New With {
                                .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                .BlockTimestamp = blockDateTime, .Type = "Token Creation", .From = "N/A", .To = owner,
                                .AmountString = $"+{initialSupply} {symbol} ({name})", .Token = symbol, .Value = initialSupply,
                                .BlockHash = block.Hash, .TxId = txId, .Status = "Completed"
                            })
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for history: {ex.Message}")
                End Try
            Next
        Next

        For Each mempoolTxWrapper As JObject In _mempool.GetTransactions()
            Try
                Dim transactionData = JObject.Parse(mempoolTxWrapper("transaction").ToString())
                Dim txType = transactionData("type")?.ToString()
                Dim txId = transactionData("txId")?.ToString()
                Dim txTimestampString = If(transactionData.ContainsKey("timestamp"), transactionData("timestamp").ToString(), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                Dim txTimestampDateTime As DateTime
                DateTime.TryParseExact(txTimestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime)

                If txType = "transfer" Then
                    Dim from = transactionData("from")?.ToString()
                    Dim too = transactionData("to")?.ToString()
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()
                    If from = address OrElse too = address Then
                        Dim amountString = ""
                        If from = address Then amountString = $"-{amountVal} {symbol}"
                        If too = address Then amountString = $"+{amountVal} {symbol}"
                        historyItems.Add(New With {
                            .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .BlockTimestamp = "Pending", .Type = "Transfer", .From = from, .To = too,
                            .AmountString = amountString,
                            .Token = symbol, .Value = amountVal, .BlockHash = "Pending", .TxId = txId, .Status = "Pending"
                        })
                    End If
                ElseIf txType = "tokenCreation" Then
                    Dim owner = transactionData("owner")?.ToString()
                    Dim symbol = transactionData("symbol")?.ToString()
                    Dim name = transactionData("name")?.ToString()
                    Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                    If owner = address Then
                        historyItems.Add(New With {
                            .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .BlockTimestamp = "Pending", .Type = "Token Creation", .From = "N/A", .To = owner,
                            .AmountString = $"+{initialSupply} {symbol} ({name})", .Token = symbol, .Value = initialSupply,
                            .BlockHash = "Pending", .TxId = txId, .Status = "Pending"
                        })
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for history: {ex.Message}")
            End Try
        Next
        ' Sort by BlockTimestamp (desc for completed, specific handling for pending), then TxTimestamp (desc)
        Return historyItems.OrderByDescending(Function(h) If(h.Status = "Pending", DateTime.MaxValue, DateTime.ParseExact(h.BlockTimestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))) _
                           .ThenByDescending(Function(h) DateTime.ParseExact(h.TxTimestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) _
                           .ToList()
    End Function

    Public Function GetTransactionByTxId(txId As String) As Object
        For Each block As Block In Chain
            For Each transactionWrapper As JObject In block.Data
                Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                If transactionData("txId")?.ToString() = txId Then
                    Return New With {
                        .status = "completed", .timestamp = block.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                        .blockIndex = block.Index, .blockHash = block.Hash, .transaction = transactionData
                    }
                End If
            Next
        Next
        Dim mempoolTransactionWrapper = _mempool.GetTransactionByTxId(txId)
        If mempoolTransactionWrapper IsNot Nothing Then
            Dim transactionData = JObject.Parse(mempoolTransactionWrapper("transaction").ToString())
            Return New With {
                .status = "pending", .timestamp = transactionData("timestamp")?.ToString(),
                .blockIndex = -1, .blockHash = "N/A", .transaction = transactionData
            }
        End If
        Return Nothing
    End Function

    Public Function GetTokenNames() As Dictionary(Of String, String)
        Dim tokenNames As New Dictionary(Of String, String)
        For Each block As Block In Chain
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    If transactionData("type")?.ToString() = "tokenCreation" Then
                        Dim symbol As String = transactionData("symbol")?.ToString()
                        Dim name As String = transactionData("name")?.ToString()
                        If symbol IsNot Nothing AndAlso name IsNot Nothing Then
                            tokenNames(symbol) = name ' Last one wins if duplicate symbols (should be prevented by validation)
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction for token names: {ex.Message}")
                End Try
            Next
        Next
        Return tokenNames
    End Function

#End Region

#Region "Helper Functions and Validation Support"
    Public Function IsChainValid() As Boolean
        If Chain Is Nothing OrElse Chain.Count = 0 Then Return True ' Empty chain is valid by definition

        For i As Integer = 0 To Chain.Count - 1
            Dim currentBlock = Chain(i)

            If currentBlock.Hash <> currentBlock.CalculateHash() Then
                Console.WriteLine($"Chain invalid: Block {currentBlock.Index} (Hash: {currentBlock.Hash.Substring(0, 8)}) hash mismatch. Calculated: {currentBlock.CalculateHash().Substring(0, 8)}")
                Return False
            End If

            If i > 0 Then
                Dim previousBlock = Chain(i - 1)
                If currentBlock.PreviousHash <> previousBlock.Hash Then
                    Console.WriteLine($"Chain invalid: Block {currentBlock.Index} previous hash mismatch.")
                    Return False
                End If
            Else ' Genesis block specific checks
                If currentBlock.Index <> 0 OrElse currentBlock.PreviousHash <> "0" Then
                    Console.WriteLine($"Chain invalid: Genesis block (Index {currentBlock.Index}) structure error (PrevHash: {currentBlock.PreviousHash}).")
                    Return False
                End If
            End If

            If Not currentBlock.Hash.StartsWith(New String("0", currentBlock.Difficulty)) Then
                Console.WriteLine($"Chain invalid: Block {currentBlock.Index} PoW not met (difficulty {currentBlock.Difficulty}). Hash: {currentBlock.Hash}")
                Return False
            End If
        Next
        Return True
    End Function

    Public Function TokenNameExists(name As String) As Boolean
        ' Checks confirmed chain
        If TokenNameExistsOnChain_Private(name, If(Chain.Any, Chain.Last.Index, -1)) Then Return True
        ' Checks mempool
        For Each mempoolTxWrapper As JObject In _mempool.GetTransactions()
            Try
                Dim transactionData = JObject.Parse(mempoolTxWrapper("transaction").ToString())
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

    Public Function TokenSymbolExists(symbol As String) As Boolean
        ' Checks confirmed chain
        If TokenSymbolExistsOnChain_Private(symbol, If(Chain.Any, Chain.Last.Index, -1)) Then Return True
        ' Checks mempool
        For Each mempoolTxWrapper As JObject In _mempool.GetTransactions()
            Try
                Dim transactionData = JObject.Parse(mempoolTxWrapper("transaction").ToString())
                If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                   transactionData("symbol")?.ToString() = symbol Then
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
        If upToBlockIndex < 0 AndAlso Chain.Count = 0 Then Return 0D ' No blocks to check

        For i As Integer = 0 To Math.Min(upToBlockIndex, Me.Chain.Count - 1)
            Dim block = Me.Chain(i)
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    Dim txType = transactionData("type")?.ToString()

                    If txType = "tokenCreation" Then
                        Dim owner = transactionData("owner")?.ToString()
                        Dim symbol = transactionData("symbol")?.ToString()
                        Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                        If owner = address AndAlso symbol = tokenSymbol Then
                            balance += initialSupply
                        End If
                    ElseIf txType = "transfer" Then
                        Dim from = transactionData("from")?.ToString()
                        Dim too = transactionData("to")?.ToString()
                        Dim symbol = transactionData("token")?.ToString()
                        Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()

                        If symbol = tokenSymbol Then
                            If from = address Then
                                balance -= amountVal
                            End If
                            If too = address Then
                                balance += amountVal
                            End If
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing chain transaction for balance (GetBalanceAtBlock_Private): {ex.Message} - {transactionWrapper.ToString()}")
                End Try
            Next
        Next
        Return If(balance < 0D, 0D, balance) ' Should not really happen with valid tx but as a safeguard
    End Function

    Friend Function TokenNameExistsOnChain_Private(name As String, upToBlockIndex As Integer) As Boolean
        If upToBlockIndex < 0 AndAlso Chain.Count = 0 Then Return False

        For i As Integer = 0 To Math.Min(upToBlockIndex, Me.Chain.Count - 1)
            Dim block = Me.Chain(i)
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                       String.Equals(transactionData("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase) Then
                        Return True
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction in TokenNameExistsOnChain_Private: {ex.Message}")
                End Try
            Next
        Next
        Return False
    End Function

    Friend Function TokenSymbolExistsOnChain_Private(symbolVal As String, upToBlockIndex As Integer) As Boolean
        If upToBlockIndex < 0 AndAlso Chain.Count = 0 Then Return False

        For i As Integer = 0 To Math.Min(upToBlockIndex, Me.Chain.Count - 1)
            Dim block = Me.Chain(i)
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    If transactionData("type")?.ToString() = "tokenCreation" AndAlso
                       transactionData("symbol")?.ToString() = symbolVal Then
                        Return True
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction in TokenSymbolExistsOnChain_Private: {ex.Message}")
                End Try
            Next
        Next
        Return False
    End Function

    Public Function GetTotalSupply(tokenSymbol As String) As Decimal
        Dim totalSupply As Decimal = 0D
        For Each block As Block In Chain
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionData = JObject.Parse(transactionWrapper("transaction").ToString())
                    Dim txType = transactionData("type")?.ToString()

                    If txType = "tokenCreation" AndAlso transactionData("symbol")?.ToString() = tokenSymbol Then
                        totalSupply += transactionData("initialSupply")?.ToObject(Of Decimal)()
                    ElseIf txType = "transfer" AndAlso
                           transactionData("token")?.ToString() = tokenSymbol AndAlso
                           transactionData("from")?.ToString() = "miningReward" Then ' Coinbase for the specified token
                        totalSupply += transactionData("amount")?.ToObject(Of Decimal)()
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction for total supply: {ex.Message}")
                End Try
            Next
        Next
        Return totalSupply
    End Function
#End Region
End Class