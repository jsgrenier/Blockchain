Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading

Public Class WebServer

    Private blockchain As Blockchain
    Private server As HttpListener
    Private listenerThread As Thread
    Private IsRunning As Boolean = False ' Added Volatile

    Public Sub New(ByVal blockchain As Blockchain)
        Me.blockchain = blockchain
    End Sub

    Public Sub Start()
        Try
            server = New HttpListener()
            Dim prefixes As New List(Of String) From {
                "http://localhost:8080/",
                "http://127.0.0.1:8080/"
            }
            Dim localIp = GetLocalIPAddress()
            If localIp <> "localhost" AndAlso localIp <> "127.0.0.1" Then
                prefixes.Add($"http://{localIp}:8080/")
            End If


            For Each prefix In prefixes
                Try
                    server.Prefixes.Add(prefix)
                Catch exHttp As HttpListenerException
                    Console.WriteLine($"Warning: Could not add prefix {prefix}. May require admin rights or 'netsh http add urlacl'. Error: {exHttp.Message}")
                End Try
            Next

            If server.Prefixes.Count = 0 Then
                Console.WriteLine("Error: No valid HttpListener prefixes could be added. API server cannot start.")
                Return
            End If

            server.Start()
            IsRunning = True ' Set IsRunning to true AFTER server has successfully started
            Console.WriteLine("API server started. Listening on:")
            For Each prefix In server.Prefixes
                Console.WriteLine(prefix)
            Next

            listenerThread = New Thread(AddressOf HandleRequests)
            listenerThread.IsBackground = True
            listenerThread.Start()
        Catch ex As Exception
            Console.WriteLine($"Failed to start the API server: {ex.Message}")
            IsRunning = False
        End Try
    End Sub

    Public Sub Kill()
        Try
            IsRunning = False ' Signal thread to stop

            If server IsNot Nothing AndAlso server.IsListening Then
                Console.WriteLine("Stopping API server...")
                server.Stop() ' Stop listening for new requests
                server.Close() ' Release resources
                Console.WriteLine("API server stopped.")
            End If

            If listenerThread IsNot Nothing AndAlso listenerThread.IsAlive Then
                Console.WriteLine("Waiting for API request handler thread to finish...")
                If Not listenerThread.Join(TimeSpan.FromSeconds(5)) Then ' Wait for 5 seconds
                    Console.WriteLine("API request handler thread did not finish in time. Aborting.")
                    listenerThread.Interrupt()
                End If
            End If

        Catch ex As Exception
            Console.WriteLine($"Error stopping the API server: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleRequests()
        Dim handler As New RequestHandler(blockchain)
        Try
            While IsRunning AndAlso (server IsNot Nothing AndAlso server.IsListening)
                Dim context As HttpListenerContext = Nothing
                Try
                    context = server.GetContext()
                    If Not IsRunning Then Exit While

                    Task.Run(Sub()
                                 Dim currentContext As HttpListenerContext = context ' Capture context for the task
                                 Try
                                     handler.HandleRequest(currentContext)
                                 Catch exTask As Exception
                                     Console.WriteLine($"Error processing request in task for {currentContext.Request.Url}: {exTask.Message}")
                                     Try
                                         If currentContext IsNot Nothing AndAlso
                                            currentContext.Response.StatusCode = CInt(HttpStatusCode.OK) AndAlso
                                            currentContext.Response.ContentLength64 = -1 AndAlso
                                            Not currentContext.Response.SendChunked Then

                                             currentContext.Response.StatusCode = CInt(HttpStatusCode.InternalServerError)
                                             Dim buffer = System.Text.Encoding.UTF8.GetBytes($"{{""error"":""Internal server error processing request: {exTask.Message.Replace("""", "'")}""}}")
                                             currentContext.Response.ContentType = "application/json"
                                             currentContext.Response.ContentLength64 = buffer.Length
                                             currentContext.Response.OutputStream.Write(buffer, 0, buffer.Length)
                                         End If
                                     Catch exResponse As Exception
                                         Console.WriteLine($"Further error trying to send error response: {exResponse.Message}")
                                     Finally
                                         If currentContext IsNot Nothing Then
                                             Try
                                                 currentContext.Response.Close()
                                             Catch
                                                 ' suppress
                                             End Try
                                         End If
                                     End Try
                                 End Try
                             End Sub)

                Catch exHttp As HttpListenerException
                    If IsRunning Then
                        ' ErrorCode 995 is "Operation Aborted", common during graceful shutdown
                        If exHttp.ErrorCode <> 995 Then
                            Console.WriteLine($"HttpListenerException in request loop (Code {exHttp.ErrorCode}): {exHttp.Message}")
                        End If
                    End If
                End Try
            End While
        Catch exThread As ThreadAbortException
            Console.WriteLine("API Request handling thread aborted.")
            Thread.ResetAbort()
        Catch exThread As ThreadInterruptedException
            Console.WriteLine("API Request handling thread interrupted.")
        Catch ex As Exception
            If IsRunning Then
                Console.WriteLine($"Unexpected error in API request handling loop: {ex.Message}")
            End If
        Finally
            Console.WriteLine("API request handling loop finished.")
        End Try
    End Sub

    Private Function GetLocalIPAddress() As String
        Try
            For Each netInterface As NetworkInformation.NetworkInterface In NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If netInterface.NetworkInterfaceType = NetworkInformation.NetworkInterfaceType.Wireless80211 OrElse
                   netInterface.NetworkInterfaceType = NetworkInformation.NetworkInterfaceType.Ethernet Then
                    For Each addrInfo As UnicastIPAddressInformation In netInterface.GetIPProperties().UnicastAddresses
                        If addrInfo.Address.AddressFamily = AddressFamily.InterNetwork Then
                            Return addrInfo.Address.ToString()
                        End If
                    Next
                End If
            Next
        Catch ex As Exception
            Console.WriteLine($"Error getting local IP address: {ex.Message}")
        End Try
        Return "localhost" ' Fallback
    End Function

End Class