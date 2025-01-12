Imports System.Net
Imports System.Net.Sockets
Imports System.Threading

Public Class WebServer

    Private blockchain As Blockchain
    Private server As HttpListener
    Private listenerThread As Thread
    Private isRunning As Boolean = True

    Public Sub New(ByVal blockchain As Blockchain)
        Me.blockchain = blockchain
    End Sub

    Public Sub Start()
        Try
            server = New HttpListener()

            ' Bind to all local addresses
            Dim prefixes As String() = {
                "http://127.0.0.1:8080/",
                "http://localhost:8080/"
                }
            '$"http://{GetLocalIPAddress()}:8080/"


            For Each prefix In prefixes
                server.Prefixes.Add(prefix)
            Next

            server.Start()
            Console.WriteLine("API server started on the following addresses:")
            For Each prefix In server.Prefixes
                Console.WriteLine(prefix)
            Next

            ' Start a new thread to handle incoming requests
            listenerThread = New Thread(AddressOf HandleRequests)
            listenerThread.IsBackground = True
            listenerThread.Start()
        Catch ex As Exception
            Console.WriteLine($"Failed to start the API server: {ex.Message}")
        End Try
    End Sub

    Public Sub Kill()
        Try
            isRunning = False

            If server IsNot Nothing AndAlso server.IsListening Then
                server.Stop()
                server.Close()
                Console.WriteLine("API server stopped.")
            End If

            If listenerThread IsNot Nothing AndAlso listenerThread.IsAlive Then
                listenerThread.Join()
            End If

        Catch ex As Exception
            Console.WriteLine($"Error stopping the server: {ex.Message}")
        End Try
    End Sub

    Private Sub HandleRequests()
        Dim handler As New RequestHandler(blockchain)

        Try
            While isRunning
                Dim context As HttpListenerContext = server.GetContext()
                handler.HandleRequest(context)
            End While
        Catch ex As HttpListenerException
            If isRunning Then
                Console.WriteLine($"Error in request handling: {ex.Message}")
            End If
        Catch ex As Exception
            Console.WriteLine($"Unexpected error in request handling: {ex.Message}")
        End Try
    End Sub

    Private Function GetLocalIPAddress() As String
        Try
            Using s As Socket = New Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                s.Connect(New IPEndPoint(IPAddress.Parse("8.8.8.8"), 53))
                Return CType(s.LocalEndPoint, IPEndPoint).Address.ToString()
            End Using
        Catch ex As Exception
            Console.WriteLine($"Error getting local IP address: {ex.Message}")
            Return "localhost"
        End Try
    End Function

End Class