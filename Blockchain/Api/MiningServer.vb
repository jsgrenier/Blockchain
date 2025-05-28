Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Collections.Concurrent ' For ConcurrentBag

Public Class MiningServer

    Private _listener As TcpListener
    Private _blockchain As Blockchain
    Private _clients As New ConcurrentBag(Of TcpClient) ' Thread-safe collection
    Private _stopMining As Boolean = False

    Private Const MaxSupply As Decimal = 21000000 ' Maximum BEAN supply
    Private Const BaseReward As Decimal = 50 ' Initial block reward
    Private Const RewardHalvingInterval As Integer = 210000

    Private _lastBlockTime As DateTime
    Private Const TargetBlockTimeSeconds As Integer = 10 ' Target block time in seconds
    Private Const DifficultyAdjustmentInterval As Integer = 10 ' Blocks

    Public Sub New(port As Integer, blockchain As Blockchain)
        _listener = New TcpListener(IPAddress.Any, port)
        _blockchain = blockchain
        _lastBlockTime = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Timestamp, DateTime.UtcNow)
    End Sub

    Public Sub Start()
        _listener.Start()
        Console.WriteLine("Mining server started on port " & CType(_listener.LocalEndpoint, IPEndPoint).Port)

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
                If _stopMining Then Exit While ' Expected exception on stop
                Console.WriteLine("SocketException accepting client: " & ex.Message)
            Catch ex As Exception
                If _stopMining Then Exit While ' Could be ObjectDisposedException if listener is closed
                Console.WriteLine("Error accepting client: " & ex.Message)
            End Try
        End While
        Console.WriteLine("AcceptClientsLoop ended.")
    End Sub


    Public Sub Kill()
        _stopMining = True
        If _listener IsNot Nothing AndAlso _listener.Server IsNot Nothing AndAlso _listener.Server.IsBound Then
            _listener.Stop() ' This will cause AcceptTcpClient to throw an exception, handled in AcceptClientsLoop
        End If
        For Each client As TcpClient In _clients
            Try
                client.Close()
            Catch
                ' Ignore errors during shutdown
            End Try
        Next
        Console.WriteLine("Mining server stopped.")
    End Sub

    Private Sub HandleClient(clientObject As Object)
        Dim client As TcpClient = DirectCast(clientObject, TcpClient)
        Dim stream As NetworkStream = Nothing
        Dim writer As StreamWriter = Nothing
        Dim reader As StreamReader = Nothing
        Dim minerAddress As String = "Unknown" ' Declare outside Try, initialize to a default
        Dim remoteEndPointString As String = "Unknown"

        Try
            If client IsNot Nothing AndAlso client.Client IsNot Nothing AndAlso client.Client.RemoteEndPoint IsNot Nothing Then
                remoteEndPointString = client.Client.RemoteEndPoint.ToString()
            End If

            stream = client.GetStream()
            writer = New StreamWriter(stream) With {.AutoFlush = True}
            reader = New StreamReader(stream)
            ' minerAddress will be set by client's first message

            ' Initial message from miner: {"minerAddress": "their_public_key"}
            Dim initMessageJson = reader.ReadLine()
            If String.IsNullOrEmpty(initMessageJson) Then Throw New Exception("Miner did not send initial address.")

            Dim initMessage = JObject.Parse(initMessageJson)
            Dim receivedAddress = initMessage("minerAddress")?.ToString() ' Use a temp variable for parsing

            If String.IsNullOrEmpty(receivedAddress) OrElse Not Wallet.IsValidPublicKey(receivedAddress) Then
                Throw New Exception($"Invalid or missing miner address received: '{receivedAddress}'")
            End If
            minerAddress = receivedAddress ' Assign to the higher-scoped variable
            Console.WriteLine($"Miner {remoteEndPointString} identified as {minerAddress}")


            While client.Connected And Not _stopMining
                ' --- Construct and send work package ---
                Dim workPackage As New JObject()
                SyncLock _blockchain ' Ensure consistent read of chain state
                    workPackage("lastIndex") = _blockchain.Chain.Count - 1
                    workPackage("lastHash") = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Hash, "0")
                    workPackage("difficulty") = _blockchain._difficulty
                End SyncLock

                workPackage("rewardAmount") = CalculateBlockReward()
                workPackage("minerAddressForReward") = minerAddress ' Server tells miner where to send reward

                Dim mempoolTransactions = _blockchain._mempool.GetTransactions() ' Get a copy
                workPackage("mempool") = JArray.FromObject(mempoolTransactions)

                writer.WriteLine(workPackage.ToString(Formatting.None))

                ' --- Receive mined block from the miner ---
                Dim blockDataJson As String = reader.ReadLine()
                If String.IsNullOrEmpty(blockDataJson) Then
                    If Not client.Connected OrElse _stopMining Then Exit While ' Client disconnected or server stopping
                    Continue While ' Empty line, wait for actual data
                End If

                ' Miner sends: {"block": {block_json_here}}
                Dim receivedJson = JObject.Parse(blockDataJson)
                Dim block As Block = JsonConvert.DeserializeObject(Of Block)(receivedJson("block").ToString())

                ' --- Validate and Process Block ---
                SyncLock _blockchain ' Lock for validation against chain and adding to chain
                    If block.PreviousHash <> _blockchain.GetLatestBlock().Hash Then
                        Console.WriteLine($"Stale block received from {minerAddress}. Expected prevHash: {_blockchain.GetLatestBlock().Hash}, Got: {block.PreviousHash}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Stale block"}).ToString(Formatting.None))
                        Continue While ' Miner is working on an old chain tip
                    End If

                    If ValidateBlock(block, minerAddress) Then
                        _blockchain.Chain.Add(block)
                        _blockchain.SaveBlockToDatabase(block)
                        _blockchain._mempool.RemoveTransactions(block.Data) ' Remove confirmed transactions

                        Console.WriteLine($"Block {block.Index} (Hash: {block.Hash.Substring(0, 8)}...) added by {minerAddress}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "success", .message = "Block accepted", .blockHash = block.Hash}).ToString(Formatting.None))

                        BroadcastBlockNotification(block, client, minerAddress) ' Pass minerAddress for logging
                        AdjustDifficulty()
                        _lastBlockTime = block.Timestamp ' Update last block time for difficulty adjustment
                    Else
                        Console.WriteLine($"Invalid block received from {minerAddress} ({remoteEndPointString}).")
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
            ' Attempt to remove client if it's still in the list (ConcurrentBag doesn't have a direct Remove)
            ' For precise tracking, _clients could be a ConcurrentDictionary<TcpClient, string_minerAddress_or_bool_active>.
            ' For now, we just let it be. The bag will shrink as disconnected clients' threads end.
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
        notification("minerAddress") = submittedByMinerAddress ' Use the identified miner address

        Dim notificationString = notification.ToString(Formatting.None) & vbCrLf
        Dim notificationBytes = System.Text.Encoding.UTF8.GetBytes(notificationString)


        For Each otherClient As TcpClient In _clients
            If otherClient Is submittedByClient OrElse Not otherClient.Connected Then Continue For ' Don't send to self or disconnected
            Dim otherClientRemoteEndpoint As String = "Unknown"
            Try
                If otherClient.Client IsNot Nothing AndAlso otherClient.Client.RemoteEndPoint IsNot Nothing Then
                    otherClientRemoteEndpoint = otherClient.Client.RemoteEndPoint.ToString()
                End If

                Dim otherStream As NetworkStream = otherClient.GetStream()
                ' Send bytes directly to avoid StreamWriter issues if client thread also uses one
                otherStream.Write(notificationBytes, 0, notificationBytes.Length)
            Catch ex As Exception
                Console.WriteLine($"Error broadcasting new block notification to {otherClientRemoteEndpoint}: {ex.Message}")
                ' Consider strategies for handling persistently failing clients (e.g., remove from _clients)
            End Try
        Next
    End Sub


    Private Function ValidateBlock(block As Block, expectedMinerAddress As String) As Boolean
        ' (Assumes _blockchain is locked by caller)
        If block Is Nothing Then
            Console.WriteLine("ValidateBlock Fail: Block object is null.")
            Return False
        End If

        ' 1. Check block structure and recalculate hash
        Dim calculatedHash = block.CalculateHash()
        If block.Hash <> calculatedHash Then
            Console.WriteLine($"ValidateBlock Fail: Hash mismatch. Block Hash: {block.Hash}, Calculated: {calculatedHash}")
            Return False
        End If

        ' 2. Check previous hash
        Dim latestChainBlock As Block = Nothing
        If _blockchain.Chain.Any() Then latestChainBlock = _blockchain.Chain.Last()

        If latestChainBlock IsNot Nothing Then
            If block.PreviousHash <> latestChainBlock.Hash Then
                Console.WriteLine($"ValidateBlock Fail: PreviousHash mismatch. Expected: {latestChainBlock.Hash}, Got: {block.PreviousHash}")
                Return False
            End If
        Else ' This is the first block after genesis (or genesis itself if chain was empty)
            If block.Index = 0 AndAlso block.PreviousHash <> "0" Then ' Genesis block
                Console.WriteLine($"ValidateBlock Fail: Genesis PreviousHash mismatch. Expected '0', Got: {block.PreviousHash}")
                Return False
            ElseIf block.Index > 0 Then ' Should not happen if chain is empty and index > 0
                Console.WriteLine($"ValidateBlock Fail: Non-genesis block index {block.Index} but no previous block in chain.")
                Return False
            End If
        End If


        ' 3. Check Proof of Work
        If Not block.Hash.StartsWith(New String("0", block.Difficulty)) Then
            Console.WriteLine($"ValidateBlock Fail: PoW not met. Difficulty {_blockchain._difficulty}, Hash {block.Hash}")
            Return False
        End If

        ' 4. Timestamp validation (basic)
        Dim previousBlockTimestamp = If(latestChainBlock IsNot Nothing, latestChainBlock.Timestamp, DateTime.MinValue.ToUniversalTime())
        If block.Timestamp <= previousBlockTimestamp AndAlso block.Index > 0 Then ' Allow genesis to have any reasonable past time
            Console.WriteLine($"ValidateBlock Fail: Timestamp too old ({block.Timestamp}) compared to previous ({previousBlockTimestamp}).")
            Return False
        End If
        If block.Timestamp > DateTime.UtcNow.AddMinutes(10) Then ' Allow generous 10 min clock skew for future
            Console.WriteLine($"ValidateBlock Fail: Timestamp too far in future ({block.Timestamp}). Current UTC: {DateTime.UtcNow}")
            Return False
        End If

        ' 5. Transaction Validation
        Dim coinbaseFound As Boolean = False
        Dim coinbaseTx As JObject = Nothing
        Dim tempBalances As New Dictionary(Of String, Dictionary(Of String, Decimal)) ' Address -> TokenSymbol -> Balance
        Dim processedTxIdsInBlock As New HashSet(Of String)

        For i As Integer = 0 To block.Data.Count - 1
            Dim txWrapper As JObject = block.Data(i)
            Dim transaction As JObject
            Try
                transaction = JObject.Parse(txWrapper("transaction").ToString())
            Catch ex As Exception
                Console.WriteLine($"ValidateBlock Fail: Could not parse transaction JObject at index {i} in block. {ex.Message}")
                Return False
            End Try

            Dim txType = transaction("type")?.ToString()
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


            If txType = "transfer" AndAlso transaction("from")?.ToString() = "miningReward" Then
                If coinbaseFound Then
                    Console.WriteLine("ValidateBlock Fail: Multiple coinbase transactions.")
                    Return False
                End If
                If i <> block.Data.Count - 1 Then
                    ' Conventionally, coinbase is last. Not strictly required by all protocols, but good practice for some.
                    ' For this implementation, we'll be flexible.
                    ' Console.WriteLine("ValidateBlock Warning: Coinbase transaction not the last transaction in the block.")
                End If
                coinbaseFound = True
                coinbaseTx = transaction ' Store it for later checks

                If transaction("to")?.ToString() <> expectedMinerAddress Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase recipient mismatch. Expected {expectedMinerAddress}, got {transaction("to")?.ToString()}")
                    Return False
                End If
                Dim expectedReward = CalculateBlockReward() ' Recalculate, as difficulty might have changed if another block came in
                Dim actualReward = transaction("amount")?.ToObject(Of Decimal)()
                If actualReward <> expectedReward Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase amount incorrect. Expected {expectedReward}, got {actualReward}")
                    Return False
                End If
                If transaction("token")?.ToString() <> "BEAN" Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase token incorrect. Expected BEAN, got {transaction("token")?.ToString()}")
                    Return False
                End If

                ' Update temp balance for miner
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

                ' Balance Check (using current chain state + effects of *previous* transactions in *this* block)
                Dim senderCurrentChainBalance = _blockchain.GetTokensOwned(fromAddress).GetValueOrDefault(tokenSymbol, 0D)
                Dim senderEffectiveBalance = senderCurrentChainBalance ' Start with chain balance
                If tempBalances.ContainsKey(fromAddress) AndAlso tempBalances(fromAddress).ContainsKey(tokenSymbol) Then
                    senderEffectiveBalance = tempBalances(fromAddress)(tokenSymbol) ' Use balance after previous txs in this block
                Else
                    ' If fromAddress not in tempBalances yet for this token, it means this is their first action in the block.
                    ' Their effective balance *is* their current chain balance.
                    If Not tempBalances.ContainsKey(fromAddress) Then tempBalances(fromAddress) = New Dictionary(Of String, Decimal)()
                    tempBalances(fromAddress)(tokenSymbol) = senderCurrentChainBalance ' Initialize for this block
                End If


                If senderEffectiveBalance < amount Then
                    Console.WriteLine($"ValidateBlock Fail: Insufficient balance for txId {txId}. Sender {fromAddress}, needs {amount} {tokenSymbol}, has {senderEffectiveBalance} (Chain: {senderCurrentChainBalance}).")
                    Return False
                End If

                ' Update temporary balances
                tempBalances(fromAddress)(tokenSymbol) -= amount
                If Not tempBalances.ContainsKey(toAddress) Then tempBalances(toAddress) = New Dictionary(Of String, Decimal)()
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

                ' Check for duplicates on chain (Blockchain.TokenNameExists/SymbolExists already check chain + mempool,
                ' but for block validation, we only care about chain state *before* this block)
                If _blockchain.Chain.Any(Function(b) b.Data.Any(Function(txW)
                                                                    Dim txD = JObject.Parse(txW("transaction").ToString())
                                                                    Return txD("type")?.ToString() = "tokenCreation" AndAlso
                                                                           (String.Equals(txD("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase) OrElse
                                                                            txD("symbol")?.ToString() = symbol)
                                                                End Function)) Then
                    Console.WriteLine($"ValidateBlock Fail: Token name '{name}' or symbol '{symbol}' already exists on confirmed chain (txId {txId}).")
                    Return False
                End If
                ' Also check for duplicates *earlier in this same block*
                For k As Integer = 0 To i - 1
                    Dim prevTxWrapperInBlock = block.Data(k)
                    Dim prevTxDataInBlock = JObject.Parse(prevTxWrapperInBlock("transaction").ToString())
                    If prevTxDataInBlock("type")?.ToString() = "tokenCreation" Then
                        If String.Equals(prevTxDataInBlock("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase) OrElse
                           prevTxDataInBlock("symbol")?.ToString() = symbol Then
                            Console.WriteLine($"ValidateBlock Fail: Token name '{name}' or symbol '{symbol}' created earlier in the same block (txId {txId}).")
                            Return False
                        End If
                    End If
                Next


                ' Update temp balance for owner
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
        If coinbaseTx Is Nothing Then ' Should not happen if coinbaseFound is true
            Console.WriteLine("ValidateBlock Fail: Coinbase transaction marked found but not stored.")
            Return False
        End If

        Return True ' All checks passed
    End Function


    Private Sub AdjustDifficulty()
        ' Assumes _blockchain is locked by caller
        If _blockchain.Chain.Count < DifficultyAdjustmentInterval Then ' Not enough blocks yet
            Return
        End If

        ' Only adjust exactly on the interval, or if we somehow skipped one (e.g. loading a chain)
        ' and the current count is a multiple of the interval.
        If _blockchain.Chain.Count Mod DifficultyAdjustmentInterval <> 0 Then
            Return
        End If

        Dim lastBlockInInterval = _blockchain.Chain.Last() ' This is Chain(_blockchain.Chain.Count - 1)
        Dim startTimeStampForInterval As DateTime

        If _blockchain.Chain.Count = DifficultyAdjustmentInterval Then
            ' This is the first adjustment. The interval starts from the genesis block.
            startTimeStampForInterval = _blockchain.Chain(0).Timestamp
        Else
            ' For subsequent adjustments, get the block *before* the current interval started.
            ' The current interval consists of blocks from index (Count - DifficultyAdjustmentInterval) to (Count - 1).
            ' So the block *before* this interval is at index (Count - DifficultyAdjustmentInterval - 1).
            Dim blockIndexBeforeInterval = _blockchain.Chain.Count - DifficultyAdjustmentInterval - 1
            If blockIndexBeforeInterval < 0 Then
                ' This should ideally not happen if the first check (Chain.Count < DifficultyAdjustmentInterval) is correct
                ' And if Chain.Count = DifficultyAdjustmentInterval is handled above.
                ' But as a safeguard:
                Console.WriteLine($"[WARNING] AdjustDifficulty: blockIndexBeforeInterval was {blockIndexBeforeInterval}. Using genesis block time.")
                startTimeStampForInterval = _blockchain.Chain(0).Timestamp
            Else
                startTimeStampForInterval = _blockchain.Chain(blockIndexBeforeInterval).Timestamp
            End If
        End If

        Dim actualTimeTakenSeconds = (lastBlockInInterval.Timestamp - startTimeStampForInterval).TotalSeconds
        Dim expectedTimeSeconds = DifficultyAdjustmentInterval * TargetBlockTimeSeconds
        Dim oldDifficulty = _blockchain._difficulty

        Console.WriteLine("-----------------------------------------------------")
        Console.WriteLine($"Difficulty Adjustment Check (Block {_blockchain.Chain.Count}):")
        Console.WriteLine($"  Interval Blocks: {DifficultyAdjustmentInterval}, Target Block Time: {TargetBlockTimeSeconds}s")
        Console.WriteLine($"  Last Block in Interval Timestamp: {lastBlockInInterval.Timestamp:o}")
        Console.WriteLine($"  Start Timestamp for Interval:     {startTimeStampForInterval:o}")
        Console.WriteLine($"  Actual time for interval: {actualTimeTakenSeconds:F2}s. Target for interval: {expectedTimeSeconds:F0}s.")

        ' Adjust difficulty:
        ' If blocks are mined too fast (actualTime < expectedTime * ratio), increase difficulty.
        ' If blocks are mined too slow (actualTime > expectedTime * ratio), decrease difficulty.
        Dim adjustmentFactor As Double = 0.75 ' e.g., if time is < 75% of target, increase. If > 125% of target, decrease.

        If actualTimeTakenSeconds < (expectedTimeSeconds * adjustmentFactor) AndAlso actualTimeTakenSeconds > 0 Then ' actualTimeTakenSeconds > 0 to avoid issues with very fast initial blocks
            If _blockchain._difficulty < 15 Then ' Max difficulty cap for safety/testing
                _blockchain._difficulty += 1
                Console.WriteLine($"  Difficulty INCREASED from {oldDifficulty} to {_blockchain._difficulty} (mined too fast).")
            Else
                Console.WriteLine($"  Difficulty at MAX CAP ({_blockchain._difficulty}). Not increasing (mined too fast).")
            End If
        ElseIf actualTimeTakenSeconds > (expectedTimeSeconds / adjustmentFactor) Then
            _blockchain._difficulty = Math.Max(1, _blockchain._difficulty - 1) ' Min difficulty is 1
            Console.WriteLine($"  Difficulty DECREASED from {oldDifficulty} to {_blockchain._difficulty} (mined too slow).")
        Else
            Console.WriteLine($"  Difficulty REMAINS at {_blockchain._difficulty} (mining speed is within target range).")
        End If
        Console.WriteLine("-----------------------------------------------------")
        ' _lastBlockTime is updated when a block is added, so no need to set it here.
    End Sub

    Private Function CalculateBlockReward() As Decimal
        ' Assumes _blockchain lock is NOT held here, or if it is, it's for a short read.
        ' For simplicity, read Chain.Count without explicit lock if this is called frequently
        ' outside the main block processing lock. If called only within, lock is already held.
        Dim currentBlockHeight As Integer
        SyncLock _blockchain ' Brief lock just to get count if called from various places
            currentBlockHeight = _blockchain.Chain.Count ' Next block's height
        End SyncLock

        Dim halvingEpochs As Integer = currentBlockHeight \ RewardHalvingInterval
        Dim reward As Decimal = BaseReward / CDec(Math.Pow(2, halvingEpochs))

        ' This max supply check is simplified. A real blockchain might have specific rules.
        ' SyncLock _blockchain ' Lock again for GetTotalSupply if needed
        '    Dim currentBEANSupply As Decimal = _blockchain.GetTotalSupply("BEAN")
        '    If currentBEANSupply + reward > MaxSupply Then
        '        reward = Math.Max(0, MaxSupply - currentBEANSupply)
        '    End If
        ' End SyncLock
        ' For now, assume MaxSupply is a soft cap handled by miners not getting rewards if it's hit.
        ' The Blockchain's GetTotalSupply would reflect the current state.
        If reward < 0 Then reward = 0

        Return reward
    End Function
End Class