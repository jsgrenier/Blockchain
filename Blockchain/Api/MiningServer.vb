' --- START OF FILE MiningServer.vb ---

Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Collections.Concurrent

Public Class MiningServer

    Private _listener As TcpListener
    Private _blockchain As Blockchain
    Private _clients As New ConcurrentBag(Of TcpClient)
    Private _stopMining As Boolean = False

    ' Configuration properties - will be loaded from config file
    Private _config As MiningServerConfig

    Private _lastBlockTime As DateTime

    Private Const ConfigFileName As String = "miningserver_config.json" ' Specific name for this config

    Public Sub New(blockchain As Blockchain) ' Removed port from constructor, will get from config
        Me._blockchain = blockchain
        LoadOrCreateConfig() ' Load configuration

        ' Initialize listener with port from config
        _listener = New TcpListener(IPAddress.Any, _config.Port)

        _lastBlockTime = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Timestamp, DateTime.UtcNow)
    End Sub

    Private Sub LoadOrCreateConfig()
        Dim configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName)
        Try
            If File.Exists(configFilePath) Then
                Console.WriteLine($"Loading mining server configuration from {configFilePath}...")
                Dim jsonString = File.ReadAllText(configFilePath)
                _config = JsonConvert.DeserializeObject(Of MiningServerConfig)(jsonString)
                Console.WriteLine("Mining server configuration loaded.")
            Else
                Console.WriteLine($"Mining server configuration file not found at {configFilePath}. Creating default config...")
                _config = New MiningServerConfig() ' Create with default values
                SaveConfig() ' Save the default config
                Console.WriteLine($"Default mining server configuration created and saved to {configFilePath}.")
            End If
        Catch ex As Exception
            Console.WriteLine($"Error loading or creating mining server configuration: {ex.Message}")
            Console.WriteLine("Using internal default mining server configuration values.")
            _config = New MiningServerConfig() ' Fallback to internal defaults
        End Try

        ' Validate critical config values or apply fallbacks
        If _config.Port <= 0 OrElse _config.Port > 65535 Then
            Console.WriteLine($"Warning: Invalid port {_config.Port} in config. Falling back to default port 8081.")
            _config.Port = 8081
        End If
        If _config.TargetBlockTimeSeconds <= 0 Then _config.TargetBlockTimeSeconds = 4
        If _config.DifficultyAdjustmentInterval <= 1 Then _config.DifficultyAdjustmentInterval = 10
        ' Add more validations as needed
    End Sub

    Private Sub SaveConfig()
        Dim configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName)
        Try
            Dim jsonString = JsonConvert.SerializeObject(_config, Formatting.Indented)
            File.WriteAllText(configFilePath, jsonString)
        Catch ex As Exception
            Console.WriteLine($"Error saving mining server configuration to {configFilePath}: {ex.Message}")
        End Try
    End Sub

    Public Sub Start()
        _listener.Start()
        Console.WriteLine($"Mining server started on port {_config.Port}") ' Use config port

        Dim acceptThread As New Thread(AddressOf AcceptClientsLoop)
        acceptThread.IsBackground = True
        acceptThread.Start()
        Console.WriteLine("Mining server listening for connections...")
    End Sub

    Private Sub AcceptClientsLoop()
        While Not _stopMining
            Try
                Dim client As TcpClient = _listener.AcceptTcpClient()
                _clients.Add(client)
                Console.WriteLine("Miner connected: " & client.Client.RemoteEndPoint.ToString())

                Dim clientThread As New Thread(AddressOf HandleClient)
                clientThread.IsBackground = True
                clientThread.Start(client)
            Catch ex As SocketException
                If _stopMining Then Exit While
                Console.WriteLine("SocketException accepting client: " & ex.Message)
            Catch ex As Exception
                If _stopMining Then Exit While
                Console.WriteLine("Error accepting client: " & ex.Message)
            End Try
        End While
        Console.WriteLine("AcceptClientsLoop ended.")
    End Sub


    Public Sub Kill()
        _stopMining = True
        If _listener IsNot Nothing AndAlso _listener.Server IsNot Nothing AndAlso _listener.Server.IsBound Then
            _listener.Stop()
        End If
        For Each client As TcpClient In _clients
            Try
                client.Close()
            Catch
            End Try
        Next
        Console.WriteLine("Mining server stopped.")
    End Sub

    Private Sub HandleClient(clientObject As Object)
        Dim client As TcpClient = DirectCast(clientObject, TcpClient)
        Dim stream As NetworkStream = Nothing
        Dim writer As StreamWriter = Nothing
        Dim reader As StreamReader = Nothing
        Dim minerAddress As String = "Unknown"
        Dim remoteEndPointString As String = "Unknown"

        Try
            If client IsNot Nothing AndAlso client.Client IsNot Nothing AndAlso client.Client.RemoteEndPoint IsNot Nothing Then
                remoteEndPointString = client.Client.RemoteEndPoint.ToString()
            End If

            stream = client.GetStream()
            writer = New StreamWriter(stream) With {.AutoFlush = True}
            reader = New StreamReader(stream)

            Dim initMessageJson = reader.ReadLine()
            If String.IsNullOrEmpty(initMessageJson) Then Throw New Exception("Miner did not send initial address.")

            Dim initMessage = JObject.Parse(initMessageJson)
            Dim receivedAddress = initMessage("minerAddress")?.ToString()

            If String.IsNullOrEmpty(receivedAddress) OrElse Not Wallet.IsValidPublicKey(receivedAddress) Then
                ' If DefaultMinerAddressForEmptyJobs is configured and valid, maybe use it as a fallback?
                ' For now, strict validation.
                Throw New Exception($"Invalid or missing miner address received: '{receivedAddress}'")
            End If
            minerAddress = receivedAddress
            Console.WriteLine($"Miner {remoteEndPointString} identified as {minerAddress}")


            While client.Connected And Not _stopMining
                Dim workPackage As New JObject()
                SyncLock _blockchain
                    workPackage("lastIndex") = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Index, -1)
                    workPackage("lastHash") = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Hash, "0")
                    workPackage("difficulty") = _blockchain._difficulty
                End SyncLock

                workPackage("rewardAmount") = CalculateBlockReward() ' Uses _config.BaseReward and _config.RewardHalvingInterval
                workPackage("minerAddressForReward") = minerAddress

                Dim mempoolTransactions = _blockchain._mempool.GetTransactions()
                workPackage("mempool") = JArray.FromObject(mempoolTransactions)

                writer.WriteLine(workPackage.ToString(Formatting.None))

                Dim blockDataJson As String = reader.ReadLine()
                If String.IsNullOrEmpty(blockDataJson) Then
                    If Not client.Connected OrElse _stopMining Then Exit While
                    Continue While
                End If

                Dim receivedJson = JObject.Parse(blockDataJson)
                Dim deserializationSettings = New JsonSerializerSettings With {
                    .DateParseHandling = DateParseHandling.None,
                    .MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                    .FloatParseHandling = FloatParseHandling.Decimal
                }
                Dim block As Block = JsonConvert.DeserializeObject(Of Block)(receivedJson("block").ToString(), deserializationSettings)

                SyncLock _blockchain
                    Dim latestBlockOnChain = _blockchain.GetLatestBlock()
                    If latestBlockOnChain IsNot Nothing AndAlso block.PreviousHash <> latestBlockOnChain.Hash Then
                        Console.WriteLine($"Stale block received from {minerAddress}. Expected prevHash: {latestBlockOnChain.Hash}, Got: {block.PreviousHash}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Stale block"}).ToString(Formatting.None))
                        Continue While
                    ElseIf latestBlockOnChain Is Nothing AndAlso block.PreviousHash <> "0" AndAlso block.Index = 0 Then
                        Console.WriteLine($"Stale block (genesis context) received from {minerAddress}. Expected prevHash '0' for index 0, Got: {block.PreviousHash}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Stale block (genesis)"}).ToString(Formatting.None))
                        Continue While
                    End If

                    If block.Difficulty <> _blockchain._difficulty Then
                        Console.WriteLine($"Invalid block difficulty from {minerAddress}. Expected: {_blockchain._difficulty}, Got: {block.Difficulty}. Block rejected.")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Invalid block difficulty"}).ToString(Formatting.None))
                        Continue While
                    End If

                    If ValidateBlock(block, minerAddress) Then
                        _blockchain.Chain.Add(block)
                        _blockchain.SaveBlockToDatabase(block)
                        _blockchain._mempool.RemoveTransactions(block.Data)

                        _blockchain.UpdateAccountBalancesFromBlock(block)

                        _blockchain.UpdateAddressTransactionLinksFromBlock(block)

                        Console.WriteLine($"Block {block.Index} (Hash: {block.Hash.Substring(0, 8)}...) added by {minerAddress}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "success", .message = "Block accepted", .blockHash = block.Hash}).ToString(Formatting.None))

                        BroadcastBlockNotification(block, client, minerAddress)

                        Dim oldDifficultyForLog = _blockchain._difficulty
                        AdjustDifficulty() ' Uses _config.DifficultyAdjustmentInterval and _config.TargetBlockTimeSeconds
                        _blockchain.UpdateNetworkHashRateEstimate()

                        _lastBlockTime = block.Timestamp
                    Else
                        Console.WriteLine($"Invalid block received from {minerAddress} ({remoteEndPointString}). Validation failed.")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Invalid block"}).ToString(Formatting.None))
                    End If
                End SyncLock
            End While

        Catch ex As IOException When TypeOf ex.InnerException Is SocketException
            Console.WriteLine($"IO/SocketException (client likely disconnected): Miner '{minerAddress}' ({remoteEndPointString}): {ex.Message}")
        Catch ex As JsonReaderException
            Console.WriteLine($"JSON parsing error from client Miner '{minerAddress}' ({remoteEndPointString}): {ex.Message}")
        Catch ex As ObjectDisposedException
            Console.WriteLine($"ObjectDisposedException (stream likely closed) for Miner '{minerAddress}' ({remoteEndPointString}): {ex.Message}")
        Catch ex As Exception
            Console.WriteLine($"Error handling client Miner '{minerAddress}' ({remoteEndPointString}): {ex.Message}{vbCrLf}{ex.StackTrace}")
        Finally
            Console.WriteLine($"Miner disconnected: '{minerAddress}' ({remoteEndPointString})")
            If client IsNot Nothing Then client.Close()
            If writer IsNot Nothing Then writer.Dispose()
            If reader IsNot Nothing Then reader.Dispose()
            If stream IsNot Nothing Then stream.Dispose()
        End Try
    End Sub

    Private Sub BroadcastBlockNotification(newBlock As Block, submittedByClient As TcpClient, submittedByMinerAddress As String)
        Dim notification As New JObject()
        notification("type") = "newBlock"
        notification("index") = newBlock.Index
        notification("hash") = newBlock.Hash
        notification("previousHash") = newBlock.PreviousHash
        notification("minerAddress") = submittedByMinerAddress

        Dim notificationString = notification.ToString(Formatting.None) & vbCrLf
        Dim notificationBytes = System.Text.Encoding.UTF8.GetBytes(notificationString)

        For Each otherClient As TcpClient In _clients
            If otherClient Is submittedByClient OrElse Not otherClient.Connected Then Continue For
            Dim otherClientRemoteEndpoint As String = "Unknown"
            Try
                If otherClient.Client IsNot Nothing AndAlso otherClient.Client.RemoteEndPoint IsNot Nothing Then
                    otherClientRemoteEndpoint = otherClient.Client.RemoteEndPoint.ToString()
                End If

                Dim otherStream As NetworkStream = otherClient.GetStream()
                otherStream.Write(notificationBytes, 0, notificationBytes.Length)
            Catch ex As Exception
                Console.WriteLine($"Error broadcasting new block notification to {otherClientRemoteEndpoint}: {ex.Message}")
            End Try
        Next
    End Sub

    ' --- REFACTORED FOR HIGH PERFORMANCE ---
    Private Function ValidateBlock(block As Block, expectedMinerAddress As String) As Boolean
        ' Assumes _blockchain is locked by caller
        If block Is Nothing Then
            Console.WriteLine("ValidateBlock Fail: Block object is null.")
            Return False
        End If

        ' --- Basic integrity checks (unchanged) ---
        Dim calculatedHash = block.CalculateHash()
        Dim serverDataToHash = Block.LastCalculatedDataToHash

        If block.Hash <> calculatedHash Then
            Console.WriteLine("------------------- HASH MISMATCH DEBUG (SERVER) -------------------")
            Console.WriteLine($"ValidateBlock Fail: Hash mismatch for Block Index {block.Index}.")
            Console.WriteLine($"  Client Submitted Hash: {block.Hash}")
            Console.WriteLine($"  Server Calculated Hash: {calculatedHash}")
            Console.WriteLine($"  Server Data Hashed: '{serverDataToHash}'")
            Console.WriteLine("--------------------------------------------------------------------")
            Return False
        End If

        Dim latestChainBlock As Block = Nothing
        If block.Index > 0 Then
            If block.Index > _blockchain.Chain.Count OrElse _blockchain.Chain.Count = 0 Then
                Console.WriteLine($"ValidateBlock Fail: Block index {block.Index} implies a previous block that doesn't exist in current chain of length {_blockchain.Chain.Count}.")
                Return False
            End If
            latestChainBlock = _blockchain.Chain(block.Index - 1)
        End If

        If block.Index > 0 Then
            If latestChainBlock Is Nothing Then
                Console.WriteLine($"ValidateBlock Fail: Non-genesis block index {block.Index} but no actual previous block found in chain by index.")
                Return False
            End If
            If block.PreviousHash <> latestChainBlock.Hash Then
                Console.WriteLine($"ValidateBlock Fail: PreviousHash mismatch for block {block.Index}. Expected: {latestChainBlock.Hash}, Got: {block.PreviousHash}")
                Return False
            End If
        Else
            If block.PreviousHash <> "0" Then
                Console.WriteLine($"ValidateBlock Fail: Genesis PreviousHash mismatch. Expected '0', Got: {block.PreviousHash}")
                Return False
            End If
        End If

        If Not block.Hash.StartsWith(New String("0", block.Difficulty)) Then
            Console.WriteLine($"ValidateBlock Fail: PoW not met. Block Difficulty {block.Difficulty}, Hash {block.Hash}")
            Return False
        End If

        Dim previousBlockTimestamp = If(latestChainBlock IsNot Nothing, latestChainBlock.Timestamp, DateTime.MinValue.ToUniversalTime())
        If block.Timestamp <= previousBlockTimestamp AndAlso block.Index > 0 Then
            Console.WriteLine($"ValidateBlock Fail: Timestamp too old ({block.Timestamp}) compared to previous ({previousBlockTimestamp}).")
            Return False
        End If
        If block.Timestamp > DateTime.UtcNow.AddMinutes(10) Then
            Console.WriteLine($"ValidateBlock Fail: Timestamp too far in future ({block.Timestamp}). Current UTC: {DateTime.UtcNow}")
            Return False
        End If

        ' --- REVISED TRANSACTION AND STATE VALIDATION ---
        Dim coinbaseFound As Boolean = False
        Dim processedTxIdsInBlock As New HashSet(Of String)
        Dim symbolsCreatedInBlock As New HashSet(Of String)
        Dim namesCreatedInBlock As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        ' Step 1: Gather all addresses involved in this block to fetch their starting balances efficiently.
        Dim addressesInBlock As New HashSet(Of String)
        For Each txWrapper As JObject In block.Data
            Dim transaction = CType(txWrapper("transaction"), JObject)
            Dim txType = transaction("type")?.ToString()
            If txType = "transfer" Then
                addressesInBlock.Add(transaction("from")?.ToString())
                addressesInBlock.Add(transaction("to")?.ToString())
            ElseIf txType = "tokenCreation" Then
                addressesInBlock.Add(transaction("owner")?.ToString())
            End If
        Next
        addressesInBlock.RemoveWhere(Function(addr) String.IsNullOrEmpty(addr) OrElse addr = "miningReward")
        addressesInBlock.Add(expectedMinerAddress) ' Ensure miner's address is included for the reward

        ' Step 2: Fetch all confirmed balances for these addresses in a single DB call.
        ' This `tempBalances` dictionary will hold the *effective* balance as we process the block.
        Dim tempBalances As Dictionary(Of String, Dictionary(Of String, Decimal)) = _blockchain.GetConfirmedBalancesForValidation(addressesInBlock)

        ' Step 3: Iterate through the block and validate transactions against the effective balances.
        For i As Integer = 0 To block.Data.Count - 1
            Dim txWrapper As JObject = block.Data(i)
            Dim transaction As JObject
            Try
                transaction = CType(txWrapper("transaction"), JObject)
            Catch ex As Exception
                Console.WriteLine($"ValidateBlock Fail: Could not parse transaction JObject at index {i} in block. {ex.Message}")
                Return False
            End Try

            Dim txId = transaction("txId")?.ToString()

            If String.IsNullOrEmpty(txId) Then
                Console.WriteLine($"ValidateBlock Fail: Transaction at index {i} missing txId.")
                Return False
            End If
            If processedTxIdsInBlock.Contains(txId) Then
                Console.WriteLine($"ValidateBlock Fail: Duplicate txId '{txId}' within block.")
                Return False
            End If
            processedTxIdsInBlock.Add(txId)

            Dim txType = transaction("type")?.ToString()

            If txType = "transfer" AndAlso transaction("from")?.ToString() = "miningReward" Then
                If coinbaseFound Then
                    Console.WriteLine("ValidateBlock Fail: Multiple coinbase transactions.")
                    Return False
                End If
                coinbaseFound = True

                If transaction("to")?.ToString() <> expectedMinerAddress Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase recipient mismatch. Expected {expectedMinerAddress}, got {transaction("to")?.ToString()}")
                    Return False
                End If
                Dim expectedReward = CalculateBlockReward() ' Uses _config
                Dim actualReward = transaction("amount")?.ToObject(Of Decimal)()
                If actualReward <> expectedReward Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase amount incorrect. Expected {expectedReward}, got {actualReward}")
                    Return False
                End If
                If transaction("token")?.ToString() <> "BEAN" Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase token incorrect. Expected BEAN, got {transaction("token")?.ToString()}")
                    Return False
                End If

                Dim miner = transaction("to").ToString()
                Dim token = transaction("token").ToString()
                If Not tempBalances.ContainsKey(miner) Then tempBalances(miner) = New Dictionary(Of String, Decimal)
                tempBalances(miner)(token) = tempBalances(miner).GetValueOrDefault(token, 0D) + actualReward

            ElseIf txType = "transfer" Then
                Dim fromAddress = transaction("from")?.ToString()
                Dim toAddress = transaction("to")?.ToString()
                Dim amount = transaction("amount")?.ToObject(Of Decimal)()
                Dim tokenSymbol = transaction("token")?.ToString()

                If String.IsNullOrEmpty(fromAddress) OrElse fromAddress = "miningReward" OrElse String.IsNullOrEmpty(toAddress) OrElse String.IsNullOrEmpty(tokenSymbol) OrElse amount <= 0 Then
                    Console.WriteLine($"ValidateBlock Fail: Invalid transfer transaction structure or amount for txId {txId}.")
                    Return False
                End If

                ' Get the effective balance from our in-memory dictionary for this block
                Dim senderEffectiveBalance = tempBalances.GetValueOrDefault(fromAddress, New Dictionary(Of String, Decimal)).GetValueOrDefault(tokenSymbol, 0D)

                If senderEffectiveBalance < amount Then
                    Console.WriteLine($"ValidateBlock Fail: Insufficient balance for txId {txId}. Sender {fromAddress}, needs {amount} {tokenSymbol}, has effective balance of {senderEffectiveBalance} in block.")
                    Return False
                End If

                ' Update balances in our temporary dictionary
                tempBalances(fromAddress)(tokenSymbol) = senderEffectiveBalance - amount
                If Not tempBalances.ContainsKey(toAddress) Then tempBalances(toAddress) = New Dictionary(Of String, Decimal)
                tempBalances(toAddress)(tokenSymbol) = tempBalances(toAddress).GetValueOrDefault(tokenSymbol, 0D) + amount

            ElseIf txType = "tokenCreation" Then
                Dim name = transaction("name")?.ToString()
                Dim symbol = transaction("symbol")?.ToString()
                Dim owner = transaction("owner")?.ToString()
                Dim initialSupply = transaction("initialSupply")?.ToObject(Of Decimal)()

                If String.IsNullOrEmpty(name) OrElse String.IsNullOrEmpty(symbol) OrElse String.IsNullOrEmpty(owner) OrElse initialSupply < 0 Then
                    Console.WriteLine($"ValidateBlock Fail: Invalid token creation transaction structure for txId {txId}.")
                    Return False
                End If

                ' Use the FAST in-memory set lookups from Blockchain.vb
                If _blockchain.TokenNameExistsOnChain_Private(name, -1) OrElse namesCreatedInBlock.Contains(name) Then
                    Console.WriteLine($"ValidateBlock Fail: Token name '{name}' already exists (on-chain or in-block) for txId {txId}.")
                    Return False
                End If
                If _blockchain.TokenSymbolExistsOnChain_Private(symbol, -1) OrElse symbolsCreatedInBlock.Contains(symbol) Then
                    Console.WriteLine($"ValidateBlock Fail: Token symbol '{symbol}' already exists (on-chain or in-block) for txId {txId}.")
                    Return False
                End If
                namesCreatedInBlock.Add(name)
                symbolsCreatedInBlock.Add(symbol)

                ' Update balances in our temporary dictionary
                If Not tempBalances.ContainsKey(owner) Then tempBalances(owner) = New Dictionary(Of String, Decimal)
                tempBalances(owner)(symbol) = tempBalances(owner).GetValueOrDefault(symbol, 0D) + initialSupply

            Else
                Console.WriteLine($"ValidateBlock Fail: Unknown transaction type '{txType}' for txId {txId}.")
                Return False
            End If
        Next

        If Not coinbaseFound Then
            Console.WriteLine("ValidateBlock Fail: No coinbase transaction found in block.")
            Return False
        End If

        Return True
    End Function

    Private Sub AdjustDifficulty()
        ' Use _config.DifficultyAdjustmentInterval
        If _blockchain.Chain.Count < _config.DifficultyAdjustmentInterval Then
            Return
        End If

        If _blockchain.Chain.Count Mod _config.DifficultyAdjustmentInterval <> 0 Then
            Return
        End If

        Dim lastBlockInInterval = _blockchain.Chain.Last()
        Dim startTimeStampForInterval As DateTime

        ' Note: The start block is the one *before* the interval begins.
        ' The interval spans from block (N - 10) to (N - 1).
        ' The time is measured from the timestamp of block (N-11) to (N-1).
        Dim startBlockIndex = _blockchain.Chain.Count - _config.DifficultyAdjustmentInterval
        If startBlockIndex <= 0 Then
            ' First adjustment interval. Compare against genesis block's time.
            startTimeStampForInterval = _blockchain.Chain(0).Timestamp
        Else
            startTimeStampForInterval = _blockchain.Chain(startBlockIndex - 1).Timestamp
        End If

        Dim actualTimeTakenSeconds = (lastBlockInInterval.Timestamp - startTimeStampForInterval).TotalSeconds
        ' Use _config.TargetBlockTimeSeconds
        Dim expectedTimeSeconds = _config.DifficultyAdjustmentInterval * _config.TargetBlockTimeSeconds
        Dim oldDifficulty = _blockchain._difficulty

        Console.WriteLine("-----------------------------------------------------")
        Console.WriteLine($"Difficulty Adjustment Check (Block {_blockchain.Chain.Count}):")
        Console.WriteLine($"  Interval Blocks: {_config.DifficultyAdjustmentInterval}, Target Block Time: {_config.TargetBlockTimeSeconds}s")
        Console.WriteLine($"  Last Block in Interval Timestamp: {lastBlockInInterval.Timestamp:o}")
        Console.WriteLine($"  Start Timestamp for Interval:     {startTimeStampForInterval:o}")
        Console.WriteLine($"  Actual time for interval: {actualTimeTakenSeconds:F2}s. Target for interval: {expectedTimeSeconds:F0}s.")

        Dim adjustmentFactor As Double = 0.75 ' This could also be moved to config if desired

        If actualTimeTakenSeconds < (expectedTimeSeconds * adjustmentFactor) AndAlso actualTimeTakenSeconds > 0 Then
            _blockchain._difficulty += 1
            Console.WriteLine($"  Difficulty INCREASED from {oldDifficulty} to {_blockchain._difficulty} (mined too fast).")
        ElseIf actualTimeTakenSeconds > (expectedTimeSeconds / adjustmentFactor) Then
            _blockchain._difficulty = Math.Max(1, _blockchain._difficulty - 1)
            Console.WriteLine($"  Difficulty DECREASED from {oldDifficulty} to {_blockchain._difficulty} (mined too slow).")
        Else
            Console.WriteLine($"  Difficulty REMAINS at {_blockchain._difficulty} (mining speed is within target range).")
        End If
        Console.WriteLine("-----------------------------------------------------")
    End Sub

    Private Function CalculateBlockReward() As Decimal
        Dim currentBlockHeight As Integer
        SyncLock _blockchain
            currentBlockHeight = _blockchain.Chain.Count
        End SyncLock

        ' Use _config.RewardHalvingInterval and _config.BaseReward
        Dim halvingEpochs As Integer = currentBlockHeight \ _config.RewardHalvingInterval
        Dim reward As Decimal = _config.BaseReward / CDec(Math.Pow(2, halvingEpochs))

        ' Check against MaxSupply (from config for consistency, though Blockchain class also has it)
        ' This is a simplified check; a real system would have more nuanced supply management.
        Dim currentTotalSupply = _blockchain.GetTotalSupply("BEAN") ' Assuming BEAN is the native coin
        If currentTotalSupply + reward > _config.MaxSupply Then
            reward = Math.Max(0D, _config.MaxSupply - currentTotalSupply)
        End If

        If reward < 0 Then reward = 0D ' Should not happen with above logic but good failsafe

        Return reward
    End Function
End Class