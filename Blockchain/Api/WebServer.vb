Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading

Public Class WebServer

    Private blockchain As Blockchain
    Private server As HttpListener
    Private listenerThread As Thread
    Private IsRunning As Boolean = False

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
            IsRunning = True
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
            IsRunning = False

            If server IsNot Nothing AndAlso server.IsListening Then
                Console.WriteLine("Stopping API server...")
                server.Stop()
                server.Close()
                Console.WriteLine("API server stopped.")
            End If

            If listenerThread IsNot Nothing AndAlso listenerThread.IsAlive Then
                Console.WriteLine("Waiting for API request handler thread to finish...")
                If Not listenerThread.Join(TimeSpan.FromSeconds(5)) Then
                    Console.WriteLine("API request handler thread did not finish in time. Aborting.")
                    listenerThread.Interrupt() ' Consider Thread.Abort() if Interrupt is not effective, but use with caution
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
                    context = server.GetContext() ' Blocks until a request comes in
                    If Not IsRunning Then
                        If context IsNot Nothing Then
                            ' If server is stopping, abort the request
                            context.Response.StatusCode = CInt(HttpStatusCode.ServiceUnavailable)
                            context.Response.Close()
                        End If
                        Exit While
                    End If

                    ' --- Crucial: Capture context for the task ---
                    Dim capturedContext As HttpListenerContext = context

                    Task.Run(Sub()
                                 Try
                                     ' Check if the listener is still running and context is valid before processing
                                     ' This check might be a bit late if context is disposed between GetContext and here,
                                     ' but it's an attempt to catch some scenarios.
                                     If IsRunning AndAlso server.IsListening AndAlso capturedContext IsNot Nothing Then
                                         handler.HandleRequest(capturedContext) ' HandleRequest is now fully responsible for this context
                                     ElseIf capturedContext IsNot Nothing Then
                                         ' If server stopped or context became invalid before task could run properly
                                         Try
                                             If capturedContext.Response.OutputStream.CanWrite Then ' Check before accessing
                                                 capturedContext.Response.StatusCode = CInt(HttpStatusCode.ServiceUnavailable)
                                                 capturedContext.Response.Close()
                                             End If
                                         Catch ex As Exception
                                             ' Likely already disposed, suppress
                                             Console.WriteLine($"WebServer Task: Error trying to close context for stopped server: {ex.Message}")
                                         End Try
                                     End If
                                 Catch exTask As Exception
                                     ' This catch is for UNEXPECTED exceptions escaping handler.HandleRequest
                                     ' handler.HandleRequest should ideally handle its own errors and send responses.
                                     Console.WriteLine($"WebServer Task: Unhandled exception processing request for {capturedContext?.Request?.Url}: {exTask.Message}{vbCrLf}{exTask.StackTrace}")
                                     Try
                                         If capturedContext?.Response IsNot Nothing AndAlso capturedContext.Response.OutputStream.CanWrite Then
                                             capturedContext.Response.StatusCode = CInt(HttpStatusCode.InternalServerError)
                                             Dim buffer = System.Text.Encoding.UTF8.GetBytes($"{{""error"":""Task Level: Internal server error: {exTask.Message.Replace("""", "'")}""}}")
                                             capturedContext.Response.ContentType = "application/json"
                                             capturedContext.Response.ContentLength64 = buffer.Length
                                             capturedContext.Response.OutputStream.Write(buffer, 0, buffer.Length)
                                             capturedContext.Response.Close() ' Close it here if we sent the error
                                         End If
                                     Catch exResp As Exception
                                         Console.WriteLine($"WebServer Task: Further error sending task-level error response: {exResp.Message}")
                                     End Try
                                 End Try
                             End Sub)
                    context = Nothing ' Release reference to the context in the main loop after dispatching

                Catch exHttp As HttpListenerException
                    If IsRunning Then
                        If exHttp.ErrorCode <> 995 Then
                            Console.WriteLine($"HttpListenerException in request loop (Code {exHttp.ErrorCode}): {exHttp.Message}")
                        End If
                    Else
                        Console.WriteLine($"HttpListenerException (Code {exHttp.ErrorCode}) while shutting down: {exHttp.Message}")
                    End If
                    ' If context was obtained but GetContext threw an error, it might need closing
                    If context IsNot Nothing Then
                        Try : context.Response.Close() : Catch : End Try
                        context = Nothing
                    End If
                Catch exDisposed As ObjectDisposedException
                    If IsRunning Then
                        Console.WriteLine($"ObjectDisposedException in request loop (HttpListener likely disposed): {exDisposed.Message}")
                    Else
                        Console.WriteLine($"ObjectDisposedException (HttpListener likely disposed) while shutting down: {exDisposed.Message}")
                    End If
                    If context IsNot Nothing Then
                        Try : context.Response.Close() : Catch : End Try
                        context = Nothing
                    End If
                Catch exGeneric As Exception
                    If IsRunning Then
                        Console.WriteLine($"Generic Exception in GetContext loop: {exGeneric.Message}{vbCrLf}{exGeneric.StackTrace}")
                    End If
                    If context IsNot Nothing Then
                        Try : context.Response.Close() : Catch : End Try
                        context = Nothing
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
                Console.WriteLine($"Unexpected error in API request handling loop (outer): {ex.Message}{vbCrLf}{ex.StackTrace}")
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