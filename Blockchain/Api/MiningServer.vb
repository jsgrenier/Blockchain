Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Class MiningServer

    Private _listener As TcpListener
    Private _blockchain As Blockchain
    Private _clients As New List(Of TcpClient)
    Private _stopMining As Boolean = False ' Flag to signal thread termination


    Private Const MaxSupply As Decimal = 21000000 ' Maximum BEAN supply
    Private Const BaseReward As Decimal = 50 ' Initial block reward
    Private Const RewardHalvingInterval As Integer = 210000 ' Halving interval (like Bitcoin)


    Private _lastBlockTime As DateTime ' Time of the last mined block
    Private Const TargetBlockTime As Integer = 60 ' Target block time in seconds
    Private Const DifficultyAdjustmentInterval As Integer = 10 ' Number of blocks to consider for difficulty adjustment

    Public Sub New(port As Integer, blockchain As Blockchain)
        _listener = New TcpListener(IPAddress.Any, port)
        _blockchain = blockchain
        _lastBlockTime = DateTime.Now ' Initialize last block time
    End Sub

    Public Sub Start()
        _listener.Start()
        Console.WriteLine("Mining server started on port " & _listener.LocalEndpoint.ToString())

        While True
            Try
                Dim client As TcpClient = _listener.AcceptTcpClient()
                _clients.Add(client)
                Console.WriteLine("Miner connected: " & client.Client.RemoteEndPoint.ToString())

                Dim clientThread As New Thread(AddressOf HandleClient)
                clientThread.Start(client)

            Catch ex As Exception
                Console.WriteLine("Error accepting client: " & ex.Message)
            End Try
        End While
    End Sub

    Public Sub Kill()
        _stopMining = True
        _listener.Stop()
        For Each client As TcpClient In _clients
            client.Close()
        Next
        Console.WriteLine("Mining server stopped.")
    End Sub

    Private Sub HandleClient(clientObject As Object)
        Dim client As TcpClient = DirectCast(clientObject, TcpClient)
        Dim stream As NetworkStream = client.GetStream()
        Dim writer As New StreamWriter(stream)
        writer.AutoFlush = True

        Try
            While client.Connected
                ' --- Construct the blockchain information JSON object ---
                Dim blockchainInfo As New JObject()
                blockchainInfo("lastIndex") = _blockchain.Chain.Count - 1
                blockchainInfo("lastHash") = If(_blockchain.Chain.Count > 0, _blockchain.Chain.Last().Hash, "0")

                ' --- Add the mining difficulty ---
                blockchainInfo("difficulty") = _blockchain._difficulty

                ' Convert the JSON object to a string with formatting
                Dim blockchainInfoString = blockchainInfo.ToString(Formatting.None)

                ' Send the blockchain information as bytes with a newline delimiter
                Dim blockchainInfoBytes = System.Text.Encoding.UTF8.GetBytes(blockchainInfoString & vbCrLf)

                ' Lock the stream to ensure thread safety
                SyncLock stream
                    stream.Write(blockchainInfoBytes, 0, blockchainInfoBytes.Length)
                End SyncLock

                ' --- Send mempool transactions ---
                Dim mempoolTransactions = _blockchain._mempool.GetTransactions()

                ' Serialize the mempool transactions, even if it's empty
                Dim mempoolData = JsonConvert.SerializeObject(mempoolTransactions)
                writer.WriteLine(mempoolData) ' Write with newline

                ' --- Receive mined block from the miner (only if mempool is not empty) ---
                If mempoolTransactions.Count > 0 Then
                    Dim reader As New StreamReader(stream)
                    Dim blockDataJson As String = reader.ReadLine()

                    ' Check if blockDataJson is not null or empty before deserialization
                    If Not String.IsNullOrEmpty(blockDataJson) Then
                        Try
                            ' Deserialize the JSON object
                            Dim jsonObject = JObject.Parse(blockDataJson)

                            ' Extract the block data and miner address
                            Dim blockData = jsonObject("block").ToString()
                            Dim minerAddress = jsonObject("minerAddress").ToString()

                            ' Deserialize the block data
                            Dim block = JsonConvert.DeserializeObject(Of Block)(blockData)

                            If ValidateBlock(block) Then


                                ' Remove transactions from mempool
                                For Each transaction In block.Data
                                    Dim txId = JObject.Parse(transaction("transaction").ToString())("txId").ToString()
                                    Dim mempoolTx = _blockchain._mempool.GetTransactionByTxId(txId)
                                    If mempoolTx IsNot Nothing Then
                                        _blockchain._mempool.RemoveTransaction(mempoolTx)
                                    End If
                                Next

                                ' Reward the miner
                                Dim rewardAmount As Decimal = CalculateBlockReward()
                                ' Create a coinbase transaction
                                Dim coinbaseTx = CreateCoinbaseTransaction(minerAddress, rewardAmount, jsonObject("block")("Timestamp"))

                                block.Data.Add(New JObject From {
                                    New JProperty("transaction", JObject.Parse(JsonConvert.SerializeObject(coinbaseTx)))
                                })

                                ' Broadcast the new block to all connected miners
                                BroadcastBlock(block)
                                'AdjustDifficulty()

                                ' Add the block to the blockchain
                                _blockchain.Chain.Add(block)
                                _blockchain.SaveBlockToDatabase(block)
                                Console.WriteLine("Block added to the chain: " & block.Hash)

                            Else
                                Console.WriteLine("Invalid block received from miner: " & client.Client.RemoteEndPoint.ToString())
                            End If

                        Catch ex As Exception
                            Console.WriteLine("Error deserializing block data: " & ex.Message)
                        End Try
                    End If
                End If

                Thread.Sleep(1000) ' Wait for a short period
            End While

        Catch ex As Exception
            Console.WriteLine("Error handling client: " & ex.Message)
            _clients.Remove(client)
            client.Close()
        End Try
    End Sub

    Private Function CreateCoinbaseTransaction(minerAddress As String, rewardAmount As Decimal, timestamp As DateTime) As JObject
        ' Create a new transaction object
        Dim transaction = New JObject()
        transaction("timestamp") = timestamp.ToString("yyyy-MM-dd HH:mm:ss")
        transaction("type") = "transfer"
        transaction("from") = "miningReward"
        transaction("to") = minerAddress
        transaction("amount") = rewardAmount
        transaction("token") = "BEAN" ' Or your desired token symbol
        transaction("txId") = Guid.NewGuid.ToString("N") ' Implement a function to generate unique transaction IDs

        Return transaction
    End Function


    Private Sub BroadcastBlock(block As Block)
        Dim blockData = JsonConvert.SerializeObject(block)
        Dim blockBytes = System.Text.Encoding.UTF8.GetBytes(blockData & vbCrLf) ' Add newline delimiter

        For Each client As TcpClient In _clients
            Try
                Dim stream As NetworkStream = client.GetStream()
                stream.Write(blockBytes, 0, blockBytes.Length)
            Catch ex As Exception
                Console.WriteLine("Error broadcasting block: " & ex.Message)
            End Try
        Next
    End Sub

    Private Function ValidateBlock(block As Block) As Boolean
        ' Check if the block's hash is correctly calculated.
        If block.Hash <> block.CalculateHash() Then
            Return False
        End If

        ' Check if the previous hash matches the hash of the previous block.
        If _blockchain.Chain.Count > 0 AndAlso block.PreviousHash <> _blockchain.Chain.Last().Hash Then
            Return False
        End If

        ' Check if the Proof of Work is valid (hash difficulty)
        If Not block.Hash.StartsWith(New String("0", _blockchain._difficulty)) Then
            Return False
        End If

        For Each transaction As JObject In block.Data
            Try
                Dim transactionData = JObject.Parse(transaction("transaction").ToString())

                ' 1. Double-Spending Check
                If IsDoubleSpend(transactionData) Then
                    Console.WriteLine($"Invalid block: Double-spending detected in transaction - {transactionData}")
                    Return False
                End If
                Console.WriteLine("Double-spending check passed")

                ' 2. Sufficient Balance Check
                If Not HasSufficientBalance(transactionData) Then
                    Console.WriteLine($"Invalid block: Insufficient balance in transaction - {transactionData}")
                    Return False
                End If
                Console.WriteLine("Wallet has sufficient balance")


            Catch ex As Exception
                Console.WriteLine($"Error validating transaction in block: {ex.Message}")
                Return False
            End Try
        Next

        Return True
    End Function

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

    Private Sub AdjustDifficulty()
        ' Check if it's time to adjust the difficulty
        If _blockchain.Chain.Count Mod DifficultyAdjustmentInterval = 0 Then
            Dim currentTime = DateTime.Now
            Dim blockCount = DifficultyAdjustmentInterval
            Dim totalTime = currentTime - _lastBlockTime ' Time taken to mine the last 'DifficultyAdjustmentInterval' blocks
            Dim averageBlockTime = totalTime.TotalSeconds / blockCount

            ' Adjust difficulty based on the average block time
            If averageBlockTime > TargetBlockTime Then
                ' Increase difficulty if blocks are taking too long to mine
                _blockchain._difficulty += 1
                Console.WriteLine($"Difficulty increased to {_blockchain._difficulty}")
            ElseIf averageBlockTime < TargetBlockTime Then
                ' Decrease difficulty if blocks are being mined too quickly
                _blockchain._difficulty = Math.Max(1, _blockchain._difficulty - 1) ' Ensure difficulty doesn't go below 1
                Console.WriteLine($"Difficulty decreased to {_blockchain._difficulty}")
            End If

            _lastBlockTime = currentTime ' Update the last block time
        End If
    End Sub


    Private Function CalculateBlockReward() As Decimal
        Dim currentSupply As Decimal = _blockchain.GetTotalSupply("BEAN")
        Dim currentBlock As Integer = _blockchain.Chain.Count

        If currentSupply >= MaxSupply Then
            Return 0 ' No more rewards if max supply is reached
        End If

        ' Calculate the current halving epoch
        Dim halvingEpoch As Integer = currentBlock \ RewardHalvingInterval

        ' Calculate the reward based on the halving epoch
        Dim reward As Decimal = BaseReward / (2 ^ halvingEpoch)

        Return reward
    End Function
End Class