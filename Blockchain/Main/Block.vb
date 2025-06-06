Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization
' Imports Newtonsoft.Json.Converters ' Not strictly needed if not using IsoDateTimeConverter directly in CalculateHash

Public Class Block
    Public Property Index As Integer
    Public Property Timestamp As DateTime ' Stored as DateTime
    Public Property Data As List(Of JObject)
    Public Property PreviousHash As String
    Public Property Hash As String
    Public Property Nonce As Long ' Changed from Integer to Long
    Public Property Difficulty As Integer
    Public Property BlockSize As Integer

    ' Keep this for debugging outside this class if needed, but CalculateHash won't write to it now.
    Public Shared LastCalculatedDataToHash As String = ""

    Public Const PreciseTimestampFormat As String = "yyyy-MM-ddTHH:mm:ss.fffffffZ"
    Public Const MAX_NONCE_VALUE_UINT32_EQUIVALENT As Long = 4294967295L ' Represents UInteger.MaxValue

    ' Constructor for creating new blocks before mining
    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, difficulty As Integer)
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Difficulty = difficulty
        Me.Nonce = 0L ' Initialize Nonce as Long
        Me.Hash = CalculateHash() ' Calculate initial hash (with nonce 0)
        Me.BlockSize = CalculateBlockSize() ' Calculate initial block size
    End Sub

    ' JsonConstructor for deserializing blocks (e.g., from database or network)
    <JsonConstructor>
    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, hash As String, nonce As Long, difficulty As Integer, blockSize As Integer)
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Hash = hash
        Me.Nonce = nonce ' Assign deserialized Long nonce
        Me.Difficulty = difficulty
        Me.BlockSize = blockSize
    End Sub

    Public Function CalculateHash() As String
        Dim dataJson As String
        Try
            ' --- Manual construction of dataJson ---
            If Me.Data Is Nothing OrElse Me.Data.Count = 0 Then
                dataJson = "[]"
            Else
                Dim sbJsonArray As New StringBuilder("[")
                For i As Integer = 0 To Me.Data.Count - 1
                    Dim jobjWrapper As JObject = Me.Data(i)
                    If jobjWrapper IsNot Nothing Then
                        sbJsonArray.Append(jobjWrapper.ToString(Formatting.None))
                    Else
                        sbJsonArray.Append("null") 
                    End If
                    If i < Me.Data.Count - 1 Then
                        sbJsonArray.Append(",")
                    End If
                Next
                sbJsonArray.Append("]")
                dataJson = sbJsonArray.ToString()
            End If
            ' --- End Manual construction ---

        Catch ex As Exception
            ' Console.WriteLine($"CRITICAL ERROR during Me.Data serialization in CalculateHash: {ex.ToString()}")
            dataJson = "[]" 
        End Try

        Dim formattedBlockTimestamp As String = Me.Timestamp.ToUniversalTime().ToString(PreciseTimestampFormat, CultureInfo.InvariantCulture)

        Dim tempDataToHash As String = Me.Index.ToString() &
                                     formattedBlockTimestamp &
                                     dataJson &
                                     Me.PreviousHash &
                                     Me.Nonce.ToString() & ' .ToString() on a Long is fine
                                     Me.Difficulty.ToString()

        ' LastCalculatedDataToHash = tempDataToHash ' For debugging if needed

        Return HashString(tempDataToHash)
    End Function

    ' This Mine method is primarily for server-side testing or solo mining if ever implemented directly in VB.NET.
    ' The Python client uses its own mining loops.
    Public Sub Mine()
        Dim leadingZeros As String = New String("0"c, Me.Difficulty)
        Dim currentNonceInLoop As Long = 0L ' Use a local Long variable for the loop

        Me.Nonce = currentNonceInLoop ' Set initial nonce for the block property
        Me.Hash = CalculateHash()     ' Calculate hash with initial nonce

        While Not Me.Hash.StartsWith(leadingZeros)
            currentNonceInLoop += 1

            If currentNonceInLoop > MAX_NONCE_VALUE_UINT32_EQUIVALENT Then
                ' Nonce search space for a 32-bit unsigned integer equivalent is exhausted.
                ' This means for the current block data (timestamp, transactions), no solution was found.
                ' In a real scenario, the mining process would typically update the timestamp
                ' or select different transactions to change the hash input and restart the nonce search.
                ' For this simplified Mine() method, we'll just log and exit the loop.
                Console.WriteLine($"Block.Mine (Index: {Me.Index}): Nonce search exceeded {MAX_NONCE_VALUE_UINT32_EQUIVALENT}. Stopping search for this attempt.")
                Exit While ' Stop mining this particular configuration of the block
            End If

            Me.Nonce = currentNonceInLoop ' Update the block's actual Nonce property
            Me.Hash = CalculateHash()     ' Recalculate hash with the new nonce
        End While
        
        ' Update block size after mining (nonce and hash might have changed its length)
        Me.BlockSize = CalculateBlockSize()
    End Sub

    Private Function HashString(input As String) As String
        Using sha256 As SHA256 = SHA256.Create()
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(input)
            Dim hashBytes As Byte() = sha256.ComputeHash(bytes)
            Return BitConverter.ToString(hashBytes).Replace("-", "").ToLower()
        End Using
    End Function

    Public Function CalculateBlockSize() As Integer
        Dim tempBlockForSizing As New JObject()
        tempBlockForSizing("Index") = Me.Index
        tempBlockForSizing("Timestamp") = Me.Timestamp.ToUniversalTime().ToString(PreciseTimestampFormat, CultureInfo.InvariantCulture)
        tempBlockForSizing("Data") = JToken.FromObject(Me.Data) 
        tempBlockForSizing("PreviousHash") = Me.PreviousHash
        tempBlockForSizing("Hash") = Me.Hash 
        tempBlockForSizing("Nonce") = Me.Nonce ' Nonce is now Long
        tempBlockForSizing("Difficulty") = Me.Difficulty
        
        Dim blockDataString = tempBlockForSizing.ToString(Formatting.None)
        Return Encoding.UTF8.GetBytes(blockDataString).Length
    End Function
End Class