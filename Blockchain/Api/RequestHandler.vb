Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Net
Imports System.IO

Public Class RequestHandler

    Private blockchain As Blockchain

    Public Sub New(blockchain As Blockchain)
        Me.blockchain = blockchain
    End Sub

    Public Sub HandleRequest(context As HttpListenerContext)
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response
        response.AddHeader("Access-Control-Allow-Origin", "*") ' Allow CORS for development
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type")

        If request.HttpMethod = "OPTIONS" Then
            response.StatusCode = CInt(HttpStatusCode.OK)
            response.Close()
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
                Case "/api/transaction" ' Renamed from /api/get_transaction_by_hash for clarity
                    HandleGetTransactionByTxIdRequest(request, response) ' Changed handler name
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
                Case Else
                    HandleNotFoundRequest(response)
            End Select
        Catch ex As Exception
            HandleErrorResponse(response, HttpStatusCode.InternalServerError, $"An unexpected error occurred: {ex.Message}")
        End Try
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
                Dim fileContent As String = File.ReadAllText(filePath)
                response.StatusCode = CInt(HttpStatusCode.OK)
                response.ContentType = contentType
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(fileContent)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
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
        response.StatusCode = CInt(statusCode)
        response.ContentType = "application/json"
        Dim responseObject = New With {.error = message}
        Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
        Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
        response.ContentLength64 = buffer.Length
        Using output As System.IO.Stream = response.OutputStream
            output.Write(buffer, 0, buffer.Length)
        End Using
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
                Dim publicKey As String = request.QueryString("publicKey")
                If String.IsNullOrEmpty(publicKey) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing publicKey parameter")
                    Return
                End If
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
                ' Set the response status code and content type
                response.StatusCode = 200 ' Corrected to use CInt or HttpStatusCode enum
                response.ContentType = "application/json"

                ' Create the JSON response
                Dim jsonResponse = JsonConvert.SerializeObject(blockchain.Chain) ' Potential issue here
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length

                ' Print the response in the console
                'Console.WriteLine("Response: " & jsonResponse) ' Useful for debugging

                ' Write the response to the output stream
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
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
                    Dim fromAddressPublicKey As String = jsonObject("fromAddress")?.ToString() ' This is the sender's public key

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
                Dim address As String = request.QueryString("address")
                If String.IsNullOrEmpty(address) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing address parameter")
                    Return
                End If

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
                Dim address As String = request.QueryString("address")
                If String.IsNullOrEmpty(address) Then
                    HandleErrorResponse(response, HttpStatusCode.BadRequest, "Missing address parameter")
                    Return
                End If

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
End Class