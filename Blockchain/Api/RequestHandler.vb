﻿Imports Newtonsoft.Json
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

        Try
            ' Route the request based on the URL path
            Select Case request.Url.AbsolutePath
                Case "/" ' Serve the index.html file for the root path
                    HandleBlockchainPageRequest(response)
                Case "/mempool" ' Serve the index.html file for the root path
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
                    HandleTransactionTrackingRequest(request, response)
                Case "/api/get_token_names"
                    HandleGetTokenNamesRequest(request, response)
                Case "/api/get_transaction_by_hash"
                    HandleGetTransactionByHashRequest(request, response)
                Case "/api/validate_public_key"
                    HandleValidatePublicKeyRequest(request, response)
                Case "/api/get_mempool" ' New case for getting mempool transactions
                    HandleGetMempoolRequest(request, response)
                Case Else
                    HandleNotFoundRequest(response)
            End Select
        Catch ex As Exception
            HandleErrorResponse(response, 500, $"An unexpected error occurred: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleStaticFileRequest(response As HttpListenerResponse, filePath As String, contentType As String)
        Try
            ' Load the static file
            Dim fileContent As String = File.ReadAllText(filePath)

            ' Set the response status code and content type
            response.StatusCode = 200
            response.ContentType = contentType

            ' Write the response
            Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(fileContent)
            response.ContentLength64 = buffer.Length
            Using output As System.IO.Stream = response.OutputStream
                output.Write(buffer, 0, buffer.Length)
            End Using
        Catch ex As Exception
            HandleErrorResponse(response, 500, $"Error loading file {filePath}: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleNotFoundRequest(response As HttpListenerResponse)
        HandleErrorResponse(response, 404, "Not Found")
    End Sub

    Private Sub HandleErrorResponse(response As HttpListenerResponse, statusCode As Integer, message As String)
        ' Set the response status code and content type
        response.StatusCode = statusCode
        response.ContentType = "application/json"
        ' Write the response
        Dim responseObject = New With {
            .error = message
        }
        Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
        Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
        response.ContentLength64 = buffer.Length
        Using output As System.IO.Stream = response.OutputStream
            output.Write(buffer, 0, buffer.Length)
        End Using
    End Sub

    Private Sub HandleBlockchainPageRequest(response As HttpListenerResponse)
        Try
            ' Load the index.html file
            Dim htmlContent As String = File.ReadAllText("webpage/blockchain.html") ' Replace with the actual path to your index.html

            ' Set the response status code and content type
            response.StatusCode = 200
            response.ContentType = "text/html"

            ' Write the response
            Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(htmlContent)
            response.ContentLength64 = buffer.Length
            Using output As System.IO.Stream = response.OutputStream
                output.Write(buffer, 0, buffer.Length)
            End Using
        Catch ex As Exception
            HandleErrorResponse(response, 500, $"Error loading home page: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleMempoolPageRequest(response As HttpListenerResponse)
        Try
            ' Load the index.html file
            Dim htmlContent As String = File.ReadAllText("webpage/mempool.html") ' Replace with the actual path to your index.html

            ' Set the response status code and content type
            response.StatusCode = 200
            response.ContentType = "text/html"

            ' Write the response
            Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(htmlContent)
            response.ContentLength64 = buffer.Length
            Using output As System.IO.Stream = response.OutputStream
                output.Write(buffer, 0, buffer.Length)
            End Using
        Catch ex As Exception
            HandleErrorResponse(response, 500, $"Error loading home page: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleGetTransactionByHashRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim hash As String = request.QueryString("hash")

                If String.IsNullOrEmpty(hash) Then
                    HandleErrorResponse(response, 400, "Missing hash parameter")
                    Return
                End If

                Dim transaction = blockchain.GetTransactionByHash(hash)

                If transaction Is Nothing Then
                    HandleErrorResponse(response, 404, $"Transaction with hash {hash} not found")
                    Return
                End If

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim responseObject = New With {
                .transaction = transaction
            }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting transaction by hash: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub


    Private Sub HandleGetTokenNamesRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                ' Get the token names from the blockchain
                Dim tokenNames As Dictionary(Of String, String) = blockchain.GetTokenNames()

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Create a list to hold the token name objects
                Dim tokenList As New List(Of JObject)
                For Each kvp In tokenNames
                    Dim tokenObject = New JObject()
                    tokenObject.Add("symbol", kvp.Key)
                    tokenObject.Add("name", kvp.Value)
                    tokenList.Add(tokenObject)
                Next

                ' Write the response
                Dim jsonResponse = JsonConvert.SerializeObject(tokenList) ' Serialize the list
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting token names: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleTransactionTrackingRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                ' Get the txId from the query parameters
                Dim txId As String = request.QueryString("id")

                ' Check if txId is provided
                If String.IsNullOrEmpty(txId) Then
                    HandleErrorResponse(response, 400, "id is required")
                    Return
                End If

                ' Check mempool first
                Dim mempoolTransaction As JObject = blockchain._mempool.GetTransactionByTxId(txId)
                If mempoolTransaction IsNot Nothing Then
                    ' Transaction found in mempool
                    Dim transactionData = JObject.Parse(mempoolTransaction("transaction").ToString()) ' Parse transaction data
                    Dim responseObject = New With {
                    .status = "pending",
                    .transaction = transactionData
                }
                    Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                    SendJsonResponse(response, 200, jsonResponse)
                    Return
                End If

                ' If not in mempool, search the blockchain
                For Each block As Block In blockchain.Chain
                    For Each transaction As JObject In block.Data
                        Dim transactionData = JObject.Parse(transaction("transaction").ToString())
                        If transactionData("txId").ToString() = txId Then
                            ' Transaction found in blockchain
                            Dim responseObject = New With {
                            .status = "completed",
                            .timestamp = block.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                            .blockIndex = block.Index,
                            .blockHash = block.Hash,
                            .nonce = block.Nonce,
                            .transaction = transactionData
                        }
                            Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                            SendJsonResponse(response, 200, jsonResponse)
                            Return
                        End If
                    Next
                Next

                ' Transaction not found
                HandleErrorResponse(response, 404, "Transaction not found")

            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error tracking transaction: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    ' Helper function to send JSON response
    Private Sub SendJsonResponse(response As HttpListenerResponse, statusCode As Integer, jsonResponse As String)
        response.StatusCode = statusCode
        response.ContentType = "application/json"
        Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
        response.ContentLength64 = buffer.Length
        Using output As System.IO.Stream = response.OutputStream
            output.Write(buffer, 0, buffer.Length)
        End Using
    End Sub

    Private Sub HandleValidatePublicKeyRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim publicKey As String = request.QueryString("publicKey")

                If String.IsNullOrEmpty(publicKey) Then
                    HandleErrorResponse(response, 400, "Missing publicKey parameter")
                    Return
                End If

                Dim isValid = Wallet.IsValidPublicKey(publicKey)

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim responseObject = New With {
                .isValid = isValid
            }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error validating public key: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetBlockchainRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Create the JSON response
                Dim jsonResponse = JsonConvert.SerializeObject(blockchain.Chain)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length

                ' Print the response in the console
                'Console.WriteLine("Response: " & jsonResponse)

                ' Write the response to the output stream
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting blockchain: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub


    Private Sub HandleCheckValidityRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim isValid = blockchain.IsChainValid()
                Dim responseObject = New With {
                    .isValid = isValid
                }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error checking validity: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleCreateTokenRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "POST" Then
            Try
                Dim txId As String ' Declare txId here
                Using reader As New StreamReader(request.InputStream)
                    Dim requestBody As String = reader.ReadToEnd()
                    Dim jsonObject As JObject = JObject.Parse(requestBody)

                    Dim name As String = jsonObject("name").ToString()
                    Dim symbol As String = jsonObject("symbol").ToString()
                    Dim initialSupply As Decimal = Decimal.Parse(jsonObject("initialSupply").ToString())
                    Dim ownerPublicKey As String = jsonObject("ownerPublicKey").ToString()
                    Dim signature As String = jsonObject("signature").ToString() ' Get the signature

                    txId = blockchain.CreateToken(name, symbol, initialSupply, ownerPublicKey, signature) ' Pass the signature

                    ' Set the response status code and content type
                    response.StatusCode = 200
                    response.ContentType = "application/json"

                    ' Write the response
                    Dim responseObject = New With {
                    .message = "Transaction added to mempool successfully!", ' Changed message
                    .txId = txId ' Include the txId in the response
                }
                    Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                    Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                    response.ContentLength64 = buffer.Length
                    Using output As System.IO.Stream = response.OutputStream
                        output.Write(buffer, 0, buffer.Length)
                    End Using
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error creating token: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleTransferTokensRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "POST" Then
            Try
                Dim txId As String ' Declare txId here
                Using reader As New StreamReader(request.InputStream)
                    Dim jsonContent = reader.ReadToEnd()
                    Dim jsonObject = JObject.Parse(jsonContent)

                    Dim toAddress As String = jsonObject("toAddress").ToString()
                    Dim amount As Decimal = jsonObject("amount").ToObject(Of Decimal)()
                    Dim tokenSymbol As String = jsonObject("tokenSymbol").ToString()
                    Dim signature As String = jsonObject("signature").ToString()
                    Dim fromAddressPublicKey As String = jsonObject("fromAddress").ToString() ' Get public key

                    ' You might want to add validation here to ensure all required fields are present

                    'blockchain.TransferTokens(toAddress, amount, tokenSymbol, signature, fromAddressPublicKey) ' Pass public key
                    ' Call TransferTokens and get the txId
                    txId = blockchain.TransferTokens(toAddress, amount, tokenSymbol, signature, fromAddressPublicKey)

                End Using

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim responseObject = New With {
                .message = "Transaction added to mempool successfully!",
                .txId = txId ' Include the txId in the response
            }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using

            Catch ex As Exception
                ' Generate the error response here:
                HandleErrorResponse(response, 500, $"Error transferring tokens: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetTokensOwnedRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim address As String = request.QueryString("address")

                If String.IsNullOrEmpty(address) Then
                    HandleErrorResponse(response, 400, "Missing address parameter")
                    Return
                End If

                Dim tokensOwned = blockchain.GetTokensOwned(address)

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim responseObject = New With {
                    .address = address,
                    .tokensOwned = tokensOwned
                }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting tokens owned: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetTransactionHistoryRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                Dim address As String = request.QueryString("address")

                If String.IsNullOrEmpty(address) Then
                    HandleErrorResponse(response, 400, "Missing address parameter")
                    Return
                End If

                Dim transactions = blockchain.GetTransactionHistory(address)

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim responseObject = New With {
                    .address = address,
                    .transactions = transactions
                }
                Dim jsonResponse = JsonConvert.SerializeObject(responseObject)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting transaction history: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub

    Private Sub HandleGetMempoolRequest(request As HttpListenerRequest, response As HttpListenerResponse)
        If request.HttpMethod = "GET" Then
            Try
                ' Get the mempool transactions from the blockchain
                Dim mempoolTransactions As List(Of JObject) = blockchain._mempool.GetTransactions()

                ' Set the response status code and content type
                response.StatusCode = 200
                response.ContentType = "application/json"

                ' Write the response
                Dim jsonResponse = JsonConvert.SerializeObject(mempoolTransactions)
                Dim buffer As Byte() = System.Text.Encoding.UTF8.GetBytes(jsonResponse)
                response.ContentLength64 = buffer.Length
                Using output As System.IO.Stream = response.OutputStream
                    output.Write(buffer, 0, buffer.Length)
                End Using
            Catch ex As Exception
                HandleErrorResponse(response, 500, $"Error getting mempool transactions: {ex.Message}")
            End Try
        Else
            HandleErrorResponse(response, 405, "Method Not Allowed")
        End If
    End Sub


End Class