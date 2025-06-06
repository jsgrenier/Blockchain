Imports Newtonsoft.Json

Public Class MiningServerConfig
    Public Property Port As Integer
    Public Property TargetBlockTimeSeconds As Integer
    Public Property DifficultyAdjustmentInterval As Integer
    Public Property BaseReward As Decimal
    Public Property RewardHalvingInterval As Integer
    Public Property MaxSupply As Decimal ' Though MaxSupply is also in Blockchain.vb, having it here for reference if needed for server logic directly
    Public Property DefaultMinerAddressForEmptyJobs As String ' Optional: a fallback address

    Public Sub New()
        ' Default values
        Port = 8081
        TargetBlockTimeSeconds = 4
        DifficultyAdjustmentInterval = 10
        BaseReward = 50D
        RewardHalvingInterval = 210000
        MaxSupply = 21000000D
        DefaultMinerAddressForEmptyJobs = "server_default_miner_reward_address_if_needed" ' Replace or remove if not needed
    End Sub
End Class