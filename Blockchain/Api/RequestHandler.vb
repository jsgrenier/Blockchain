Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Net
Imports System.IO
Imports Microsoft.Data.Sqlite ' Assuming you'll use Sqlite for the historical data
Imports System.Globalization
Imports System.Data   ' For DateTime parsing


Public Class RequestHandler

    Private blockchain As Blockchain
    Private ReadOnly _dbFilePath As String ' You'll need the path to your DB for historical data

    Public Sub New(blockchain As Blockchain, dbFilePathFromWebServer As String)
        Me.blockchain = blockchain
        Me._dbFilePath = dbFilePathFromWebServer ' Assign the passed path
        If String.IsNullOrEmpty(Me._dbFilePath) Then
            Console.WriteLine("WARNING: RequestHandler initialized with a null or empty dbFilePath.")
        End If
    End Sub

    Public Sub HandleRequest(context As HttpListenerContext)
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response
        response.AddHeader("Access-Control-Allow-Origin", "*")
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type")

        If request.HttpMethod = "OPTIONS" Then
            Try
                response.StatusCode = CInt(HttpStatusCode.OK)
            Catch ex As Exception
                Console.WriteLine($"RequestHandler: Error setting OPTIONS status code: {ex.Message}")
                Return
            Finally
                Try
                    response.Close()
                Catch exClose As Exception
                    Console.WriteLine($"RequestHandler: Error closing response after OPTIONS: {exClose.Message}")
                End Try
            End Try
            Return
        End If


        Try
            Select Case request.Url.AbsolutePath
                Case "/"
                    HandleBlockchainPageRequest(response)
                Case "/mempool"
                    HandleMempoolPageRequest(response)
                Case "/styles.css"
                    HandleStaticFileRequest(response, "webpage/styles.css", "text/css")
                Case "/blockchain.js"
                    HandleStaticFileRequest(response, "webpage/blockchain.js", "application/javascript")
                Case "/mempool.js"
                    HandleStaticFileRequest(response, "webpage/mempool.js", "application/javascript")
                Case "/api/get_blockchain"
                    HandleGetBlockchainRequest(request, response)
                Case "/api/check_validity"
                    HandleCheckValidityRequest(request, response)
                Case "/api/create_token"
                    HandleCreateTokenRequest(request, response)
                Case "/api/transfer_tokens"
                    HandleTransferTokensRequest(request, response)
                Case "/api/get_tokens_owned"
                    HandleGetTokensOwnedRequest(request, response)
                Case "/api/get_transaction_history"
                    HandleGetTransactionHistoryRequest(request, response)
                Case "/api/transaction"
                    HandleGetTransactionByTxIdRequest(request, response)
                Case "/api/get_token_names"
                    HandleGetTokenNamesRequest(request, response)
                Case "/api/validate_public_key"
                    HandleValidatePublicKeyRequest(request, response)
                Case "/api/get_mempool"
                    HandleGetMempoolRequest(request, response)
                Case "/api/get_latest_block"
                    HandleGetLatestBlockRequest(request, response)
                Case "/api/get_difficulty"
                    HandleGetDifficultyRequest(request, response)
                Case "/api/block"
                    HandleGetBlockByHashRequest(request, response)
                Case "/api/get_network_overview"
                    HandleGetNetworkOverviewRequest(request, response)
                Case "/api/get_all_registered_tokens"
                    HandleGetAllRegisteredTokensRequest(request, response)
                Case "/api/get_historical_hashrate" ' <<<< NEW ENDPOINT CASE
                    HandleGetHistoricalHashrateRequest(request, response)
                Case "/api/get_aggregated_block_times" ' <<<< NEW ENDPOINT CASE
                    HandleGetAggregatedBlockTimesRequest(request, response)
                Case Else
                    HandleNotFoundRequest(response)
            End Select
        Catch ex As Exception
            Console.WriteLine($"RequestHandler: Error during endpoint processing for {request.Url}: {ex.Message}{vbCrLf}{ex.StackTrace}")
            Try
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error processing request: {ex.Message}")
            Catch exHandler As Exception
                Console.WriteLine($"RequestHandler: Critical error trying to send error response: {exHandler.Message}")
            End Try
        Finally
            Try
                If response IsNot Nothing Then
                    response.Close()
                End If
            Catch exClose As ObjectDisposedException
                ' Expected
            Catch exClose As Exception
                Console.WriteLine($"RequestHandler: Error during final response.Close(): {exClose.Message}")
            End Try
        End Try
    End Sub

    ' --- Placeholder for HandleGetHistoricalHashrateRequest ---
    Private Sub HandleGetHistoricalHashrateRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod <> "GET" Then
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
            Return
        End If

        Dim rangeParam As String = request.QueryString("range")?.ToLower()
        ' Dim startTimeParam As String = request.QueryString("startTime") ' TODO: Add support for custom start/end
        ' Dim endTimeParam As String = request.QueryString("endTime")   ' TODO: Add support for custom start/end

        Dim dataPoints As New List(Of Object)()
        Dim sqlQuery As String = ""
        Dim groupByClause As String = ""
        Dim timeFormatForGrouping As String = "'%Y-%m-%d %H:%M:00'" ' Default: Group by minute for raw-ish data
        Dim whereClause As String = " WHERE 1=1 " ' Start with a valid WHERE

        Dim nowUtc As DateTime = DateTime.UtcNow

        ' Determine WHERE clause and GROUP BY based on range
        Select Case rangeParam
            Case "24h"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddHours(-24).ToString("o", CultureInfo.InvariantCulture)}'"
                ' For 24h, maybe group by every 5 or 10 minutes, or return more raw points
                timeFormatForGrouping = "'%Y-%m-%d %H:' || substr('0' || (strftime('%M', Timestamp) / 5) * 5, -2) || ':00'" ' Group by 5-min intervals
            Case "3d"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddDays(-3).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d %H:00:00'" ' Group by hour
            Case "7d"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddDays(-7).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d %H:00:00'" ' Group by hour
            Case "1m"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddMonths(-1).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d'" ' Group by day
            Case "6m"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddMonths(-6).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d'" ' Group by day
            Case "12m", "1y"
                whereClause &= $" AND Timestamp >= '{nowUtc.AddYears(-1).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d'" ' Group by day
            Case "all"
                ' No additional time-based WHERE clause for "all"
                timeFormatForGrouping = "'%Y-%m-%d'" ' Group by day for "all" to keep data manageable
            Case Else
                ' Default to last 24h if range is invalid or not provided
                whereClause &= $" AND Timestamp >= '{nowUtc.AddHours(-24).ToString("o", CultureInfo.InvariantCulture)}'"
                timeFormatForGrouping = "'%Y-%m-%d %H:' || substr('0' || (strftime('%M', Timestamp) / 5) * 5, -2) || ':00'"
                Console.WriteLine($"HandleGetHistoricalHashrateRequest: Invalid or no range specified, defaulting to 24h. Received: {rangeParam}")
        End Select

        ' Construct the SQL query with aggregation
        ' We select the (start of the) interval as timestamp and the AVG of the rate
        sqlQuery = $"SELECT strftime({timeFormatForGrouping}, Timestamp) as AggregatedTimestamp, AVG(EstimatedHashRate) as Value " &
                   $"FROM HistoricalHashRates " &
                   whereClause &
                   $"GROUP BY AggregatedTimestamp " &
                   $"ORDER BY AggregatedTimestamp ASC"



        Using connection As New SqliteConnection($"Data Source={_dbFilePath}")
            Try
                connection.Open()
                Using command As New SqliteCommand(sqlQuery, connection)
                    ' TODO: Add parameters if using startTimeParam/endTimeParam directly in whereClause to prevent SQL injection
                    ' Example: command.Parameters.AddWithValue("@StartTime", startTimeParamValue)

                    Using reader As SqliteDataReader = command.ExecuteReader()
                        While reader.Read()
                            Dim timestampStr As String = reader.GetString(0)
                            Dim value As Double = reader.GetDouble(1)
                            Dim parsedTimestamp As DateTime
                            Dim success As Boolean = False

                            ' Define an array of possible formats, ordered from most specific to least, or most common
                            Dim expectedDateTimeFormats As String() = {
            "yyyy-MM-dd HH:mm:ss",  ' Standard two-digit minute/second
            "yyyy-MM-dd HH:m:ss",   ' HH:single-m:ss
            "yyyy-MM-dd H:mm:ss",   ' single-H:mm:ss (less likely from your strftime but good to have)
            "yyyy-MM-dd H:m:ss"     ' single-H:single-m:ss
        }
                            Dim expectedDateOnlyFormats As String() = {
            "yyyy-MM-dd"
        }

                            ' Try parsing as DateTime first
                            success = DateTime.TryParseExact(timestampStr, expectedDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, parsedTimestamp)

                            ' If not a DateTime, try parsing as DateOnly (for daily aggregations)
                            If Not success Then
                                success = DateTime.TryParseExact(timestampStr, expectedDateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, parsedTimestamp)
                                If success Then
                                    ' If it's a date only, it defaults to 00:00:00 time, which is fine for daily aggregates.
                                    ' DateTimeStyles.AdjustToUniversal will ensure Kind is Utc if AssumeUniversal is used.
                                End If
                            End If

                            If success Then
                                ' Ensure the Kind is Utc before converting to ISO "o" format for full compatibility.
                                ' If AssumeUniversal and AdjustToUniversal are used, parsedTimestamp.Kind should be Utc.
                                ' If not, force it:
                                ' If parsedTimestamp.Kind <> DateTimeKind.Utc Then
                                '    parsedTimestamp = DateTime.SpecifyKind(parsedTimestamp, DateTimeKind.Utc)
                                ' End If
                                dataPoints.Add(New With {.timestamp = parsedTimestamp.ToString("o", CultureInfo.InvariantCulture), .value = value})
                            Else
                                Console.WriteLine($"Could not parse aggregated timestamp: '{timestampStr}' with any of the expected formats.")
                                ' Option: Log this error more permanently or return an error response if too many fail.
                                ' For now, we just skip this data point.
                            End If
                        End While
                    End Using
                End Using
                SendJsonResponse(response, HttpStatusCode.OK, dataPoints)
            Catch ex As Exception
                Console.WriteLine($"Error querying historical hashrate: {ex.Message}{vbCrLf}{ex.StackTrace}")
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error retrieving historical hashrate: {ex.Message}")
            Finally
                If connection.State = ConnectionState.Open Then
                    connection.Close()
                End If
            End Try
        End Using
    End Sub

    Private Sub HandleGetAggregatedBlockTimesRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod <> "GET" Then
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
            Return
        End If

        Try
            ' Get the range parameter from the query string
            Dim rangeParam As String = request.QueryString("range")
            If String.IsNullOrEmpty(rangeParam) Then
                rangeParam = "24h" ' Default to 24h if not provided
            End If

            ' Call the refactored blockchain method
            Dim aggregatedData = blockchain.GetAggregatedBlockTimes(rangeParam)

            SendJsonResponse(response, HttpStatusCode.OK, aggregatedData)

        Catch ex As Exception
            Console.WriteLine($"Error getting aggregated block times: {ex.Message}{vbCrLf}{ex.StackTrace}")
            HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error retrieving aggregated block times: {ex.Message}")
        End Try
    End Sub


    ' --- Existing Request Handler Methods ---
    Private Sub HandleGetNetworkOverviewRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim latestBlock = blockchain.GetLatestBlock()
                Dim latestBlockHeight As Integer = -1
                If latestBlock IsNot Nothing Then
                    latestBlockHeight = latestBlock.Index
                End If

                Dim currentDifficulty As Integer = blockchain._difficulty
                Dim mempoolTxCount As Integer = blockchain._mempool.Count()

                Dim beanCurrentSupply As Decimal = blockchain.GetTotalSupply("BEAN")
                Dim beanMaxSupplyValue As Decimal = Blockchain.MaxBeanSupply

                Const blocksForAvgTimeAndChart As Integer = 20
                Dim avgBlockTime As Double = blockchain.GetAverageBlockTime(blocksForAvgTimeAndChart)
                Dim blockStatsForChart = blockchain.GetBlockStatsForChart(blocksForAvgTimeAndChart)

                Dim estimatedHashRate As Double = blockchain.CurrentEstimatedNetworkHashRate

                Dim overviewData = New With {
                    .latestBlockHeight = latestBlockHeight,
                    .currentDifficulty = currentDifficulty,
                    .mempoolTxCount = mempoolTxCount,
                    .beanCurrentSupply = beanCurrentSupply,
                    .beanMaxSupply = beanMaxSupplyValue,
                    .averageBlockTimeSeconds = avgBlockTime,
                    .blockStats = blockStatsForChart,
                    .estimatedNetworkHashRate = estimatedHashRate
                }
                SendJsonResponse(response, HttpStatusCode.OK, overviewData)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting network overview: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetAllRegisteredTokensRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim tokenInfos As List(Of TokenChainInfo) = blockchain.GetAllRegisteredTokensInfo()
                SendJsonResponse(response, HttpStatusCode.OK, tokenInfos)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting registered token list: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    ' ... (rest of your existing Handle... methods like SendJsonResponse, HandleStaticFileRequest, etc.) ...
    Private Sub SendJsonResponse(response As HttpListenerResponse, statusCode As HttpStatusCode, data As Object)
        response.StatusCode = CInt(statusCode)
        response.ContentType = "application/json"
        Dim jsonResponse = JsonConvert.SerializeObject(data)
        Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
        response.ContentLength64 = buffer.Length
        Using output As System.IO.Stream = response.OutputStream
            output.Write(buffer, 0, buffer.Length)
        End Using
    End Sub

    Private Sub HandleStaticFileRequest(response As HttpListenerResponse, filePath As String, contentType As String)
        Try
            If File.Exists(filePath) Then
                Dim fileBytes As Byte() = File.ReadAllBytes(filePath)
                response.StatusCode = CInt(HttpStatusCode.OK)
                response.ContentType = contentType
                response.ContentLength64 = fileBytes.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(fileBytes, 0, fileBytes.Length)
                End Using
            Else
                HandleErrorResponse(response, HttpStatusCode.NotFound, $"File not found: {filePath}")
            End If
        Catch ex As Exception
            HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error loading file {filePath}: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleNotFoundRequest(response As HttpListenerResponse)
        HandleErrorResponse(response, HttpStatusCode.NotFound, "Not Found")
    End Sub

    Private Sub HandleErrorResponse(response As HttpListenerResponse, statusCode As HttpStatusCode, message As String)
        Try
            If response.OutputStream.CanWrite Then
                response.StatusCode = CInt(statusCode)
                response.ContentType = "application/json"
                Dim responseObject = New With {.error = message}
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Else
                Console.WriteLine($"ErrorResponse: Cannot write to output stream. Status: {statusCode}, Msg: {message}")
            End If
        Catch ex As Exception
            Console.WriteLine($"ErrorResponse: Further error while sending error response: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleBlockchainPageRequest(response As HttpListenerResponse)
        HandleStaticFileRequest(response, "webpage/blockchain.html", "text/html")
    End Sub

    Private Sub HandleMempoolPageRequest(response As HttpListenerResponse)
        HandleStaticFileRequest(response, "webpage/mempool.html", "text/html")
    End Sub

    Private Sub HandleGetTransactionByTxIdRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim txId As String = request.QueryString("id")
                If String.IsNullOrEmpty(txId) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing txId parameter")
                    Return
                End If
                Dim transactionInfo = blockchain.GetTransactionByTxId(txId)
                If transactionInfo Is Nothing Then
                    HandleErrorResponse(response, HttpStatusCode.NotFound, $"Transaction with txId {txId} not found")
                    Return
                End If
                SendJsonResponse(response, HttpStatusCode.OK, transactionInfo)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting transaction by txId: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub
    Private Sub HandleGetTokenNamesRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim tokenNames As Dictionary(Of String, String) = blockchain.GetTokenNames()
                Dim tokenList As New List(Of JObject)
                For Each kvp In tokenNames
                    tokenList.Add(New JObject From {{"symbol", kvp.Key}, {"name", kvp.Value}})
                Next
                SendJsonResponse(response, HttpStatusCode.OK, tokenList)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting token names: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleValidatePublicKeyRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim encodedPublicKey As String = request.QueryString("publicKey")
                If String.IsNullOrEmpty(encodedPublicKey) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing publicKey parameter")
                    Return
                End If
                Dim publicKey As String = WebUtility.UrlDecode(encodedPublicKey)
                Dim isValid = Wallet.IsValidPublicKey(publicKey)
                SendJsonResponse(response, HttpStatusCode.OK, New With {.isValid = isValid})
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error validating public key: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetBlockchainRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim pageParam As String = request.QueryString("page")
                Dim limitParam As String = request.QueryString("limit")
                Dim sortOrderParam As String = request.QueryString("sortOrder")
                Dim page As Integer = 1
                If Not String.IsNullOrEmpty(pageParam) AndAlso Integer.TryParse(pageParam, page) Then
                    If page < 1 Then page = 1
                End If
                Dim limit As Integer = 10
                If Not String.IsNullOrEmpty(limitParam) AndAlso Integer.TryParse(limitParam, limit) Then
                    If limit < 1 Then limit = 1
                    If limit > 100 Then limit = 100
                End If
                Dim sortDescending As Boolean = True
                If Not String.IsNullOrEmpty(sortOrderParam) AndAlso sortOrderParam.ToLowerInvariant() = "asc" Then
                    sortDescending = False
                End If
                Dim fullChainView As IReadOnlyList(Of Block) = blockchain.Chain
                Dim totalBlocks As Integer = fullChainView.Count
                Dim pagedBlocks As List(Of Block)
                Dim orderedChain As IEnumerable(Of Block)
                If sortDescending Then
                    orderedChain = fullChainView.OrderByDescending(Function(b) b.Index)
                Else
                    orderedChain = fullChainView.OrderBy(Function(b) b.Index)
                End If
                pagedBlocks = orderedChain.Skip((page - 1) * limit).Take(limit).ToList()
                Dim responseObject = New With {
                    .totalBlocks = totalBlocks,
                    .page = page,
                    .limit = limit,
                    .totalPages = If(totalBlocks = 0, 0, Math.Ceiling(CDbl(totalBlocks) / limit)),
                    .data = pagedBlocks
                }
                SendJsonResponse(response, HttpStatusCode.OK, responseObject)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting blockchain: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleCheckValidityRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim isValid = blockchain.IsChainValid()
                SendJsonResponse(response, HttpStatusCode.OK, New With {.isValid = isValid})
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error checking validity: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleCreateTokenRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "POST" Then
            Try
                Using reader As New StreamReader(request.InputStream)
                    Dim requestBody As String = reader.ReadToEnd()
                    Dim jsonObject As JObject = JObject.Parse(requestBody)
                    Dim name As String = jsonObject("name")?.ToString()
                    Dim symbol As String = jsonObject("symbol")?.ToString()
                    Dim initialSupply As Decimal = jsonObject("initialSupply")?.ToObject(Of Decimal)()
                    Dim ownerPublicKey As String = jsonObject("ownerPublicKey")?.ToString()
                    Dim signature As String = jsonObject("signature")?.ToString()
                    If String.IsNullOrEmpty(name) OrElse String.IsNullOrEmpty(symbol) OrElse initialSupply < 0 OrElse
                       String.IsNullOrEmpty(ownerPublicKey) OrElse String.IsNullOrEmpty(signature) Then
                        HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing or invalid parameters for token creation.")
                        Return
                    End If
                    Dim txId = blockchain.CreateToken(name, symbol, initialSupply, ownerPublicKey, signature)
                    SendJsonResponse(response, HttpStatusCode.OK, New With {
                        .message = "Token creation transaction added to mempool.",
                        .txId = txId
                    })
                End Using
            Catch ex As JsonReaderException
                HandleErrorResponse(response, HttpStatusCode.BadRequest, $"Invalid JSON format: {ex.Message}")
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error creating token: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleTransferTokensRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "POST" Then
            Try
                Using reader As New StreamReader(request.InputStream)
                    Dim jsonContent = reader.ReadToEnd()
                    Dim jsonObject = JObject.Parse(jsonContent)
                    Dim toAddress As String = jsonObject("toAddress")?.ToString()
                    Dim amount As Decimal = jsonObject("amount")?.ToObject(Of Decimal)()
                    Dim tokenSymbol As String = jsonObject("tokenSymbol")?.ToString()
                    Dim signature As String = jsonObject("signature")?.ToString()
                    Dim fromAddressPublicKey As String = jsonObject("fromAddress")?.ToString()
                    If String.IsNullOrEmpty(toAddress) OrElse amount <= 0 OrElse String.IsNullOrEmpty(tokenSymbol) OrElse
                       String.IsNullOrEmpty(signature) OrElse String.IsNullOrEmpty(fromAddressPublicKey) Then
                        HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing or invalid parameters for token transfer.")
                        Return
                    End If
                    Dim txId = blockchain.TransferTokens(toAddress, amount, tokenSymbol, signature, fromAddressPublicKey)
                    SendJsonResponse(response, HttpStatusCode.OK, New With {
                        .message = "Token transfer transaction added to mempool.",
                        .txId = txId
                    })
                End Using
            Catch ex As JsonReaderException
                HandleErrorResponse(response, HttpStatusCode.BadRequest, $"Invalid JSON format: {ex.Message}")
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error transferring tokens: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetTokensOwnedRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim encodedAddress As String = request.QueryString("address")
                If String.IsNullOrEmpty(encodedAddress) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing address parameter")
                    Return
                End If
                Dim address As String = WebUtility.UrlDecode(encodedAddress)
                Dim tokensOwned = blockchain.GetTokensOwned(address)
                SendJsonResponse(response, HttpStatusCode.OK, New With {
                    .address = address,
                    .tokensOwned = tokensOwned
                })
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting tokens owned: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetTransactionHistoryRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim encodedAddress As String = request.QueryString("address")
                If String.IsNullOrEmpty(encodedAddress) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing address parameter")
                    Return
                End If
                Dim address As String = WebUtility.UrlDecode(encodedAddress)
                Dim transactions = blockchain.GetTransactionHistory(address)
                SendJsonResponse(response, HttpStatusCode.OK, New With {
                    .address = address,
                    .transactions = transactions
                })
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting transaction history: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetMempoolRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim mempoolTransactions As List(Of JObject) = blockchain._mempool.GetTransactions()
                SendJsonResponse(response, HttpStatusCode.OK, mempoolTransactions)
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting mempool transactions: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub
    Private Sub HandleGetDifficultyRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim difficulty = blockchain._difficulty
                SendJsonResponse(response, HttpStatusCode.OK, New With {.difficulty = difficulty})
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting difficulty: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub
    Private Sub HandleGetLatestBlockRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim latestBlock = blockchain.GetLatestBlock()
                If latestBlock IsNot Nothing Then
                    SendJsonResponse(response, HttpStatusCode.OK, latestBlock)
                Else
                    HandleErrorResponse(response, HttpStatusCode.NotFound, "Blockchain is empty or no latest block found.")
                End If
            Catch ex As Exception
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting latest block: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetBlockByHashRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim blockHashFromQuery As String = request.QueryString("hash") ' Get the raw value


                If String.IsNullOrEmpty(blockHashFromQuery) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing block hash parameter")
                    Return
                End If


                Dim foundBlock As Block = Nothing
                Dim chainForDebug As List(Of Block)
                SyncLock blockchain.Chain ' Ensure thread-safe access if Chain can be modified
                    chainForDebug = blockchain.Chain.ToList() ' Make a copy for iteration if needed
                End SyncLock

                For Each b As Block In chainForDebug ' Iterate over the copy
                    ' Console.WriteLine($"API: Checking against chain block hash: '{b.Hash}' (Index: {b.Index})") ' Verbose: uncomment if needed
                    If String.Equals(b.Hash, blockHashFromQuery, StringComparison.Ordinal) Then
                        foundBlock = b
                        Exit For
                    End If
                Next

                If foundBlock Is Nothing Then
                    Console.WriteLine($"API: Block with hash '{blockHashFromQuery}' NOT FOUND in current chain (Chain count: {chainForDebug.Count}).")
                    ' --- Log first few hashes in chain for comparison ---
                    If chainForDebug.Any() Then
                        Console.WriteLine("API: First few hashes in current chain for debugging:")
                        For i As Integer = 0 To Math.Min(4, chainForDebug.Count - 1)
                            Console.WriteLine($"  - Index {chainForDebug(i).Index}: {chainForDebug(i).Hash}")
                        Next
                    Else
                        Console.WriteLine("API: Chain is currently empty.")
                    End If
                    ' --- End logging ---
                    HandleErrorResponse(response, HttpStatusCode.NotFound, $"Block with hash '{blockHashFromQuery}' not found.")
                    Return
                End If

                SendJsonResponse(response, HttpStatusCode.OK, foundBlock)
            Catch ex As Exception
                Console.WriteLine($"API: Error getting block by hash: {ex.Message}")
                HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"Error getting block by hash: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
        End If
    End Sub

End Class