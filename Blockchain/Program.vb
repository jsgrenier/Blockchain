Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading


Module Program

    Private blockchain As Blockchain

    Private _validation As Validation

    Sub Main()
        Try
            ' Initialize the blockchain
            blockchain = New Blockchain("blockchain.db")
            ' Start the API server
            Dim server As New WebServer(blockchain)
            server.Start()
            '_validation.StartValidationThread()
            Dim miningServer As New MiningServer(8081, blockchain)
            miningServer.Start()

            ' Wait for user input to stop the server
            Console.WriteLine("Press Enter to stop the server...")
            Console.ReadLine()

            miningServer.Kill()
            '_validation.StopValidationThread()
            ' Stop the API server gracefully
            server.Kill()


        Catch ex As Exception
            Console.WriteLine($"Error: {ex.Message}")
        End Try
    End Sub

End Module