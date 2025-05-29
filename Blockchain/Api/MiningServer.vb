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
    Private _stopMining As Boolean = False ' Added Volatile

    Private Const MaxSupply As Decimal = 21000000
    Private Const BaseReward As Decimal = 50
    Private Const RewardHalvingInterval As Integer = 210000

    Private _lastBlockTime As DateTime
    Private Const TargetBlockTimeSeconds As Integer = 2
    Private Const DifficultyAdjustmentInterval As Integer = 10

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
                Throw New Exception($"Invalid or missing miner address received: '{receivedAddress}'")
            End If
            minerAddress = receivedAddress
            Console.WriteLine($"Miner {remoteEndPointString} identified as {minerAddress}")


            While client.Connected And Not _stopMining
                Dim workPackage As New JObject()
                SyncLock _blockchain
                    workPackage("lastIndex") = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Index, -1) ' Index of last block
                    workPackage("lastHash") = If(_blockchain.Chain.Any(), _blockchain.Chain.Last().Hash, "0")
                    workPackage("difficulty") = _blockchain._difficulty ' Current difficulty for the NEW block
                End SyncLock

                workPackage("rewardAmount") = CalculateBlockReward()
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
    .MetadataPropertyHandling = MetadataPropertyHandling.Ignore, ' Might help if $type or other metadata is an issue
    .FloatParseHandling = FloatParseHandling.Decimal ' Ensure numbers become Decimals if possible
}
                ' Try adding a specific String converter if JObject is still misbehaving
                ' For "Known" string properties we want to keep literal, like timestamps
                ' This is complex to apply globally to all JObject strings.

                Dim block As Block = JsonConvert.DeserializeObject(Of Block)(receivedJson("block").ToString(), deserializationSettings)

                SyncLock _blockchain
                    Dim latestBlockOnChain = _blockchain.GetLatestBlock()
                    If latestBlockOnChain IsNot Nothing AndAlso block.PreviousHash <> latestBlockOnChain.Hash Then
                        Console.WriteLine($"Stale block received from {minerAddress}. Expected prevHash: {latestBlockOnChain.Hash}, Got: {block.PreviousHash}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Stale block"}).ToString(Formatting.None))
                        Continue While
                    ElseIf latestBlockOnChain Is Nothing AndAlso block.PreviousHash <> "0" AndAlso block.Index = 0 Then ' Handling genesis case from miner (if possible)
                        Console.WriteLine($"Stale block (genesis context) received from {minerAddress}. Expected prevHash '0' for index 0, Got: {block.PreviousHash}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Stale block (genesis)"}).ToString(Formatting.None))
                        Continue While
                    End If

                    ' Validate block difficulty against the one in the work package (current _blockchain._difficulty)
                    If block.Difficulty <> _blockchain._difficulty Then
                        Console.WriteLine($"Invalid block difficulty from {minerAddress}. Expected: {_blockchain._difficulty}, Got: {block.Difficulty}. Block rejected.")
                        writer.WriteLine(JObject.FromObject(New With {.status = "error", .message = "Invalid block difficulty"}).ToString(Formatting.None))
                        Continue While
                    End If

                    If ValidateBlock(block, minerAddress) Then
                        _blockchain.Chain.Add(block)
                        _blockchain.SaveBlockToDatabase(block)
                        _blockchain._mempool.RemoveTransactions(block.Data)

                        Console.WriteLine($"Block {block.Index} (Hash: {block.Hash.Substring(0, 8)}...) added by {minerAddress}")
                        writer.WriteLine(JObject.FromObject(New With {.status = "success", .message = "Block accepted", .blockHash = block.Hash}).ToString(Formatting.None))

                        BroadcastBlockNotification(block, client, minerAddress)
                        AdjustDifficulty() ' Adjusts _blockchain._difficulty for NEXT block
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


    Private Function ValidateBlock(block As Block, expectedMinerAddress As String) As Boolean
        ' Assumes _blockchain is locked by caller
        If block Is Nothing Then
            Console.WriteLine("ValidateBlock Fail: Block object is null.")
            Return False
        End If

        Dim calculatedHash = block.CalculateHash() ' This will update Block.LastCalculatedDataToHash
        Dim serverDataToHash = Block.LastCalculatedDataToHash ' Capture it FROM THE STATIC SHARED MEMBER

        If block.Hash <> calculatedHash Then
            Console.WriteLine("------------------- HASH MISMATCH DEBUG (SERVER) -------------------")
            Console.WriteLine($"ValidateBlock Fail: Hash mismatch for Block Index {block.Index}.")
            Console.WriteLine($"  Client Submitted Hash: {block.Hash}")
            Console.WriteLine($"  Server Calculated Hash: {calculatedHash}")
            Console.WriteLine($"  Server Data Hashed: '{serverDataToHash}'") ' LOG THE STRING
            Console.WriteLine("--------------------------------------------------------------------")
            Return False
        End If

        Dim latestChainBlock As Block = Nothing
        If block.Index > 0 Then
            If block.Index > _blockchain.Chain.Count OrElse _blockchain.Chain.Count = 0 Then ' requested index is beyond current chain or chain is empty
                Console.WriteLine($"ValidateBlock Fail: Block index {block.Index} implies a previous block that doesn't exist in current chain of length {_blockchain.Chain.Count}.")
                Return False
            End If
            latestChainBlock = _blockchain.Chain(block.Index - 1) ' Get by actual index
        End If

        If block.Index > 0 Then ' Non-genesis block
            If latestChainBlock Is Nothing Then
                Console.WriteLine($"ValidateBlock Fail: Non-genesis block index {block.Index} but no actual previous block found in chain by index.")
                Return False
            End If
            If block.PreviousHash <> latestChainBlock.Hash Then
                Console.WriteLine($"ValidateBlock Fail: PreviousHash mismatch for block {block.Index}. Expected: {latestChainBlock.Hash}, Got: {block.PreviousHash}")
                Return False
            End If
        Else ' Genesis block (Index 0)
            If block.PreviousHash <> "0" Then
                Console.WriteLine($"ValidateBlock Fail: Genesis PreviousHash mismatch. Expected '0', Got: {block.PreviousHash}")
                Return False
            End If
        End If

        ' Check PoW using difficulty FROM THE BLOCK ITSELF (which should match _blockchain._difficulty at time of work assignment)
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

        Dim coinbaseFound As Boolean = False
        Dim coinbaseTx As JObject = Nothing
        Dim tempBalances As New Dictionary(Of String, Dictionary(Of String, Decimal))
        Dim processedTxIdsInBlock As New HashSet(Of String)
        Dim symbolsCreatedInBlock As New HashSet(Of String)
        Dim namesCreatedInBlock As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

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
                coinbaseFound = True
                coinbaseTx = transaction

                If transaction("to")?.ToString() <> expectedMinerAddress Then
                    Console.WriteLine($"ValidateBlock Fail: Coinbase recipient mismatch. Expected {expectedMinerAddress}, got {transaction("to")?.ToString()}")
                    Return False
                End If
                Dim expectedReward = CalculateBlockReward()
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

                ' Get balance from confirmed chain state *before this block*.
                Dim senderChainBalance = _blockchain.GetBalanceAtBlock_Private(fromAddress, tokenSymbol, block.Index - 1)

                ' Get current effective balance from tempBalances (reflecting prior tx in *this* block).
                Dim senderEffectiveBalanceInBlock As Decimal
                If tempBalances.ContainsKey(fromAddress) AndAlso tempBalances(fromAddress).ContainsKey(tokenSymbol) Then
                    senderEffectiveBalanceInBlock = tempBalances(fromAddress)(tokenSymbol)
                Else
                    senderEffectiveBalanceInBlock = senderChainBalance ' First action in this block for this token
                End If

                If senderEffectiveBalanceInBlock < amount Then
                    Console.WriteLine($"ValidateBlock Fail: Insufficient balance for txId {txId}. Sender {fromAddress}, needs {amount} {tokenSymbol}, has {senderEffectiveBalanceInBlock} effective in block (Chain balance before block: {senderChainBalance}).")
                    Return False
                End If

                If Not tempBalances.ContainsKey(fromAddress) Then tempBalances(fromAddress) = New Dictionary(Of String, Decimal)
                tempBalances(fromAddress)(tokenSymbol) = senderEffectiveBalanceInBlock - amount

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

                If _blockchain.TokenNameExistsOnChain_Private(name, block.Index - 1) Then
                    Console.WriteLine($"ValidateBlock Fail: Token name '{name}' already exists on confirmed chain (txId {txId}).")
                    Return False
                End If
                If _blockchain.TokenSymbolExistsOnChain_Private(symbol, block.Index - 1) Then
                    Console.WriteLine($"ValidateBlock Fail: Token symbol '{symbol}' already exists on confirmed chain (txId {txId}).")
                    Return False
                End If

                If namesCreatedInBlock.Contains(name) Then
                    Console.WriteLine($"ValidateBlock Fail: Token name '{name}' created earlier in the same block (txId {txId}).")
                    Return False
                End If
                If symbolsCreatedInBlock.Contains(symbol) Then
                    Console.WriteLine($"ValidateBlock Fail: Token symbol '{symbol}' created earlier in the same block (txId {txId}).")
                    Return False
                End If
                namesCreatedInBlock.Add(name)
                symbolsCreatedInBlock.Add(symbol)

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
        If _blockchain.Chain.Count < DifficultyAdjustmentInterval Then
            Return
        End If

        If _blockchain.Chain.Count Mod DifficultyAdjustmentInterval <> 0 Then
            Return
        End If

        Dim lastBlockInInterval = _blockchain.Chain.Last()
        Dim startTimeStampForInterval As DateTime

        If _blockchain.Chain.Count = DifficultyAdjustmentInterval Then
            startTimeStampForInterval = _blockchain.Chain(0).Timestamp
        Else
            Dim blockIndexBeforeInterval = _blockchain.Chain.Count - DifficultyAdjustmentInterval - 1
            If blockIndexBeforeInterval < 0 Then
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

        Dim adjustmentFactor As Double = 0.75

        If actualTimeTakenSeconds < (expectedTimeSeconds * adjustmentFactor) AndAlso actualTimeTakenSeconds > 0 Then
            _blockchain._difficulty += 1 ' Removed arbitrary cap
            Console.WriteLine($"  Difficulty INCREASED from {oldDifficulty} to {_blockchain._difficulty} (mined too fast).")
        ElseIf actualTimeTakenSeconds > (expectedTimeSeconds / adjustmentFactor) Then
            _blockchain._difficulty = Math.Max(1, _blockchain._difficulty - 1) ' Min difficulty is 1
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

        Dim halvingEpochs As Integer = currentBlockHeight \ RewardHalvingInterval
        Dim reward As Decimal = BaseReward / CDec(Math.Pow(2, halvingEpochs))

        If reward < 0 Then reward = 0

        Return reward
    End Function
End Class