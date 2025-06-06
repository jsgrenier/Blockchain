Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization
Imports System.Data
Imports System.Diagnostics ' For Debug.WriteLine if you prefer over Console


Public Class Blockchain

    Public Property Chain As List(Of Block)
    ' REMOVED: Private _dbConnection As SqliteConnection ' This was a potential source of threading issues.
    Public Property _difficulty As Integer = 4 ' Default difficulty
    Public _mempool As New Mempool()
    Public Property CurrentEstimatedNetworkHashRate As Double = 0.0
    Private ReadOnly _hashRateLock As New Object()
    Public Const MaxBeanSupply As Decimal = 21000000D

    Private ReadOnly _dbFilePath_internal As String
    Private _isInitialBalancePopulationDone As Boolean = False
    Private _isAddrTxLinkPopulationDone As Boolean = False

    ' --- NEW: In-memory sets for fast validation ---
    Private ReadOnly _tokenNamesOnChain As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _tokenSymbolsOnChain As New HashSet(Of String)(StringComparer.Ordinal)


    Public Sub New(dbFilePath As String)
        Me._dbFilePath_internal = dbFilePath
        Chain = New List(Of Block)

        CreateDatabaseIfNotExists()

        LoadChainFromDatabase() ' This will now call PopulateTokenSets

        ' --- Initial Population Logic for AccountBalances ---
        If Chain.Any() Then
            Dim needsBalancePopulation As Boolean = False
            Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
                Try
                    connection.Open()
                    Using cmdCheck As New SqliteCommand("SELECT COUNT(*) FROM AccountBalances", connection)
                        Dim balanceRows = Convert.ToInt64(cmdCheck.ExecuteScalar())
                        If balanceRows = 0 Then
                            needsBalancePopulation = True
                        Else
                            Console.WriteLine("AccountBalances table appears to have data. Skipping initial population for this session.")
                        End If
                    End Using
                Catch ex As Exception
                    Console.WriteLine($"Error checking AccountBalances table state: {ex.Message}. Assuming population is needed.")
                    needsBalancePopulation = True
                End Try
            End Using

            If needsBalancePopulation AndAlso Not Me._isInitialBalancePopulationDone Then
                Console.WriteLine("AccountBalances table requires initial population. This may take some time...")
                InitialPopulateAccountBalances()
                Me._isInitialBalancePopulationDone = True
                Console.WriteLine("Initial population of AccountBalances table finished.")
            ElseIf needsBalancePopulation AndAlso Me._isInitialBalancePopulationDone Then
                Console.WriteLine("AccountBalances table needs population, but it was already attempted this session. Manual check might be needed if data is missing.")
            End If
        End If

        ' --- Initial Population Logic for AddressTransactionLinks ---
        If Chain.Any() Then
            Dim needsAddrTxLinkPopulation As Boolean = False
            Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
                Try
                    connection.Open()
                    Using cmdCheck As New SqliteCommand("SELECT COUNT(*) FROM AddressTransactionLinks", connection)
                        Dim linkRows = Convert.ToInt64(cmdCheck.ExecuteScalar())
                        If linkRows = 0 Then
                            needsAddrTxLinkPopulation = True
                        Else
                            Console.WriteLine("AddressTransactionLinks table appears to have data. Skipping initial population for this session.")
                        End If
                    End Using
                Catch ex As Exception
                    Console.WriteLine($"Error checking AddressTransactionLinks table state: {ex.Message}. Assuming population is needed.")
                    needsAddrTxLinkPopulation = True
                End Try
            End Using

            If needsAddrTxLinkPopulation AndAlso Not Me._isAddrTxLinkPopulationDone Then
                Console.WriteLine("AddressTransactionLinks table requires initial population. This may take some time...")
                InitialPopulateAddressTransactionLinks()
                Me._isAddrTxLinkPopulationDone = True
                Console.WriteLine("Initial population of AddressTransactionLinks table finished.")
            ElseIf needsAddrTxLinkPopulation AndAlso Me._isAddrTxLinkPopulationDone Then
                Console.WriteLine("AddressTransactionLinks table needs population, but it was already attempted this session. Manual check might be needed if data is missing.")
            End If
        End If

        If Not Chain.Any() Then
            AddGenesisBlock()
        End If
    End Sub

    ' --- NEW: Helper method to populate the fast-lookup sets ---
    Private Sub PopulateTokenSets()
        _tokenNamesOnChain.Clear()
        _tokenSymbolsOnChain.Clear()
        For Each block As Block In Chain
            For Each txWrapper As JObject In block.Data
                Dim txData = CType(txWrapper("transaction"), JObject)
                If txData("type")?.ToString() = "tokenCreation" Then
                    Dim name = txData("name")?.ToString()
                    Dim symbol = txData("symbol")?.ToString()
                    If Not String.IsNullOrEmpty(name) Then _tokenNamesOnChain.Add(name)
                    If Not String.IsNullOrEmpty(symbol) Then _tokenSymbolsOnChain.Add(symbol)
                End If
            Next
        Next
    End Sub

    Public Function GetDatabaseFilePath() As String
        Return Me._dbFilePath_internal
    End Function

