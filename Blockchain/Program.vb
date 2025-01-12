Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading

'This version contains:
'API Call to retrieve the Mempool transactions + updated website.
'Removed signature in data block
'Updated wallet balance - it removes the funds from sender, after validation, adds the funds to the recipient
'TODO
'Add transaction ID so will be easier to track Tx from mempool or blockchain
'If possible, being able to add multiple transactions to a block

Module Program

    Private blockchain As Blockchain

    Sub Main()
        Try
            ' Initialize the blockchain
            blockchain = New Blockchain("blockchain.db")
            ' Start the API server
            Dim server As New WebServer(blockchain)
            server.Start()
            blockchain.StartValidationThread()

            ' Wait for user input to stop the server
            Console.WriteLine("Press Enter to stop the server...")
            Console.ReadLine()

            ' Stop the API server gracefully
            server.Kill()
            blockchain.StopValidationThread()

        Catch ex As Exception
            Console.WriteLine($"Error: {ex.Message}")
        End Try
    End Sub

End Module