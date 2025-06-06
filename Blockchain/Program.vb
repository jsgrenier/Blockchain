Imports System.Net
Imports System.Threading

Module Program

    Private blockchain As Blockchain
    Private webServer As WebServer
    Private miningServer As MiningServer

    Sub Main()
        Console.WriteLine("Starting Blockchain Node...")
        Try
            blockchain = New Blockchain("blockchain.db")
            Console.WriteLine("Blockchain initialized.")

            If blockchain.Chain.Any() AndAlso blockchain.Chain.Count >= 2 Then
                blockchain.UpdateNetworkHashRateEstimate()
                Console.WriteLine($"Initial network hash rate estimated: {blockchain.CurrentEstimatedNetworkHashRate:F2} H/s")
            End If

            webServer = New WebServer(blockchain)
            webServer.Start()
            Console.WriteLine("Web server initialization attempted.")

            ' miningServer = New MiningServer(8081, blockchain) ' OLD Instantiation
            miningServer = New MiningServer(blockchain)      ' NEW Instantiation (port comes from config)
            miningServer.Start() ' Start will use the port loaded from config
            Console.WriteLine("Mining server initialization attempted.")


            Console.WriteLine("Node components started. Press Enter to stop.")
            Console.ReadLine()

        Catch ex As Exception
            Console.WriteLine($"Critical error during startup: {ex.Message}{vbCrLf}{ex.StackTrace}")
            Console.WriteLine("Application will exit.")
            Console.ReadLine()
            Return
        Finally
            Console.WriteLine("Shutting down...")
            If miningServer IsNot Nothing Then
                Console.WriteLine("Stopping Mining Server...")
                miningServer.Kill()
            End If
            If webServer IsNot Nothing Then
                Console.WriteLine("Stopping Web Server...")
                webServer.Kill()
            End If
            Console.WriteLine("Shutdown complete. Press Enter to exit application.")
            Console.ReadLine()
        End Try
    End Sub

End Module