#Region "DB"
    Private Sub CreateDatabaseIfNotExists()
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim createBlocksTableQuery As String = "CREATE TABLE IF NOT EXISTS Blocks (
                                                [Index] INTEGER PRIMARY KEY,
                                                Timestamp TEXT,
                                                Data TEXT, 
                                                PreviousHash TEXT,
                                                Hash TEXT,
                                                Nonce INTEGER,
                                                Difficulty INTEGER,
                                                BlockSize INTEGER
                                                );"
                Using blocksCommand As New SqliteCommand(createBlocksTableQuery, connection)
                    blocksCommand.ExecuteNonQuery()
                End Using


                Dim createHistoricalHashRateTableQuery As String = "CREATE TABLE IF NOT EXISTS HistoricalHashRates (
                                                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                                            Timestamp TEXT NOT NULL,
                                                            EstimatedHashRate REAL NOT NULL
                                                            );"
                Using hashRateCommand As New SqliteCommand(createHistoricalHashRateTableQuery, connection)
                    hashRateCommand.ExecuteNonQuery()
                End Using
                Console.WriteLine("HistoricalHashRates table checked/created.")

                Dim createAccountBalancesTableQuery As String = "CREATE TABLE IF NOT EXISTS AccountBalances (
                                                    Address TEXT NOT NULL,
                                                    TokenSymbol TEXT NOT NULL,
                                                    Balance REAL NOT NULL,
                                                    LastUpdatedBlockIndex INTEGER NOT NULL,
                                                    PRIMARY KEY (Address, TokenSymbol)
                                                    );"
                Using balancesCommand As New SqliteCommand(createAccountBalancesTableQuery, connection)
                    balancesCommand.ExecuteNonQuery()
                End Using
                Console.WriteLine("AccountBalances table checked/created.")

                Dim createAddrTxLinksTableQuery As String = "
        CREATE TABLE IF NOT EXISTS AddressTransactionLinks (
            Address TEXT NOT NULL,
            TxId TEXT NOT NULL,
            BlockIndex INTEGER NOT NULL,
            BlockHash TEXT NOT NULL, 
            TxTimestamp TEXT NOT NULL, 
            TransactionRole TEXT NOT NULL, 
            TokenSymbol TEXT,
            Amount REAL,
            OtherPartyAddress TEXT,
            PRIMARY KEY (Address, TxId, BlockIndex, TransactionRole) 
        );"
                Using addrTxLinksCommand As New SqliteCommand(createAddrTxLinksTableQuery, connection)
                    addrTxLinksCommand.ExecuteNonQuery()
                End Using

                Dim createAddrTxLinksIndexQuery As String = "
        CREATE INDEX IF NOT EXISTS IDX_AddressTransactionLinks_Address_BlockIndex_Timestamp 
        ON AddressTransactionLinks (Address, BlockIndex DESC, TxTimestamp DESC);"
                Using addrTxLinksIndexCommand As New SqliteCommand(createAddrTxLinksIndexQuery, connection)
                    addrTxLinksIndexCommand.ExecuteNonQuery()
                End Using

            Catch ex As Exception
                Console.WriteLine($"Error creating/checking database tables: {ex.Message}")
            End Try
        End Using
    End Sub

    Private Sub LoadChainFromDatabase()
        Chain.Clear()
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim selectQuery As String = "SELECT * FROM Blocks ORDER BY [Index]"
                Using command As New SqliteCommand(selectQuery, connection)
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

                            Dim nonceFromDb As Long
                            If reader.IsDBNull(5) Then
                                nonceFromDb = 0L
                                Console.WriteLine($"Warning: Nonce for block index {reader.GetInt32(0)} is DBNull. Defaulting to 0.")
                            Else
                                nonceFromDb = reader.GetInt64(5)
                            End If

                            Dim block As New Block(
                                reader.GetInt32(0), timestamp, data,
                                reader.GetString(3), reader.GetString(4),
                                nonceFromDb, difficultyFromDb, blockSizeFromDb
                            )
                            If blockSizeFromDb = 0 Then
                                block.BlockSize = block.CalculateBlockSize()
                            End If

                            Chain.Add(block)
                        End While
                    End Using
                End Using

                If Chain.Any() Then
                    _difficulty = Chain.Last().Difficulty
                    ' --- NEW: Populate sets after loading chain ---
                    PopulateTokenSets()
                    Console.WriteLine($"Loaded {Chain.Count} blocks. Populated {_tokenNamesOnChain.Count} unique token names and {_tokenSymbolsOnChain.Count} symbols for validation.")
                Else
                    _difficulty = 4
                End If

            Catch ex As Exception
                Console.WriteLine($"Error loading chain from database: {ex.Message}{vbCrLf}{ex.StackTrace}")
            End Try
        End Using

        If Chain.Count = 0 Then
            AddGenesisBlock()
        End If
    End Sub

    Public Sub SaveBlockToDatabase(block As Block)
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim insertQuery As String = "INSERT INTO Blocks ([Index], Timestamp, Data, PreviousHash, Hash, Nonce, Difficulty, BlockSize) 
                                                VALUES (@Index, @Timestamp, @Data, @PreviousHash, @Hash, @Nonce, @Difficulty, @BlockSize)"
                Using command As New SqliteCommand(insertQuery, connection)
                    command.Parameters.AddWithValue("@Index", block.Index)
                    command.Parameters.AddWithValue("@Timestamp", block.Timestamp.ToString("o", CultureInfo.InvariantCulture))
                    command.Parameters.AddWithValue("@Data", JsonConvert.SerializeObject(block.Data))
                    command.Parameters.AddWithValue("@PreviousHash", block.PreviousHash)
                    command.Parameters.AddWithValue("@Hash", block.Hash)
                    command.Parameters.AddWithValue("@Nonce", block.Nonce)
                    command.Parameters.AddWithValue("@Difficulty", block.Difficulty)
                    command.Parameters.AddWithValue("@BlockSize", block.BlockSize)
                    command.ExecuteNonQuery()
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error saving block to database: {ex.Message}")
            End Try
        End Using
        ' --- NEW: Update in-memory sets after saving a new block ---
        For Each txWrapper As JObject In block.Data
            Dim txData = CType(txWrapper("transaction"), JObject)
            If txData("type")?.ToString() = "tokenCreation" Then
                Dim name = txData("name")?.ToString()
                Dim symbol = txData("symbol")?.ToString()
                If Not String.IsNullOrEmpty(name) Then _tokenNamesOnChain.Add(name)
                If Not String.IsNullOrEmpty(symbol) Then _tokenSymbolsOnChain.Add(symbol)
            End If
        Next
    End Sub

    Public Sub UpdateAccountBalancesFromBlock(block As Block)
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Using dbTx As SqliteTransaction = connection.BeginTransaction()
                    For Each txWrapper As JObject In block.Data
                        Dim transactionData As JObject = CType(txWrapper("transaction"), JObject)
                        Dim txType = transactionData("type")?.ToString()

                        If txType = "tokenCreation" Then
                            Dim owner = transactionData("owner")?.ToString()
                            Dim symbol = transactionData("symbol")?.ToString()
                            Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                            If Not String.IsNullOrEmpty(owner) AndAlso Not String.IsNullOrEmpty(symbol) AndAlso initialSupply >= 0 Then
                                UpsertBalanceInDb(owner, symbol, initialSupply, block.Index, "add", connection, dbTx)
                            End If
                        ElseIf txType = "transfer" Then
                            Dim fromAddress = transactionData("from")?.ToString()
                            Dim toAddress = transactionData("to")?.ToString()
                            Dim symbol = transactionData("token")?.ToString()
                            Dim amount = transactionData("amount")?.ToObject(Of Decimal)()

                            If Not String.IsNullOrEmpty(symbol) AndAlso amount > 0 Then
                                If Not String.IsNullOrEmpty(fromAddress) AndAlso fromAddress <> "miningReward" Then
                                    UpsertBalanceInDb(fromAddress, symbol, amount, block.Index, "subtract", connection, dbTx)
                                End If
                                If Not String.IsNullOrEmpty(toAddress) Then
                                    UpsertBalanceInDb(toAddress, symbol, amount, block.Index, "add", connection, dbTx)
                                End If
                            End If
                        End If
                    Next
                    dbTx.Commit()
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error updating account balances for block {block.Index}: {ex.Message}")
            End Try
        End Using
    End Sub

    Private Sub UpsertBalanceInDb(address As String, tokenSymbol As String, amount As Decimal, blockIndex As Integer, operationType As String, connection As SqliteConnection, dbTx As SqliteTransaction)
        Dim currentBalance As Decimal = 0D
        Dim balanceExists As Boolean = False

        Dim selectQuery As String = "SELECT Balance FROM AccountBalances WHERE Address = @Address AND TokenSymbol = @TokenSymbol"
        Using cmdSelect As New SqliteCommand(selectQuery, connection, dbTx)
            cmdSelect.Parameters.AddWithValue("@Address", address)
            cmdSelect.Parameters.AddWithValue("@TokenSymbol", tokenSymbol)
            Dim result = cmdSelect.ExecuteScalar()
            If result IsNot Nothing AndAlso result IsNot DBNull.Value Then
                currentBalance = Convert.ToDecimal(result)
                balanceExists = True
            End If
        End Using

        If operationType = "add" Then
            currentBalance += amount
        ElseIf operationType = "subtract" Then
            currentBalance -= amount
        End If

        If currentBalance < 0 Then
            Console.WriteLine($"CRITICAL WARNING: Balance for {address} {tokenSymbol} would go negative ({currentBalance}). This might indicate an issue. Setting to 0.")
            currentBalance = 0D
        End If

        Dim upsertQuery As String = If(balanceExists,
                                    "UPDATE AccountBalances SET Balance = @Balance, LastUpdatedBlockIndex = @BlockIndex WHERE Address = @Address AND TokenSymbol = @TokenSymbol",
                                    "INSERT INTO AccountBalances (Address, TokenSymbol, Balance, LastUpdatedBlockIndex) VALUES (@Address, @TokenSymbol, @Balance, @BlockIndex)")

        Using cmdUpsert As New SqliteCommand(upsertQuery, connection, dbTx)
            cmdUpsert.Parameters.AddWithValue("@Address", address)
            cmdUpsert.Parameters.AddWithValue("@TokenSymbol", tokenSymbol)
            cmdUpsert.Parameters.AddWithValue("@Balance", currentBalance)
            cmdUpsert.Parameters.AddWithValue("@BlockIndex", blockIndex)
            cmdUpsert.ExecuteNonQuery()
        End Using
    End Sub

    Public Sub UpdateAddressTransactionLinksFromBlock(block As Block)
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Using dbTx As SqliteTransaction = connection.BeginTransaction()
                    For Each txWrapper As JObject In block.Data
                        Dim transactionToken As JToken = txWrapper("transaction")
                        If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                        Dim txData As JObject = CType(transactionToken, JObject)

                        Dim txId = txData("txId")?.ToString()
                        Dim txTimestamp = If(txData.ContainsKey("timestamp") AndAlso txData("timestamp").Type = JTokenType.String,
                                         txData("timestamp").ToString(),
                                         block.Timestamp.ToString("o", CultureInfo.InvariantCulture))

                        If String.IsNullOrEmpty(txId) Then Continue For

                        Dim txType = txData("type")?.ToString()
                        Dim tokenSymbol As String = Nothing
                        Dim amountVal As Decimal = 0D

                        If txType = "tokenCreation" Then
                            Dim owner = txData("owner")?.ToString()
                            tokenSymbol = txData("symbol")?.ToString()
                            If txData("initialSupply") IsNot Nothing Then amountVal = txData("initialSupply").ToObject(Of Decimal)()
                            If Not String.IsNullOrEmpty(owner) AndAlso Not String.IsNullOrEmpty(tokenSymbol) Then
                                InsertAddressTxLink(owner, txId, block.Index, block.Hash, txTimestamp, "owner", tokenSymbol, amountVal, Nothing, connection, dbTx)
                            End If
                        ElseIf txType = "transfer" Then
                            Dim fromAddress = txData("from")?.ToString()
                            Dim toAddress = txData("to")?.ToString()
                            tokenSymbol = txData("token")?.ToString()
                            If txData("amount") IsNot Nothing Then amountVal = txData("amount").ToObject(Of Decimal)()

                            If Not String.IsNullOrEmpty(tokenSymbol) AndAlso amountVal > 0 Then
                                If Not String.IsNullOrEmpty(fromAddress) Then
                                    Dim role = If(String.Equals(fromAddress, "miningReward", StringComparison.OrdinalIgnoreCase), "reward_source", "sender")
                                    InsertAddressTxLink(fromAddress, txId, block.Index, block.Hash, txTimestamp, role, tokenSymbol, amountVal, toAddress, connection, dbTx)
                                End If
                                If Not String.IsNullOrEmpty(toAddress) Then
                                    Dim role = If(String.Equals(fromAddress, "miningReward", StringComparison.OrdinalIgnoreCase), "reward_recipient", "receiver")
                                    InsertAddressTxLink(toAddress, txId, block.Index, block.Hash, txTimestamp, role, tokenSymbol, amountVal, fromAddress, connection, dbTx)
                                End If
                            End If
                        End If
                    Next
                    dbTx.Commit()
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error updating AddressTransactionLinks for block {block.Index}: {ex.Message}{vbCrLf}{ex.StackTrace}")
            End Try
        End Using
    End Sub

    Private Sub InsertAddressTxLink(address As String, txId As String, blockIndex As Integer, blockHash As String, txTimestamp As String,
                                role As String, tokenSymbol As String, amount As Decimal, otherParty As String,
                                connection As SqliteConnection, dbTx As SqliteTransaction)
        If String.IsNullOrEmpty(address) OrElse String.IsNullOrEmpty(txId) Then Return
        If String.Equals(address, "miningReward", StringComparison.OrdinalIgnoreCase) AndAlso role = "reward_source" Then Return

        Dim query As String = "
        INSERT INTO AddressTransactionLinks 
        (Address, TxId, BlockIndex, BlockHash, TxTimestamp, TransactionRole, TokenSymbol, Amount, OtherPartyAddress) 
        VALUES (@Address, @TxId, @BlockIndex, @BlockHash, @TxTimestamp, @TransactionRole, @TokenSymbol, @Amount, @OtherPartyAddress)"

        Using cmd As New SqliteCommand(query, connection, dbTx)
            cmd.Parameters.AddWithValue("@Address", address)
            cmd.Parameters.AddWithValue("@TxId", txId)
            cmd.Parameters.AddWithValue("@BlockIndex", blockIndex)
            cmd.Parameters.AddWithValue("@BlockHash", blockHash)
            cmd.Parameters.AddWithValue("@TxTimestamp", txTimestamp)
            cmd.Parameters.AddWithValue("@TransactionRole", role)
            cmd.Parameters.AddWithValue("@TokenSymbol", If(tokenSymbol Is Nothing, DBNull.Value, CObj(tokenSymbol)))
            cmd.Parameters.AddWithValue("@Amount", amount)
            cmd.Parameters.AddWithValue("@OtherPartyAddress", If(otherParty Is Nothing, DBNull.Value, CObj(otherParty)))
            cmd.ExecuteNonQuery()
        End Using
    End Sub
#End Region

#Region "Block related"
    Private Sub AddGenesisBlock()
        Console.WriteLine("Adding Genesis Block as chain is empty...")
        Dim tokenCreationData = JObject.FromObject(New With {
        .timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        .type = "tokenCreation",
        .name = "Beancoin",
        .symbol = "BEAN",
        .initialSupply = CObj(0D),
        .owner = "genesis",
        .txId = Guid.NewGuid().ToString("N")
    })

        Dim genesisTransactionWrapper As New JObject From {{"transaction", tokenCreationData}}
        Dim genesisBlockData As New List(Of JObject) From {genesisTransactionWrapper}

        Dim genesisBlock As New Block(0, DateTime.UtcNow, genesisBlockData, "0", _difficulty)
        genesisBlock.Mine()

        Chain.Add(genesisBlock)
        SaveBlockToDatabase(genesisBlock)

        UpdateAccountBalancesFromBlock(genesisBlock)
        UpdateAddressTransactionLinksFromBlock(genesisBlock)

        Console.WriteLine($"Genesis block (Index {genesisBlock.Index}, Hash: {genesisBlock.Hash.Substring(0, 8)}...) created, saved, and indexed with difficulty {genesisBlock.Difficulty}.")
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

        Dim txId As String = txDataAsJObject("txId")?.ToString()
        If String.IsNullOrEmpty(txId) Then
            txId = Guid.NewGuid().ToString("N")
            txDataAsJObject.Add("txId", txId)
        End If

        Dim transactionWrapper As New JObject From {{"transaction", txDataAsJObject}}

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

        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim selectConfirmedQuery As String = "SELECT TokenSymbol, Balance FROM AccountBalances WHERE Address = @Address"
                Using cmd As New SqliteCommand(selectConfirmedQuery, connection)
                    cmd.Parameters.AddWithValue("@Address", address)
                    Using reader As SqliteDataReader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim symbol = reader.GetString(0)
                            Dim balance = reader.GetDecimal(1)
                            If balance > 0.00000000D Then
                                tokensOwned(symbol) = balance
                            End If
                        End While
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error fetching confirmed balances for {address} from DB: {ex.Message}. Balances might be incomplete.")
            End Try
        End Using

        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionToken As JToken = mempoolTxWrapper("transaction")
                If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                Dim transactionData As JObject = CType(transactionToken, JObject)
                Dim txType = transactionData("type")?.ToString()

                If txType = "transfer" Then
                    Dim from = transactionData("from")?.ToString()
                    Dim to_addr = transactionData("to")?.ToString()
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amountVal = transactionData("amount")?.ToObject(Of Decimal)()

                    If Not String.IsNullOrEmpty(symbol) AndAlso amountVal > 0D Then
                        If String.Equals(from, address, StringComparison.Ordinal) Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) - amountVal
                        End If
                        If String.Equals(to_addr, address, StringComparison.Ordinal) Then
                            tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + amountVal
                        End If
                    End If
                ElseIf txType = "tokenCreation" Then
                    Dim owner = transactionData("owner")?.ToString()
                    Dim symbol = transactionData("symbol")?.ToString()
                    Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                    If String.Equals(owner, address, StringComparison.Ordinal) AndAlso Not String.IsNullOrEmpty(symbol) AndAlso initialSupply > 0D Then
                        tokensOwned(symbol) = tokensOwned.GetValueOrDefault(symbol, 0D) + initialSupply
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for balance (GetTokensOwned): {ex.Message}")
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
        Dim pageLimit As Integer = 100
        Dim allTokenNames As Dictionary(Of String, String) = GetTokenNames()

        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim query As String = "
            SELECT atl.TxId, atl.BlockIndex, atl.BlockHash, atl.TxTimestamp, atl.TransactionRole, 
                    atl.TokenSymbol, atl.Amount, atl.OtherPartyAddress, b.Timestamp AS BlockChainTimestamp
            FROM AddressTransactionLinks atl
            JOIN Blocks b ON atl.BlockIndex = b.[Index]
            WHERE atl.Address = @Address
            ORDER BY atl.BlockIndex DESC, atl.TxTimestamp DESC
            LIMIT @Limit"

                Using cmd As New SqliteCommand(query, connection)
                    cmd.Parameters.AddWithValue("@Address", address)
                    cmd.Parameters.AddWithValue("@Limit", pageLimit)
                    Using reader As SqliteDataReader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim txId = reader.GetString(reader.GetOrdinal("TxId"))
                            Dim blockHash = reader.GetString(reader.GetOrdinal("BlockHash"))
                            Dim txTimestampStr = reader.GetString(reader.GetOrdinal("TxTimestamp"))
                            Dim role = reader.GetString(reader.GetOrdinal("TransactionRole"))
                            Dim tokenSymbol = If(reader.IsDBNull(reader.GetOrdinal("TokenSymbol")), Nothing, reader.GetString(reader.GetOrdinal("TokenSymbol")))
                            Dim amount = reader.GetDecimal(reader.GetOrdinal("Amount"))
                            Dim otherParty = If(reader.IsDBNull(reader.GetOrdinal("OtherPartyAddress")), Nothing, reader.GetString(reader.GetOrdinal("OtherPartyAddress")))
                            Dim blockChainTimestampStr = reader.GetString(reader.GetOrdinal("BlockChainTimestamp"))

                            Dim dateTimeFormats As String() = {
                            "o", "yyyy-MM-ddTHH:mm:ss.fffffffZ", "yyyy-MM-ddTHH:mm:ss.ffffffZ", "yyyy-MM-ddTHH:mm:ss.fffffZ",
                            "yyyy-MM-ddTHH:mm:ss.ffffZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ss.ffZ",
                            "yyyy-MM-ddTHH:mm:ss.fZ", "yyyy-MM-ddTHH:mm:ssZ"
                        }

                            Dim txTimestampDateTime As DateTime
                            If Not DateTime.TryParseExact(txTimestampStr, dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime) Then
                                txTimestampDateTime = DateTime.MinValue
                            End If

                            Dim blockChainTimestampDateTime As DateTime
                            If Not DateTime.TryParseExact(blockChainTimestampStr, dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, blockChainTimestampDateTime) Then
                                blockChainTimestampDateTime = DateTime.MinValue
                            End If

                            Dim itemType As String = ""
                            Dim fromDisplay = If(role = "sender" OrElse role = "owner", address, otherParty)
                            Dim toDisplay = If(role = "receiver" OrElse role = "owner" OrElse role = "reward_recipient", address, otherParty)
                            Dim amountString As String = ""

                            Select Case role
                                Case "sender"
                                    itemType = "Transfer (Sent)"
                                    amountString = $"-{amount} {tokenSymbol}"
                                Case "receiver"
                                    itemType = "Transfer (Received)"
                                    amountString = $"+{amount} {tokenSymbol}"
                                Case "owner"
                                    itemType = "Token Creation"
                                    fromDisplay = "N/A" ' Explicitly set for clarity
                                    Dim tokenNameForDisplay = If(tokenSymbol IsNot Nothing AndAlso allTokenNames.ContainsKey(tokenSymbol), allTokenNames(tokenSymbol), tokenSymbol)
                                    amountString = $"+{amount} {tokenSymbol} ({tokenNameForDisplay})"
                                Case "reward_recipient"
                                    itemType = "Mining Reward"
                                    fromDisplay = "miningReward" ' Explicitly set for clarity
                                    amountString = $"+{amount} {tokenSymbol}"
                                Case Else ' e.g. "reward_source" is a role we don't need to display for an address's history
                                    Continue While
                            End Select

                            historyItems.Add(New With {
                            .TxTimestamp = txTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .BlockTimestamp = blockChainTimestampDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            .Type = itemType, .From = fromDisplay, .To = toDisplay,
                            .AmountString = amountString, .Token = tokenSymbol, .Value = amount,
                            .BlockHash = blockHash, .TxId = txId, .Status = "Completed"
                        })
                        End While
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error fetching transaction history for {address} from DB: {ex.Message}{vbCrLf}{ex.StackTrace}")
            End Try
        End Using

        ' The rest of the mempool logic remains unchanged...
        Dim mempoolSnapshot As List(Of JObject) = _mempool.GetTransactions()
        For Each mempoolTxWrapper As JObject In mempoolSnapshot
            Try
                Dim transactionTokenM As JToken = mempoolTxWrapper("transaction")
                If transactionTokenM Is Nothing OrElse transactionTokenM.Type <> JTokenType.Object Then Continue For
                Dim txDataM As JObject = CType(transactionTokenM, JObject)

                Dim txIdM = txDataM("txId")?.ToString()
                Dim txTypeM = txDataM("type")?.ToString()
                Dim txTimestampMStr = If(txDataM.ContainsKey("timestamp") AndAlso txDataM("timestamp").Type = JTokenType.String,
                            txDataM("timestamp").ToString(), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))

                Dim txTimestampMDateTime As DateTime
                If Not DateTime.TryParse(txTimestampMStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampMDateTime) Then
                    txTimestampMDateTime = DateTime.UtcNow
                End If

                Dim tokenSymbolM As String = Nothing
                Dim amountM As Decimal = 0D
                Dim fromM As String = Nothing
                Dim toM As String = Nothing
                Dim nameM As String = Nothing

                If txTypeM = "tokenCreation" Then
                    tokenSymbolM = txDataM("symbol")?.ToString()
                    nameM = txDataM("name")?.ToString()
                    If txDataM("initialSupply") IsNot Nothing Then amountM = txDataM("initialSupply").ToObject(Of Decimal)()
                    fromM = "N/A"
                    toM = txDataM("owner")?.ToString()

                    If String.Equals(toM, address, StringComparison.Ordinal) Then
                        historyItems.Add(New With {
                        .TxTimestamp = txTimestampMDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        .BlockTimestamp = "Pending", .Type = "Token Creation", .From = fromM, .To = toM,
                        .AmountString = $"+{amountM} {tokenSymbolM} ({nameM})", .Token = tokenSymbolM, .Value = amountM,
                        .BlockHash = "Pending", .TxId = txIdM, .Status = "Pending", .Name = nameM
                    })
                    End If
                ElseIf txTypeM = "transfer" Then
                    tokenSymbolM = txDataM("token")?.ToString()
                    If txDataM("amount") IsNot Nothing Then amountM = txDataM("amount").ToObject(Of Decimal)()
                    fromM = txDataM("from")?.ToString()
                    toM = txDataM("to")?.ToString()

                    If String.Equals(fromM, address, StringComparison.Ordinal) OrElse String.Equals(toM, address, StringComparison.Ordinal) Then
                        Dim amountStringM As String = ""
                        Dim itemTypeM As String = "Transfer"
                        If String.Equals(fromM, address, StringComparison.Ordinal) Then
                            amountStringM = $"-{amountM} {tokenSymbolM}"
                            itemTypeM = "Transfer (Sent)"
                        ElseIf String.Equals(toM, address, StringComparison.Ordinal) Then
                            amountStringM = $"+{amountM} {tokenSymbolM}"
                            itemTypeM = "Transfer (Received)"
                        End If
                        historyItems.Add(New With {
                        .TxTimestamp = txTimestampMDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        .BlockTimestamp = "Pending", .Type = itemTypeM, .From = fromM, .To = toM,
                        .AmountString = amountStringM, .Token = tokenSymbolM, .Value = amountM,
                        .BlockHash = "Pending", .TxId = txIdM, .Status = "Pending"
                    })
                    End If
                End If
            Catch ex As Exception
                Console.WriteLine($"Error processing mempool transaction for history for {address}: {ex.Message}{vbCrLf}{ex.StackTrace} - Tx: {mempoolTxWrapper.ToString(Formatting.None)}")
            End Try
        Next

        Return historyItems.OrderByDescending(
        Function(h)
            Dim dt As DateTime
            If h.Status = "Pending" Then Return DateTime.MaxValue
            DateTime.TryParse(h.BlockTimestamp, dt)
            Return dt
        End Function).ThenByDescending(
        Function(h)
            Dim dt As DateTime
            DateTime.TryParse(h.TxTimestamp, dt)
            Return dt
        End Function).ToList()
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

    Public Function GetAllRegisteredTokensInfo() As List(Of TokenChainInfo)
        Dim tokenList As New List(Of TokenChainInfo)()
        Dim uniqueSymbols As New HashSet(Of String)

        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        For Each block As Block In chainSnapshot.OrderBy(Function(b) b.Index)
            For Each transactionWrapper As JObject In block.Data
                Try
                    Dim transactionToken As JToken = transactionWrapper("transaction")
                    If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                    Dim transactionData As JObject = CType(transactionToken, JObject)

                    If transactionData("type")?.ToString() = "tokenCreation" Then
                        Dim symbol As String = transactionData("symbol")?.ToString()

                        If symbol IsNot Nothing AndAlso Not uniqueSymbols.Contains(symbol) Then
                            Dim name As String = transactionData("name")?.ToString()
                            Dim initialSupply As Decimal = 0D
                            If transactionData("initialSupply") IsNot Nothing Then
                                Try : initialSupply = transactionData("initialSupply").ToObject(Of Decimal)() : Catch : End Try
                            End If
                            Dim owner As String = transactionData("owner")?.ToString()
                            Dim txId As String = transactionData("txId")?.ToString()

                            Dim txTimestampString = If(transactionData.ContainsKey("timestamp") AndAlso transactionData("timestamp").Type = JTokenType.String,
                                                   transactionData("timestamp").ToString(),
                                                   block.Timestamp.ToString("o", CultureInfo.InvariantCulture))
                            Dim txTimestampDateTime As DateTime
                            If Not DateTime.TryParseExact(txTimestampString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, txTimestampDateTime) Then
                                DateTime.TryParse(txTimestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, txTimestampDateTime)
                                txTimestampDateTime = txTimestampDateTime.ToUniversalTime()
                            Else
                                txTimestampDateTime = txTimestampDateTime.ToUniversalTime()
                            End If


                            tokenList.Add(New TokenChainInfo With {
                                .Name = name, .Symbol = symbol,
                                .TotalSupply = initialSupply, .CreatorAddress = owner,
                                .CreationTxId = txId, .CreationBlockIndex = block.Index,
                                .CreationTimestamp = txTimestampDateTime
                            })
                            uniqueSymbols.Add(symbol)
                        End If
                    End If
                Catch ex As Exception
                    Console.WriteLine($"Error processing transaction for token list in block {block.Index}: {ex.Message} - Wrapper: {transactionWrapper.ToString(Formatting.None)}")
                End Try
            Next
        Next
        Return tokenList.OrderBy(Function(t) t.Name).ToList()
    End Function

    Public Function GetAggregatedBlockTimes(range As String) As List(Of BlockTimeAggregate)
        Dim aggregatedTimes As New List(Of BlockTimeAggregate)()
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        If chainSnapshot.Count < 2 Then Return aggregatedTimes

        Dim startTime As DateTime = DateTime.UtcNow
        Dim groupByInterval As TimeSpan
        Dim maxDataPoints As Integer = 120

        Select Case range?.ToLower()
            Case "24h"
                startTime = DateTime.UtcNow.AddHours(-24)
                groupByInterval = TimeSpan.FromMinutes(15)
            Case "3d"
                startTime = DateTime.UtcNow.AddDays(-3)
                groupByInterval = TimeSpan.FromHours(1)
            Case "7d"
                startTime = DateTime.UtcNow.AddDays(-7)
                groupByInterval = TimeSpan.FromHours(2)
            Case "1m"
                startTime = DateTime.UtcNow.AddMonths(-1)
                groupByInterval = TimeSpan.FromHours(12)
            Case "6m"
                startTime = DateTime.UtcNow.AddMonths(-6)
                groupByInterval = TimeSpan.FromDays(1)
            Case "12m", "1y"
                startTime = DateTime.UtcNow.AddYears(-1)
                groupByInterval = TimeSpan.FromDays(2)
            Case "all"
                startTime = DateTime.MinValue
                If chainSnapshot.Any() Then
                    Dim totalDuration = chainSnapshot.Last().Timestamp - chainSnapshot.First().Timestamp
                    groupByInterval = TimeSpan.FromTicks(totalDuration.Ticks \ maxDataPoints)
                    If groupByInterval.TotalSeconds < 60 Then groupByInterval = TimeSpan.FromMinutes(1)
                Else
                    groupByInterval = TimeSpan.FromDays(7)
                End If
            Case Else
                startTime = DateTime.UtcNow.AddHours(-24)
                groupByInterval = TimeSpan.FromMinutes(15)
        End Select

        Dim relevantBlocks = chainSnapshot.Where(Function(b) b.Timestamp >= startTime).OrderBy(Function(b) b.Index).ToList()
        If relevantBlocks.Count < 2 Then Return aggregatedTimes

        Dim groupedBlocks As New Dictionary(Of Long, List(Of Block))
        For Each block In relevantBlocks
            Dim groupKeyTicks = (block.Timestamp.ToUniversalTime().Ticks \ groupByInterval.Ticks) * groupByInterval.Ticks
            If Not groupedBlocks.ContainsKey(groupKeyTicks) Then
                groupedBlocks(groupKeyTicks) = New List(Of Block)
            End If
            groupedBlocks(groupKeyTicks).Add(block)
        Next

        For Each kvp In groupedBlocks.OrderBy(Function(x) x.Key)
            Dim group = kvp.Value
            Dim groupTimestamp = New DateTime(kvp.Key, DateTimeKind.Utc)

            If group.Count < 2 Then Continue For

            Dim totalTimeSpanSeconds As Double = 0
            Dim blockPairsCount As Integer = 0
            group.Sort(Function(a, b) a.Index.CompareTo(b.Index))

            For i As Integer = 1 To group.Count - 1
                Dim timeDiff As TimeSpan = group(i).Timestamp.ToUniversalTime() - group(i - 1).Timestamp.ToUniversalTime()
                If timeDiff.TotalSeconds > 0 Then
                    totalTimeSpanSeconds += timeDiff.TotalSeconds
                    blockPairsCount += 1
                End If
            Next

            If blockPairsCount > 0 Then
                aggregatedTimes.Add(New BlockTimeAggregate With {
                    .EndBlockIndex = group.Last().Index,
                    .AvgTimeSeconds = totalTimeSpanSeconds / blockPairsCount,
                    .GroupTimestamp = groupTimestamp
                })
            End If
        Next

        If aggregatedTimes.Count > maxDataPoints Then
            Dim resampledList As New List(Of BlockTimeAggregate)
            Dim bucketSize As Double = CDbl(aggregatedTimes.Count) / CDbl(maxDataPoints)

            For i As Integer = 0 To maxDataPoints - 1
                Dim startIndex = CInt(Math.Floor(i * bucketSize))
                Dim endIndex = CInt(Math.Floor((i + 1) * bucketSize))

                If startIndex >= aggregatedTimes.Count Then Exit For
                endIndex = Math.Min(endIndex, aggregatedTimes.Count)

                Dim chunkCount = endIndex - startIndex
                If chunkCount <= 0 Then Continue For

                Dim chunk = aggregatedTimes.GetRange(startIndex, chunkCount)
                Dim averageTimeInChunk = chunk.Average(Function(agg) agg.AvgTimeSeconds)
                Dim representativePoint = chunk.Last()

                resampledList.Add(New BlockTimeAggregate With {
                    .EndBlockIndex = representativePoint.EndBlockIndex,
                    .GroupTimestamp = representativePoint.GroupTimestamp,
                    .AvgTimeSeconds = averageTimeInChunk
                })
            Next

            Return resampledList
        End If

        Return aggregatedTimes
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

    ' REVISED: This now checks the in-memory set first, then the mempool. Much faster.
    Public Function TokenNameExists(name As String) As Boolean
        If _tokenNamesOnChain.Contains(name) Then Return True

        ' Mempool check remains the same
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

    ' REVISED: This now checks the in-memory set first, then the mempool. Much faster.
    Public Function TokenSymbolExists(symbolText As String) As Boolean
        If _tokenSymbolsOnChain.Contains(symbolText) Then Return True

        ' Mempool check remains the same
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

    ' --- NEW: High-performance replacement for GetBalanceAtBlock_Private ---
    ' This will be used by the validator. It gets confirmed balances from the DB table.
    Friend Function GetConfirmedBalancesForValidation(addresses As HashSet(Of String)) As Dictionary(Of String, Dictionary(Of String, Decimal))
        Dim balances As New Dictionary(Of String, Dictionary(Of String, Decimal))
        If addresses Is Nothing OrElse addresses.Count = 0 Then Return balances

        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                ' Create a parameterized query to avoid SQL injection, even with internal data.
                Dim placeholders = String.Join(",", addresses.Select(Function(a, i) $"@p{i}"))
                Dim query As String = $"SELECT Address, TokenSymbol, Balance FROM AccountBalances WHERE Address IN ({placeholders})"

                Using cmd As New SqliteCommand(query, connection)
                    For i As Integer = 0 To addresses.Count - 1
                        cmd.Parameters.AddWithValue($"@p{i}", addresses.ElementAt(i))
                    Next

                    Using reader As SqliteDataReader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim address = reader.GetString(0)
                            Dim symbol = reader.GetString(1)
                            Dim balanceValue = reader.GetDecimal(2)

                            If Not balances.ContainsKey(address) Then
                                balances(address) = New Dictionary(Of String, Decimal)
                            End If
                            balances(address)(symbol) = balanceValue
                        End While
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error getting confirmed balances for validation: {ex.Message}")
                ' Return an empty dictionary on error to force validation failure rather than proceeding with bad data
                Return New Dictionary(Of String, Dictionary(Of String, Decimal))
            End Try
        End Using
        Return balances
    End Function

    ' DEPRECATED / SLOW: Kept for reference, but should not be used by the validator anymore.
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

    ' REVISED: Now uses the in-memory HashSet for instant lookup.
    Friend Function TokenNameExistsOnChain_Private(name As String, upToBlockIndex As Integer) As Boolean
        ' The upToBlockIndex parameter is no longer needed but kept for signature compatibility
        ' if it were called from other places. The HashSet represents the entire chain's state.
        Return _tokenNamesOnChain.Contains(name)
    End Function

    ' REVISED: Now uses the in-memory HashSet for instant lookup.
    Friend Function TokenSymbolExistsOnChain_Private(symbolValText As String, upToBlockIndex As Integer) As Boolean
        Return _tokenSymbolsOnChain.Contains(symbolValText)
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
    Public Sub UpdateNetworkHashRateEstimate()
        Dim blocksToConsiderForHashRate As Integer = Math.Max(2, Me._difficultyAdjustmentInterval_internal())
        Dim currentRateToSave As Double

        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        If chainSnapshot.Count < 2 Then
            SyncLock _hashRateLock
                CurrentEstimatedNetworkHashRate = 0.0
            End SyncLock
            Return
        End If

        Dim relevantBlocks = chainSnapshot.OrderByDescending(Function(b) b.Index).Take(blocksToConsiderForHashRate).OrderBy(Function(b) b.Index).ToList()

        If relevantBlocks.Count < 2 Then
            SyncLock _hashRateLock
                CurrentEstimatedNetworkHashRate = 0.0
            End SyncLock
            Return
        End If

        Dim cumulativeExpectedHashes As Double = 0
        Dim cumulativeBlockTimeSeconds As Double = 0

        For i As Integer = 1 To relevantBlocks.Count - 1
            Dim currentBlock = relevantBlocks(i)
            Dim previousBlock = relevantBlocks(i - 1)

            Dim expectedHashesForThisBlock As Double = Math.Pow(2, currentBlock.Difficulty)
            cumulativeExpectedHashes += expectedHashesForThisBlock

            Dim timeDiff As TimeSpan = currentBlock.Timestamp.ToUniversalTime() - previousBlock.Timestamp.ToUniversalTime()

            If timeDiff.TotalSeconds > 0.001 Then
                cumulativeBlockTimeSeconds += timeDiff.TotalSeconds
            End If
        Next

        If cumulativeBlockTimeSeconds > 0 AndAlso cumulativeExpectedHashes > 0 Then
            SyncLock _hashRateLock
                CurrentEstimatedNetworkHashRate = cumulativeExpectedHashes / cumulativeBlockTimeSeconds
            End SyncLock
        Else
            SyncLock _hashRateLock
                CurrentEstimatedNetworkHashRate = 0.0
            End SyncLock
        End If
        SyncLock _hashRateLock
            currentRateToSave = CurrentEstimatedNetworkHashRate
        End SyncLock

        If currentRateToSave > 0 Then
            SaveHistoricalHashRate(DateTime.UtcNow, currentRateToSave)
        End If
    End Sub
    Private Function _difficultyAdjustmentInterval_internal() As Integer
        Return 10 ' Matches MiningServer's constant
    End Function
    Private Sub SaveHistoricalHashRate(timestamp As DateTime, rate As Double)
        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Dim insertQuery As String = "INSERT INTO HistoricalHashRates (Timestamp, EstimatedHashRate) VALUES (@Timestamp, @EstimatedHashRate)"
                Using command As New SqliteCommand(insertQuery, connection)
                    command.Parameters.AddWithValue("@Timestamp", timestamp.ToString("o", CultureInfo.InvariantCulture))
                    command.Parameters.AddWithValue("@EstimatedHashRate", rate)
                    command.ExecuteNonQuery()
                End Using
            Catch ex As Exception
                Console.WriteLine($"Error saving historical hash rate: {ex.Message}")
            End Try
        End Using
    End Sub

    Private Sub InitialPopulateAccountBalances()
        Console.WriteLine("Starting initial population of AccountBalances table...")
        Dim allBalances As New Dictionary(Of String, Dictionary(Of String, Decimal))

        For Each block As Block In Chain
            Dim blockIndexForUpdate = block.Index
            For Each txWrapper As JObject In block.Data
                Dim transactionData As JObject = CType(txWrapper("transaction"), JObject)
                Dim txType = transactionData("type")?.ToString()

                If txType = "tokenCreation" Then
                    Dim owner = transactionData("owner")?.ToString()
                    Dim symbol = transactionData("symbol")?.ToString()
                    Dim initialSupply = transactionData("initialSupply")?.ToObject(Of Decimal)()
                    If Not String.IsNullOrEmpty(owner) AndAlso Not String.IsNullOrEmpty(symbol) AndAlso initialSupply >= 0 Then
                        If Not allBalances.ContainsKey(owner) Then allBalances(owner) = New Dictionary(Of String, Decimal)()
                        allBalances(owner)(symbol) = allBalances(owner).GetValueOrDefault(symbol, 0D) + initialSupply
                    End If
                ElseIf txType = "transfer" Then
                    Dim fromAddress = transactionData("from")?.ToString()
                    Dim toAddress = transactionData("to")?.ToString()
                    Dim symbol = transactionData("token")?.ToString()
                    Dim amount = transactionData("amount")?.ToObject(Of Decimal)()

                    If Not String.IsNullOrEmpty(symbol) AndAlso amount > 0 Then
                        If Not String.IsNullOrEmpty(fromAddress) AndAlso fromAddress <> "miningReward" Then
                            If Not allBalances.ContainsKey(fromAddress) Then allBalances(fromAddress) = New Dictionary(Of String, Decimal)()
                            allBalances(fromAddress)(symbol) = allBalances(fromAddress).GetValueOrDefault(symbol, 0D) - amount
                        End If
                        If Not String.IsNullOrEmpty(toAddress) Then
                            If Not allBalances.ContainsKey(toAddress) Then allBalances(toAddress) = New Dictionary(Of String, Decimal)()
                            allBalances(toAddress)(symbol) = allBalances(toAddress).GetValueOrDefault(symbol, 0D) + amount
                        End If
                    End If
                End If
            Next
            If blockIndexForUpdate Mod 1000 = 0 Then Console.WriteLine($"Initial population processed up to block {blockIndexForUpdate}...")
        Next

        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Using dbTx As SqliteTransaction = connection.BeginTransaction()
                    Dim lastBlockIdxProcessed = If(Chain.Any, Chain.Last().Index, -1)
                    For Each addrKey In allBalances.Keys
                        For Each tokenKey In allBalances(addrKey).Keys
                            Dim finalBalance = allBalances(addrKey)(tokenKey)
                            If finalBalance > 0.00000000D Then
                                Dim insertQuery As String = "INSERT INTO AccountBalances (Address, TokenSymbol, Balance, LastUpdatedBlockIndex) VALUES (@Address, @TokenSymbol, @Balance, @BlockIndex)"
                                Using cmdInsert As New SqliteCommand(insertQuery, connection, dbTx)
                                    cmdInsert.Parameters.AddWithValue("@Address", addrKey)
                                    cmdInsert.Parameters.AddWithValue("@TokenSymbol", tokenKey)
                                    cmdInsert.Parameters.AddWithValue("@Balance", finalBalance)
                                    cmdInsert.Parameters.AddWithValue("@BlockIndex", lastBlockIdxProcessed)
                                    cmdInsert.ExecuteNonQuery()
                                End Using
                            End If
                        Next
                    Next
                    dbTx.Commit()
                    Console.WriteLine("Finished writing initial balances to AccountBalances table.")
                End Using
            Catch ex As Exception
                Console.WriteLine($"DATABASE ERROR during initial population write: {ex.Message}")
            End Try
        End Using
    End Sub

    Private Sub InitialPopulateAddressTransactionLinks()
        Console.WriteLine("Starting initial population of AddressTransactionLinks table from the chain...")
        Dim blocksProcessedCounter As Integer = 0
        Dim lastReportedBlock As Integer = 0
        Dim linksWritten As Long = 0

        Using connection As New SqliteConnection($"Data Source={Me._dbFilePath_internal};")
            Try
                connection.Open()
                Using dbTx As SqliteTransaction = connection.BeginTransaction()
                    Using cmdClear As New SqliteCommand("DELETE FROM AddressTransactionLinks", connection, dbTx)
                        cmdClear.ExecuteNonQuery()
                        Console.WriteLine("Cleared existing AddressTransactionLinks table entries.")
                    End Using

                    For Each block As Block In Chain
                        For Each txWrapper As JObject In block.Data
                            Dim transactionToken As JToken = txWrapper("transaction")
                            If transactionToken Is Nothing OrElse transactionToken.Type <> JTokenType.Object Then Continue For
                            Dim txData As JObject = CType(transactionToken, JObject)

                            Dim txId = txData("txId")?.ToString()
                            Dim txTimestamp = If(txData.ContainsKey("timestamp") AndAlso txData("timestamp").Type = JTokenType.String,
                                             txData("timestamp").ToString(),
                                             block.Timestamp.ToString("o", CultureInfo.InvariantCulture))

                            If String.IsNullOrEmpty(txId) Then Continue For

                            Dim txType = txData("type")?.ToString()
                            Dim tokenSymbol As String = Nothing
                            Dim amountVal As Decimal = 0D

                            If txType = "tokenCreation" Then
                                Dim owner = txData("owner")?.ToString()
                                tokenSymbol = txData("symbol")?.ToString()
                                If txData("initialSupply") IsNot Nothing Then amountVal = txData("initialSupply").ToObject(Of Decimal)()
                                If Not String.IsNullOrEmpty(owner) AndAlso Not String.IsNullOrEmpty(tokenSymbol) Then
                                    InsertAddressTxLink(owner, txId, block.Index, block.Hash, txTimestamp, "owner", tokenSymbol, amountVal, Nothing, connection, dbTx)
                                    linksWritten += 1
                                End If
                            ElseIf txType = "transfer" Then
                                Dim fromAddress = txData("from")?.ToString()
                                Dim toAddress = txData("to")?.ToString()
                                tokenSymbol = txData("token")?.ToString()
                                If txData("amount") IsNot Nothing Then amountVal = txData("amount").ToObject(Of Decimal)()

                                If Not String.IsNullOrEmpty(tokenSymbol) AndAlso amountVal > 0 Then
                                    If Not String.IsNullOrEmpty(fromAddress) Then
                                        Dim role = If(String.Equals(fromAddress, "miningReward", StringComparison.OrdinalIgnoreCase), "reward_source", "sender")
                                        InsertAddressTxLink(fromAddress, txId, block.Index, block.Hash, txTimestamp, role, tokenSymbol, amountVal, toAddress, connection, dbTx)
                                        If Not (String.Equals(fromAddress, "miningReward", StringComparison.OrdinalIgnoreCase) AndAlso role = "reward_source") Then linksWritten += 1
                                    End If
                                    If Not String.IsNullOrEmpty(toAddress) Then
                                        Dim role = If(String.Equals(fromAddress, "miningReward", StringComparison.OrdinalIgnoreCase), "reward_recipient", "receiver")
                                        InsertAddressTxLink(toAddress, txId, block.Index, block.Hash, txTimestamp, role, tokenSymbol, amountVal, fromAddress, connection, dbTx)
                                        linksWritten += 1
                                    End If
                                End If
                            End If
                        Next
                        blocksProcessedCounter += 1
                        If blocksProcessedCounter >= lastReportedBlock + 1000 Then
                            Console.WriteLine($"Initial AddrTxLink population: Processed {blocksProcessedCounter}/{Chain.Count} blocks... Links written so far: {linksWritten}")
                            lastReportedBlock = blocksProcessedCounter
                        End If
                    Next
                    dbTx.Commit()
                    Console.WriteLine($"Initial population of AddressTransactionLinks finished. Processed {blocksProcessedCounter} blocks. Total links written: {linksWritten}.")
                End Using
            Catch ex As Exception
                Console.WriteLine($"DATABASE ERROR during initial population of AddressTransactionLinks: {ex.Message}{vbCrLf}{ex.StackTrace}")
            End Try
        End Using
    End Sub
#End Region

#Region "Network Overview Data"
    Public Function GetAverageBlockTime(numberOfBlocksToConsider As Integer) As Double
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        If chainSnapshot.Count < 2 OrElse numberOfBlocksToConsider < 2 Then
            Return 0.0
        End If

        Dim relevantBlocks = chainSnapshot.OrderByDescending(Function(b) b.Index).Take(numberOfBlocksToConsider).OrderBy(Function(b) b.Index).ToList()

        If relevantBlocks.Count < 2 Then
            Return 0.0
        End If

        Dim totalTimeSpanSeconds As Double = 0
        Dim blockPairsCount As Integer = 0

        For i As Integer = 1 To relevantBlocks.Count - 1
            Dim timeDiff As TimeSpan = relevantBlocks(i).Timestamp.ToUniversalTime() - relevantBlocks(i - 1).Timestamp.ToUniversalTime()
            totalTimeSpanSeconds += timeDiff.TotalSeconds
            blockPairsCount += 1
        Next

        If blockPairsCount = 0 Then Return 0.0

        Return totalTimeSpanSeconds / blockPairsCount
    End Function

    Public Function GetBlockStatsForChart(numberOfBlocksToConsider As Integer) As List(Of Object)
        Dim statsList As New List(Of Object)()
        Dim chainSnapshot As List(Of Block)
        SyncLock Chain
            chainSnapshot = Chain.ToList()
        End SyncLock

        Dim relevantBlocks = chainSnapshot.OrderByDescending(Function(b) b.Index).Take(numberOfBlocksToConsider).OrderBy(Function(b) b.Index).ToList()

        For Each block As Block In relevantBlocks
            statsList.Add(New With {
                .Index = block.Index,
                .Difficulty = block.Difficulty,
                .TransactionCount = If(block.Data IsNot Nothing, block.Data.Count, 0)
            })
        Next
        Return statsList
    End Function

#End Region

End Class